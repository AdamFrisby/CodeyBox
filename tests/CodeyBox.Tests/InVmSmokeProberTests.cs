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
[Collection("Background service timing")]
public sealed class InVmSmokeProberTests
{
    private static readonly InVmSmokeSandboxTarget WorkTarget =
        new("work-profile", SandboxProfileFlavor.Headless);

    private static readonly AgentCredential CursorCred = new(
        AgentKind.Cursor,
        new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    private static readonly AgentCredential OpencodeCred = new(
        AgentKind.Opencode,
        new Dictionary<string, string> { ["OPENCODE_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    // ISandboxProvider (not FakeSandboxProvider) so timeout tests that swap in a
    // hanging-create / non-cooperative provider reuse the same construction site
    // — keeps future option-list edits to one place rather than 4+ inline
    // InVmSmokeProber constructions.
    private static InVmSmokeProber Build(
        ISandboxProvider provider,
        AgentAvailabilityRegistry registry,
        InVmSmokeCache cache,
        FakeBaselineResolver resolver,
        InVmSmokeOptions? opts = null,
        ICredentialProvider? credentials = null,
        IEnumerable<IInVmSmokeProbe>? probes = null,
        bool fillDefaultNetworkProfile = true,
        SmokeOptionsSnapshot? smokeOptions = null,
        IAgentAuthFailureClassifier? authFailureClassifier = null)
    {
        var effectiveOpts = opts ?? new InVmSmokeOptions
        {
            Enabled = true,
            ImageReference = "img",
            NetworkProfile = WorkTarget.NetworkProfile,
            SweepIntervalSeconds = 0,
        };
        if (fillDefaultNetworkProfile && string.IsNullOrWhiteSpace(effectiveOpts.NetworkProfile))
            effectiveOpts = effectiveOpts with { NetworkProfile = WorkTarget.NetworkProfile };

        return new InVmSmokeProber(
            provider,
            resolver,
            resolver,
            credentials ?? new ConstantCredentialProvider(CursorCred),
            probes ?? [new CursorInVmSmokeProbe()],
            registry,
            cache,
            new NullWebhookDispatcher(),
            effectiveOpts,
            NullLogger<InVmSmokeProber>.Instance,
            smokeOptions,
            authFailureClassifier);
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
    public async Task StatusExitZeroWithAuthPrompt_ExcludesAgent()
    {
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var provider = new FakeSandboxProvider(exec =>
            IsAgent(exec, "status")
                ? new SandboxExecResult(0, transcript, "")
                : new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"));

        await prober.ProbeAllAsync(CancellationToken.None);

        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("auth/login prompt detected", availability.Reason);
    }

    [Fact]
    public async Task StatusExitZeroWithConfiguredAuthPrompt_ExcludesAgent()
    {
        var provider = new FakeSandboxProvider(exec =>
            IsAgent(exec, "status")
                ? new SandboxExecResult(0, "operator-only cursor login prompt", "")
                : new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var classifier = new AgentAuthFailureClassifier(
            new Dictionary<string, IReadOnlyList<AuthFailurePattern>>(StringComparer.OrdinalIgnoreCase)
            {
                ["cursor"] =
                [
                    new AuthFailurePattern(
                        "operator-only cursor login prompt",
                        AuthFailurePatternStream.Stdout),
                ],
            });
        var prober = Build(
            provider,
            registry,
            NewCache(),
            new FakeBaselineResolver("base-A"),
            authFailureClassifier: classifier);

        await prober.ProbeAllAsync(CancellationToken.None);

        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("auth/login prompt detected", availability.Reason);
    }

    [Fact]
    public async Task CacheHit_DoesNotReprovision()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "Logged in", ""));
        var cache = new InVmSmokeCache(TimeSpan.FromMinutes(60));
        var resolver = new FakeBaselineResolver("base-A");
        var prober = Build(provider, NewRegistry(), cache, resolver);

        await prober.ProbeAllAsync(CancellationToken.None);
        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.Single(resolver.EnsureCalls);
    }

    [Fact]
    public async Task CacheHit_WithPinnedBaseline_DoesNotWarmBaselineOrProvision()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "Logged in", ""));
        var cache = NewCache();
        cache.Set(AgentKind.Cursor, "base-A",
            new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
        var resolver = new FakeBaselineResolver("base-A") { CanEnsure = false };
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, resolver);

        await prober.EnsureProbedAsync(
            AgentKind.Cursor,
            WorkTarget.WithBaselineRef("base-A"),
            CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        Assert.Empty(resolver.EnsureCalls);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
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
    public async Task ProbeAllAsync_UsesResolvedProfileAndBaselineCloneTarget()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "Logged in", ""));
        var resolver = new FakeBaselineResolver("base-A");
        var prober = Build(provider, NewRegistry(), NewCache(), resolver);

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal("base-A", provider.LastBaselineRef);
        Assert.Equal(WorkTarget.NetworkProfile, provider.LastProfileName);
        Assert.Equal(WorkTarget.Flavor, provider.LastFlavor);
        var ensureCall = Assert.Single(resolver.EnsureCalls);
        Assert.Equal(WorkTarget.NetworkProfile, ensureCall.Profile);
        Assert.Equal(WorkTarget.Flavor, ensureCall.Flavor);
        Assert.Equal("base-A", ensureCall.PinnedRef);
    }

