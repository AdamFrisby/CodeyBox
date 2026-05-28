using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="InVmSmokeProber"/> — the sandbox-side smoke probe that
/// execs the agent CLI inside a baseline-cloned VM. Covers the failure cascade
/// the host-only credential gate could not catch (exit 127, auth-path drift),
/// per-baseline caching, and the rule that infra failures must not exclude a
/// working agent.
/// </summary>
public sealed class InVmSmokeProberTests
{
    private static readonly AgentCredential CursorCred = new(
        AgentKind.Cursor,
        new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    private static InVmSmokeProber Build(
        FakeSandboxProvider provider,
        AgentAvailabilityRegistry registry,
        InVmSmokeCache cache,
        FakeBaselineResolver resolver,
        InVmSmokeOptions? opts = null)
    {
        return new InVmSmokeProber(
            provider,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            cache,
            new NullWebhookDispatcher(),
            opts ?? new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);
    }

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    [Fact]
    public async Task Exit127OnVersion_ExcludesAgent_WhichIsWhatTheRouterSkipsOn()
    {
        // `agent --version` not found on PATH — the cb-216a2230 cascade stage 1.
        var provider = new FakeSandboxProvider(exec =>
            IsAgent(exec, "--version")
                ? new SandboxExecResult(127, "", "bash: agent: command not found")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, new InVmSmokeCache(TimeSpan.FromMinutes(60)), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        // The router (AgentClassRouter) skips any member whose availability is
        // not Available — so excluding here routes the work item elsewhere.
        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("agent binary not runnable", availability.Reason);
    }

    [Fact]
    public async Task AllStepsPass_AgentStaysAvailable()
    {
        // version, materialize, and `agent status` all exit 0.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, new InVmSmokeCache(TimeSpan.FromMinutes(60)), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task VersionOkButStatusFails_ExcludesAgent()
    {
        // Stage 2 of the cascade: binary runs but auth materialised to the wrong
        // path → `agent status` exits non-zero ("Authentication required").
        var provider = new FakeSandboxProvider(exec =>
            IsAgent(exec, "status")
                ? new SandboxExecResult(1, "", "Authentication required")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, new InVmSmokeCache(TimeSpan.FromMinutes(60)), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("agent status failed", availability.Reason);
    }

    [Fact]
    public async Task CacheHit_DoesNotReprovision()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "Logged in", ""));
        var cache = new InVmSmokeCache(TimeSpan.FromMinutes(60));
        var prober = Build(provider, NewRegistry(), cache, new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);
        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
    }

    [Fact]
    public async Task BaselineRefChange_Reprovisions()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "Logged in", ""));
        var cache = new InVmSmokeCache(TimeSpan.FromMinutes(60));
        var resolver = new FakeBaselineResolver("base-A");
        var prober = Build(provider, NewRegistry(), cache, resolver);

        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);
        Assert.Equal("base-A", provider.LastBaselineRef);

        // A rebake changes the content-hash ref — the cache key changes, so the
        // next sweep re-provisions against the new baseline (AC#3).
        resolver.Ref = "base-B";
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(2, provider.CreateCount);
        Assert.Equal("base-B", provider.LastBaselineRef);
    }

    [Fact]
    public async Task ProvisioningFailure_DoesNotExcludeAndIsNotCached()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""))
        {
            ThrowOnCreate = new InvalidOperationException("multipass socket unavailable"),
        };
        var cache = new InVmSmokeCache(TimeSpan.FromMinutes(60));
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        // Infra failure must not bench a possibly-working agent...
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        // ...and must not be cached, so the next sweep retries.
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task NullBaselineRef_FallsBackToLiveSentinel_AndStillProbes()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "not found"));
        var registry = NewRegistry();
        // Non-baseline provider (process/bubblewrap): ResolveBaselineRef -> null.
        var prober = Build(provider, registry, new InVmSmokeCache(TimeSpan.FromMinutes(60)), new FakeBaselineResolver(null));

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.Null(provider.LastBaselineRef); // spec pins null, not the sentinel string
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    private static bool IsAgent(SandboxExec exec, string sub) =>
        exec.Argv.Count >= 2 && exec.Argv[0] == "agent" && exec.Argv[1] == sub;

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public int CreateCount { get; private set; }
        public string? LastBaselineRef { get; private set; }
        public Exception? ThrowOnCreate { get; set; }

        public FakeSandboxProvider(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;

        public string Name => "fake";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCount++;
            LastBaselineRef = spec.BaselineImageRef;
            if (ThrowOnCreate is not null) throw ThrowOnCreate;
            return Task.FromResult<ISandbox>(new FakeSandbox(_onExec));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public FakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;
        public string Id => "fake-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(_onExec(exec));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBaselineResolver : IBaselineImageResolver
    {
        public string? Ref { get; set; }
        public FakeBaselineResolver(string? r) => Ref = r;

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => Ref;

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
