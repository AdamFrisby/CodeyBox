using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodically re-runs every registered <see cref="IAgentSmokeProbe"/> on a
/// fixed cadence and feeds the result back into the
/// <see cref="AgentAvailabilityRegistry"/>. Together with the fast-fail circuit
/// breaker this gives the dispatcher a self-healing path: an agent excluded
/// by either signal automatically rejoins the routing chain as soon as a
/// probe succeeds again.
///
/// <para>The interval is bounded by
/// <see cref="AvailabilityOptions.PeriodicSweepInterval"/>; set it to
/// <see cref="TimeSpan.Zero"/> to disable the sweep entirely. The first sweep
/// fires after one full interval — startup probes (<see cref="StartupSmokeProbeService"/>)
/// already cover boot-time coverage, so re-firing on tick 0 would be redundant.</para>
/// </summary>
public sealed class PeriodicSmokeProbeService : BackgroundService
{
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyList<IAgentSmokeProbe> _probes;
    private readonly IWebhookDispatcher _webhooks;
    private readonly SmokeOptions _smokeOpts;
    private readonly AvailabilityOptions _availOpts;
    private readonly AgentAvailabilityRegistry _availability;
    private readonly ILogger<PeriodicSmokeProbeService> _log;

    public PeriodicSmokeProbeService(
        ICredentialProvider credentials,
        IEnumerable<IAgentSmokeProbe> probes,
        IWebhookDispatcher webhooks,
        SmokeOptions smokeOpts,
        AvailabilityOptions availOpts,
        AgentAvailabilityRegistry availability,
        ILogger<PeriodicSmokeProbeService> log)
    {
        _credentials = credentials;
        _probes = probes.ToList();
        _webhooks = webhooks;
        _smokeOpts = smokeOpts;
        _availOpts = availOpts;
        _availability = availability;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_smokeOpts.Enabled || _probes.Count == 0)
            return;
        if (_availOpts.PeriodicSweepInterval <= TimeSpan.Zero)
            return;

        // Mostly to keep tests deterministic — Task.Delay on a long interval would
        // run the first sweep many minutes in. Tests can shorten the interval.
        var interval = _availOpts.PeriodicSweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            await SweepOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs every registered probe once. Public for tests and for the
    /// <c>/admin/agent/{name}/smoke</c> operator endpoint, which calls into a
    /// per-probe variant.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var tasks = _probes.Select(p => ProbeOneAsync(p, ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Runs the probe registered for <paramref name="kind"/>, if any, and
    /// returns the result. Returns null when no probe is registered.
    /// </summary>
    public async Task<AgentSmokeResult?> ProbeAsync(AgentKind kind, CancellationToken ct)
    {
        var probe = _probes.FirstOrDefault(p => p.Kind == kind);
        if (probe is null) return null;
        return await ProbeOneAsync(probe, ct);
    }

    private async Task<AgentSmokeResult?> ProbeOneAsync(IAgentSmokeProbe probe, CancellationToken ct)
    {
        AgentCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(probe.Kind, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Periodic smoke: could not resolve credential for {Agent}", probe.Kind.Value);
            return null;
        }
        if (credential is null) return null;

        AgentSmokeResult result;
        try
        {
            using var perProbeCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_smokeOpts.StartupTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, perProbeCts.Token);
            result = await probe.SmokeTestAsync(credential, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new AgentSmokeResult(false, "timeout", TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Periodic smoke probe for {Agent} threw", probe.Kind.Value);
            result = new AgentSmokeResult(false, "transient: try later", TimeSpan.Zero);
        }

        var transition = _availability.MarkSmokeResult(probe.Kind, result);
        await EmitTransitionEventsAsync(probe.Kind, result, transition);
        return result;
    }

    private async Task EmitTransitionEventsAsync(
        AgentKind kind, AgentSmokeResult result, AvailabilityTransition transition)
    {
        if (result.Ok)
            AuditLog.AgentSmokeSucceeded(kind, result.Duration);
        else
            AuditLog.AgentSmokeFailed(kind, result.FailureReason, result.Duration);

        // Only emit webhook on edge transitions so repeated steady-state probes
        // don't flood the bus.
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
