using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Runs credential smoke tests for each registered agent at orchestrator
/// startup. Failure is <b>non-fatal</b>: the orchestrator starts regardless,
/// but <c>agent.smoke_failed</c> audit and webhook events are emitted so
/// monitoring catches stale credentials early.
///
/// Probes run concurrently in the background so they do not extend the host
/// startup time. Each probe is bounded by
/// <see cref="SmokeOptions.StartupTimeoutSeconds"/>.
/// </summary>
public sealed class StartupSmokeProbeService : IHostedService
{
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyList<IAgentSmokeProbe> _probes;
    private readonly IWebhookDispatcher _webhooks;
    private readonly SmokeOptionsSnapshot _opts;
    private readonly ILogger<StartupSmokeProbeService> _log;
    private readonly ISmokeAvailabilityRegistry? _availability;
    private readonly InVmSmokeOptions? _inVmSmokeOptions;

    // Exposed for test awaiting — callers can await this after StartAsync
    // to know when all background probes have completed.
    internal Task StartupTask { get; private set; } = Task.CompletedTask;

    public StartupSmokeProbeService(
        ICredentialProvider credentials,
        IEnumerable<IAgentSmokeProbe> probes,
        IWebhookDispatcher webhooks,
        SmokeOptions opts,
        ILogger<StartupSmokeProbeService> log,
        ISmokeAvailabilityRegistry? availability = null,
        InVmSmokeOptions? inVmSmokeOptions = null)
        : this(credentials, probes, webhooks, new SmokeOptionsSnapshot(opts), log, availability, inVmSmokeOptions)
    {
    }

    public StartupSmokeProbeService(
        ICredentialProvider credentials,
        IEnumerable<IAgentSmokeProbe> probes,
        IWebhookDispatcher webhooks,
        SmokeOptionsSnapshot opts,
        ILogger<StartupSmokeProbeService> log,
        ISmokeAvailabilityRegistry? availability = null,
        InVmSmokeOptions? inVmSmokeOptions = null)
    {
        _credentials = credentials;
        _probes = probes.ToList();
        _webhooks = webhooks;
        _opts = opts;
        _log = log;
        _availability = availability;
        _inVmSmokeOptions = inVmSmokeOptions;
    }

    public Task StartAsync(CancellationToken ct)
    {
        LogEffectivePosture();

        var opts = _opts.Current;
        if (!opts.Enabled || _probes.Count == 0)
            return Task.CompletedTask;

        // Fire and forget — startup probes must not block the host from starting.
        StartupTask = Task.Run(() => RunAllAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task RunAllAsync(CancellationToken ct)
    {
        var tasks = _probes.Select(p => ProbeOneAsync(p, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProbeOneAsync(IAgentSmokeProbe probe, CancellationToken ct)
    {
        if (!_opts.Enabled)
            return;

        AgentCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(probe.Kind, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Startup smoke: could not resolve credential for {Agent}", probe.Kind.Value);
            return;
        }

        if (credential is null)
        {
            _log.LogDebug("Startup smoke: no credential configured for {Agent}; skipping", probe.Kind.Value);
            return;
        }

        AgentSmokeResult result;
        try
        {
            using var perProbeCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_opts.Current.StartupTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, perProbeCts.Token);
            result = await probe.SmokeTestAsync(credential, linked.Token);
        }
        catch (OperationCanceledException)
        {
            result = new AgentSmokeResult(false, "timeout", TimeSpan.Zero, SmokeFailureCategory.Transient);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Startup smoke probe for {Agent} threw", probe.Kind.Value);
            result = new AgentSmokeResult(
                false, "transient: try later", TimeSpan.Zero, SmokeFailureCategory.Transient);
        }

        _availability?.MarkSmokeResult(probe.Kind, result);

        if (result.Ok)
        {
            AuditLog.AgentSmokeSucceeded(probe.Kind, result.Duration);
        }
        else
        {
            _log.LogWarning(
                "Startup smoke: agent {Agent} credential check failed ({Category}): {Reason}",
                probe.Kind.Value, result.Category, result.FailureReason);
            AuditLog.AgentSmokeFailed(probe.Kind, result.FailureReason, result.Duration, result.Category);

            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.smoke_failed",
                Details = new AgentSmokeFailedDetails
                {
                    AgentKind = probe.Kind.Value,
                    Reason = result.FailureReason,
                    Category = result.Category,
                },
            }, CancellationToken.None);
        }
    }

    private void LogEffectivePosture()
    {
        var smoke = _opts.Current;
        var inVmConfigured = _inVmSmokeOptions?.Enabled;
        var inVmEffective = smoke.Enabled && inVmConfigured == true;
        _log.LogInformation(
            "Smoke posture: CodeyBox:Smoke:Enabled={SmokeEnabled}; credential startup probes={StartupProbes}; " +
            "credential pickup gate={CredentialPickupGate}; router smoke exclusions={RouterSmokeExclusions}; " +
            "in-VM smoke gate={InVmSmokeGate}; CodeyBox:Smoke:InVm:Enabled={InVmConfigured}",
            smoke.Enabled,
            smoke.Enabled && _probes.Count > 0,
            smoke.Enabled,
            smoke.Enabled,
            inVmEffective,
            inVmConfigured);

        if (smoke.Enabled && inVmConfigured == false)
        {
            _log.LogWarning(
                "Smoke posture partial: CodeyBox:Smoke:InVm:Enabled=false disables only in-VM smoke. " +
                "Credential smoke and router smoke exclusions remain active; set CodeyBox:Smoke:Enabled=false to disable all smoke gates.");
        }
    }
}
