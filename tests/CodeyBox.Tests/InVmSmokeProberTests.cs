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
    public async Task AllStepsPass_ExecutesVersionMaterialiseStatusTrustInOrder()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var prober = Build(provider, NewRegistry(), NewCache(), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        // Pins the smoke contract: same binary + auth script + trust prefix the runner uses.
        Assert.Equal(4, provider.ExecutedArgv.Count);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "--version"], provider.ExecutedArgv[0]);
        Assert.Equal(["bash", "-c", CursorAgentRunner.AuthMaterialiseScript], provider.ExecutedArgv[1]);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "status"], provider.ExecutedArgv[2]);
        Assert.Equal(
            CursorAgentRunner.WorkspaceTrustInvocationPrefix(CursorAgentRunner.DefaultBinary),
            provider.ExecutedArgv[3]);
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

        // Stage 3 — "Workspace Trust Required": the trust-bearing turn
        // (`agent --print --trust --force`) exits non-zero, so cursor is benched
        // at SMOKE TIME (AC#5), not on first dispatch. The trust step is the
        // only one whose argv[1] is "--print".
        var s3 = new FakeSandboxProvider(exec =>
            IsAgent(exec, "--print") ? new SandboxExecResult(1, "", "Workspace Trust Required")
                                     : new SandboxExecResult(0, "", ""));
        var r3 = NewRegistry();
        await Build(s3, r3, NewCache(), new FakeBaselineResolver("base-A")).ProbeAllAsync(CancellationToken.None);
        Assert.False(r3.GetAvailability(AgentKind.Cursor).Available);
        Assert.Contains("workspace turn failed", r3.GetAvailability(AgentKind.Cursor).Reason);

        // The trust step couples to the SAME prefix builder real dispatch uses,
        // so dropping --trust regresses both together. Keep the fast argv-level
        // runner pin too (CursorAgentRunnerTrustRegressionTests is exhaustive).
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
    public async Task StepTimeout_OnDispatchGate_DefaultFailClosed_Benches()
    {
        // Companion to StepTimeout_IsTransient_* (which exercises the sweep /
        // fail-open path): on the DISPATCH gate under the default fail-closed
        // policy a step timeout must bench the agent rather than leave it
        // routable, so the router never hands work to a CLI whose probe could not
        // reach a verdict. Without this, a regression that only applied
        // fail-closed to the credential / provisioning catches (not the timeout
        // catch at InVmSmokeProber.cs:249) would go uncaught.
        var provider = new HangingSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        // Build() uses the default InVmSmokeOptions → FailClosedOnProbeFault = true;
        // StepTimeoutSeconds=0 trips the timeout path immediately.
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

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        Assert.Contains("probe step timed out", av.Reason);
        // The fault is not cached, so a later (recovered) probe self-heals it.
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
    public async Task CredentialResolutionFailure_OnDispatchGate_DefaultFailClosed_Benches()
    {
        // Companion to CredentialResolutionFailure_IsTransient_* (which exercises
        // the sweep / fail-open path): on the DISPATCH gate under the default
        // fail-closed policy a credential-store fault must bench the agent rather
        // than leave it routable, so the router never hands work to a CLI whose
        // auth could not even be resolved. Without this, a regression that only
        // applied fail-closed to provisioning/exec faults (not the credential
        // catch) would go uncaught.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        // Build() uses the default InVmSmokeOptions → FailClosedOnProbeFault = true.
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            credentials: new ThrowingCredentialProvider());

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        Assert.Contains("credential resolution failed", av.Reason);
        // Never provisioned (the fault is before sandbox create) and never cached,
        // so a later recovered probe self-heals it.
        Assert.Equal(0, provider.CreateCount);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureAvailableAsync_AlreadyExcluded_SkipsProbe_NoProvision()
    {
        // The gate short-circuits when the registry already marks the agent
        // unavailable: there is no point provisioning a probe VM for an agent the
        // router will skip regardless, and a redundant probe could reconcile away
        // or overwrite an exclusion earned from another source. Pre-bench cursor
        // (via the host-smoke source, distinct from in-VM) and assert the gate
        // returns the excluded verdict without ever creating a sandbox.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(false, "host smoke benched it", TimeSpan.Zero), SmokeExclusionSource.HostSmoke);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);

        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"));

        var av = await prober.EnsureAvailableAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        Assert.False(av.Available);
        Assert.Equal(0, provider.CreateCount); // no redundant provision for an already-skipped agent
    }

    [Fact]
    public async Task EnsureAvailableAsync_GloballyBenched_PinnedRefHasCachedPass_ReProbesAndRecovers()
    {
        // B1 pinning: in-VM verdicts are cached per (agent, baselineRef) but the
        // registry exclusion is global. A failure probed against the active
        // baseline benches cursor everywhere — but a work item pinned to a
        // different, known-good baseline (cached pass) must still route there.
        // The gate must NOT short-circuit on the global bench when the pinned ref
        // has its own cached pass; the cache hit reconciles the pass back onto
        // the registry without provisioning a VM.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        // Active-baseline probe failed → cursor globally benched under InVmSmoke.
        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(false, "exit 127 on base-ACTIVE", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);
        // But the pinned baseline was previously probed clean (cached pass).
        cache.Set(AgentKind.Cursor, "base-PINNED", new AgentSmokeResult(true, null, TimeSpan.Zero));
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-ACTIVE"));

        var av = await prober.EnsureAvailableAsync(AgentKind.Cursor, baselineRef: "base-PINNED", CancellationToken.None);

        Assert.True(av.Available); // pinned-image verdict, not the active-image bench
        Assert.Equal(0, provider.CreateCount); // cache hit → no VM provisioned
    }

    [Fact]
    public async Task EnsureAvailableAsync_GloballyBenched_PinnedRefNeverProbed_HonoursBench_NoProvision()
    {
        // Companion to the cached-pass case: with no positive per-ref evidence
        // the gate honours the global bench rather than provisioning a fresh VM
        // for an agent the router skips everywhere else (hot-path / sandbox-slot
        // protection). A never-probed pinned image is no proof the CLI works there.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        registry.MarkSmokeResult(AgentKind.Cursor,
            new AgentSmokeResult(false, "exit 127 on base-ACTIVE", TimeSpan.Zero), SmokeExclusionSource.InVmSmoke);
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-ACTIVE"));

        var av = await prober.EnsureAvailableAsync(AgentKind.Cursor, baselineRef: "base-OTHER", CancellationToken.None);

        Assert.False(av.Available);
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task EnsureProbedAsync_PinnedBaselineRef_ProbesAndCachesAgainstPinnedImage()
    {
        // B1 pinning: the dispatch gate must probe the work item's pinned baseline
        // (the image the dispatch will clone), not the active baseline the
        // resolver returns. The probe VM's spec and the cache key must both use
        // the pinned ref so a pass proves the CLI on THAT image.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        // Resolver's active baseline is "base-ACTIVE"; the work item is pinned to
        // "base-PINNED". The gate must use the pinned one.
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-ACTIVE"));

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: "base-PINNED", CancellationToken.None);

        Assert.Equal("base-PINNED", provider.LastBaselineRef);
        Assert.NotNull(cache.TryGet(AgentKind.Cursor, "base-PINNED"));
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-ACTIVE"));
    }

    [Fact]
    public async Task EnsureProbedAsync_FailOpenOptOut_NeverThrows_AndDoesNotBench_OnProbeFault()
    {
        // With the opt-out fail-open policy (FailClosedOnProbeFault:false), a
        // provisioning/exec fault on the dispatch gate must be swallowed (not
        // thrown) and must NOT bench a possibly-working agent.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""))
        {
            ThrowOnCreate = new InvalidOperationException("provider blew up"),
        };
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                SweepIntervalSeconds = 0,
                FailClosedOnProbeFault = false,
            });

        // Must complete without throwing.
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task EnsureProbedAsync_DefaultFailClosed_NeverThrows_AndBenches_OnProbeFault()
    {
        // The default dispatch-gate policy is fail-closed: a provisioning/exec
        // fault must be swallowed (not thrown) but bench the agent so the router
        // never dispatches to a CLI that was never verified in-sandbox.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""))
        {
            ThrowOnCreate = new InvalidOperationException("provider blew up"),
        };
        var registry = NewRegistry();
        var cache = NewCache();
        // Build() uses the default InVmSmokeOptions, where FailClosedOnProbeFault
        // now defaults to true.
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        // The fault is not cached, so a later (recovered) probe self-heals it.
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task FastFailBench_CacheHitDoesNotClear_FreshPassClears()
    {
        // End-to-end pin for the clearsFastFail wiring at the prober's two call
        // sites (cache hit → false, fresh pass → true). A fast-fail bench earned
        // from real dispatch failures must survive a cached in-VM reconciliation
        // (no CLI was re-executed) and only be lifted by a freshly executed pass.
        // Swapping the two boolean arguments would make this test fail.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        // 1) Fresh pass populates the cache (clearsFastFail:true, nothing to clear yet).
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);

        // 2) Real dispatch fast-fails three times → fast-fail circuit breaker benches it.
        for (var i = 0; i < 3; i++)
            registry.RecordRunOutcome(AgentKind.Cursor, success: false, duration: TimeSpan.FromMilliseconds(500));
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);

        // 3) A CACHE HIT re-asserts the in-VM pass but re-executed no CLI, so it
        //    must NOT lift the fast-fail bench (clearsFastFail:false).
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount); // cache hit: no new VM
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);

        // 4) A FRESH pass (cache invalidated → CLI actually re-executed) IS valid
        //    evidence the binary launches, so it clears the fast-fail bench.
        cache.Invalidate(AgentKind.Cursor);
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(2, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task FailClosed_DispatchGate_BenchesOnProbeFault_SweepStillFailsOpen()
    {
        // Opt-in fail-closed dispatch policy: an inconclusive dispatch-gate probe
        // (provisioning fault) must temporarily bench the agent so routing avoids
        // an unverified CLI — but the background sweep must still fail open.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""))
        {
            ThrowOnCreate = new InvalidOperationException("provider blew up"),
        };
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                SweepIntervalSeconds = 0,
                FailClosedOnProbeFault = true,
            });

        // The sweep performs no dispatch, so a transient fault there never benches.
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A")); // fault never cached

        // The dispatch gate, under fail-closed, benches the agent so the router
        // routes past it instead of dispatching to a CLI it could not verify.
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        // The fault is not cached, so a later (recovered) probe self-heals it.
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureProbedAsync_UnexpectedException_DefaultFailClosed_NeverThrows_AndBenches()
    {
        // A bug that escapes ProbeAgentAsync's inner transient-fault handling
        // (e.g. a throwing IInVmSmokeProbe.BuildSteps, or a fault after a partial
        // verdict) must be caught by EnsureProbedAsync's OUTER catch
        // (InVmSmokeProber.cs:176): swallowed (the dispatch hot path must never
        // throw) but, under the default fail-closed policy, benched so an
        // unverified CLI is not left routable on the first dispatch. The inner
        // catches cover credential / provisioning / exec / timeout faults; this
        // pins the outer net none of those tests force.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            probes: [new ThrowingProbe()]);

        // Must complete without throwing despite the probe blowing up.
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        Assert.Contains("probe threw unexpectedly", av.Reason);
        // The fault throws before any sandbox is created, and the bench is never
        // cached, so a later (recovered) probe self-heals it.
        Assert.Equal(0, provider.CreateCount);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureProbedAsync_UnexpectedException_FailOpenOptOut_NeverThrows_AndDoesNotBench()
    {
        // Companion to the fail-closed case: with FailClosedOnProbeFault:false an
        // unexpected exception in the outer catch must still be swallowed but must
        // NOT bench a possibly-working agent.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                SweepIntervalSeconds = 0,
                FailClosedOnProbeFault = false,
            },
            probes: [new ThrowingProbe()]);

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

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

    /// <summary>
    /// In-VM probe whose <see cref="IInVmSmokeProbe.BuildSteps"/> throws an
    /// unexpected exception — a stand-in for an implementation bug that escapes
    /// ProbeAgentAsync's inner transient-fault handling and must be caught by
    /// EnsureProbedAsync's outer net. Kind is cursor so EnsureProbedAsync resolves it.
    /// </summary>
    private sealed class ThrowingProbe : IInVmSmokeProbe
    {
        public AgentKind Kind => AgentKind.Cursor;
        public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
            throw new InvalidOperationException("probe construction bug");
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
