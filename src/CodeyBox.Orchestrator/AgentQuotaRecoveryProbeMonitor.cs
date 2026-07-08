using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentQuotaRecoveryProbeMonitor : BackgroundService
{
    private static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<AgentQuotaMemberKey, AgentMembership> _tracked = new();
    private readonly IAgentQuotaAvailabilityObservationSource _observations;
    private readonly IAgentQuotaAvailabilityPublisher _publisher;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly IAgentQuotaGate _quotaGate;
    private readonly QuotaRouterOptions _options;
    private readonly ILogger<AgentQuotaRecoveryProbeMonitor> _log;
    private readonly TimeProvider _time;

    public AgentQuotaRecoveryProbeMonitor(
        IAgentQuotaAvailabilityObservationSource observations,
        IAgentQuotaAvailabilityPublisher publisher,
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentQuotaGate quotaGate,
        QuotaRouterOptions options,
        ILogger<AgentQuotaRecoveryProbeMonitor> log,
        TimeProvider? timeProvider = null)
    {
        _observations = observations;
        _publisher = publisher;
        _probesByKind = AgentQuotaProbeCatalog.BuildKindLookup(probes);
        _quotaGate = quotaGate;
        _options = options;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;

        _observations.QuotaUsabilityObserved += OnQuotaUsabilityObserved;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeTrackedMembersOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(ResolveProbeInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quota recovery probe monitor iteration failed; continuing");
                await Task.Delay(ResolveProbeInterval(), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<int> ProbeTrackedMembersOnceAsync(CancellationToken ct = default)
    {
        if (_tracked.IsEmpty)
            return 0;

        var recovered = 0;
        foreach (var (key, member) in _tracked.ToArray())
        {
            ct.ThrowIfCancellationRequested();

            if (member.Billing != AgentBilling.Subscription
                || !_probesByKind.TryGetValue(member.Agent, out var probe))
            {
                _tracked.TryRemove(key, out _);
                continue;
            }

            AgentQuotaSnapshot snapshot;
            try
            {
                if (probe is IAgentQuotaCacheInvalidator invalidator)
                    invalidator.InvalidateResponseCache();

                snapshot = await probe.GetAvailabilityAsync(member, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Quota recovery probe for {Agent}/{Model} failed; member remains tracked",
                    member.Agent.Value,
                    member.ModelId ?? "(default)");
                continue;
            }

            var nowUtc = _time.GetUtcNow();
            if (!snapshot.IsKnown
                || !await _quotaGate.AllowsAsync(member, snapshot, nowUtc, ct).ConfigureAwait(false))
            {
                _publisher.RecordQuotaUsability(
                    member,
                    isUsable: false,
                    publishRecoverySignal: false);
                continue;
            }

            var published = _publisher.RecordQuotaUsability(
                member,
                isUsable: true,
                publishRecoverySignal: true);
            if (!published)
                continue;

            _tracked.TryRemove(key, out _);
            recovered++;
        }

        return recovered;
    }

    private void OnQuotaUsabilityObserved(AgentQuotaUsabilityObservation observation)
    {
        if (!observation.PublishRecoverySignal)
            return;

        var key = AgentQuotaMemberKey.From(observation.Member);
        if (observation.IsUsable)
            _tracked.TryRemove(key, out _);
        else
            _tracked[key] = observation.Member;
    }

    private TimeSpan ResolveProbeInterval()
    {
        var configured = _options.QuotaRecoveryProbeInterval;
        if (configured <= TimeSpan.Zero)
            return DefaultProbeInterval;

        return configured;
    }

    public override void Dispose()
    {
        _observations.QuotaUsabilityObserved -= OnQuotaUsabilityObserved;
        base.Dispose();
    }
}
