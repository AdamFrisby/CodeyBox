using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Opencode;
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

    private static readonly AgentCredential OpencodeCred = new(
        AgentKind.Opencode,
        new Dictionary<string, string> { ["OPENCODE_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    private static InVmSmokeProber Build(
        FakeSandboxProvider provider,
        AgentAvailabilityRegistry registry,
        InVmSmokeCache cache,
        FakeBaselineResolver resolver,
        InVmSmokeOptions? opts = null,
        ICredentialProvider? credentials = null,
        IEnumerable<IInVmSmokeProbe>? probes = null)
    {
        return new InVmSmokeProber(
            provider,
            resolver,
            credentials ?? new ConstantCredentialProvider(CursorCred),
            probes ?? [new CursorInVmSmokeProbe()],
            registry,
            cache,
            new NullWebhookDispatcher(),
            opts ?? new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);
    }

    private static InVmSmokeCache NewCache() => new(TimeSpan.FromMinutes(60));

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

    [Fact]
    public async Task Disabled_DoesNotProvisionOrMutateRegistry()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "not found"));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions { Enabled = false, ImageReference = "img", SweepIntervalSeconds = 0 });

        Assert.False(prober.Enabled);
        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task NullCredential_StillRunsVersionStep_AndExcludesOnExit127()
    {
        // No credential bundle at all: the prober must still exec the
        // credential-independent --version step (BuildSteps(null) contract), so
        // a binary missing from PATH is caught rather than silently skipped.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "command not found"));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            credentials: new NullCredentialProvider());

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.Single(provider.ExecutedArgv); // only --version; no auth steps
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Contains("agent binary not runnable", registry.GetAvailability(AgentKind.Cursor).Reason);
    }

    [Fact]
    public async Task AuthMaterialiseStepFailure_ExcludesWithMaterialiseHint()
    {
        // The PR #138 path-drift stage: version is fine, but the bash auth
        // materialisation step exits non-zero (e.g. unwritable dest).
        var provider = new FakeSandboxProvider(exec =>
            exec.Argv.Count > 0 && exec.Argv[0] == "bash"
                ? new SandboxExecResult(1, "", "permission denied")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("materialise cursor auth.json", av.Reason);
    }

    [Fact]
    public async Task FailingProbe_IsNotCached_NextSweepReprobes()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "not found"));
        var cache = NewCache();
        var prober = Build(provider, NewRegistry(), cache, new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A")); // failures are never cached
        await prober.ProbeAllAsync(CancellationToken.None);

        // No cached pass to short-circuit on, so the failing agent is re-probed
        // every sweep — the self-healing path once the CLI is fixed.
        Assert.Equal(2, provider.CreateCount);
    }

    [Fact]
    public async Task CacheHit_ReappliesPassToRegistry_WithoutReprovisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);

        // Simulate the registry diverging from the cache (e.g. an operator reset
        // cleared it, or another signal benched it). A cache hit must reconcile.
        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(false, "drift", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount); // cache hit: no new VM
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available); // reconciled
    }

    [Fact]
    public async Task CacheInvalidate_ForcesReprobe()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var cache = NewCache();
        var prober = Build(provider, NewRegistry(), cache, new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);

        // /admin/agent/{name}/reset invalidates the cache so the next sweep
        // re-execs the CLI instead of replaying the stale pass.
        cache.Invalidate(AgentKind.Cursor);
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(2, provider.CreateCount);
    }

    [Fact]
    public async Task CacheTtlExpiry_Reprobes()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var cache = new InVmSmokeCache(TimeSpan.FromMinutes(60), clock);
        var prober = Build(provider, NewRegistry(), cache, new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);

        clock.Advance(TimeSpan.FromMinutes(61)); // TTL elapsed
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(2, provider.CreateCount);
    }

    [Fact]
    public async Task AllStepsPass_ExecutesVersionMaterialiseStatusInOrder()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var prober = Build(provider, NewRegistry(), NewCache(), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        // Pins the smoke contract: same binary + auth script the runner uses.
        Assert.Equal(3, provider.ExecutedArgv.Count);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "--version"], provider.ExecutedArgv[0]);
        Assert.Equal(["bash", "-c", CursorAgentRunner.AuthMaterialiseScript], provider.ExecutedArgv[1]);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "status"], provider.ExecutedArgv[2]);
    }

    [Fact]
    public async Task ThreeStageCascade_EachStageCaughtAtSmokeTime()
    {
        // AC#5: the 2026-05-28 cursor cascade, stage by stage, all caught here.

        // Stage 1 — binary missing from PATH (exit 127).
        var s1 = new FakeSandboxProvider(exec =>
            IsAgent(exec, "--version") ? new SandboxExecResult(127, "", "command not found")
                                       : new SandboxExecResult(0, "", ""));
        var r1 = NewRegistry();
        await Build(s1, r1, NewCache(), new FakeBaselineResolver("base-A")).ProbeAllAsync(CancellationToken.None);
        Assert.False(r1.GetAvailability(AgentKind.Cursor).Available);
        Assert.Contains("agent binary not runnable", r1.GetAvailability(AgentKind.Cursor).Reason);

        // Stage 2 — auth materialised to the wrong path → `agent status` fails.
        var s2 = new FakeSandboxProvider(exec =>
            IsAgent(exec, "status") ? new SandboxExecResult(1, "", "Authentication required")
                                    : new SandboxExecResult(0, "", ""));
        var r2 = NewRegistry();
        await Build(s2, r2, NewCache(), new FakeBaselineResolver("base-A")).ProbeAllAsync(CancellationToken.None);
        Assert.False(r2.GetAvailability(AgentKind.Cursor).Available);
        Assert.Contains("agent status failed", r2.GetAvailability(AgentKind.Cursor).Reason);

        // Stage 3 (workspace trust) is NOT a smoke-time check — the version/status
        // probe steps do not engage workspace trust — so with both prior stages
        // fixed the agent smokes clean and is routable.
        var s3 = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var r3 = NewRegistry();
        await Build(s3, r3, NewCache(), new FakeBaselineResolver("base-A")).ProbeAllAsync(CancellationToken.None);
        Assert.True(r3.GetAvailability(AgentKind.Cursor).Available);

        // Stage 3 is instead guaranteed at dispatch: the runner must always pass
        // --trust. Assert that here too (and see CursorAgentRunnerTrustRegressionTests
        // for the exhaustive sweep) so this cascade test fails if the --trust pin
        // is removed and the agent would otherwise smoke clean yet fail first run.
        var trustSandbox = new TrustRecordingSandbox();
        await new CursorAgentRunner().RunAsync(trustSandbox, "/work", "p", credential: null);
        var agentExec = Assert.Single(trustSandbox.Execs,
            e => e.Argv.Count > 0 && e.Argv[0] == CursorAgentRunner.DefaultBinary);
        Assert.Contains("--trust", agentExec.Argv);
    }

    private sealed class TrustRecordingSandbox : ISandbox
    {
        public string Id => "trust-recording";
        public List<SandboxExec> Execs { get; } = [];
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Opencode_ProvidersStepFailure_ExcludesAgent()
    {
        var provider = new FakeSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[0] == OpencodeAgentRunner.DefaultBinary && exec.Argv[1] == "providers"
                ? new SandboxExecResult(1, "", "no providers configured")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            credentials: new ConstantCredentialProvider(OpencodeCred),
            probes: [new OpencodeInVmSmokeProbe()]);

        await prober.ProbeAllAsync(CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Opencode);
        Assert.False(av.Available);
        Assert.Contains("opencode providers failed", av.Reason);
    }

    [Fact]
    public async Task HostSmokePass_DoesNotClearInVmExclusion()
    {
        // The core defect: the over-permissive host credential probe (env-var
        // only) must not be able to un-bench an agent that the in-VM probe
        // failed (exit 127 / auth drift it cannot itself observe).
        var registry = NewRegistry();
        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(false, "exit 127", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);

        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(true, null, TimeSpan.Zero), SmokeExclusionSource.HostSmoke);

        // In-VM exclusion still stands — only an in-VM pass clears it.
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);

        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(true, null, TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task StepTimeout_IsTransient_DoesNotExcludeOrCache()
    {
        // A step that exceeds StepTimeoutSeconds is infra flakiness, not an agent
        // fault: the agent must stay Available and the (non-)result must not cache,
        // so the next sweep re-probes. StepTimeoutSeconds=0 cancels the step token
        // immediately while the fake exec only completes if the token fires.
        var provider = new HangingSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = new InVmSmokeProber(
            provider,
            new FakeBaselineResolver("base-A"),
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            cache,
            new NullWebhookDispatcher(),
            new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                SweepIntervalSeconds = 0,
                StepTimeoutSeconds = 0,
            },
            NullLogger<InVmSmokeProber>.Instance);

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task CredentialResolutionFailure_IsTransient_DoesNotExcludeOrProvision()
    {
        // ICredentialProvider.GetAsync throwing is a credential-store fault, not an
        // agent fault: leave availability unchanged and never even provision a VM.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            credentials: new ThrowingCredentialProvider());

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task EnsureProbedAsync_NeverThrows_AndDoesNotBench_OnProbeFault()
    {
        // The dispatch gate runs on the router hot path: a provisioning/exec fault
        // must be swallowed (not thrown) and must not bench a possibly-working agent.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""))
        {
            ThrowOnCreate = new InvalidOperationException("provider blew up"),
        };
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"));

        // Must complete without throwing.
        await prober.EnsureProbedAsync(AgentKind.Cursor, CancellationToken.None);

        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    private static bool IsAgent(SandboxExec exec, string sub) =>
        exec.Argv.Count >= 2 && exec.Argv[0] == CursorAgentRunner.DefaultBinary && exec.Argv[1] == sub;

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public int CreateCount { get; private set; }
        public string? LastBaselineRef { get; private set; }
        public Exception? ThrowOnCreate { get; set; }
        // Every argv exec'd across all sandboxes this provider created, in order.
        public List<IReadOnlyList<string>> ExecutedArgv { get; } = new();

        public FakeSandboxProvider(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;

        public string Name => "fake";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCount++;
            LastBaselineRef = spec.BaselineImageRef;
            if (ThrowOnCreate is not null) throw ThrowOnCreate;
            return Task.FromResult<ISandbox>(new FakeSandbox(exec =>
            {
                ExecutedArgv.Add(exec.Argv);
                return _onExec(exec);
            }));
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

    /// <summary>Credential provider that resolves null for every agent.</summary>
    private sealed class NullCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default) =>
            Task.FromResult<AgentCredential?>(null);
    }

    /// <summary>Credential provider that simulates a credential-store fault.</summary>
    private sealed class ThrowingCredentialProvider : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default) =>
            throw new InvalidOperationException("credential store unavailable");
    }

    /// <summary>
    /// Provider whose sandbox exec only completes when its cancellation token
    /// fires — so a zero step-timeout reliably trips the prober's timeout path
    /// without real waiting. CreateAsync itself succeeds (provisioning is fine;
    /// the step is what hangs).
    /// </summary>
    private sealed class HangingSandboxProvider : ISandboxProvider
    {
        public int CreateCount { get; private set; }
        public string Name => "hanging";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCount++;
            return Task.FromResult<ISandbox>(new HangingSandbox());
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        private sealed class HangingSandbox : ISandbox
        {
            public string Id => "hanging-sandbox";

            public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            {
                await Task.Delay(Timeout.Infinite, ct); // completes only on cancellation
                return new SandboxExecResult(0, "", "");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
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
