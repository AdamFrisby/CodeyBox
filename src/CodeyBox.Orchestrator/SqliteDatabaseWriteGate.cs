using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Classifies a SQLite write-gate holder so waiters can tell an expected,
/// policy-bounded maintenance hold apart from a genuinely stuck writer.
/// Maintenance holds announce their expected duration up front; while the
/// holder is inside that budget, waiter-side escalation (dispatch host-stop,
/// overlong-hold diagnostics) stays quiet. Past the budget the hold is
/// treated exactly like any other stuck holder.
/// </summary>
internal enum WriteGateHolderKind
{
    Normal,
    Maintenance,
}

/// <summary>
/// Shared in-process write gate for a SQLite database file. SQLite WAL still
/// permits only one writer at a time, so all stores that point at the same file
/// must coordinate before issuing write commands on their separate connections.
/// </summary>
internal sealed class SqliteDatabaseWriteGate : IDisposable
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<OwnershipScope?> Ownership = new();

    private readonly string _path;
    private readonly SqliteDatabaseWriteGateFactory _factory;
    private Entry? _entry;

    private SqliteDatabaseWriteGate(
        string path,
        Entry entry,
        SqliteDatabaseWriteGateFactory factory)
    {
        _path = path;
        _entry = entry;
        _factory = factory;
    }

    public static SqliteDatabaseWriteGate ForPath(
        string path,
        SqliteDatabaseWriteGateFactory? factory = null)
    {
        var fullPath = Path.GetFullPath(path);
        lock (Sync)
        {
            if (!Entries.TryGetValue(fullPath, out var entry))
            {
                entry = new Entry();
                Entries.Add(fullPath, entry);
            }

            entry.RefCount++;
            return new SqliteDatabaseWriteGate(
                fullPath,
                entry,
                SqliteDatabaseWriteGateFactory.Resolve(factory));
        }
    }

    public void Wait(
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "")
    {
        var entry = Current;
        var holderIdentity = FormatHolderIdentity(sourceFilePath, memberName);
        ThrowIfReentrant(entry, holderIdentity);

        var settings = _factory.GetSettings();
        var sw = Stopwatch.StartNew();
        bool acquired;
        try
        {
            acquired = entry.Semaphore.Wait(0, ct);
            if (!acquired)
            {
                EnterWaitQueue(entry, settings.MaxQueuedWaiters, holderIdentity);
                try
                {
                    acquired = entry.Semaphore.Wait(settings.AcquisitionTimeout, ct);
                }
                finally
                {
                    LeaveWaitQueue(entry);
                }
            }
        }
        catch (OperationCanceledException)
        {
            RecordWait(sw, "canceled");
            throw;
        }
        catch (SqliteWriteGateWaitQueueFullException)
        {
            RecordWait(sw, "queue_full");
            throw;
        }

        if (!acquired)
        {
            RecordWait(sw, "timed_out");
            throw CreateAcquisitionTimeout(entry, holderIdentity, settings.AcquisitionTimeout);
        }

        RecordWait(sw, "acquired");
        CreateLeaseFailureAtomic(entry, holderIdentity, settings).Activate();
    }

    public WriteGateAcquisition WaitAsync(
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "")
    {
        var entry = Current;
        var holderIdentity = FormatHolderIdentity(sourceFilePath, memberName);
        ThrowIfReentrant(entry, holderIdentity);
        return new WriteGateAcquisition(WaitCoreAsync(entry, holderIdentity, ct));
    }

    /// <summary>
    /// Acquires the gate for a planned maintenance hold (e.g. a SQLite VACUUM)
    /// whose expected duration is announced up front via
    /// <paramref name="expectedHoldDuration"/>. Waiters observe the announced
    /// budget through <see cref="SqliteWriteGateAcquisitionTimeoutException"/>
    /// and suppress escalation while the holder stays inside it; the lease
    /// watchdog likewise stays quiet until the budget is exceeded.
    /// </summary>
    /// <param name="expectedHoldDuration">
    /// Maximum gate-hold the maintenance operation plans for (statement
    /// timeout plus local overhead). Must be positive.
    /// </param>
    public WriteGateAcquisition WaitForMaintenanceAsync(
        TimeSpan expectedHoldDuration,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "")
    {
        if (expectedHoldDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(expectedHoldDuration),
                "The expected maintenance hold duration must be positive.");

        var entry = Current;
        var holderIdentity = FormatHolderIdentity(sourceFilePath, memberName);
        ThrowIfReentrant(entry, holderIdentity);
        return new WriteGateAcquisition(
            WaitCoreAsync(entry, holderIdentity, ct, WriteGateHolderKind.Maintenance, expectedHoldDuration));
    }

    public void Release()
    {
        var entry = Current;
        var lease = Volatile.Read(ref entry.Holder)
            ?? throw new SynchronizationLockException("The SQLite write gate is not held.");
        lease.Release();
    }

    public void Dispose()
    {
        Entry? disposeEntry = null;

        lock (Sync)
        {
            var entry = _entry;
            if (entry is null)
                return;

            _entry = null;
            entry.RefCount--;
            if (entry.RefCount == 0 && Entries.TryGetValue(_path, out var current) && ReferenceEquals(current, entry))
            {
                Entries.Remove(_path);
                disposeEntry = entry;
            }
        }

        disposeEntry?.Semaphore.Dispose();
    }

    private async Task<HolderLease> WaitCoreAsync(
        Entry entry,
        string holderIdentity,
        CancellationToken ct,
        WriteGateHolderKind holderKind = WriteGateHolderKind.Normal,
        TimeSpan? expectedHoldDuration = null)
    {
        var settings = _factory.GetSettings();
        var watchdogThreshold = holderKind == WriteGateHolderKind.Maintenance && expectedHoldDuration.HasValue
            ? expectedHoldDuration.Value
            : settings.MaxHoldDuration;
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var acquired = entry.Semaphore.Wait(0, ct);
            if (!acquired)
            {
                EnterWaitQueue(entry, settings.MaxQueuedWaiters, holderIdentity);
                try
                {
                    using var timeout = new CancellationTokenSource(settings.AcquisitionTimeout, _factory.TimeProvider);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    try
                    {
                        await entry.Semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
                        RecordWait(sw, "acquired");
                        return CreateLeaseFailureAtomic(entry, holderIdentity, settings, holderKind, watchdogThreshold);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
                    {
                        RecordWait(sw, "timed_out");
                        throw CreateAcquisitionTimeout(entry, holderIdentity, settings.AcquisitionTimeout);
                    }
                }
                finally
                {
                    LeaveWaitQueue(entry);
                }
            }

            RecordWait(sw, "acquired");
            return CreateLeaseFailureAtomic(entry, holderIdentity, settings, holderKind, watchdogThreshold);
        }
        catch (OperationCanceledException)
        {
            RecordWait(sw, "canceled");
            throw;
        }
        catch (SqliteWriteGateWaitQueueFullException)
        {
            RecordWait(sw, "queue_full");
            throw;
        }
    }

    private HolderLease CreateLeaseFailureAtomic(
        Entry entry,
        string holderIdentity,
        SqliteWriteGateSettings settings,
        WriteGateHolderKind holderKind = WriteGateHolderKind.Normal,
        TimeSpan? watchdogThreshold = null)
    {
        HolderLease? lease = null;
        try
        {
            lease = new HolderLease(
                entry,
                holderIdentity,
                watchdogThreshold ?? settings.MaxHoldDuration,
                _factory.TimeProvider,
                _factory.Logger,
                holderKind);
            var prior = Interlocked.CompareExchange(ref entry.Holder, lease, null);
            if (prior is null)
            {
                lease.ArmWatchdog();
                return lease;
            }

            throw new InvalidOperationException("The SQLite write gate recorded multiple simultaneous holders.");
        }
        catch
        {
            if (lease is not null)
                lease.DisposeWithoutRelease();
            Interlocked.CompareExchange(ref entry.Holder, null, lease);
            entry.Semaphore.Release();
            throw;
        }
    }

    private static void EnterWaitQueue(Entry entry, int maxQueuedWaiters, string holderIdentity)
    {
        var waiters = Interlocked.Increment(ref entry.WaiterCount);
        if (waiters <= maxQueuedWaiters)
            return;

        Interlocked.Decrement(ref entry.WaiterCount);
        throw new SqliteWriteGateWaitQueueFullException(holderIdentity, maxQueuedWaiters);
    }

    private static void LeaveWaitQueue(Entry entry) => Interlocked.Decrement(ref entry.WaiterCount);

    private void TryLogAcquisitionTimeout(
        string waitingHolderIdentity,
        string? currentHolder,
        TimeSpan timeout)
    {
        try
        {
            _factory.Logger.LogError(
                "Timed out after {AcquisitionTimeout} acquiring the SQLite write gate for {WaitingHolder}; current holder: {CurrentHolder}",
                timeout,
                waitingHolderIdentity,
                currentHolder ?? "unknown");
        }
        catch
        {
        }
    }

    private SqliteWriteGateAcquisitionTimeoutException CreateAcquisitionTimeout(
        Entry entry,
        string waitingHolderIdentity,
        TimeSpan timeout)
    {
        var currentLease = Volatile.Read(ref entry.Holder);
        var currentHolder = currentLease?.Identity;
        var currentHolderKind = currentLease?.Kind ?? WriteGateHolderKind.Normal;
        var currentHolderWithinExpectedHold = currentLease is not null
            && currentLease.IsWithinExpectedHold(_factory.TimeProvider.GetUtcNow());
        if (Interlocked.Exchange(ref entry.TimeoutDiagnosticQueued, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem<TimeoutDiagnostic>(
                static state =>
                {
                    try
                    {
                        state.Gate.TryLogAcquisitionTimeout(state.WaitingHolder, state.CurrentHolder, state.Timeout);
                    }
                    finally
                    {
                        Volatile.Write(ref state.Entry.TimeoutDiagnosticQueued, 0);
                    }
                },
                new TimeoutDiagnostic(this, entry, waitingHolderIdentity, currentHolder, timeout),
                preferLocal: false);
        }
        return new SqliteWriteGateAcquisitionTimeoutException(
            waitingHolderIdentity,
            currentHolder,
            timeout,
            currentHolderKind,
            currentHolderWithinExpectedHold);
    }

    private static void ThrowIfReentrant(
        Entry entry,
        string waitingHolderIdentity)
    {
        for (var scope = Ownership.Value; scope is not null; scope = scope.Previous)
        {
            if (scope.IsActive && ReferenceEquals(scope.Entry, entry))
            {
                throw new SqliteWriteGateReentrancyException(
                    waitingHolderIdentity,
                    scope.Identity);
            }
        }
    }

    private static string FormatHolderIdentity(string sourceFilePath, string memberName)
    {
        var typeName = Path.GetFileNameWithoutExtension(sourceFilePath);
        return string.IsNullOrEmpty(typeName) ? memberName : $"{typeName}.{memberName}";
    }

    private Entry Current => _entry ?? throw new ObjectDisposedException(nameof(SqliteDatabaseWriteGate));

    private static void RecordWait(Stopwatch sw, string outcome) =>
        CodeyBoxMeters.CoordinatorSqliteWriteGateWait.Record(
            sw.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// Activates AsyncLocal ownership in GetResult so the caller's continuation,
    /// including ConfigureAwait(false) continuations, carries the re-entry guard.
    /// </summary>
    internal readonly struct WriteGateAcquisition
    {
        private readonly Task<HolderLease> _task;

        internal WriteGateAcquisition(Task<HolderLease> task) => _task = task;

        public Awaiter GetAwaiter() => new(_task.GetAwaiter());

        public ConfiguredAwaitable ConfigureAwait(bool continueOnCapturedContext)
            => new(_task.ConfigureAwait(continueOnCapturedContext).GetAwaiter());

        internal readonly struct Awaiter(TaskAwaiter<HolderLease> awaiter) : ICriticalNotifyCompletion
        {
            public bool IsCompleted => awaiter.IsCompleted;

            public void OnCompleted(Action continuation) => awaiter.OnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation) => awaiter.UnsafeOnCompleted(continuation);

            public void GetResult() => awaiter.GetResult().Activate();
        }

        internal readonly struct ConfiguredAwaitable(
            ConfiguredTaskAwaitable<HolderLease>.ConfiguredTaskAwaiter awaiter)
        {
            public ConfiguredAwaiter GetAwaiter() => new(awaiter);
        }

        internal readonly struct ConfiguredAwaiter(
            ConfiguredTaskAwaitable<HolderLease>.ConfiguredTaskAwaiter awaiter) : ICriticalNotifyCompletion
        {
            public bool IsCompleted => awaiter.IsCompleted;

            public void OnCompleted(Action continuation) => awaiter.OnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation) => awaiter.UnsafeOnCompleted(continuation);

            public void GetResult() => awaiter.GetResult().Activate();
        }
    }

    internal sealed class HolderLease
    {
        private readonly Entry _entry;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly TimeSpan _watchdogThreshold;
        private readonly DateTimeOffset _acquiredAt;
        private ITimer? _watchdog;
        private OwnershipScope? _scope;
        private int _released;
        private int _overlongReported;

        internal HolderLease(
            Entry entry,
            string identity,
            TimeSpan watchdogThreshold,
            TimeProvider timeProvider,
            ILogger logger,
            WriteGateHolderKind kind = WriteGateHolderKind.Normal)
        {
            if (watchdogThreshold <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(watchdogThreshold),
                    "The watchdog threshold must be positive.");

            _entry = entry;
            Identity = identity;
            Kind = kind;
            _watchdogThreshold = watchdogThreshold;
            _timeProvider = timeProvider;
            _logger = logger;
            _acquiredAt = timeProvider.GetUtcNow();
        }

        public string Identity { get; }

        public WriteGateHolderKind Kind { get; }

        public bool IsWithinExpectedHold(DateTimeOffset now) => now - _acquiredAt < _watchdogThreshold;

        public void ArmWatchdog()
        {
            _watchdog = _timeProvider.CreateTimer(
                static state => ((HolderLease)state!).ReportOverlongHold(),
                this,
                _watchdogThreshold,
                Timeout.InfiniteTimeSpan);
        }

        public void Activate()
        {
            if (Volatile.Read(ref _released) != 0)
                throw new ObjectDisposedException(nameof(HolderLease));
            if (_scope is not null)
                throw new InvalidOperationException("The SQLite write gate lease was activated more than once.");

            var scope = new OwnershipScope(_entry, Identity, Ownership.Value);
            _scope = scope;
            Ownership.Value = scope;
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                throw new SynchronizationLockException("The SQLite write gate lease was released more than once.");

            Exception? releaseFailure = null;
            var scope = _scope;
            if (scope is not null)
            {
                scope.Deactivate();
                if (ReferenceEquals(Ownership.Value, scope))
                    Ownership.Value = scope.Previous;
            }

            if (!ReferenceEquals(Interlocked.CompareExchange(ref _entry.Holder, null, this), this))
                releaseFailure = new SynchronizationLockException("The SQLite write gate holder changed before release.");
            else
                _entry.Semaphore.Release();

            try
            {
                _watchdog?.Dispose();
            }
            catch
            {
            }

            ReportOverlongHoldIfNeeded();
            if (releaseFailure is not null)
                throw releaseFailure;
        }

        public void DisposeWithoutRelease()
        {
            Interlocked.Exchange(ref _released, 1);
            try
            {
                _watchdog?.Dispose();
            }
            catch
            {
            }
        }

        private void ReportOverlongHold()
        {
            try
            {
                if (Volatile.Read(ref _released) != 0 || !ReferenceEquals(Volatile.Read(ref _entry.Holder), this))
                    return;

                ReportOverlongHoldIfNeeded();
            }
            catch
            {
            }
        }

        private void ReportOverlongHoldIfNeeded()
        {
            var elapsed = _timeProvider.GetUtcNow() - _acquiredAt;
            if (elapsed < _watchdogThreshold || Interlocked.Exchange(ref _overlongReported, 1) != 0)
                return;

            try
            {
                _logger.LogError(
                    "SQLite write gate holder {HolderIdentity} exceeded the configured maximum hold duration {MaxHoldDuration}; held for {Elapsed}",
                    Identity,
                    _watchdogThreshold,
                    elapsed);
            }
            catch
            {
            }
        }
    }

    private sealed record TimeoutDiagnostic(
        SqliteDatabaseWriteGate Gate,
        Entry Entry,
        string WaitingHolder,
        string? CurrentHolder,
        TimeSpan Timeout);

    private sealed class OwnershipScope(Entry entry, string identity, OwnershipScope? previous)
    {
        private int _active = 1;

        public Entry Entry { get; } = entry;
        public string Identity { get; } = identity;
        public OwnershipScope? Previous { get; } = previous;
        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Deactivate() => Volatile.Write(ref _active, 0);
    }

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public HolderLease? Holder;
        public int RefCount;
        public int WaiterCount;
        public int TimeoutDiagnosticQueued;
    }
}

