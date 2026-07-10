using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Bounds SQLite write-gate acquisition waits and read-side concurrency. The
/// hold duration value is a diagnostic threshold, not an enforced cancellation
/// point. The API supplies these values through a reloadable options accessor,
/// so edits affect subsequent acquisitions without restarting the process.
/// </summary>
public sealed class SqliteWriteGateOptions
{
    private static readonly TimeSpan MaximumSupportedTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Maximum time a caller waits to enter the gate. Must be positive and no greater than 24.20:31:23.6470000.</summary>
    public TimeSpan AcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Elapsed hold time that emits a holder diagnostic. Must be positive and no greater than 24.20:31:23.6470000.</summary>
    public TimeSpan MaxHoldDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum callers allowed to queue behind a held write gate. Must be positive.</summary>
    public int MaxQueuedWaiters { get; set; } = 1024;

    /// <summary>Maximum concurrent read-side SQLite connections opened by enrichment paths. Must be positive.</summary>
    public int MaxConcurrentReadConnections { get; set; } = 64;

    public void Validate()
    {
        if (AcquisitionTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:SqliteWriteGate:AcquisitionTimeout must be positive");
        if (AcquisitionTimeout > MaximumSupportedTimeout)
            throw new InvalidOperationException($"CodeyBox:SqliteWriteGate:AcquisitionTimeout must be <= {MaximumSupportedTimeout}");
        if (MaxHoldDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:SqliteWriteGate:MaxHoldDuration must be positive");
        if (MaxHoldDuration > MaximumSupportedTimeout)
            throw new InvalidOperationException($"CodeyBox:SqliteWriteGate:MaxHoldDuration must be <= {MaximumSupportedTimeout}");
        if (MaxQueuedWaiters <= 0)
            throw new InvalidOperationException("CodeyBox:SqliteWriteGate:MaxQueuedWaiters must be positive");
        if (MaxConcurrentReadConnections <= 0)
            throw new InvalidOperationException("CodeyBox:SqliteWriteGate:MaxConcurrentReadConnections must be positive");
    }

    internal SqliteWriteGateSettings ToSettings()
    {
        Validate();
        return new SqliteWriteGateSettings(
            AcquisitionTimeout,
            MaxHoldDuration,
            MaxQueuedWaiters,
            MaxConcurrentReadConnections);
    }
}

internal readonly record struct SqliteWriteGateSettings(
    TimeSpan AcquisitionTimeout,
    TimeSpan MaxHoldDuration,
    int MaxQueuedWaiters,
    int MaxConcurrentReadConnections);

/// <summary>
/// Supplies hot-reloadable policy, diagnostics, and time to every store handle
/// while the underlying per-database semaphore remains shared process-wide.
/// </summary>
public sealed class SqliteDatabaseWriteGateFactory
{
    private readonly Func<SqliteWriteGateOptions> _optionsAccessor;
    private readonly ConcurrentDictionary<string, ReadConcurrencyGate> _readGates = new(StringComparer.Ordinal);

    internal static SqliteDatabaseWriteGateFactory Default { get; } = new(
        static () => new SqliteWriteGateOptions(),
        NullLoggerFactory.Instance,
        TimeProvider.System);

    public SqliteDatabaseWriteGateFactory(
        Func<SqliteWriteGateOptions> optionsAccessor,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _optionsAccessor = optionsAccessor;
        Logger = loggerFactory.CreateLogger<SqliteDatabaseWriteGate>();
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    internal ILogger Logger { get; }
    internal TimeProvider TimeProvider { get; }

    internal SqliteDatabaseWriteGate ForPath(string path)
        => SqliteDatabaseWriteGate.ForPath(path, this);

    internal async ValueTask<IDisposable> AcquireReadConnectionSlotAsync(string path, CancellationToken ct)
    {
        var gate = _readGates.GetOrAdd(Path.GetFullPath(path), static _ => new ReadConcurrencyGate());
        return await gate.WaitAsync(GetSettings().MaxConcurrentReadConnections, ct).ConfigureAwait(false);
    }

    internal SqliteWriteGateSettings GetSettings()
    {
        var options = _optionsAccessor()
            ?? throw new InvalidOperationException("The SQLite write gate options accessor returned null.");
        return options.ToSettings();
    }

    private sealed class ReadConcurrencyGate
    {
        private int _active;

        public async ValueTask<IDisposable> WaitAsync(int maxConcurrentReads, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var observed = Volatile.Read(ref _active);
                if (observed >= maxConcurrentReads)
                    throw new SqliteReadConcurrencyLimitExceededException(maxConcurrentReads);
                if (Interlocked.CompareExchange(ref _active, observed + 1, observed) == observed)
                    return new ReadLease(this);

                await Task.Yield();
            }
        }

        private void Release() => Interlocked.Decrement(ref _active);

        private sealed class ReadLease(ReadConcurrencyGate gate) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    gate.Release();
            }
        }
    }
}

internal sealed class SqliteReadConcurrencyLimitExceededException : InvalidOperationException
{
    public SqliteReadConcurrencyLimitExceededException(int maxConcurrentReads)
        : base($"SQLite read-side enrichment was rejected because {maxConcurrentReads} read connections are already active.")
    {
        MaxConcurrentReads = maxConcurrentReads;
    }

    public int MaxConcurrentReads { get; }
}
