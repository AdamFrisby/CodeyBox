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
/// <para><b>Caching (AC#2 / AC#3).</b> Each result is cached by
/// <c>(agent, baselineRef)</c>. A cache hit provisions nothing, so steady-state
/// sweeps are free; a baseline rebake changes the content-hash ref and the next
/// sweep re-probes against the new image.</para>
///
/// <para><b>What excludes vs. what is transient.</b> Only a <em>clean</em>
/// negative signal — a step that exits non-zero, or whose output is missing the
/// expected marker — excludes an agent. Provisioning failures, exec
/// exceptions, and step timeouts are treated as transient infrastructure
/// problems: they are logged and skipped without mutating availability or the
/// cache, so a flaky host never wrongly benches a working agent.</para>
/// </summary>
public sealed class InVmSmokeProber
{
    private const string LiveRefSentinel = "live";

    private readonly ISandboxProvider _provider;
    private readonly IBaselineImageResolver _resolver;
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyList<IInVmSmokeProbe> _probes;
    private readonly AgentAvailabilityRegistry _availability;
    private readonly IInVmSmokeCache _cache;
    private readonly IWebhookDispatcher _webhooks;
    private readonly InVmSmokeOptions _opts;
    private readonly ILogger<InVmSmokeProber> _log;

    public InVmSmokeProber(
        ISandboxProvider provider,
        IBaselineImageResolver resolver,
        ICredentialProvider credentials,
        IEnumerable<IInVmSmokeProbe> probes,
        AgentAvailabilityRegistry availability,
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

        var baselineRef = _resolver.ResolveBaselineRef(_opts.NetworkProfile, SandboxProfileFlavor.Headless)
            ?? LiveRefSentinel;

        foreach (var probe in _probes)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await ProbeAgentAsync(probe, baselineRef, ct);
            }
            catch (Exception ex)
            {
                // Defensive: a probe must never take down the sweep for the rest.
                _log.LogDebug(ex, "In-VM smoke: probe for {Agent} threw; treating as transient", probe.Kind.Value);
            }
        }
    }

    /// <summary>
    /// Probes one agent. Returns the result that was applied, or null when the
    /// probe was skipped (cache hit, no credential, or a transient failure that
    /// must not change availability).
    /// </summary>
    internal async Task<AgentSmokeResult?> ProbeAgentAsync(
        IInVmSmokeProbe probe, string baselineRef, CancellationToken ct)
    {
        if (_cache.TryGet(probe.Kind, baselineRef) is not null)
        {
            _log.LogDebug("In-VM smoke: cache hit for {Agent} @ {Ref}", probe.Kind.Value, baselineRef);
            return null;
        }

        AgentCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(probe.Kind, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "In-VM smoke: could not resolve credential for {Agent}", probe.Kind.Value);
            return null;
        }
        if (credential is null)
        {
            _log.LogDebug("In-VM smoke: no credential configured for {Agent}; skipping", probe.Kind.Value);
            return null;
        }

        var steps = probe.BuildSteps(credential);
        if (steps.Count == 0) return null;

        var sw = Stopwatch.StartNew();
        AgentSmokeResult result;
        try
        {
            result = await RunStepsInSandboxAsync(probe.Kind, credential, baselineRef, steps, sw, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A step timed out — treat as transient infra, not an agent fault.
            _log.LogWarning("In-VM smoke for {Agent}: timed out; treating as transient", probe.Kind.Value);
            return null;
        }
        catch (Exception ex)
        {
            // Provisioning or exec error — the host/provider is unhealthy, not
            // the agent CLI. Do not exclude; let the next sweep retry.
            _log.LogWarning(ex, "In-VM smoke for {Agent}: provisioning/exec failed; treating as transient", probe.Kind.Value);
            return null;
        }

        _cache.Set(probe.Kind, baselineRef, result);
        var transition = _availability.MarkSmokeResult(probe.Kind, result);
        await EmitTransitionEventsAsync(probe.Kind, result, transition);
        return result;
    }

    private async Task<AgentSmokeResult> RunStepsInSandboxAsync(
        AgentKind kind,
        AgentCredential credential,
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

    private SandboxSpec BuildSpec(AgentCredential credential, string baselineRef)
    {
        var mounts = new List<SandboxMount>(credential.Mounts)
        {
            new() { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
        };

        var env = new Dictionary<string, string>(credential.EnvironmentVariables);

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
