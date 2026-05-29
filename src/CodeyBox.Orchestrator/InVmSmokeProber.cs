using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Provisions a sandbox cloned from the active baseline image and execs each
/// agent's in-VM smoke sequence (<see cref="IInVmSmokeProbe"/>) inside it. This
/// is the probe the host-only <see cref="CredentialSmokeGate"/> could not be:
/// it catches an agent CLI that is missing from the sandbox PATH (exit 127),
/// has its credential materialised to the wrong path, or otherwise refuses to
/// run — at smoke time, before a work item is dispatched.
///
/// <para>Results feed the <see cref="AgentAvailabilityRegistry"/>, which the
/// <see cref="AgentClassRouter"/> already consults to skip excluded agents — so
/// a failing in-VM probe routes the work item past the broken agent to a
/// working alternative (AC#1) without any router change.</para>
///
/// <para><b>Caching (AC#2 / AC#3).</b> Only a <em>passing</em> result is cached,
/// keyed by <c>(agent, baselineRef)</c>: a cache hit provisions nothing, so
/// steady-state sweeps and dispatches are free. A baseline rebake changes the
/// content-hash ref and the next sweep re-probes against the new image. A
/// failing probe is never cached, so the next sweep (or an operator who fixes
/// the CLI) re-execs immediately rather than waiting out a TTL — that is the
/// excluded-agent self-healing path.</para>
///
/// <para><b>Reconciliation.</b> Every probe — including a cache hit — feeds its
/// verdict into <see cref="AgentAvailabilityRegistry"/> under
/// <see cref="SmokeExclusionSource.InVmSmoke"/>, so the registry and cache can
/// never silently diverge (e.g. after an operator reset clears the registry).</para>
///
/// <para><b>What excludes vs. what is transient.</b> A <em>clean</em> negative
/// signal — a step that exits non-zero — always excludes an agent. Provisioning
/// failures, exec exceptions, and step timeouts are transient infrastructure
/// problems; how they are handled depends on the call path. On the background
/// sweep (<see cref="ProbeAllAsync"/>) they are always logged and skipped
/// without mutating availability or the cache, so a flaky host never wrongly
/// benches a working agent. On the dispatch gate
/// (<see cref="EnsureProbedAsync"/>) the operator's
/// <see cref="InVmSmokeOptions.FailClosedOnProbeFault"/> policy applies: under
/// the default fail-closed policy a transient fault temporarily benches the
/// agent (never cached, so it self-heals on the next successful probe) so the
/// router does not dispatch to a CLI it could not verify; under the opt-out
/// fail-open policy it leaves availability unchanged like the sweep.</para>
/// </summary>
public sealed class InVmSmokeProber : IInVmSmokeGate
{
    private const string LiveRefSentinel = "live";

    private readonly ISandboxProvider _provider;
    private readonly IBaselineImageResolver _resolver;
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyList<IInVmSmokeProbe> _probes;
    private readonly ISmokeAvailabilityRegistry _availability;
    private readonly IInVmSmokeCache _cache;
    private readonly IWebhookDispatcher _webhooks;
    private readonly InVmSmokeOptions _opts;
    private readonly ILogger<InVmSmokeProber> _log;

    public InVmSmokeProber(
        ISandboxProvider provider,
        IBaselineImageResolver resolver,
        ICredentialProvider credentials,
        IEnumerable<IInVmSmokeProbe> probes,
        ISmokeAvailabilityRegistry availability,
        IInVmSmokeCache cache,
        IWebhookDispatcher webhooks,
        InVmSmokeOptions opts,
        ILogger<InVmSmokeProber> log)
    {
        _provider = provider;
        _resolver = resolver;
        _credentials = credentials;
        _probes = probes.ToList();
        _availability = availability;
        _cache = cache;
        _webhooks = webhooks;
        _opts = opts;
        _log = log;
    }

    public bool Enabled => _opts.Enabled && _probes.Count > 0;

