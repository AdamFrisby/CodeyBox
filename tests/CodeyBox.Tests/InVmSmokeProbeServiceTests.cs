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
/// (<c>SweepIntervalSeconds &lt;= 0</c>) path, and that a sweep fault is
/// swallowed instead of escaping <c>ExecuteAsync</c>.
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
}
