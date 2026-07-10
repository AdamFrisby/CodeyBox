using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

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
                factory ?? SqliteDatabaseWriteGateFactory.Default);
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
        if (!entry.Semaphore.Wait(settings.AcquisitionTimeout, ct))
            throw CreateAcquisitionTimeout(entry, holderIdentity, settings.AcquisitionTimeout);

        CreateLease(entry, holderIdentity, settings).Activate();
    }

    public WaitOperation WaitAsync(
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "")
    {
        var entry = Current;
        var holderIdentity = FormatHolderIdentity(sourceFilePath, memberName);
        ThrowIfReentrant(entry, holderIdentity);
        return new WaitOperation(WaitCoreAsync(entry, holderIdentity, ct));
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
        CancellationToken ct)
    {
        var settings = _factory.GetSettings();
        using var timeout = new CancellationTokenSource(settings.AcquisitionTimeout, _factory.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await entry.Semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw CreateAcquisitionTimeout(entry, holderIdentity, settings.AcquisitionTimeout);
        }

        return CreateLease(entry, holderIdentity, settings);
    }

    private HolderLease CreateLease(
        Entry entry,
        string holderIdentity,
        SqliteWriteGateSettings settings)
    {
        var lease = new HolderLease(
            entry,
            holderIdentity,
            settings.MaxHoldDuration,
            _factory.TimeProvider,
            _factory.Logger);
        var prior = Interlocked.CompareExchange(ref entry.Holder, lease, null);
        if (prior is null)
            return lease;

        entry.Semaphore.Release();
        lease.DisposeWithoutRelease();
        throw new InvalidOperationException("The SQLite write gate recorded multiple simultaneous holders.");
    }

    private SqliteWriteGateAcquisitionTimeoutException CreateAcquisitionTimeout(
        Entry entry,
        string waitingHolderIdentity,
        TimeSpan timeout)
    {
        var currentHolder = Volatile.Read(ref entry.Holder)?.Identity;
        _factory.Logger.LogError(
            "Timed out after {AcquisitionTimeout} acquiring the SQLite write gate for {WaitingHolder}; current holder: {CurrentHolder}",
            timeout,
            waitingHolderIdentity,
            currentHolder ?? "unknown");
        return new SqliteWriteGateAcquisitionTimeoutException(
            waitingHolderIdentity,
            currentHolder,
            timeout);
    }

    private static void ThrowIfReentrant(Entry entry, string waitingHolderIdentity)
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

    internal readonly struct WaitOperation
    {
        private readonly Task<HolderLease> _task;

        internal WaitOperation(Task<HolderLease> task) => _task = task;

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
        private readonly TimeSpan _maxHoldDuration;
        private readonly DateTimeOffset _acquiredAt;
        private readonly ITimer _watchdog;
        private OwnershipScope? _scope;
        private int _released;

        internal HolderLease(
            Entry entry,
            string identity,
            TimeSpan maxHoldDuration,
            TimeProvider timeProvider,
            ILogger logger)
        {
            _entry = entry;
            Identity = identity;
            _maxHoldDuration = maxHoldDuration;
            _timeProvider = timeProvider;
            _logger = logger;
            _acquiredAt = timeProvider.GetUtcNow();
            _watchdog = timeProvider.CreateTimer(
                static state => ((HolderLease)state!).ReportOverlongHold(),
                this,
                maxHoldDuration,
                Timeout.InfiniteTimeSpan);
        }

        public string Identity { get; }

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

            _watchdog.Dispose();
            var scope = _scope;
            if (scope is not null)
            {
                scope.Deactivate();
                if (ReferenceEquals(Ownership.Value, scope))
                    Ownership.Value = scope.Previous;
            }

            if (!ReferenceEquals(Interlocked.CompareExchange(ref _entry.Holder, null, this), this))
                throw new SynchronizationLockException("The SQLite write gate holder changed before release.");
            _entry.Semaphore.Release();
        }

        public void DisposeWithoutRelease()
        {
            Interlocked.Exchange(ref _released, 1);
            _watchdog.Dispose();
        }

        private void ReportOverlongHold()
        {
            if (Volatile.Read(ref _released) != 0 || !ReferenceEquals(Volatile.Read(ref _entry.Holder), this))
                return;

            var elapsed = _timeProvider.GetUtcNow() - _acquiredAt;
            _logger.LogError(
                "SQLite write gate holder {HolderIdentity} exceeded the configured maximum hold duration {MaxHoldDuration}; held for {Elapsed}",
                Identity,
                _maxHoldDuration,
                elapsed);
        }
    }

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
    }
}

internal sealed class SqliteWriteGateAcquisitionTimeoutException : TimeoutException
{
    public SqliteWriteGateAcquisitionTimeoutException(
        string waitingHolder,
        string? currentHolder,
        TimeSpan timeout)
        : base($"SQLite write gate acquisition by '{waitingHolder}' timed out after {timeout}; current holder: '{currentHolder ?? "unknown"}'.")
    {
        WaitingHolder = waitingHolder;
        CurrentHolder = currentHolder;
        Timeout = timeout;
    }

    public string WaitingHolder { get; }
    public string? CurrentHolder { get; }
    public TimeSpan Timeout { get; }
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
