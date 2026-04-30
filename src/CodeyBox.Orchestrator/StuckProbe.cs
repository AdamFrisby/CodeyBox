using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A point-in-time activity snapshot from a running agent process.
/// The agent is considered active in an interval when either:
///   - <see cref="CpuTicks"/> increased relative to the previous sample (CPU work), or
///   - <see cref="TcpConnections"/> is greater than zero (waiting for a network response).
/// </summary>
internal sealed record ActivitySample(long CpuTicks, int TcpConnections);

/// <summary>
/// Reads activity from the agent process at one instant.
/// Returns <c>null</c> when the agent process is not visible — e.g. it has
/// not yet started, it already exited cleanly, or it runs inside a
/// separate VM (Multipass) where host-side /proc is not applicable.
/// </summary>
internal interface IAgentActivitySource
{
    ActivitySample? TryRead();
}

/// <summary>
/// Null implementation used on non-Linux hosts or when probing is disabled.
/// Always returns null so the probe immediately treats every sample as
/// "agent not found" and never classifies the agent as stuck.
/// </summary>
internal sealed class NullAgentActivitySource : IAgentActivitySource
{
    public static readonly NullAgentActivitySource Instance = new();
    public ActivitySample? TryRead() => null;
}

/// <summary>
/// Shared state written by the probe, read by the caller in an
/// <c>OperationCanceledException</c> filter to distinguish stuck-kill from
/// a normal phase timeout or root cancellation.
///
/// The probe sets <see cref="Detected"/> BEFORE calling
/// <c>phaseCts.Cancel()</c>, exploiting the fact that
/// <c>CancellationTokenSource.Cancel()</c> includes a full memory barrier.
/// By the time the resulting <c>OperationCanceledException</c> propagates to
/// any observer, <see cref="Detected"/> is guaranteed visible.
/// </summary>
internal sealed class StuckContext
{
    private volatile bool _detected;

    /// <summary>True once the probe has classified the agent as stuck.</summary>
    public bool Detected => _detected;

    public string Phase { get; init; } = "";
    public AgentKind AgentKind { get; init; }

    /// <summary>When the first zero-activity sample was recorded in the current streak.</summary>
    public DateTimeOffset FirstZeroAt { get; set; }

    /// <summary>When stuck was officially detected (threshold exceeded).</summary>
    public DateTimeOffset DetectedAt { get; private set; }

    /// <summary>How long the agent appeared idle before the probe fired.</summary>
    public TimeSpan StuckDuration =>
        DetectedAt == default || FirstZeroAt == default
            ? TimeSpan.Zero
            : DetectedAt - FirstZeroAt;

    /// <summary>
    /// Called by the probe immediately before cancelling the phase CTS.
    /// The volatile write ensures <see cref="Detected"/> is visible to other
    /// threads observing the subsequent cancellation.
    /// </summary>
    internal void SignalDetected()
    {
        DetectedAt = DateTimeOffset.UtcNow;
        _detected = true;
    }
}

/// <summary>
/// Periodic liveness probe that runs alongside an agent sandbox phase.
/// Samples CPU ticks and open-socket count every <see cref="PollInterval"/>.
/// After <paramref name="thresholdSamples"/> consecutive samples with zero
/// activity on both dimensions, cancels the phase CTS and sets the
/// <see cref="StuckContext.Detected"/> flag.
///
/// <para>The probe never counts a sample taken before the agent process is
/// first observed alive — this prevents false positives during the git-clone
/// and setup steps that precede the agent binary launch.</para>
///
/// <para>When the activity source returns <c>null</c> (agent not visible —
/// e.g. Multipass guest process, non-Linux host), the probe resets its
/// previous-sample baseline and waits for the next poll. In practice this
/// means the probe is silently disabled for Multipass deployments.</para>
/// </summary>
internal sealed class StuckProbe
{
    /// <summary>Default wall-clock gap between consecutive activity samples.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly IAgentActivitySource _source;
    private readonly int _thresholdSamples;
    private readonly StuckContext _ctx;
    private readonly CancellationTokenSource _phaseCts;
    private readonly ILogger _log;
    private readonly TimeSpan _pollInterval;

