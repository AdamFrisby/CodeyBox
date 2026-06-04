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
/// problems; how they are handled depends on the call path. Typed provisioning
/// deferrals are allowed to bubble from the dispatch gate so the orchestrator can
/// requeue the work item instead of converting host provisioning exhaustion into
/// agent unavailability. On the background
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
    private readonly ISandboxProvider _provider;
    private readonly IBaselineImageResolver _resolver;
    private readonly IBaselineImageProvisioner _baselineProvisioner;
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyList<IInVmSmokeProbe> _probes;
    private readonly ISmokeAvailabilityRegistry _availability;
    private readonly IInVmSmokeCache _cache;
    private readonly IWebhookDispatcher _webhooks;
    private readonly InVmSmokeOptions _opts;
    private readonly SmokeOptionsSnapshot? _smokeOptions;
    private readonly ILogger<InVmSmokeProber> _log;

    public InVmSmokeProber(
        ISandboxProvider provider,
        IBaselineImageResolver resolver,
        IBaselineImageProvisioner baselineProvisioner,
        ICredentialProvider credentials,
        IEnumerable<IInVmSmokeProbe> probes,
        ISmokeAvailabilityRegistry availability,
        IInVmSmokeCache cache,
        IWebhookDispatcher webhooks,
        InVmSmokeOptions opts,
        ILogger<InVmSmokeProber> log,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        _provider = provider;
        _resolver = resolver;
        _baselineProvisioner = baselineProvisioner;
        _credentials = credentials;
        _probes = probes.ToList();
        _availability = availability;
        _cache = cache;
        _webhooks = webhooks;
        _opts = opts;
        _smokeOptions = smokeOptions;
        _log = log;
    }

    public bool Enabled => (_smokeOptions?.Enabled ?? true) && _opts.Enabled && _probes.Count > 0;

    /// <summary>
    /// Probes every registered agent against the active baseline. Sequential so
    /// the sweep never holds more than one probe VM at a time. Never throws.
    /// </summary>
    public async Task ProbeAllAsync(CancellationToken ct)
    {
        if (!TryGetConfiguredTarget(out var target))
        {
            if (Enabled)
                _log.LogWarning(
                    "In-VM smoke sweep skipped: no explicit Smoke:InVm:NetworkProfile is configured and no project target was supplied");
            return;
        }

        await ProbeAllAsync(target, ct);
    }

    /// <summary>
    /// Probes every registered agent against the active baseline for the
    /// resolved dispatch target. Sequential so the sweep never holds more than
    /// one probe VM at a time. Never throws.
    /// </summary>
    public async Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct)
    {
        if (!Enabled) return;

        foreach (var probe in _probes)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await ProbeAgentAsync(probe, target, baselineRef: null, ct);
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

    private bool TryGetConfiguredTarget(out InVmSmokeSandboxTarget target)
    {
        if (!string.IsNullOrWhiteSpace(_opts.NetworkProfile))
        {
            target = new InVmSmokeSandboxTarget(_opts.NetworkProfile, SandboxProfileFlavor.Headless);
            return true;
        }

        target = default;
        return false;
    }

    /// <summary>
    /// <see cref="IInVmSmokeGate.ForceProbeAsync"/>. Operator recovery path: force
    /// a re-probe of a single agent regardless of its current exclusion (the admin
    /// endpoint calls this after a fix), so an in-VM bench is cleared here rather
    /// than only by the background sweep. Never throws. Returns null when disabled
    /// or no probe is registered for the kind so the admin endpoint can fall back
    /// to the host-probe verdict for its response / 404 decision.
    ///
    /// <para>Bypasses the cache: the operator triggers this <em>after</em> fixing
    /// a CLI, so replaying a verdict captured before the fix (a stale cached pass
    /// for the same ref within TTL, or a stale fail) would defeat the recovery.
    /// It always re-execs the in-VM sequence and reconciles the fresh verdict.</para>
    /// </summary>
    public async Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
    {
        if (!Enabled) return null;
        if (_probes.All(p => p.Kind != kind)) return null;
        if (!TryGetConfiguredTarget(out var target))
        {
            _log.LogWarning(
                "In-VM smoke force-probe for {Agent} skipped: no explicit Smoke:InVm:NetworkProfile is configured",
                kind.Value);
            return null;
        }

        await EnsureProbedAsync(kind, target, ct, bypassCache: true);
        return _availability.GetAvailability(kind);
    }

    /// <summary>
    /// <see cref="IInVmSmokeGate.EnsureAvailableAsync"/>. Owns the full
    /// read→probe→re-read sequence so routing consumers get a verdict from this
    /// one call. Returns the smoke-disabled effective availability when the
    /// master smoke switch is off, the agent's prior availability untouched
    /// when only the in-VM gate is disabled, and otherwise probes and returns
    /// the reconciled availability. A cache hit is free.
    /// <paramref name="target"/> carries the sandbox profile/flavor and optional
    /// pinned baseline ref; the probe runs against the image the dispatch will
    /// actually clone, not just whatever baseline is active now.
    ///
    /// <para>When the agent is already excluded we normally short-circuit — no
    /// point probing a binary the router will skip regardless. But in-VM verdicts
    /// are cached per <c>(agent, baselineRef)</c> while registry exclusions are
    /// <em>global</em> per agent, so a failure probed against one baseline (e.g. a
    /// rebaked active image) benches the agent for every baseline. That must not
    /// permanently strand work pinned to a <em>different</em>, known-good baseline:
    /// if this work item's pinned ref has its own cached pass, we fall through to
    /// (re-)probe — a free cache hit that reconciles the pinned-image verdict back
    /// onto the registry — rather than returning the unrelated active-image bench
    /// (B1 pinning contract). With no positive per-ref evidence we honour the
    /// global bench: provisioning a fresh VM for an agent benched everywhere else
    /// would defeat the short-circuit and risk exhausting sandbox slots on the hot
    /// path, and a never-probed pinned image is no evidence the CLI works there.</para>
    /// </summary>
    public async Task<AgentAvailability> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        if (_smokeOptions?.Enabled == false)
            return _availability.GetAvailabilityWithoutSmokeGateExclusions(kind);

        var current = _availability.GetAvailability(kind);
        if (!Enabled)
            return current;
        var cacheBaselineRef = target.BaselineRef ?? TryResolveBaselineRef(target);
        if (!current.Available &&
            (cacheBaselineRef is null || _cache.TryGet(kind, cacheBaselineRef) is null))
            return current;
        await EnsureProbedAsync(kind, target, ct);
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
    /// <paramref name="baselineRef"/> is the work item's pinned baseline ref; the
    /// probe (and its cache key) target that exact image so a pass proves the CLI
    /// on the pinned image, not on a freshly rebaked active baseline. Null falls
    /// back to the active baseline for unpinned work.
    /// </summary>
    internal Task EnsureProbedAsync(AgentKind kind, string? baselineRef, CancellationToken ct, bool bypassCache = false)
    {
        if (!Enabled) return Task.CompletedTask;
        if (!TryGetConfiguredTarget(out var target))
        {
            BenchTransientFaultIfRequested(
                kind,
                "baseline target has no network profile",
                _opts.FailClosedOnProbeFault);
            return Task.CompletedTask;
        }

        return EnsureProbedAsync(kind, target.WithBaselineRef(baselineRef), ct, bypassCache);
    }

    internal async Task EnsureProbedAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct,
        bool bypassCache = false)
    {
        if (!Enabled) return;
        var probe = _probes.FirstOrDefault(p => p.Kind == kind);
        if (probe is null) return;

        try
        {
            // On the dispatch gate, honour the operator's probe-fault policy:
            // fail-closed temporarily benches the agent when the probe can't run
            // to a verdict (see InVmSmokeOptions.FailClosedOnProbeFault). Probe
            // the pinned baseline when supplied so the verdict matches the image
            // the dispatch will clone; otherwise fall back to the active baseline.
            var probeTask = ProbeAgentAsync(
                probe, target, target.BaselineRef, ct,
                benchOnTransientFault: _opts.FailClosedOnProbeFault, bypassCache: bypassCache);

            // The provisioning/exec/step timeouts inside ProbeAgentAsync cover the
            // expected hangs, but a defect-in-depth deadline guarantees the gate
            // returns a verdict (or fail-closed bench) within a bounded wall-clock
            // even if some inner step those timeouts don't cover (a stuck sandbox
            // DisposeAsync, an unanticipated synchronous hang in a custom probe)
            // would otherwise leave the worker waiting forever. Non-positive
            // disables it (tests with synthetic clocks).
            if (_opts.GateDeadlineSeconds <= 0)
            {
                await probeTask;
                return;
            }

            var gateDeadline = TimeSpan.FromSeconds(_opts.GateDeadlineSeconds);
            var winner = await Task.WhenAny(probeTask, Task.Delay(gateDeadline, ct));
            ct.ThrowIfCancellationRequested();

            if (winner != probeTask)
            {
                // Gate deadline elapsed before the probe produced a verdict.
                // Under fail-closed we bench so the router does not dispatch to
                // an unverified CLI; under fail-open we leave availability
                // unchanged. Either way we return so the worker can continue —
                // NEVER block longer than the deadline, even if the inner probe
                // never returns. The in-flight probeTask is left running and
                // observed for its eventual exception via ContinueWith; its
                // later verdict reconciles via the registry/cache on the next
                // gate call. Log mirrors the actual policy so operators do not
                // see "benching" on a probe the prober deliberately left routable.
                ObserveOrphanedProbe(kind, probeTask);
                _log.LogWarning(
                    "In-VM smoke gate: probe for {Agent} exceeded deadline {Deadline}s; {Action}",
                    kind.Value, _opts.GateDeadlineSeconds,
                    _opts.FailClosedOnProbeFault ? "benching" : "leaving availability unchanged (fail-open)");
                BenchTransientFaultIfRequested(
                    kind, $"probe deadline exceeded ({_opts.GateDeadlineSeconds}s)",
                    _opts.FailClosedOnProbeFault);
                return;
            }

            // Probe finished within the deadline; surface its outcome / exception
            // via the existing catches.
            await probeTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine dispatch/shutdown cancellation must propagate so the
            // router aborts cleanly rather than continuing to route on a
            // half-probed agent. Only step timeouts (ct NOT signalled) are
            // handled as transient inside ProbeAgentAsync; those never reach here.
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The gate runs on the router hot path and must never throw. The
            // expected transient faults are already handled inside ProbeAgentAsync
            // (per the fault policy), so reaching here is an unexpected fault
            // worth surfacing at Warning rather than hiding at Debug. We still
            // swallow the exception so a probe fault cannot take down dispatch,
            // but we must honour the fail-closed policy: an unexpected throw
            // reached no in-VM verdict, so under FailClosedOnProbeFault we bench
            // the agent (never cached → self-heals on the next clean probe)
            // rather than leaving an unverified CLI routable.
            _log.LogWarning(ex, "In-VM smoke gate: probe for {Agent} threw unexpectedly", kind.Value);
            BenchTransientFaultIfRequested(kind, "probe threw unexpectedly", _opts.FailClosedOnProbeFault);
        }
    }

    /// <summary>
    /// Attaches a continuation so a probe task we walked away from (gate deadline
    /// exceeded) doesn't surface as an UnobservedTaskException if it later faults,
    /// and so its eventual verdict is visible in logs for post-hoc diagnosis. The
    /// task itself may still mutate the availability registry on completion;
    /// that's the desired reconciliation path.
    /// </summary>
    private void ObserveOrphanedProbe(AgentKind kind, Task probeTask)
    {
        _ = probeTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _log.LogDebug(
                    t.Exception,
                    "In-VM smoke gate: orphaned probe for {Agent} faulted after deadline",
                    kind.Value);
            else
                _log.LogDebug(
                    "In-VM smoke gate: orphaned probe for {Agent} eventually completed after deadline",
                    kind.Value);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Probes one agent. Returns the result that was applied, or null when a
    /// transient failure (provisioning / exec / timeout) means availability must
    /// not change. A cache hit re-applies the cached <em>passing</em> verdict to
    /// the registry (reconciliation) and returns it without provisioning a VM.
    /// </summary>
    internal async Task<AgentSmokeResult?> ProbeAgentAsync(
        IInVmSmokeProbe probe,
        InVmSmokeSandboxTarget target,
        string? baselineRef,
        CancellationToken ct,
        bool benchOnTransientFault = false,
        bool bypassCache = false)
    {
        string resolvedBaselineRef;
        try
        {
            var targetBaselineRef = ResolveTargetBaselineRef(target, baselineRef);
            if (targetBaselineRef is null)
            {
                _log.LogWarning(
                    "In-VM smoke for {Agent}: no clonable baseline for profile {Profile} / flavor {Flavor}; treating as transient",
                    probe.Kind.Value, target.NetworkProfile ?? "(none)", target.Flavor);
                return BenchTransientFaultIfRequested(
                    probe.Kind,
                    "no clonable baseline for smoke target",
                    benchOnTransientFault);
            }

            resolvedBaselineRef = targetBaselineRef;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "In-VM smoke for {Agent}: baseline warm-up failed for profile {Profile} / flavor {Flavor}; treating as transient",
                probe.Kind.Value, target.NetworkProfile ?? "(none)", target.Flavor);
            return BenchTransientFaultIfRequested(
                probe.Kind,
                "baseline warm-up failed",
                benchOnTransientFault);
        }

        if (!bypassCache && _cache.TryGet(probe.Kind, resolvedBaselineRef) is { } cached)
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
            _log.LogDebug("In-VM smoke: cache hit for {Agent} @ {Ref}", probe.Kind.Value, resolvedBaselineRef);
            var hitTransition = _availability.MarkSmokeResult(
                probe.Kind, cached, SmokeExclusionSource.InVmSmoke, clearsFastFail: false);
            await EmitTransitionWebhookAsync(probe.Kind, cached, hitTransition);
            return cached;
        }

        try
        {
            var readyBaselineRef = await EnsureReadyBaselineRefAsync(target, resolvedBaselineRef, ct);
            if (readyBaselineRef is null)
            {
                _log.LogWarning(
                    "In-VM smoke for {Agent}: no clonable baseline for profile {Profile} / flavor {Flavor}; treating as transient",
                    probe.Kind.Value, target.NetworkProfile ?? "(none)", target.Flavor);
                return BenchTransientFaultIfRequested(
                    probe.Kind,
                    "no clonable baseline for smoke target",
                    benchOnTransientFault);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "In-VM smoke for {Agent}: baseline warm-up failed for profile {Profile} / flavor {Flavor}; treating as transient",
                probe.Kind.Value, target.NetworkProfile ?? "(none)", target.Flavor);
            return BenchTransientFaultIfRequested(
                probe.Kind,
                "baseline warm-up failed",
                benchOnTransientFault);
        }

        AgentCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(probe.Kind, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            result = await RunStepsInSandboxAsync(credential, target, resolvedBaselineRef, steps, sw, ct);
        }
        catch (TimeoutException ex)
        {
            // Provisioning ran past ProvisionTimeoutSeconds — typed exception so
            // the operator signal in audit/logs distinguishes a stuck VM clone
            // from a per-step exec timeout (both are transient infra, but the
            // root cause and remediation differ — clone hangs point at the
            // sandbox host / multipass daemon; per-step hangs point at the CLI).
            _log.LogWarning(ex, "In-VM smoke for {Agent}: provisioning timed out; treating as transient", probe.Kind.Value);
            return BenchTransientFaultIfRequested(probe.Kind, "probe provisioning timed out", benchOnTransientFault);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation (worker / shutdown token fired) is NOT
            // evidence the CLI is broken — propagate so the wrapper's distinct
            // caller-cancellation branch surfaces a real shutdown rather than
            // being swallowed by the catch-all below and converted into a
            // "broken binary" bench. EnsureProbedAsync's outer filter on
            // ct.IsCancellationRequested re-throws to the router so dispatch
            // unwinds cleanly.
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A step timed out — treat as transient infra, not an agent fault.
            _log.LogWarning("In-VM smoke for {Agent}: timed out; treating as transient", probe.Kind.Value);
            return BenchTransientFaultIfRequested(probe.Kind, "probe step timed out", benchOnTransientFault);
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
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
        // dispatch (self-healing) rather than pinned for the whole TTL. A clean
        // failure also invalidates any prior cached pass for this exact ref so a
        // baseline that regressed within the same content-hash + TTL window can't
        // leave a stale pass that a later cache hit reconciles back to Available
        // without re-execing the CLI. Scoped to this ref so a known-good pinned
        // baseline's entry survives (B1 pinning).
        if (result.Ok)
            _cache.Set(probe.Kind, resolvedBaselineRef, result);
        else
            _cache.Invalidate(probe.Kind, resolvedBaselineRef);
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
        var result = new AgentSmokeResult(
            false, $"in-VM probe inconclusive: {reason}", TimeSpan.Zero, SmokeFailureCategory.Transient);
        _availability.MarkSmokeResult(kind, result, SmokeExclusionSource.InVmSmoke, clearsFastFail: false);
        _log.LogWarning(
            "In-VM smoke gate (fail-closed): benched {Agent} on inconclusive probe ({Reason}); will re-probe next sweep/dispatch",
            kind.Value, reason);
        return result;
    }

    private async Task<AgentSmokeResult> RunStepsInSandboxAsync(
        AgentCredential? credential,
        InVmSmokeSandboxTarget target,
        string baselineRef,
        IReadOnlyList<InVmSmokeStep> steps,
        Stopwatch sw,
        CancellationToken ct)
    {
        var spec = BuildSpec(credential, target, baselineRef);
        await using var sandbox = await CreateSandboxWithProvisionTimeoutAsync(spec, ct);

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
                // exit 127 = binary not found (operator must install / fix PATH);
                // any other nonzero exit from a smoke step (e.g. --version
                // returning 1 due to auth failure) is also operator-actionable —
                // the binary IS launching, so the bench is not a network blip.
                return new AgentSmokeResult(
                    false, $"{hint} (exit {exec.ExitCode})", sw.Elapsed, SmokeFailureCategory.Persistent);
            }
        }

        sw.Stop();
        return new AgentSmokeResult(true, null, sw.Elapsed, SmokeFailureCategory.None);
    }

    /// <summary>
    /// Provisions a sandbox under a hard provisioning timeout
    /// (<see cref="InVmSmokeOptions.ProvisionTimeoutSeconds"/>). The per-step
    /// exec timeout cannot bound this — provisioning has to produce a sandbox
    /// before any step runs — so a wedged baseline clone / "multipass launch"
    /// (observed 2026-06-01) would otherwise hang the gate forever. On overrun
    /// we surface a <see cref="TimeoutException"/> rather than the raw OCE so
    /// the caller's transient-fault catch can tag it with a clear reason
    /// ("probe provisioning timed out") distinct from per-step exec timeouts.
    /// Non-positive disables the timeout (tests with synthetic clocks).
    ///
    /// <para>The timeout is a wall-clock bound on the <em>returned Task</em>,
    /// not a cooperative-cancel signal: once <see cref="ISandboxProvider.CreateAsync"/>
    /// has handed back a Task, a wedged multipass daemon (the production hang
    /// this method exists for) cannot stall this method beyond
    /// <paramref name="provisionTimeout"/> — the <see cref="Task.WhenAny(Task[])"/>
    /// race against a wall-clock delay still fires. This does NOT guard against
    /// a provider that blocks synchronously <em>before</em> returning a Task
    /// (we'd be inside the <c>_provider.CreateAsync</c> call and have not yet
    /// armed the timer); the production <c>MultipassSandboxProvider</c> returns
    /// a Task around its CLI waits, so this is not a real boundary, but the
    /// wrapper alone cannot bound a synchronously-blocking provider. The orphaned create task is handed to
    /// <see cref="ObserveOrphanedSandboxCreateAsync"/>, which disposes any
    /// sandbox the provider eventually yields so a late-arriving VM does not
    /// leak. The linked / provisioning CTS pair is disposed before we walk away
    /// from a non-cooperative create — the token's cancelled state is preserved
    /// after dispose (so a cooperative provider still observes the cancel) and
    /// disposing unregisters the linked source from the parent worker token, so
    /// repeated timed-out probes cannot accumulate registrations against
    /// <paramref name="ct"/> for the process lifetime.</para>
    /// </summary>
    private async Task<ISandbox> CreateSandboxWithProvisionTimeoutAsync(
        SandboxSpec spec, CancellationToken ct)
    {
        if (_opts.ProvisionTimeoutSeconds <= 0)
            return await _provider.CreateAsync(spec, ct);

        var provisionTimeout = TimeSpan.FromSeconds(_opts.ProvisionTimeoutSeconds);
        var provisionCts = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, provisionCts.Token);

        // ISandboxProvider.CreateAsync is not required to be an async state
        // machine — it can throw synchronously before returning a Task. Without
        // this guard those throws would leak both CTSes (the try/finally below
        // is only reached once a Task exists), so wrap construction and dispose
        // on a synchronous boundary failure.
        Task<ISandbox> createTask;
        try
        {
            createTask = _provider.CreateAsync(spec, linked.Token);
        }
        catch
        {
            linked.Dispose();
            provisionCts.Dispose();
            throw;
        }

        var timeoutTask = Task.Delay(provisionTimeout);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var winner = await Task.WhenAny(createTask, timeoutTask, cancellationTask);
        if (winner == createTask)
        {
            try
            {
                return await createTask;
            }
            catch (OperationCanceledException) when (provisionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"in-VM smoke: VM provisioning exceeded {(int)provisionTimeout.TotalSeconds}s");
            }
            finally
            {
                linked.Dispose();
                provisionCts.Dispose();
            }
        }

        // Either the wall-clock timeout or the caller's ct fired before
        // CreateAsync returned. In BOTH cases the in-flight task is left
        // running: ownership passes to the orphan observer (it disposes any
        // sandbox the provider eventually yields) and the CTS pair is disposed
        // immediately so we don't keep registrations on the parent worker
        // token alive for a non-cooperative create that may never settle. The
        // CancellationToken state is preserved after dispose, so a cooperative
        // provider still observes cancellation; disposing only unregisters the
        // linked-source callback from ct. We always hand off / dispose BEFORE
        // propagating either cancellation or the timeout exception, so a
        // caller-cancelled provisioning never falls through unobserved.
        provisionCts.Cancel();
        _ = ObserveOrphanedSandboxCreateAsync(createTask);
        linked.Dispose();
        provisionCts.Dispose();

        // Propagate caller cancellation first so a real shutdown is
        // distinguishable from a provisioning hang.
        if (winner == cancellationTask)
            ct.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"in-VM smoke: VM provisioning exceeded {(int)provisionTimeout.TotalSeconds}s");
    }

    /// <summary>
    /// Owns the post-timeout cleanup for a sandbox-create task we walked away
    /// from: disposes the sandbox if the provider eventually returns one (so a
    /// late-arriving VM does not leak) and logs any eventual fault for
    /// diagnosis. The linked / provision CTS pair is owned by the caller and
    /// disposed before this observer is started — we deliberately do NOT keep
    /// CTS references here, since a non-cooperative create may never settle and
    /// holding them would leak registrations on the parent worker token.
    /// </summary>
    private async Task ObserveOrphanedSandboxCreateAsync(Task<ISandbox> createTask)
    {
        try
        {
            var sandbox = await createTask.ConfigureAwait(false);
            try { await sandbox.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogDebug(ex,
                    "In-VM smoke: failed to dispose post-timeout orphaned sandbox");
            }
        }
        catch (OperationCanceledException) { /* expected — we cancelled it */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "In-VM smoke: post-timeout orphaned provisioning eventually faulted");
        }
    }

    private string? TryResolveBaselineRef(InVmSmokeSandboxTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.NetworkProfile))
            return null;

        try
        {
            return _resolver.ResolveBaselineRef(target.NetworkProfile, target.Flavor);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "In-VM smoke: baseline resolver failed for profile {Profile} / flavor {Flavor}",
                target.NetworkProfile, target.Flavor);
            return null;
        }
    }

    private string? ResolveTargetBaselineRef(
        InVmSmokeSandboxTarget target,
        string? pinnedBaselineRef)
    {
        if (string.IsNullOrWhiteSpace(target.NetworkProfile))
            return null;

        var baselineRef = string.IsNullOrWhiteSpace(pinnedBaselineRef)
            ? _resolver.ResolveBaselineRef(target.NetworkProfile, target.Flavor)
            : pinnedBaselineRef;
        if (string.IsNullOrWhiteSpace(baselineRef))
            return null;

        return baselineRef;
    }

    private async Task<string?> EnsureReadyBaselineRefAsync(
        InVmSmokeSandboxTarget target,
        string baselineRef,
        CancellationToken ct)
    {
        var ensured = await _baselineProvisioner.EnsureBaselineImageAsync(
            target.NetworkProfile!,
            target.Flavor,
            baselineRef,
            ct);

        return string.IsNullOrWhiteSpace(ensured) ? null : baselineRef;
    }

    private SandboxSpec BuildSpec(
        AgentCredential? credential,
        InVmSmokeSandboxTarget target,
        string baselineRef)
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
                ProfileName = target.NetworkProfile,
            },
            Flavor = target.Flavor,
            WorkingDirectory = SandboxConventions.WorkDir,
            TimingPhase = "in-vm-smoke",
            BaselineImageRef = baselineRef,
        };
    }

    private async Task EmitTransitionEventsAsync(
        AgentKind kind, AgentSmokeResult result, AvailabilityTransition transition)
    {
        if (result.Ok)
            AuditLog.AgentSmokeSucceeded(kind, result.Duration);
        else
            AuditLog.AgentSmokeFailed(kind, result.FailureReason, result.Duration, result.Category);

        await EmitTransitionWebhookAsync(kind, result, transition);
    }

    private async Task EmitTransitionWebhookAsync(
        AgentKind kind, AgentSmokeResult result, AvailabilityTransition transition)
    {
        // Webhook emission is a side-effect of an already-recorded verdict, never
        // an input to it. The availability registry was mutated before we got
        // here (MarkSmokeResult / BenchTransientFaultIfRequested), so a publish
        // fault must not propagate: on the dispatch gate it would surface in
        // EnsureProbedAsync's catch and, under FailClosedOnProbeFault, bench an
        // agent whose probe just passed — overwriting a fresh pass with an
        // inconclusive failure. Isolate it like the sweep path: log and continue.
        try
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
                        Category = result.Category,
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
                        Category = SmokeFailureCategory.None,
                    },
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "In-VM smoke: failed to publish availability-transition webhook for {Agent}", kind.Value);
        }
    }
}