    /// <summary>
    /// Probes every registered agent against the active baseline. Sequential so
    /// the sweep never holds more than one probe VM at a time. Never throws.
    /// </summary>
    public async Task ProbeAllAsync(CancellationToken ct)
    {
        if (!Enabled) return;

        var baselineRef = ResolveBaselineRef();

        foreach (var probe in _probes)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await ProbeAgentAsync(probe, baselineRef, ct);
            }
            catch (Exception ex)
            {
                // Defensive belt-and-suspenders: ProbeAgentAsync already handles
                // the expected transient faults (provisioning / exec / timeout /
                // credential) internally, so reaching here means something
                // genuinely unexpected threw. Log at Warning — not Debug — so an
                // implementation bug or misconfiguration is visible rather than
                // silently swallowed, but still continue the sweep for the rest.
                _log.LogWarning(ex, "In-VM smoke: probe for {Agent} threw unexpectedly; skipping this agent", probe.Kind.Value);
            }
        }
    }

    private string ResolveBaselineRef() =>
        _resolver.ResolveBaselineRef(_opts.NetworkProfile, SandboxProfileFlavor.Headless) ?? LiveRefSentinel;

    /// <summary>
    /// <see cref="IInVmSmokeGate.EnsureAvailableAsync"/>. Owns the full
    /// read→probe→re-read sequence so routing consumers get a verdict from this
    /// one call. Returns the agent's prior availability untouched when the gate
    /// is disabled or the agent is already excluded (no point probing a binary
    /// the router will skip regardless); otherwise probes and returns the
    /// reconciled availability. A cache hit is free.
    /// </summary>
    public async Task<AgentAvailability> EnsureAvailableAsync(AgentKind kind, CancellationToken ct)
    {
        var current = _availability.GetAvailability(kind);
        if (!Enabled || !current.Available)
            return current;
        await EnsureProbedAsync(kind, ct);
        return _availability.GetAvailability(kind);
    }

    /// <summary>
    /// Ensures the in-VM smoke verdict for <paramref name="kind"/> is reflected
    /// in the availability registry. Called on the dispatch path (via
    /// <see cref="EnsureAvailableAsync"/>) before an agent's <c>Available</c>
    /// state is trusted, so the very first work item after startup or a baseline
    /// rebake is gated by a real in-sandbox CLI check rather than racing the
    /// background sweep. A cache hit is free (no VM); a miss provisions one VM
    /// and feeds the registry. Never throws — the dispatch path must not be
    /// taken down by a probe fault.
    /// </summary>
    public async Task EnsureProbedAsync(AgentKind kind, CancellationToken ct)
    {
        if (!Enabled) return;
        var probe = _probes.FirstOrDefault(p => p.Kind == kind);
        if (probe is null) return;

        try
        {
            // On the dispatch gate, honour the operator's probe-fault policy:
            // fail-closed temporarily benches the agent when the probe can't run
            // to a verdict (see InVmSmokeOptions.FailClosedOnProbeFault).
            await ProbeAgentAsync(probe, ResolveBaselineRef(), ct, benchOnTransientFault: _opts.FailClosedOnProbeFault);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine dispatch/shutdown cancellation must propagate so the
            // router aborts cleanly rather than continuing to route on a
            // half-probed agent. Only step timeouts (ct NOT signalled) are
            // handled as transient inside ProbeAgentAsync; those never reach here.
            throw;
        }
        catch (Exception ex)
        {
            // The gate runs on the router hot path and must never throw. The
            // expected transient faults are already handled inside ProbeAgentAsync
            // (per the fault policy), so reaching here is an unexpected fault
            // worth surfacing at Warning rather than hiding at Debug — but we
            // still swallow it so a probe fault cannot take down dispatch.
            _log.LogWarning(ex, "In-VM smoke gate: probe for {Agent} threw unexpectedly; leaving availability unchanged", kind.Value);
        }
    }

    /// <summary>
    /// Probes one agent. Returns the result that was applied, or null when a
    /// transient failure (provisioning / exec / timeout) means availability must
    /// not change. A cache hit re-applies the cached <em>passing</em> verdict to
    /// the registry (reconciliation) and returns it without provisioning a VM.
    /// </summary>
    internal async Task<AgentSmokeResult?> ProbeAgentAsync(
        IInVmSmokeProbe probe, string baselineRef, CancellationToken ct,
        bool benchOnTransientFault = false)
    {
        if (_cache.TryGet(probe.Kind, baselineRef) is { } cached)
        {
            // Only passing verdicts are cached, so this re-asserts availability.
            // Re-applying keeps the registry reconciled with the cache even after
            // an operator reset cleared the registry without a fresh probe.
            // No AuditLog/Stopwatch entry here: a cache hit happens on every
            // gated dispatch in steady state, so only surface a webhook on an
            // actual availability transition (e.g. reconciling after a reset).
            // clearsFastFail:false — a cache hit re-executed no CLI, so it must
            // not lift a fast-fail bench earned from real dispatch failures. It
            // only reconciles this source's (InVmSmoke) exclusion.
            _log.LogDebug("In-VM smoke: cache hit for {Agent} @ {Ref}", probe.Kind.Value, baselineRef);
            var hitTransition = _availability.MarkSmokeResult(
                probe.Kind, cached, SmokeExclusionSource.InVmSmoke, clearsFastFail: false);
            await EmitTransitionWebhookAsync(probe.Kind, cached, hitTransition);
            return cached;
        }

        AgentCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(probe.Kind, ct);
        }
        catch (Exception ex)
        {
            // Credential store fault is an infra problem, not an agent fault.
            // Fail open by default (return null → availability unchanged):
            // benching here would route work away from a CLI that may be
            // perfectly healthy. Under the fail-closed dispatch policy, bench
            // it temporarily instead so dispatch never proceeds unverified.
            _log.LogWarning(ex, "In-VM smoke: could not resolve credential for {Agent}; treating as transient", probe.Kind.Value);
            return BenchTransientFaultIfRequested(probe.Kind, "credential resolution failed", benchOnTransientFault);
        }

        // A null credential does NOT skip the agent: the probe still returns its
        // credential-independent steps (e.g. `--version`), so a binary missing
        // from the sandbox PATH (exit 127) is caught even before any credential
        // is configured. This is the IInVmSmokeProbe.BuildSteps(null) contract.
        var steps = probe.BuildSteps(credential);
        if (steps.Count == 0) return null;

        var sw = Stopwatch.StartNew();
        AgentSmokeResult result;
        try
        {
            result = await RunStepsInSandboxAsync(credential, baselineRef, steps, sw, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A step timed out — treat as transient infra, not an agent fault.
            _log.LogWarning("In-VM smoke for {Agent}: timed out; treating as transient", probe.Kind.Value);
            return BenchTransientFaultIfRequested(probe.Kind, "probe step timed out", benchOnTransientFault);
        }
        catch (Exception ex)
        {
            // Provisioning or exec error — the host/provider is unhealthy, not
            // the agent CLI. Do not exclude by default; let the next sweep retry.
            // Under fail-closed dispatch, bench temporarily so the gate does not
            // hand work to a CLI it could not verify.
            _log.LogWarning(ex, "In-VM smoke for {Agent}: provisioning/exec failed; treating as transient", probe.Kind.Value);
            return BenchTransientFaultIfRequested(probe.Kind, "probe provisioning/exec failed", benchOnTransientFault);
        }

        // Cache only passes: a failure must be re-checked on the next sweep /
        // dispatch (self-healing) rather than pinned for the whole TTL.
        if (result.Ok)
            _cache.Set(probe.Kind, baselineRef, result);
        // clearsFastFail:true — this verdict comes from a freshly executed in-VM
        // probe that actually ran the binary in a sandbox, so a pass is valid
        // evidence the CLI launches and may lift the fast-fail circuit breaker.
        var transition = _availability.MarkSmokeResult(
            probe.Kind, result, SmokeExclusionSource.InVmSmoke, clearsFastFail: true);
        await EmitTransitionEventsAsync(probe.Kind, result, transition);
        return result;
    }

    /// <summary>
    /// Under the fail-closed dispatch policy, benches <paramref name="kind"/>
    /// under <see cref="SmokeExclusionSource.InVmSmoke"/> for an inconclusive
    /// (transient-fault) probe so the router routes past an unverified CLI. The
    /// failing result is intentionally <em>not</em> cached (the caller's cache
    /// path only stores passes), so it is re-probed on the next sweep / gate
    /// call and self-heals once the host recovers. <c>clearsFastFail:false</c>
    /// because an inconclusive probe is not evidence the binary launches.
    /// Returns null when benching is not requested (fail-open), leaving
    /// availability unchanged.
    /// </summary>
    private AgentSmokeResult? BenchTransientFaultIfRequested(AgentKind kind, string reason, bool bench)
    {
        if (!bench) return null;
        var result = new AgentSmokeResult(false, $"in-VM probe inconclusive: {reason}", TimeSpan.Zero);
        _availability.MarkSmokeResult(kind, result, SmokeExclusionSource.InVmSmoke, clearsFastFail: false);
        _log.LogWarning(
            "In-VM smoke gate (fail-closed): benched {Agent} on inconclusive probe ({Reason}); will re-probe next sweep/dispatch",
            kind.Value, reason);
        return result;
    }

    private async Task<AgentSmokeResult> RunStepsInSandboxAsync(
        AgentCredential? credential,
        string baselineRef,
        IReadOnlyList<InVmSmokeStep> steps,
        Stopwatch sw,
        CancellationToken ct)
    {
        var spec = BuildSpec(credential, baselineRef);
        await using var sandbox = await _provider.CreateAsync(spec, ct);

        foreach (var step in steps)
        {
            using var stepCts = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.StepTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stepCts.Token);

            var exec = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = step.Argv,
                Stdin = step.Stdin,
            }, linked.Token);

            if (exec.ExitCode != 0)
            {
                sw.Stop();
                var hint = step.FailureHint ?? (step.Argv.Count > 0 ? step.Argv[0] : "step");
                return new AgentSmokeResult(false, $"{hint} (exit {exec.ExitCode})", sw.Elapsed);
            }
        }

        sw.Stop();
        return new AgentSmokeResult(true, null, sw.Elapsed);
    }

    private SandboxSpec BuildSpec(AgentCredential? credential, string baselineRef)
    {
        var mounts = new List<SandboxMount>(credential?.Mounts ?? [])
        {
            new() { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
        };

        var env = new Dictionary<string, string>(credential?.EnvironmentVariables ?? new Dictionary<string, string>());

        return new SandboxSpec
        {
            ImageReference = _opts.ImageReference,
            Mounts = mounts,
            Environment = env,
            Network = new SandboxNetworkPolicy
            {
                AllowedHosts = _opts.AllowedHosts,
                ProfileName = _opts.NetworkProfile,
            },
            Flavor = SandboxProfileFlavor.Headless,
            WorkingDirectory = SandboxConventions.WorkDir,
            TimingPhase = "in-vm-smoke",
            BaselineImageRef = baselineRef == LiveRefSentinel ? null : baselineRef,
        };
    }

    private async Task EmitTransitionEventsAsync(
        AgentKind kind, AgentSmokeResult result, AvailabilityTransition transition)
    {
        if (result.Ok)
            AuditLog.AgentSmokeSucceeded(kind, result.Duration);
        else
            AuditLog.AgentSmokeFailed(kind, result.FailureReason, result.Duration);

        await EmitTransitionWebhookAsync(kind, result, transition);
    }

    private async Task EmitTransitionWebhookAsync(
        AgentKind kind, AgentSmokeResult result, AvailabilityTransition transition)
    {
        if (!transition.PreviouslyExcluded && transition.NowExcluded)
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.smoke_failed",
                Details = new AgentSmokeFailedDetails
                {
                    AgentKind = kind.Value,
                    Reason = result.FailureReason,
                },
            }, CancellationToken.None);
        }
        else if (transition.PreviouslyExcluded && !transition.NowExcluded)
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.smoke_recovered",
                Details = new AgentSmokeFailedDetails
                {
                    AgentKind = kind.Value,
                    Reason = null,
                },
            }, CancellationToken.None);
        }
    }
}
