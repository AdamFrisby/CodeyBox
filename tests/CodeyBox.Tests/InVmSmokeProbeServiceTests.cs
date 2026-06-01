using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="InVmSmokeProbeService"/> — the background driver of the
/// in-VM smoke sweeps. Covers the Enabled gate, the single-sweep
/// (<c>SweepIntervalSeconds &lt;= 0</c>) path, the repeating interval loop
/// (<c>SweepIntervalSeconds &gt; 0</c>), and that a sweep fault is swallowed
/// instead of escaping <c>ExecuteAsync</c>.
/// </summary>
public sealed class InVmSmokeProbeServiceTests
{
    private static readonly AgentCredential CursorCred = new(
        AgentKind.Cursor,
        new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    private static InVmSmokeProber BuildProber(
        ScriptedSandboxProvider provider,
        AgentAvailabilityRegistry registry,
        StubBaselineResolver resolver,
        bool enabled)
    {
        return new InVmSmokeProber(
            provider,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            new InVmSmokeCache(TimeSpan.FromMinutes(60)),
            new NullWebhookDispatcher(),
            new InVmSmokeOptions { Enabled = enabled, ImageReference = "img", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);
    }

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    private static InVmSmokeProbeService BuildService(InVmSmokeProber prober, int sweepIntervalSeconds = 0) =>
        new(prober,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = sweepIntervalSeconds },
            NullLogger<InVmSmokeProbeService>.Instance);

    private static async Task AwaitExecute(InVmSmokeProbeService service)
    {
        var done = service.ExecuteTask ?? Task.CompletedTask;
        var winner = await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(done, winner); // single-sweep ExecuteAsync must complete promptly
        await done; // surface any exception that escaped (there must be none)
    }

    [Fact]
    public async Task StartupSweep_Runs_AndExcludesBrokenAgent()
    {
        var provider = new ScriptedSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[1] == "--version"
                ? new SandboxExecResult(127, "", "command not found")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry, new StubBaselineResolver("base-A"), enabled: true);
        var service = BuildService(prober);

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task Disabled_RunsNoSweep()
    {
        var provider = new ScriptedSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry, new StubBaselineResolver("base-A"), enabled: false);
        var service = BuildService(prober);

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task SweepException_IsSwallowed_ExecuteAsyncCompletes()
    {
        // ProbeAllAsync resolves the baseline ref before its per-probe try/catch,
        // so a resolver fault escapes it; SafeSweepAsync must swallow it.
        var provider = new ScriptedSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var resolver = new StubBaselineResolver("base-A") { ThrowOnResolve = true };
        var prober = BuildProber(provider, NewRegistry(), resolver, enabled: true);
        var service = BuildService(prober);

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service); // would throw here if SafeSweepAsync let it escape
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task PeriodicSweep_RepeatsOnInterval_UntilStopped()
    {
        // SweepIntervalSeconds > 0 takes the while-loop path: a startup sweep
        // plus at least one interval-driven re-invocation of SafeSweepAsync. The
        // one-shot tests above never exercise the Task.Delay loop, so a
        // regression that dropped the re-sweep (or the delay wiring) would slip
        // past them. A counting gate proves ProbeAllAsync fires more than once.
        var gate = new CountingGate();
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 1 },
            NullLogger<InVmSmokeProbeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var reachedTwo = await gate.WaitForAtLeastAsync(2, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.True(reachedTwo, $"expected the interval loop to sweep >=2 times, saw {gate.SweepCount}");
    }

    /// <summary>
    /// In-VM smoke gate stub that counts <see cref="ProbeAllAsync"/> invocations
    /// and lets a test await a target count, so the repeating-interval loop can
    /// be observed without sleeping a fixed duration.
    /// </summary>
    private sealed class CountingGate : IInVmSmokeGate
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        private int _target = int.MaxValue;

        public bool Enabled => true;
        public int SweepCount { get { lock (_sync) return _count; } }

        public Task ProbeAllAsync(CancellationToken ct)
        {
            lock (_sync)
            {
                _count++;
                if (_count >= _target) _reached.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public Task<AgentAvailability> EnsureAvailableAsync(AgentKind kind, string? baselineRef, CancellationToken ct)
            => Task.FromResult(new AgentAvailability(true, null, null));

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
            => Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));

        public async Task<bool> WaitForAtLeastAsync(int target, TimeSpan timeout)
        {
            lock (_sync)
            {
                _target = target;
                if (_count >= target) return true;
            }
            var winner = await Task.WhenAny(_reached.Task, Task.Delay(timeout));
            return winner == _reached.Task;
        }
    }
}
