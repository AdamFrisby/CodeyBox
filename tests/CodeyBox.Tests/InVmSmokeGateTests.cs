using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage that in-VM smoke results actually steer
/// <see cref="AgentClassRouter"/> (AC#4) and that the
/// <see cref="IInVmSmokeGate"/> hook gates the FIRST dispatch — so a broken CLI
/// is caught at routing time, not on the first work item, even before any
/// background sweep has run.
/// </summary>
public sealed class InVmSmokeGateTests
{
    private static readonly AgentKind Cursor = AgentKind.Cursor;
    private static readonly AgentKind Claude = AgentKind.Claude;

    private static readonly AgentCredential CursorCred = new(
        Cursor,
        new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    private static InVmSmokeProber BuildProber(ScriptedSandboxProvider provider, AgentAvailabilityRegistry registry)
    {
        var resolver = new StubBaselineResolver("base-A");
        return new(
            provider,
            resolver,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            new InVmSmokeCache(TimeSpan.FromMinutes(60)),
            new NullWebhookDispatcher(),
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);
    }

    private static AgentClassRouter BuildRouter(
        AgentAvailabilityRegistry registry, IInVmSmokeGate? gate)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new() { Agent = Cursor, Billing = AgentBilling.Subscription, QualityScore = 150 },
                new() { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        return new AgentClassRouter(
            [cls],
            [new FakeProbe(Cursor, 90.0), new FakeProbe(Claude, 90.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            availability: registry,
            inVmSmokeGate: gate);
    }

    private static WorkItem MakeItem(string? baselineImageRef = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "frontier",
        BaselineImageRef = baselineImageRef,
    };

    private static Project MakeProject() => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Project",
        RepositoryUrl = "https://example.invalid/repo.git",
        NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
    };

    [Fact]
    public async Task ResolveAsync_ForwardsWorkItemBaselineRef_ToGate()
    {
        // B1 pinning: a work item pinned to a specific baseline must probe THAT
        // image (the one dispatch will clone), not the active baseline. Guards the
        // router→gate wiring: a regression passing null from ResolveAsync would
        // probe the wrong image yet still route, so assert the gate saw the ref.
        var gate = new RecordingGate();
        var router = BuildRouter(NewRegistry(), gate);

        await router.ResolveAsync(MakeItem(baselineImageRef: "base-PINNED"), MakeProject(), CancellationToken.None);

        Assert.Contains("base-PINNED", gate.SeenBaselineRefs);
        var target = Assert.Single(gate.SeenTargets);
        Assert.Equal("work-profile", target.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Headless, target.Flavor);
        Assert.Equal("base-PINNED", target.BaselineRef);
    }

    [Fact]
    public async Task ResolveAsync_GraphicalProject_DoesNotForwardHeadlessWorkPin()
    {
        var gate = new RecordingGate();
        var router = BuildRouter(NewRegistry(), gate);
        var project = MakeProject() with { GraphicalSandbox = true };

        await router.ResolveAsync(MakeItem(baselineImageRef: "base-HEADLESS-WORK"), project, CancellationToken.None);

        var target = Assert.Single(gate.SeenTargets);
        Assert.Equal(SandboxConventions.GraphicalNetworkProfile, target.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Graphical, target.Flavor);
        Assert.Null(target.BaselineRef);
    }

    /// <summary>
    /// Records the baselineRef forwarded by the router and reports every agent as
    /// available so routing proceeds normally.
    /// </summary>
    private sealed class RecordingGate : IInVmSmokeGate
    {
        public List<string?> SeenBaselineRefs { get; } = [];
        public List<InVmSmokeSandboxTarget> SeenTargets { get; } = [];
        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            SeenBaselineRefs.Add(target.BaselineRef);
            SeenTargets.Add(target);
            return Task.FromResult(new AgentAvailability(true, null, null));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
    }

    [Fact]
    public async Task InVmSmokeFailure_CausesRouterToSkipCursor()
    {
        // AC#4: exit 127 on `agent --version` benches cursor; the router routes
        // the work item past it to the working alternative (claude).
        var provider = new ScriptedSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[1] == "--version"
                ? new SandboxExecResult(127, "", "command not found")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry);
        await prober.ProbeAllAsync(CancellationToken.None);

        var router = BuildRouter(registry, gate: null);
        var decision = await router.ResolveAsync(MakeItem(), MakeProject(), CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Gate_GatesFirstDispatch_BeforeAnySweep()
    {
        // No background sweep has run — cursor is Available by default. The
        // router's IInVmSmokeGate hook must probe it before trusting it, catch
        // the exit 127, and route to claude on this very first dispatch.
        var provider = new ScriptedSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[1] == "--version"
                ? new SandboxExecResult(127, "", "command not found")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry);

        var router = BuildRouter(registry, gate: prober);
        var decision = await router.ResolveAsync(MakeItem(), MakeProject(), CancellationToken.None);

        Assert.Equal(1, provider.CreateCount); // the gate provisioned exactly once
        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Gate_CacheHit_IsFreeOnSubsequentDispatch()
    {
        // A healthy agent is probed once; subsequent dispatches hit the cache
        // and provision nothing (AC#2 steady-state is free).
        var provider = new ScriptedSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry);
        var router = BuildRouter(registry, gate: prober);

        var first = await router.ResolveAsync(MakeItem(), MakeProject(), CancellationToken.None);
        var second = await router.ResolveAsync(MakeItem(), MakeProject(), CancellationToken.None);

        Assert.Equal(Cursor, first.Chosen!.Agent);
        Assert.Equal(Cursor, second.Chosen!.Agent);
        Assert.Equal(1, provider.CreateCount); // second dispatch was a cache hit
    }
}
