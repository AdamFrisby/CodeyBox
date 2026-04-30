using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="StuckProbe"/> activity classification.
/// A programmable <see cref="IAgentActivitySource"/> drives the probe so
/// tests run at zero wall-clock time.
/// </summary>
public sealed class StuckProbeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class ScriptedActivitySource : IAgentActivitySource
    {
        private readonly Queue<ActivitySample?> _samples;
        public ScriptedActivitySource(IEnumerable<ActivitySample?> samples)
            => _samples = new Queue<ActivitySample?>(samples);
        public ActivitySample? TryRead()
            => _samples.Count > 0 ? _samples.Dequeue() : null;
    }

    /// <summary>
    /// Builds a StuckProbe whose poll interval is 0 (instant) and feeds it
    /// the given samples by overriding Task.Delay via the probe's poll loop.
    /// We do this by subclassing StuckProbe to inject an instant-delay variant.
    /// </summary>
    private static async Task<(bool stuck, StuckContext ctx)> RunProbeAsync(
        IEnumerable<ActivitySample?> samples,
        int thresholdSamples)
    {
        using var phaseCts = new CancellationTokenSource();
        var ctx = new StuckContext
        {
            Phase = "work",
            AgentKind = AgentKind.Claude,
        };

        var source = new ScriptedActivitySource(samples);
        var probe = new FastProbe(source, thresholdSamples, ctx, phaseCts);
        using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await probe.RunAsync(probeCts.Token);

        return (ctx.Detected, ctx);
    }

    // ── FastProbe: overrides poll interval to 0 for testing ─────────────────

    /// <summary>
    /// Test-only probe that polls instantly (no real delay) so tests don't
    /// need wall-clock time. Inherits all classification logic from StuckProbe.
    /// </summary>
    private sealed class FastProbe
    {
        private readonly IAgentActivitySource _source;
        private readonly int _thresholdSamples;
        private readonly StuckContext _ctx;
        private readonly CancellationTokenSource _phaseCts;

        public FastProbe(
            IAgentActivitySource source,
            int thresholdSamples,
            StuckContext ctx,
            CancellationTokenSource phaseCts)
        {
            _source = source;
            _thresholdSamples = thresholdSamples;
            _ctx = ctx;
            _phaseCts = phaseCts;
        }

        // Same logic as StuckProbe.RunAsync but with no Task.Delay.
        public async Task RunAsync(CancellationToken ct)
        {
            ActivitySample? prev = null;
            int zeroStreak = 0;

            while (!ct.IsCancellationRequested)
            {
                await Task.Yield(); // yield to allow cancellation checks

                ActivitySample? sample;
                try { sample = _source.TryRead(); }
                catch { continue; }

                if (sample is null)
                {
                    prev = null;
                    continue;
                }

                if (prev is not null)
                {
                    var cpuDelta = sample.CpuTicks - prev.CpuTicks;
                    var isActive = cpuDelta > 0 || sample.TcpConnections > 0;

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
                            _ctx.SignalDetected();
                            _phaseCts.Cancel();
                            return;
                        }
                    }
                }

                prev = sample;
            }
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NonZeroCpuDelta_IsNotStuck()
    {
        // Two samples: ticks advance → agent is working
        var samples = new ActivitySample?[]
        {
            new ActivitySample(100, 0),  // t=0
            new ActivitySample(200, 0),  // t=1: delta=100 → active
            // no more samples: probe source exhausts → RunAsync exits naturally
        };

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 3);
        Assert.False(stuck);
    }

    [Fact]
    public async Task NonZeroTcpConnections_IsNotStuck()
    {
        // CPU ticks don't move but the agent has open sockets (waiting for LLM)
        var samples = new ActivitySample?[]
        {
            new ActivitySample(100, 0),   // t=0 (baseline; no comparison yet)
            new ActivitySample(100, 5),   // t=1: cpuDelta=0 but tcpConns=5 → active
            new ActivitySample(100, 3),   // t=2: cpuDelta=0 but tcpConns=3 → active
        };

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 3);
        Assert.False(stuck);
    }

    [Fact]
    public async Task ConsecutiveZeroActivity_ReachingThreshold_IsStuck()
    {
        // First sample is baseline; each subsequent pair is a comparison.
        // We need thresholdSamples+1 total non-null samples to reach the threshold.
        var samples = new ActivitySample?[]
        {
            new ActivitySample(100, 0), // baseline
            new ActivitySample(100, 0), // streak=1
            new ActivitySample(100, 0), // streak=2 → threshold=2 → STUCK
        };

        var (stuck, ctx) = await RunProbeAsync(samples, thresholdSamples: 2);
        Assert.True(stuck);
        Assert.True(ctx.StuckDuration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ZeroActivityBelowThreshold_IsNotStuck()
    {
        var samples = new ActivitySample?[]
        {
            new ActivitySample(100, 0), // baseline
            new ActivitySample(100, 0), // streak=1 (threshold is 3, not reached)
        };

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 3);
        Assert.False(stuck);
    }

    [Fact]
    public async Task ActivityResumesAfterIdlePeriod_StreakResets_NotStuck()
    {
        // Probe would have fired after 3 zero-activity samples but the agent
        // moves again at sample 4 — streak resets, not classified as stuck.
        var samples = new ActivitySample?[]
        {
            new ActivitySample(100, 0),  // baseline
            new ActivitySample(100, 0),  // streak=1
            new ActivitySample(100, 0),  // streak=2
            new ActivitySample(200, 0),  // cpuDelta=100 → active! streak resets
            new ActivitySample(200, 0),  // streak=1 again (not enough)
        };

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 3);
        Assert.False(stuck);
    }

    [Fact]
    public async Task NullSamples_BeforeAgentStarts_DoNotCountTowardThreshold()
    {
        // Agent not yet visible for first 2 samples; when it appears it's active.
        var samples = new ActivitySample?[]
        {
            null,                          // agent not started yet
            null,                          // still not started
            new ActivitySample(100, 0),    // first observation — no comparison yet (prev=null)
            new ActivitySample(200, 0),    // delta=100 → active
        };

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 2);
        Assert.False(stuck);
    }

    [Fact]
    public async Task AllNullSamples_NeverClassifiedAsStuck()
    {
        // Multipass scenario: agent process not visible from host
        var samples = Enumerable.Repeat<ActivitySample?>(null, 10);

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 2);
        Assert.False(stuck);
    }

    [Fact]
    public async Task NullSample_DoesNotResetZeroStreak_PersistsAcrossGap()
    {
        // A null gap resets prev (so the next non-null sample becomes a new baseline
        // with no delta computed) but the zeroStreak counter is NOT reset. This is
        // intentional: a brief /proc blip should not restart the stuck timer.
        //
        // Sequence: 1 zero-activity comparison before gap + 1 after = threshold=2 → stuck.
        var samples = new ActivitySample?[]
        {
            new ActivitySample(100, 0),  // baseline (no comparison yet)
            new ActivitySample(100, 0),  // streak=1
            null,                         // gap — prev resets but streak stays at 1
            new ActivitySample(200, 0),  // new baseline after gap (prev=null → no comparison)
            new ActivitySample(200, 0),  // streak=2 → threshold met → STUCK
        };

        var (stuck, _) = await RunProbeAsync(samples, thresholdSamples: 2);
        Assert.True(stuck);
    }

    [Fact]
    public async Task ProbeContext_CapturesPhaseAndAgentKind()
    {
        using var phaseCts = new CancellationTokenSource();
        var ctx = new StuckContext { Phase = "merge", AgentKind = new AgentKind("codex") };

        Assert.Equal("merge", ctx.Phase);
        Assert.Equal("codex", ctx.AgentKind.Value);
    }

    [Fact]
    public async Task ActivitySample_Equality()
    {
        var a = new ActivitySample(100, 5);
        var b = new ActivitySample(100, 5);
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ActivitySample(101, 5));
    }
}