    public StuckProbe(
        IAgentActivitySource source,
        int thresholdSamples,
        StuckContext ctx,
        CancellationTokenSource phaseCts,
        ILogger log,
        TimeSpan? pollInterval = null)
    {
        _source = source;
        _thresholdSamples = thresholdSamples;
        _ctx = ctx;
        _phaseCts = phaseCts;
        _log = log;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>
    /// Runs the probe loop until <paramref name="ct"/> is cancelled (agent
    /// exited naturally) or stuck is detected (fires once and returns).
    /// Exceptions from the activity source are swallowed with a debug log so
    /// a buggy probe never disrupts the pipeline.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        ActivitySample? prev = null;
        int zeroStreak = 0;
        bool agentObservedAlive = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return; // agent exited naturally; probe cancelled
            }

            ActivitySample? sample;
            try
            {
                sample = _source.TryRead();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Stuck probe [{Phase}]: activity read threw; skipping sample", _ctx.Phase);
                continue;
            }

            if (sample is null)
            {
                // Agent not visible — skip sample without resetting zeroStreak.
                // Reset prev so the next non-null sample starts a fresh delta.
                if (agentObservedAlive)
                    _log.LogDebug("Stuck probe [{Phase}]: agent disappeared from /proc (may have exited)", _ctx.Phase);
                prev = null;
                continue;
            }

            agentObservedAlive = true;

            if (prev is not null)
            {
                var cpuDelta = sample.CpuTicks - prev.CpuTicks;
                var tcpConns = sample.TcpConnections;
                var isActive = cpuDelta > 0 || tcpConns > 0;

                _log.LogDebug(
                    "Stuck probe [{Phase}]: cpuDelta={CpuDelta} tcpConns={TcpConns} zeroStreak={ZeroStreak}/{Threshold}",
                    _ctx.Phase, cpuDelta, tcpConns, isActive ? 0 : zeroStreak + 1, _thresholdSamples);

                if (isActive)
                {
                    zeroStreak = 0;
                }
                else
                {
                    if (zeroStreak == 0)
                        _ctx.FirstZeroAt = DateTimeOffset.UtcNow;
                    zeroStreak++;

                    if (zeroStreak >= _thresholdSamples)
                    {
                        // Set flag BEFORE cancelling so the catch filter sees it.
                        _ctx.SignalDetected();
                        _log.LogWarning(
                            "Stuck probe [{Phase}]: agent {Agent} stuck for {Seconds}s — cancelling phase CTS",
                            _ctx.Phase, _ctx.AgentKind.Value, (int)_ctx.StuckDuration.TotalSeconds);
                        _phaseCts.Cancel();
                        return;
                    }
                }
            }

            prev = sample;
        }
    }
}

/// <summary>
/// Thrown by <see cref="PipelineRunner.RunWithStuckProbeAsync"/> when the
/// stuck probe detects and kills a hung agent. Carries the
/// <see cref="StuckContext"/> for event emission at the RunAsync level.
/// </summary>
internal sealed class AgentStuckException : Exception
{
    public StuckContext Context { get; }

    public AgentStuckException(StuckContext ctx)
        : base($"Agent '{ctx.AgentKind.Value}' stuck in '{ctx.Phase}' phase for " +
               $"{(int)ctx.StuckDuration.TotalSeconds}s with no CPU or network activity")
    {
        Context = ctx;
    }
}

/// <summary>
/// Webhook details payload for the <c>work_item.agent_stuck</c> event.
/// </summary>
internal sealed record AgentStuckDetails
{
    public required string Phase { get; init; }
    public required string AgentKind { get; init; }
    public int StuckSeconds { get; init; }
    public bool Killed { get; init; }
}