internal sealed class SqliteWriteGateWaitQueueFullException : InvalidOperationException
{
    public SqliteWriteGateWaitQueueFullException(string waitingHolder, int maxQueuedWaiters)
        : base($"SQLite write gate acquisition by '{waitingHolder}' was rejected because {maxQueuedWaiters} waiters are already queued.")
    {
        WaitingHolder = waitingHolder;
        MaxQueuedWaiters = maxQueuedWaiters;
    }

    public string WaitingHolder { get; }
    public int MaxQueuedWaiters { get; }
}

internal sealed class SqliteWriteGateAcquisitionTimeoutException : TimeoutException
{
    public SqliteWriteGateAcquisitionTimeoutException(
        string waitingHolder,
        string? currentHolder,
        TimeSpan timeout,
        WriteGateHolderKind currentHolderKind = WriteGateHolderKind.Normal,
        bool currentHolderWithinExpectedHold = false)
        : base($"SQLite write gate acquisition by '{waitingHolder}' timed out after {timeout}; current holder: '{currentHolder ?? "unknown"}'.")
    {
        WaitingHolder = waitingHolder;
        CurrentHolder = currentHolder;
        Timeout = timeout;
        CurrentHolderKind = currentHolderKind;
        CurrentHolderWithinExpectedHold = currentHolderWithinExpectedHold;
    }

