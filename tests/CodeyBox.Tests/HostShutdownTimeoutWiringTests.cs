using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the composition-root wiring in <see cref="Program.ComputeHostShutdownTimeout"/>:
/// the callback that sets <c>HostOptions.ShutdownTimeout</c> must read the same
/// inputs from <see cref="CodeyBoxOptions"/> that the orchestrator pool
/// uses (via <see cref="OrchestratorOptionsFactory"/>) and feed them, together
/// with the resolved provider's suspend capability, into
/// <see cref="SuspendTimeoutPolicy.ResolveHostShutdownTimeout"/>. A drift here
/// (wrong property, inverted precedence, or a literal that bypasses the factory)
/// would leave the host SIGKILL budget too small and reproduce the acceptance
/// -criterion #1 failure the wave-scaling change targets.
/// </summary>
public sealed class HostShutdownTimeoutWiringTests
{
    private static CodeyBoxOptions Opts(
        int? concurrency = null, int? maxWorkers = null, int graceSeconds = 60)
    {
        var o = new CodeyBoxOptions
        {
            Concurrency = concurrency,
            WorkerPool = new WorkerPoolOptions { MaxConcurrentWorkers = maxWorkers },
        };
        o.Shutdown.GraceSeconds = graceSeconds;
        return o;
    }

    [Fact]
    public void NonSuspendingProvider_KeepsTheGraceWindow()
    {
        // A provider that does not implement ISuspendingSandboxProvider never
        // suspends on shutdown, so the ceiling stays at the configured grace
        // regardless of worker count.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 32, graceSeconds: 45),
            providerSuspendsOnShutdown: false,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromSeconds(45), timeout);
    }

    [Fact]
    public void SuspendingProvider_SingleWorker_RaisesToOneWaveOfTheDefaultProfile()
    {
        // One in-flight VM → one suspend wave of the default 12 GiB profile
        // budget (30 min), which dwarfs the 60s grace.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 1),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromMinutes(30), timeout);
    }

    [Fact]
    public void SuspendingProvider_ScalesByWaveCount_FromWorkerPool()
    {
        // 16 workers > the parallel-suspend cap (8) → two sequential waves → 60 min.
        // This is the exact undersizing the wave-scaling fix targets: a single-wave
        // ceiling would SIGKILL the host before wave 2 finished its snapshot.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 16),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromMinutes(60), timeout);
    }

    [Fact]
    public void WorkerCount_FollowsOrchestratorOptionsFactoryPrecedence()
    {
        // The ceiling must size off the SAME worker count the orchestrator pool
        // runs at. Legacy CodeyBox:Concurrency is the fallback when WorkerPool is
        // unset (16 → 2 waves → 60 min)...
        var legacyOnly = Program.ComputeHostShutdownTimeout(
            Opts(concurrency: 16, maxWorkers: null),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);
        Assert.Equal(TimeSpan.FromMinutes(60), legacyOnly);

        // ...and WorkerPool:MaxConcurrentWorkers wins when both are set, so a stale
        // legacy value cannot inflate (or here, would not shrink) the ceiling. 1
        // worker → a single 30-min wave even though Concurrency says 16.
        var workerPoolWins = Program.ComputeHostShutdownTimeout(
            Opts(concurrency: 16, maxWorkers: 1),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);
        Assert.Equal(TimeSpan.FromMinutes(30), workerPoolWins);
    }

    [Fact]
    public void GraceWins_WhenItExceedsTheSuspendReserve()
    {
        // ShutdownTimeout is max(grace, suspendReserve): an operator who sets a
        // very long grace is honoured rather than clamped down to the reserve.
        var timeout = Program.ComputeHostShutdownTimeout(
            Opts(maxWorkers: 1, graceSeconds: 4 * 60 * 60),
            providerSuspendsOnShutdown: true,
            NullLogger.Instance);

        Assert.Equal(TimeSpan.FromHours(4), timeout);
    }
}