    [Fact]
    public async Task ProbeAllAsync_WithDispatchTarget_UsesTargetProfile_NotConfiguredOption()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "Logged in", ""));
        var resolver = new FakeBaselineResolver("base-A");
        var dispatchTarget = new InVmSmokeSandboxTarget("dispatch-profile", SandboxProfileFlavor.Headless);
        var prober = Build(provider, NewRegistry(), NewCache(), resolver,
            opts: new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                NetworkProfile = "configured-smoke-profile",
                SweepIntervalSeconds = 0,
            });

        await prober.ProbeAllAsync(dispatchTarget, CancellationToken.None);

        Assert.Equal("base-A", provider.LastBaselineRef);
        Assert.Equal("dispatch-profile", provider.LastProfileName);
        var ensureCall = Assert.Single(resolver.EnsureCalls);
        Assert.Equal("dispatch-profile", ensureCall.Profile);
        Assert.Equal(dispatchTarget.Flavor, ensureCall.Flavor);
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
    public async Task NullBaselineRef_OnSweep_DoesNotLaunchLiveSandbox()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "not found"));
        var registry = NewRegistry();
        // Non-baseline provider (process/bubblewrap): ResolveBaselineRef -> null.
        var prober = Build(provider, registry, new InVmSmokeCache(TimeSpan.FromMinutes(60)), new FakeBaselineResolver(null));

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task NullBaselineRef_OnDispatchGate_BenchesWithoutProvisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver(null));

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("no clonable baseline", availability.Reason);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ResolverThrows_DefaultFailClosed_BenchesWithoutProvisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var resolver = new FakeBaselineResolver("base-A") { ThrowOnResolve = true };
        var prober = Build(provider, registry, NewCache(), resolver);

        var availability = await prober.EnsureAvailableAsync(
            AgentKind.Cursor,
            WorkTarget,
            CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        Assert.False(availability.Available);
        Assert.Contains("baseline warm-up failed", availability.Reason);
    }

    [Fact]
    public async Task EnsureBaselineReturnsNull_OnDispatchGate_BenchesWithoutProvisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var resolver = new FakeBaselineResolver("base-A") { CanEnsure = false };
        var prober = Build(provider, registry, NewCache(), resolver);

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("no clonable baseline", availability.Reason);
        var ensureCall = Assert.Single(resolver.EnsureCalls);
        Assert.Equal(WorkTarget.NetworkProfile, ensureCall.Profile);
        Assert.Equal(WorkTarget.Flavor, ensureCall.Flavor);
        Assert.Equal("base-A", ensureCall.PinnedRef);
    }

    [Fact]
    public async Task BaselineWarmupThrows_OnDispatchGate_BenchesWithoutProvisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        var resolver = new FakeBaselineResolver("base-A") { ThrowOnEnsure = true };
        var prober = Build(provider, registry, cache, resolver);

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("baseline warm-up failed", availability.Reason);
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
    public async Task MasterSmokeDisabled_NoOpsAllEntrypoints_ThenHotReloadEnableResumes()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "not found"));
        var registry = NewRegistry();
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        var prober = Build(
            provider,
            registry,
            NewCache(),
            new FakeBaselineResolver("base-A"),
            smokeOptions: smokeOptions);

        Assert.False(prober.Enabled);
        await prober.ProbeAllAsync(CancellationToken.None);
        var availability = await prober.EnsureAvailableAsync(AgentKind.Cursor, WorkTarget, CancellationToken.None);
        var forced = await prober.ForceProbeAsync(AgentKind.Cursor, CancellationToken.None);

        Assert.True(availability.Available);
        Assert.Null(forced);
        Assert.Equal(0, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);

        smokeOptions.Replace(new SmokeOptions { Enabled = true });

        Assert.True(prober.Enabled);
        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task MasterSmokeDisabled_EnsureAvailableAsync_IgnoresSmokeSourcesButKeepsFastFail()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "not found"));
        var registry = NewRegistry();
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        var prober = Build(
            provider,
            registry,
            NewCache(),
            new FakeBaselineResolver("base-A"),
            smokeOptions: smokeOptions);

        registry.MarkSmokeResult(
            AgentKind.Cursor,
            new AgentSmokeResult(false, "transient: try later", TimeSpan.Zero, SmokeFailureCategory.Transient),
            SmokeExclusionSource.InVmSmoke);
        registry.ExcludeForMissingProbe(AgentKind.Cursor, "no in-VM smoke probe registered");

        var smokeOnly = await prober.EnsureAvailableAsync(AgentKind.Cursor, WorkTarget, CancellationToken.None);

        Assert.True(smokeOnly.Available);
        Assert.Equal(0, provider.CreateCount);

        for (var i = 0; i < 3; i++)
            registry.RecordRunOutcome(AgentKind.Cursor, success: false, duration: TimeSpan.FromSeconds(1));

        var fastFail = await prober.EnsureAvailableAsync(AgentKind.Cursor, WorkTarget, CancellationToken.None);

        Assert.False(fastFail.Available);
        Assert.Contains("fast-fail circuit breaker", fastFail.Reason);
        Assert.DoesNotContain("transient: try later", fastFail.Reason);
        Assert.DoesNotContain("no in-VM smoke probe", fastFail.Reason);
        Assert.Equal(0, provider.CreateCount);
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
        var resolver = new FakeBaselineResolver("base-A");
        var prober = new InVmSmokeProber(
            provider,
            resolver,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            cache,
            new NullWebhookDispatcher(),
            new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                NetworkProfile = WorkTarget.NetworkProfile,
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
        var resolver = new FakeBaselineResolver("base-A");
        var prober = new InVmSmokeProber(
            provider,
            resolver,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            cache,
            new NullWebhookDispatcher(),
            new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                NetworkProfile = WorkTarget.NetworkProfile,
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
    public async Task CallerCancellationDuringCredentialLookup_PropagatesAndDoesNotBench()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var credentials = new BlockingCredentialProvider();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            credentials: credentials);

        using var cts = new CancellationTokenSource();
        var probeTask = prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, cts.Token);

        await credentials.Started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Equal(0, provider.CreateCount);
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

        var av = await prober.EnsureAvailableAsync(
            AgentKind.Cursor, WorkTarget, CancellationToken.None);

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

        var av = await prober.EnsureAvailableAsync(
            AgentKind.Cursor, WorkTarget.WithBaselineRef("base-PINNED"), CancellationToken.None);

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

        var av = await prober.EnsureAvailableAsync(
            AgentKind.Cursor, WorkTarget.WithBaselineRef("base-OTHER"), CancellationToken.None);

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
    public async Task ForceProbeAsync_ReProbesBenchedAgent_ClearsBench_WhenSandboxNowPasses()
    {
        // Operator-recovery path (the /admin/agent/{name}/smoke endpoint). Unlike
        // EnsureAvailableAsync — which short-circuits an already-excluded agent —
        // ForceProbeAsync must re-exec the CLI even when the agent stands benched,
        // because re-verifying a benched binary is the whole point of the operator
        // call. After the operator fixes the exit-127 cause, the forced re-probe
        // provisions a fresh VM, sees the binary pass, clears the in-VM bench, and
        // returns the updated (now-Available) availability — rather than waiting
        // for the next background sweep. Guards against a regression that made the
        // call a no-op, routed it through the short-circuiting EnsureAvailableAsync,
        // or failed to feed the new verdict back into the registry.
        var broken = true;
        var provider = new FakeSandboxProvider(exec =>
            IsAgent(exec, "--version") && broken
                ? new SandboxExecResult(127, "", "bash: agent: command not found")
                : new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        // First probe fails (exit 127) → cursor benched under InVmSmoke.
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
        var createsAfterBench = provider.CreateCount;

        // Operator fixes the binary; force a re-probe of the still-benched agent.
        broken = false;
        var result = await prober.ForceProbeAsync(AgentKind.Cursor, CancellationToken.None);

        // A fresh VM was provisioned despite the standing bench (i.e. it did NOT
        // short-circuit like EnsureAvailableAsync would), and both the returned
        // value and the registry reflect the now-passing CLI.
        Assert.True(provider.CreateCount > createsAfterBench);
        Assert.NotNull(result);
        Assert.True(result!.Available);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task ForceProbeAsync_WithDispatchTarget_UsesTargetAndBypassesCache()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        cache.Set(AgentKind.Cursor, "base-DISPATCH",
            new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
        var resolver = new FakeBaselineResolver("base-CONFIGURED");
        var target = new InVmSmokeSandboxTarget(
            "dispatch-profile",
            SandboxProfileFlavor.Headless,
            "base-DISPATCH");
        var prober = Build(provider, registry, cache, resolver,
            opts: new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                NetworkProfile = "configured-smoke-profile",
                SweepIntervalSeconds = 0,
            });

        var result = await prober.ForceProbeAsync(AgentKind.Cursor, target, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Available);
        Assert.Equal(1, provider.CreateCount);
        Assert.Equal("base-DISPATCH", provider.LastBaselineRef);
        Assert.Equal("dispatch-profile", provider.LastProfileName);
        Assert.Equal(target.Flavor, provider.LastFlavor);
        var ensureCall = Assert.Single(resolver.EnsureCalls);
        Assert.Equal("dispatch-profile", ensureCall.Profile);
        Assert.Equal(target.Flavor, ensureCall.Flavor);
        Assert.Equal("base-DISPATCH", ensureCall.PinnedRef);
    }

    [Fact]
    public async Task StalePass_RegressesWithinSameRef_ForceProbeReExecsAndInvalidates_NextSweepStaysBenched()
    {
        // Production regression the smoke gate exists to catch: a baseline that
        // regresses (CLI breaks, auth/trust expires) WITHIN the same content-hash
        // ref + TTL window. Only passes are cached and ProbeAgentAsync consults
        // the cache first, so without a cache bypass + failure-invalidation the
        // stale pass would be replayed forever — reconciling the agent back to
        // Available without ever re-execing the now-broken CLI.
        var broken = false;
        var provider = new FakeSandboxProvider(exec =>
            IsAgent(exec, "--version") && broken
                ? new SandboxExecResult(127, "", "bash: agent: command not found")
                : new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        // Pass: cursor Available, pass cached for base-A.
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.NotNull(cache.TryGet(AgentKind.Cursor, "base-A"));

        // Baseline regresses on the SAME ref. A plain sweep keeps hitting the
        // cached pass and never notices (CreateCount unchanged).
        broken = true;
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(1, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);

        // Operator force-probe MUST bypass the cache, re-exec the CLI, observe the
        // regression, bench the agent, and purge the now-stale cached pass.
        var forced = await prober.ForceProbeAsync(AgentKind.Cursor, CancellationToken.None);
        Assert.Equal(2, provider.CreateCount);
        Assert.NotNull(forced);
        Assert.False(forced!.Available);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));

        // With the stale pass gone, the next background sweep re-execs (cache
        // miss) rather than reconciling the old pass back to Available.
        await prober.ProbeAllAsync(CancellationToken.None);
        Assert.Equal(3, provider.CreateCount);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task ForceProbeAsync_NoRegisteredProbeForKind_ReturnsNull_NoProvision()
    {
        // The admin endpoint falls back to the host-probe verdict (and its 404
        // decision) when ForceProbeAsync returns null, which it must do for an
        // agent that has no registered in-VM probe — without provisioning a VM.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        // Only a cursor probe is registered; ask to force-probe claude.
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"));

        var result = await prober.ForceProbeAsync(AgentKind.Claude, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task ForceProbeAsync_Disabled_ReturnsNull_NoProvision()
    {
        // When in-VM smoke is disabled the gate provisions nothing and returns
        // null so the admin endpoint relies on the host probe alone.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions { Enabled = false, ImageReference = "img", SweepIntervalSeconds = 0 });

        var result = await prober.ForceProbeAsync(AgentKind.Cursor, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task ForceProbeAsync_NoConfiguredProfile_ReturnsNull_NoProvision()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            fillDefaultNetworkProfile: false);

        var result = await prober.ForceProbeAsync(AgentKind.Cursor, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task ForceProbeAsync_ProvisioningDeferred_ReturnsNullWithoutBenching()
    {
        var deferred = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "clone",
            errorClass: "multipass-instance-lock-contention",
            detail: "clone retry exhausted",
            recheckIn: TimeSpan.FromSeconds(30));
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""))
        {
            ThrowOnCreate = deferred,
        };
        var registry = NewRegistry();
        var cache = NewCache();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        var result = await prober.ForceProbeAsync(AgentKind.Cursor, WorkTarget, CancellationToken.None);

        Assert.Null(result);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task ProbeAllAsync_NoConfiguredProfile_SkipsWithoutProvisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            fillDefaultNetworkProfile: false);

        await prober.ProbeAllAsync(CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task EnsureProbedAsync_NoConfiguredProfile_FailClosedBenchesWithoutProvisioning()
    {
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            opts: new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            fillDefaultNetworkProfile: false);

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
        var availability = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(availability.Available);
        Assert.Contains("baseline target has no network profile", availability.Reason);
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
    public async Task EnsureAvailableAsync_ProvisioningDeferredFromCreate_PropagatesWithoutBenching()
    {
        var deferred = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "clone",
            errorClass: "multipass-instance-lock-contention",
            detail: "clone retry exhausted",
            recheckIn: TimeSpan.FromSeconds(30));
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""))
        {
            ThrowOnCreate = deferred,
        };
        var registry = NewRegistry();
        var cache = NewCache();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"));

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            prober.EnsureAvailableAsync(AgentKind.Cursor, WorkTarget, CancellationToken.None));

        Assert.Same(deferred, thrown);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureAvailableAsync_ProvisioningDeferredFromBaselineWarmup_PropagatesWithoutBenching()
    {
        var deferred = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "baseline-launch",
            errorClass: "multipass-instance-lock-contention",
            detail: "baseline launch retry exhausted",
            recheckIn: TimeSpan.FromSeconds(30));
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var cache = NewCache();
        var resolver = new FakeBaselineResolver("base-A")
        {
            ThrowOnEnsureException = deferred,
        };
        var prober = Build(provider, registry, cache, resolver);

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            prober.EnsureAvailableAsync(AgentKind.Cursor, WorkTarget, CancellationToken.None));

        Assert.Same(deferred, thrown);
        Assert.Equal(0, provider.CreateCount);
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
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

    // ── Category classification on the in-VM probe path ──────────────────
    //
    // The Gemini bench-loop production incident traced to "transient: try
    // later" being applied to every smoke failure, so a persistent
    // credential / missing-binary fault looked indistinguishable from a
    // network blip and the agent stayed benched indefinitely. The fix routes
    // every in-VM verdict through SmokeFailureCategory so persistent failures
    // raise an operator alert and transient ones continue to retry. These
    // tests pin the load-bearing classification at the in-VM source: swapping
    // Persistent ↔ Transient, defaulting to None on a failure, or returning
    // Unknown on the wrong path would reintroduce the silent-bench bug.

    [Fact]
    public async Task RunSmokeSteps_NonZeroExit_IsClassifiedPersistent()
    {
        // The load-bearing branch the production incident traced to: any
        // non-zero exit from an in-VM smoke step (binary missing on PATH, auth
        // rejection, --version returning 1) must be tagged Persistent so the
        // operator-alert / persistent-webhook path fires. Tagging Transient
        // would reproduce the indefinite-bench bug; tagging None would leave
        // the agent Available despite the failure.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(127, "", "command not found"));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"));

        var result = await prober.ProbeAgentAsync(
            new CursorInVmSmokeProbe(), WorkTarget, "base-A", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Equal("base-A", provider.LastBaselineRef);
        Assert.Equal(WorkTarget.NetworkProfile, provider.LastProfileName);
        Assert.Equal(WorkTarget.Flavor, provider.LastFlavor);
        // The registry encodes the category in the reason tag, so the router
        // log / /concurrency surface it alongside the message.
        Assert.Contains("[persistent]", registry.GetAvailability(AgentKind.Cursor).Reason);
    }

    [Fact]
    public async Task RunSmokeSteps_AllStepsZeroExit_IsClassifiedNone()
    {
        // Contrast for the above: a passing probe must carry None (not
        // Unknown or Transient), so a recovered-variant webhook does not
        // inherit a leftover failure category from an earlier emission.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "ok", ""));
        var prober = Build(provider, NewRegistry(), NewCache(), new FakeBaselineResolver("base-A"));

        var result = await prober.ProbeAgentAsync(
            new CursorInVmSmokeProbe(), WorkTarget, "base-A", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(SmokeFailureCategory.None, result.Category);
        Assert.Equal("base-A", provider.LastBaselineRef);
        Assert.Equal(WorkTarget.NetworkProfile, provider.LastProfileName);
        Assert.Equal(WorkTarget.Flavor, provider.LastFlavor);
    }

    [Fact]
    public async Task BenchTransientFault_StepTimeout_IsClassifiedTransient()
    {
        // The transient-fault arm of the classification: a step timeout is
        // infra flakiness, NOT operator-actionable. The fail-closed dispatch
        // gate still benches the agent (so an unverified CLI is not routable)
        // but the verdict must carry Transient so the periodic sweep clears it
        // on recovery rather than paging the operator.
        var provider = new HangingSandboxProvider();
        var registry = NewRegistry();
        var resolver = new FakeBaselineResolver("base-A");
        var prober = new InVmSmokeProber(
            provider,
            resolver,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            NewCache(),
            new NullWebhookDispatcher(),
            new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                NetworkProfile = WorkTarget.NetworkProfile,
                SweepIntervalSeconds = 0,
                StepTimeoutSeconds = 0, // immediate-timeout for deterministic test
                FailClosedOnProbeFault = true,
            },
            NullLogger<InVmSmokeProber>.Instance);

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("[transient]", av.Reason);
        Assert.Contains("probe step timed out", av.Reason);
        Assert.Equal(1, provider.CreateCount);
    }

    [Fact]
    public async Task BenchTransientFault_CredentialResolutionFault_IsClassifiedTransient()
    {
        // Companion to the timeout case for the other transient-fault catch in
        // ProbeAgentAsync (credential resolution): a credential-store fault is
        // not an agent fault, so the bench (under fail-closed) must be tagged
        // Transient so it clears on recovery rather than paging the operator.
        var provider = new FakeSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = Build(provider, registry, NewCache(), new FakeBaselineResolver("base-A"),
            credentials: new ThrowingCredentialProvider());

        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);

        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("[transient]", av.Reason);
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

    private static InVmSmokeOptions TimeoutOpts(
        int provisionTimeoutSeconds,
        int gateDeadlineSeconds,
        bool failClosed = true) =>
        new()
        {
            Enabled = true,
            ImageReference = "img",
            NetworkProfile = WorkTarget.NetworkProfile,
            SweepIntervalSeconds = 0,
            ProvisionTimeoutSeconds = provisionTimeoutSeconds,
            GateDeadlineSeconds = gateDeadlineSeconds,
            FailClosedOnProbeFault = failClosed,
        };

    [Fact]
    public async Task EnsureProbedAsync_ProvisioningHang_DefaultFailClosed_BenchesWithinProvisionTimeout()
    {
        // Production hang observed 2026-06-01: ISandboxProvider.CreateAsync (VM
        // clone + "Launching multipass VM") never returns; per-step exec timeouts
        // can't fire because no sandbox exists to exec into. Without a hard
        // provisioning timeout the entire dispatch gate hangs and the worker pool
        // wedges. ProvisionTimeoutSeconds=1 makes the prober bench-and-continue
        // rather than wait forever for the inner CreateAsync. Gate deadline well
        // above the provisioning timeout so we exercise the provisioning catch
        // rather than the outer deadline net.
        var provider = new HangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 1, gateDeadlineSeconds: 30));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; provisioning timeout did not fire");
        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        Assert.Contains("probe provisioning timed out", av.Reason);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A")); // fault never cached → self-heals
    }

    [Fact]
    public async Task ProbeAllAsync_ProvisioningHang_FailsOpen_DoesNotBench_DoesNotHang()
    {
        // Companion to the dispatch-gate case: the background sweep always fails
        // open, so a stuck CreateAsync must NOT bench a possibly-working agent —
        // but it also must NOT wedge the sweep loop forever. The provisioning
        // timeout still fires (transient fault); ProbeAllAsync swallows it
        // without mutating availability.
        var provider = new HangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 1, gateDeadlineSeconds: 30));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.ProbeAllAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"sweep took {sw.Elapsed}; provisioning timeout did not fire");
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available); // sweep fails open
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureProbedAsync_ProvisioningHang_FailOpenOptOut_DoesNotBench_DoesNotHang()
    {
        // Even on the gate path under fail-open, a stuck CreateAsync must not
        // wedge the worker — the provisioning timeout fires, the agent stays
        // available (fail-open), and the gate returns.
        var provider = new HangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 1, gateDeadlineSeconds: 30, failClosed: false));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; provisioning timeout did not fire");
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureProbedAsync_NonCooperativeProvisioningHang_StillBenchesWithinProvisionTimeout()
    {
        // The production failure mode the provisioning timeout exists for: a
        // wedged ISandboxProvider.CreateAsync that IGNORES its cancellation token
        // (multipass daemon stuck mid-clone). A cooperative-cancellation-only
        // implementation — i.e. just passing a linked CT into CreateAsync and
        // awaiting it — would still hang forever here. The provisioning timeout
        // must be a hard wall-clock bound, so we model the non-cooperative
        // provider explicitly and assert the gate still returns a transient
        // verdict within the provisioning bound. Without this test, a regression
        // back to the cooperative-only approach would not be caught.
        var provider = new NonCooperativeHangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 1, gateDeadlineSeconds: 60));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        // Provisioning timeout (1s) fires via the wall-clock race well before
        // the gate deadline (60s) would even fire — i.e. it's the provisioning
        // bound, not the outer deadline, that caught this.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; non-cooperative provisioning hang was not bounded");
        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        Assert.Contains("probe provisioning timed out", av.Reason);
        Assert.DoesNotContain("probe deadline exceeded", av.Reason);
    }

    [Fact]
    public async Task EnsureProbedAsync_GateDeadline_BenchesWithinBound_EvenIfInnerNeverReturns()
    {
        // Defect-in-depth: with the inner provisioning timeout disabled
        // (ProvisionTimeoutSeconds=0) the outer GateDeadlineSeconds is the only
        // bound left. A wedged CreateAsync must still not be allowed to stall
        // the worker forever — the gate deadline is the safety net for any inner
        // step the per-operation timeouts don't cover (e.g. a stuck sandbox
        // DisposeAsync in some future code path).
        var provider = new HangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 0, gateDeadlineSeconds: 1));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; deadline did not fire");
        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("in-VM probe inconclusive", av.Reason);
        Assert.Contains("probe deadline exceeded", av.Reason);
    }

    [Fact]
    public async Task EnsureProbedAsync_GateDeadline_FailOpenOptOut_ReturnsWithoutBenching()
    {
        // The outer gate-deadline branch (Task.WhenAny picks the deadline before
        // the inner probeTask completes) has distinct fail-open behavior from
        // every other fault path: under FailClosedOnProbeFault=false the gate
        // must RETURN without benching when the deadline elapses, leaving the
        // agent routable. The fail-open coverage elsewhere targets the inner
        // provisioning catch; a regression that always benched on the deadline
        // branch — but still observed the fail-open flag for provisioning —
        // would pass every other test, so pin this branch specifically.
        //
        // ProvisionTimeoutSeconds=0 disables the inner bound so we exercise the
        // outer gate-deadline net unambiguously.
        var provider = new HangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 0, gateDeadlineSeconds: 1, failClosed: false));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; deadline did not fire under fail-open");
        // Fail-open: deadline expiry does NOT bench the agent.
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));
    }

    [Fact]
    public async Task EnsureProbedAsync_ProvisioningHang_LateSuccess_OrphanObserverDisposesSandbox()
    {
        // The orphan observer's reason for existing: if CreateAsync eventually
        // succeeds AFTER the provisioning wall-clock timeout has fired, the
        // late-arriving sandbox must be disposed so we don't leak a real VM the
        // gate already walked away from. The hanging / non-cooperative tests
        // exercise the bench-and-bail path but cannot catch a regression that
        // removes (or breaks) the late-success cleanup, because their
        // CreateAsync never produces a sandbox.
        var sandbox = new RecordedDisposeSandbox();
        var provider = new DelayedSuccessSandboxProvider(sandbox);
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 1, gateDeadlineSeconds: 30));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        // The gate already returned on the provisioning timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; provisioning timeout did not fire");
        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("probe provisioning timed out", av.Reason);
        Assert.False(sandbox.Disposed); // CreateAsync hasn't completed yet

        // Now let CreateAsync complete with a real sandbox. The orphan observer
        // must dispose it — otherwise we'd leak a live VM for every wedged
        // provisioning that eventually recovers.
        provider.Complete();
        Assert.True(await sandbox.WaitForDisposeAsync(TimeSpan.FromSeconds(10)),
            "orphan observer did not dispose the late-arriving sandbox");
    }

    [Fact]
    public async Task EnsureProbedAsync_CallerCancelledMidProvisioning_PropagatesCancellation_DoesNotBench_AndOrphanDisposesLateSandbox()
    {
        // Distinct caller-cancellation branch of CreateSandboxWithProvisionTimeoutAsync
        // (worker / shutdown token fired before CreateAsync returns). The branch
        // MUST: (1) hand the orphaned create task off so a sandbox the provider
        // eventually yields is disposed; (2) rethrow cancellation instead of
        // converting it into a TimeoutException / transient bench, because
        // worker shutdown is not evidence the CLI is broken; (3) leave the agent
        // routable so a later worker (with a non-cancelled token) is not refused
        // by a phantom bench. All timeout / hanging-provider tests above pass
        // CancellationToken.None, so a regression that benches on shutdown
        // cancellation, swallows the OCE, or skips the orphan handoff would not
        // be caught — this pins the three behaviours together.
        var sandbox = new RecordedDisposeSandbox();
        var provider = new DelayedSuccessSandboxProvider(sandbox);
        var cache = NewCache();
        var registry = NewRegistry();
        // ProvisionTimeout / GateDeadline are well above the cancellation we
        // drive manually, so any failure of the assertions below points at the
        // caller-cancellation path, not at the wall-clock timer winning a race.
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 60, gateDeadlineSeconds: 120));

        using var cts = new CancellationTokenSource();
        var probeTask = prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, cts.Token);

        // Wait long enough for the probe to enter the wrapper's WhenAny on
        // CreateAsync (DelayedSuccessSandboxProvider returns a Task that only
        // completes when Complete() is called). Then cancel — this drives the
        // wrapper's "winner != createTask" / ct-cancelled branch.
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);

        // Shutdown cancellation must not bench the agent: a later worker (with a
        // non-cancelled token) would otherwise see a CLI that was never actually
        // smoke-checked falsely marked broken. The cache likewise stays empty.
        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Null(cache.TryGet(AgentKind.Cursor, "base-A"));

        // Orphan handoff: the gate already walked away, but CreateAsync will
        // eventually return a sandbox. Without the orphan observer disposing
        // it, every cancelled probe would leak a live multipass VM until
        // process exit. Drive the provider to completion and assert the
        // late-arriving sandbox is disposed.
        provider.Complete();
        Assert.True(await sandbox.WaitForDisposeAsync(TimeSpan.FromSeconds(10)),
            "orphan observer did not dispose the late-arriving sandbox after caller cancellation");
    }

    [Fact]
    public async Task EnsureProbedAsync_GateDeadlineDisabled_DoesNotFireAsImmediateTimeout_InnerTimeoutStillBoundsHang()
    {
        // GateDeadlineSeconds <= 0 disables the outer wall-clock deadline (for
        // tests on synthetic clocks, or operators tuning policy). The disabled
        // branch is structurally separate, so this pins two things together:
        //   1. Zero is NOT treated as an immediate-timeout (a regression that
        //      flipped the comparison would bench every probe in 0s with a
        //      "deadline exceeded" reason).
        //   2. The inner provisioning bound is still in force, so a wedged
        //      CreateAsync still produces a transient verdict — the prober
        //      doesn't silently lose all defence-in-depth when the outer
        //      deadline is off.
        var provider = new HangingCreateSandboxProvider();
        var cache = NewCache();
        var registry = NewRegistry();
        var prober = Build(provider, registry, cache, new FakeBaselineResolver("base-A"),
            opts: TimeoutOpts(provisionTimeoutSeconds: 1, gateDeadlineSeconds: 0));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await prober.EnsureProbedAsync(AgentKind.Cursor, baselineRef: null, CancellationToken.None);
        sw.Stop();

        // The provisioning bound (1s) fires; the disabled deadline did NOT
        // immediate-timeout (would have returned in ~0s with a deadline-exceeded
        // reason instead).
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"gate returned in {sw.Elapsed}; GateDeadlineSeconds=0 was wrongly treated as immediate");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"gate took {sw.Elapsed}; inner provisioning bound failed to fire under disabled deadline");
        var av = registry.GetAvailability(AgentKind.Cursor);
        Assert.False(av.Available);
        Assert.Contains("probe provisioning timed out", av.Reason);
        Assert.DoesNotContain("probe deadline exceeded", av.Reason);
    }

    private static bool IsAgent(SandboxExec exec, string sub) =>
        exec.Argv.Count >= 2 && exec.Argv[0] == CursorAgentRunner.DefaultBinary && exec.Argv[1] == sub;

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public int CreateCount { get; private set; }
        public string? LastBaselineRef { get; private set; }
        public string? LastProfileName { get; private set; }
        public SandboxProfileFlavor? LastFlavor { get; private set; }
        public Exception? ThrowOnCreate { get; set; }
        // Every argv exec'd across all sandboxes this provider created, in order.
        public List<IReadOnlyList<string>> ExecutedArgv { get; } = new();

        public FakeSandboxProvider(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;

        public string Name => "fake";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCount++;
            LastBaselineRef = spec.BaselineImageRef;
            LastProfileName = spec.Network.ProfileName;
            LastFlavor = spec.Flavor;
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

    /// <summary>Credential provider that blocks until caller cancellation fires.</summary>
    private sealed class BlockingCredentialProvider : ICredentialProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            _ = agent;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
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

    /// <summary>
    /// Sandbox provider whose CreateAsync hangs until its cancellation token
    /// fires. Models a COOPERATIVE wedge (the linked CT eventually unblocks the
    /// provider) so tests can drive both the inner provisioning timeout and the
    /// outer gate deadline through the same fake.
    /// </summary>
    private sealed class HangingCreateSandboxProvider : ISandboxProvider
    {
        public string Name => "hanging-create";

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct); // completes only on cancellation
            throw new InvalidOperationException("unreachable");
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Sandbox that records when DisposeAsync is invoked, so a test can assert
    /// the orphan-observer cleanup path actually disposes a late-arriving VM
    /// rather than leaking it.
    /// </summary>
    private sealed class RecordedDisposeSandbox : ISandbox
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "recorded-dispose";

        public bool Disposed => _disposed.Task.IsCompletedSuccessfully;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task<bool> WaitForDisposeAsync(TimeSpan timeout)
        {
            var winner = await Task.WhenAny(_disposed.Task, Task.Delay(timeout));
            return winner == _disposed.Task;
        }
    }

    /// <summary>
    /// Sandbox provider whose CreateAsync only completes when the test invokes
    /// <see cref="Complete"/>. Models the failure mode the orphan observer
    /// exists to handle: a provider that the gate timed out on, but whose
    /// CreateAsync eventually returns a usable sandbox after the gate already
    /// walked away. The orphan observer must dispose that late-arriving VM
    /// rather than leak it.
    /// </summary>
    private sealed class DelayedSuccessSandboxProvider : ISandboxProvider
    {
        private readonly TaskCompletionSource<ISandbox> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ISandbox _sandbox;

        public DelayedSuccessSandboxProvider(ISandbox sandbox) => _sandbox = sandbox;

        public string Name => "delayed-success";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) => _tcs.Task;

        public void Complete() => _tcs.TrySetResult(_sandbox);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Sandbox provider whose CreateAsync NEVER returns — it ignores its
    /// cancellation token entirely. Models the production failure that motivated
    /// the wall-clock provisioning timeout: a wedged multipass daemon stuck mid
    /// clone whose CreateAsync call observes no cancellation signal. A
    /// cooperative-cancellation-only implementation (just passing a linked CT
    /// into CreateAsync and awaiting it) would still hang forever against this
    /// fake; the test guards the wall-clock race against that regression.
    /// </summary>
    private sealed class NonCooperativeHangingCreateSandboxProvider : ISandboxProvider
    {
        // A TaskCompletionSource that is never completed by anything — and is
        // never tied to ct in any way — so the returned Task is exactly the
        // "completion never observable" shape we need.
        private readonly TaskCompletionSource<ISandbox> _never = new();

        public string Name => "non-cooperative-hanging-create";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            _never.Task;

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeBaselineResolver : IBaselineImageResolver, IBaselineImageProvisioner
    {
        public string? Ref { get; set; }
        public bool CanEnsure { get; set; } = true;
        public bool ThrowOnResolve { get; set; }
        public bool ThrowOnEnsure { get; set; }
        public Exception? ThrowOnEnsureException { get; set; }
        public List<(string Profile, SandboxProfileFlavor Flavor, string? PinnedRef)> EnsureCalls { get; } = [];
        public FakeBaselineResolver(string? r) => Ref = r;

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
        {
            if (ThrowOnResolve) throw new InvalidOperationException("baseline resolver failed");
            return Ref;
        }

        public Task<string?> EnsureBaselineImageAsync(
            string profileName,
            SandboxProfileFlavor flavor,
            string? pinnedBaselineRef,
            CancellationToken ct)
        {
            if (ThrowOnEnsureException is not null) throw ThrowOnEnsureException;
            if (ThrowOnEnsure) throw new InvalidOperationException("baseline provisioner failed");
            EnsureCalls.Add((profileName, flavor, pinnedBaselineRef));
            return Task.FromResult(CanEnsure ? pinnedBaselineRef ?? Ref : null);
        }

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