    public string WaitingHolder { get; }
    public string? CurrentHolder { get; }
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Classifies the holder that was blocking the gate when the wait timed
    /// out. A <see cref="WriteGateHolderKind.Maintenance"/> holder still inside
    /// its announced budget is an expected, policy-bounded stall — not evidence
    /// of a stuck writer.
    /// </summary>
    public WriteGateHolderKind CurrentHolderKind { get; }

    /// <summary>
    /// Whether the blocking holder was still inside its announced hold budget
    /// when the wait timed out. Only meaningful together with
    /// <see cref="CurrentHolderKind"/>.
    /// </summary>
    public bool CurrentHolderWithinExpectedHold { get; }

    /// <summary>
    /// True when the timeout was caused by a planned maintenance hold that is
    /// still within its announced budget. Such timeouts must be absorbed with
    /// a backoff, never counted toward stuck-holder escalation.
    /// </summary>
    public bool IsExpectedMaintenanceHold =>
        CurrentHolderKind == WriteGateHolderKind.Maintenance && CurrentHolderWithinExpectedHold;
}

internal sealed class SqliteWriteGateReentrancyException : InvalidOperationException
{
    public SqliteWriteGateReentrancyException(string waitingHolder, string currentHolder)
        : base($"SQLite write gate re-entry by '{waitingHolder}' while '{currentHolder}' holds the same database gate is not allowed.")
    {
        WaitingHolder = waitingHolder;
        CurrentHolder = currentHolder;
    }

