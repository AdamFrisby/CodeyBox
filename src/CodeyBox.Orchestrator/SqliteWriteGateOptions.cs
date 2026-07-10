using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Bounds SQLite write-gate acquisition and hold times. The API supplies these
/// values through a reloadable options accessor, so edits affect subsequent
/// acquisitions without restarting the process.
/// </summary>
public sealed class SqliteWriteGateOptions
{
    private static readonly TimeSpan MaximumSupportedTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Maximum time a caller waits to enter the gate. Must be positive.</summary>
    public TimeSpan AcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Elapsed hold time that emits a holder diagnostic. Must be positive.</summary>
    public TimeSpan MaxHoldDuration { get; set; } = TimeSpan.FromSeconds(5);

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
    }

    internal SqliteWriteGateSettings ToSettings()
    {
        Validate();
        return new SqliteWriteGateSettings(AcquisitionTimeout, MaxHoldDuration);
    }
}

internal readonly record struct SqliteWriteGateSettings(
    TimeSpan AcquisitionTimeout,
    TimeSpan MaxHoldDuration);

/// <summary>
/// Supplies hot-reloadable policy, diagnostics, and time to every store handle
/// while the underlying per-database semaphore remains shared process-wide.
/// </summary>
public sealed class SqliteDatabaseWriteGateFactory
{
    private readonly Func<SqliteWriteGateOptions> _optionsAccessor;

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
        Logger = loggerFactory.CreateLogger("CodeyBox.Orchestrator.SqliteDatabaseWriteGate");
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    internal ILogger Logger { get; }
    internal TimeProvider TimeProvider { get; }

    internal SqliteDatabaseWriteGate ForPath(string path)
        => SqliteDatabaseWriteGate.ForPath(path, this);

    internal SqliteWriteGateSettings GetSettings()
    {
        var options = _optionsAccessor()
            ?? throw new InvalidOperationException("The SQLite write gate options accessor returned null.");
        return options.ToSettings();
    }
}
