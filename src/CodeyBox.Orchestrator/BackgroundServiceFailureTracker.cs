namespace CodeyBox.Orchestrator;

/// <summary>
/// Process-wide record of a fatal <see cref="OrchestratorService"/> failure.
/// <see cref="BackgroundService"/> faults stop the host via
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>, which the host reports
/// as a graceful shutdown (exit code 0) — indistinguishable from an
/// intentional stop, so a supervisor configured with
/// <c>Restart=on-failure</c> never restarts. The orchestrator reports its
/// terminal exception here before rethrowing, and the composition root maps
/// a recorded fault to a non-zero process exit code.
/// </summary>
public sealed class BackgroundServiceFailureTracker
{
    /// <summary>Exit code for an intentional shutdown with no recorded fault.</summary>
    public const int IntentionalShutdownExitCode = 0;

    /// <summary>Exit code when a background service reported a terminal fault.</summary>
    public const int BackgroundServiceFaultExitCode = 1;

    private Exception? _fault;

    /// <summary>The first terminal exception reported, or null when healthy.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Records a terminal failure. The first report wins.</summary>
    public void ReportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.CompareExchange(ref _fault, exception, null);
    }

    /// <summary>
    /// Maps shutdown outcome to a process exit code: non-zero when a
    /// background-service fault was recorded, zero for an intentional stop.
    /// </summary>
    public static int ResolveExitCode(bool backgroundServiceFaulted) =>
        backgroundServiceFaulted ? BackgroundServiceFaultExitCode : IntentionalShutdownExitCode;

    /// <summary>Exit code for this tracker's current state.</summary>
    public int ResolveExitCode() => ResolveExitCode(Fault is not null);
}