    public string WaitingHolder { get; }
    public string CurrentHolder { get; }
}

/// <summary>
/// Escalation signal raised when the dispatch loop cannot acquire the SQLite
/// write gate for <c>MaxConsecutiveDispatchGateTimeoutsBeforeEscalation</c>
/// consecutive pickup attempts. A single acquisition timeout is transient and
/// handled with a backoff; a sustained run means the gate holder is stuck or
/// the database is wedged, which must surface (and stop the host with a
/// non-zero exit) rather than retry silently forever.
/// </summary>
public sealed class SqliteWriteGatePersistentlyUnavailableException : TimeoutException
{
    public SqliteWriteGatePersistentlyUnavailableException(
        int consecutiveTimeouts,
        string? lastWaitingHolder,
        string? lastCurrentHolder)
        : base($"SQLite write gate acquisition failed {consecutiveTimeouts} consecutive times (last waiter: '{lastWaitingHolder ?? "unknown"}', last holder: '{lastCurrentHolder ?? "unknown"}'); the gate holder may be stuck.")
    {
        ConsecutiveTimeouts = consecutiveTimeouts;
        LastWaitingHolder = lastWaitingHolder;
        LastCurrentHolder = lastCurrentHolder;
    }

    public int ConsecutiveTimeouts { get; }
    public string? LastWaitingHolder { get; }
    public string? LastCurrentHolder { get; }
}
