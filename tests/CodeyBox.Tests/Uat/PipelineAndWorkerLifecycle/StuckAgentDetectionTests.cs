using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.PipelineAndWorkerLifecycle;

/// <summary>
/// UAT coverage for <c>Stuck-agent detection - Detects idle agents and optionally retries the same phase</c>.
/// Plan anchor: docs/uat/00-plan.md#stuck-agent-detection---detects-idle-agents-and-optionally-retries-the-same-phase
/// </summary>
public sealed class StuckAgentDetectionTests
{
    [Fact]
    public async Task ConsecutiveZeroActivitySamples_CancelPhaseAndMarkContextDetected()
    {
        using var phaseCts = new CancellationTokenSource();
        using var runCts = new CancellationTokenSource();
        var source = new SequencedActivitySource(
            runCts,
            [
                new ActivitySample(CpuTicks: 10, TcpConnections: 0),
                new ActivitySample(CpuTicks: 10, TcpConnections: 0),
                new ActivitySample(CpuTicks: 10, TcpConnections: 0),
            ]);
        var context = new StuckContext { Phase = "work", AgentKind = AgentKind.Claude };
        var probe = new StuckProbe(
            source,
            thresholdSamples: 2,
            context,
            phaseCts,
            NullLogger<StuckAgentDetectionTests>.Instance,
            pollInterval: TimeSpan.Zero);

        await probe.RunAsync(runCts.Token);

        Assert.True(context.Detected);
        Assert.True(phaseCts.IsCancellationRequested);
    }

    [Fact]
    public async Task TcpActivityPreventsStuckClassification()
    {
        using var phaseCts = new CancellationTokenSource();
        using var runCts = new CancellationTokenSource();
        var source = new SequencedActivitySource(
            runCts,
            [
                new ActivitySample(CpuTicks: 10, TcpConnections: 0),
                new ActivitySample(CpuTicks: 10, TcpConnections: 1),
                new ActivitySample(CpuTicks: 10, TcpConnections: 1),
                new ActivitySample(CpuTicks: 10, TcpConnections: 1),
            ]);
        var context = new StuckContext { Phase = "audit", AgentKind = AgentKind.Codex };
        var probe = new StuckProbe(
            source,
            thresholdSamples: 2,
            context,
            phaseCts,
            NullLogger<StuckAgentDetectionTests>.Instance,
            pollInterval: TimeSpan.Zero);

        await probe.RunAsync(runCts.Token);

        Assert.False(context.Detected);
        Assert.False(phaseCts.IsCancellationRequested);
    }

    [Fact]
    public async Task UnavailableActivitySourceReturnsUnknownWithoutCancellingPhase()
    {
        using var phaseCts = new CancellationTokenSource();
        using var runCts = new CancellationTokenSource();
        var source = new SequencedActivitySource(runCts, [null, null, null]);
        var context = new StuckContext { Phase = "merge", AgentKind = AgentKind.Gemini };
        var probe = new StuckProbe(
            source,
            thresholdSamples: 1,
            context,
            phaseCts,
            NullLogger<StuckAgentDetectionTests>.Instance,
            pollInterval: TimeSpan.Zero);

        await probe.RunAsync(runCts.Token);

        Assert.False(context.Detected);
        Assert.False(phaseCts.IsCancellationRequested);
    }

    private sealed class SequencedActivitySource : IAgentActivitySource
    {
        private readonly CancellationTokenSource _runCts;
        private readonly Queue<ActivitySample?> _samples;

        public SequencedActivitySource(CancellationTokenSource runCts, IEnumerable<ActivitySample?> samples)
        {
            _runCts = runCts;
            _samples = new Queue<ActivitySample?>(samples);
        }

        public ActivitySample? TryRead()
        {
            if (_samples.Count == 0)
            {
                _runCts.Cancel();
                return null;
            }

            var sample = _samples.Dequeue();
            if (_samples.Count == 0)
                _runCts.Cancel();
            return sample;
        }
    }
}
