using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.QuotaResetNotifier;

/// <summary>
/// Scheduled notifier for the quota reset-optimality advisor (3/5). Runs on
/// the orchestrator's <see cref="IMetricSampler"/> loop: each tick it asks the
/// advisor (implemented by the statistics plugin) for spend advice per watched
/// agent and, when the advice flips to <c>shouldSpend=true</c>, publishes a
/// <c>quota.reset_optimal</c> webhook event through the host's existing
/// <see cref="IWebhookDispatcher"/> — which applies the configured HMAC
/// signature on delivery. Report-only: it never triggers a reset itself.
///
/// <para>De-duplication: the plugin pings once per optimal window per agent. A
/// spend verdict for an already-pinged window is never re-sent, and verdicts
/// inside <see cref="QuotaResetNotifierOptions.Cooldown"/> of the last ping
/// are suppressed so a flapping deadline cannot page the operator every tick.
/// See <see cref="ResetOptimalNotifyPolicy"/> for the exact rule.</para>
///
/// <para>Degrades gracefully: when the statistics plugin is absent there is no
/// advisor and the tick is a no-op (logged at Debug); when the credit
/// estimator is absent the payload's credit count is null rather than failing
/// the notification.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Quota Reset Notifier",
    minHostApiVersion: "1.2")]
public sealed class QuotaResetNotifierPlugin
    : IMetricSampler, IPluginInitializer, IAsyncDisposable
{
    public const string PluginId = "codeybox.quota-reset-notifier";
    public const string SamplerKind = "quota-reset-notifier";

    private readonly IResetOptimalityAdvisor? _advisor;
    private readonly IResetCreditExpiryEstimator? _estimator;
    private readonly IWebhookDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    private IConfigurationSection? _scopedConfig;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private IDisposable? _configChangeRegistration;

    private readonly object _stateLock = new();
    private QuotaResetNotifierOptions _options = new();
    private readonly Dictionary<string, ResetOptimalNotifiedState> _notifiedByAgent =
        new(StringComparer.OrdinalIgnoreCase);

    public QuotaResetNotifierPlugin(
        IEnumerable<IResetOptimalityAdvisor> advisors,
        IWebhookDispatcher dispatcher,
        IConfiguration configuration,
        IEnumerable<IResetCreditExpiryEstimator>? estimators = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(advisors);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(configuration);
        _advisor = advisors.FirstOrDefault();
        _estimator = estimators?.FirstOrDefault();
        _dispatcher = dispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Kind => SamplerKind;

    /// <inheritdoc/>
    public TimeSpan Interval
    {
        get { lock (_stateLock) return _options.Interval; }
    }

    /// <inheritdoc/>
    public bool Enabled
    {
        get { lock (_stateLock) return _options.Enabled; }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        _scopedConfig = context.ScopedConfig;
        ReloadOptions();

        _configChangeRegistration = Microsoft.Extensions.Primitives.ChangeToken.OnChange(
            () => _scopedConfig!.GetReloadToken(),
            () =>
            {
                try
                {
                    ReloadOptions();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Quota reset notifier: hot-reload of options failed");
                }
            });

        QuotaResetNotifierOptions snapshot;
        lock (_stateLock) snapshot = _options;
        _logger.LogInformation(
            "Quota reset notifier initialised: enabled={Enabled}, interval={IntervalSeconds}s, agents=[{Agents}], cooldown={CooldownSeconds}s, advisorPresent={AdvisorPresent}",
            snapshot.Enabled,
            (int)snapshot.Interval.TotalSeconds,
            string.Join(",", snapshot.Agents),
            (int)snapshot.Cooldown.TotalSeconds,
            _advisor is not null);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SampleOnceAsync(CancellationToken ct)
    {
        QuotaResetNotifierOptions options;
        lock (_stateLock) options = _options;

        if (!options.Enabled)
            return;

        if (_advisor is null)
        {
            _logger.LogDebug("Quota reset notifier: no reset-optimality advisor registered (statistics plugin absent), skipping tick");
            return;
        }

        if (options.Agents.Count == 0)
        {
            _logger.LogDebug("Quota reset notifier: no agents watched, skipping tick");
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var agents = options.Agents.Take(QuotaResetNotifierOptions.MaxAgentsPerTick).ToList();
        if (agents.Count < options.Agents.Count)
            _logger.LogWarning("Quota reset notifier: watching {Watched} of {Configured} configured agents (cap); narrow the Agents list", agents.Count, options.Agents.Count);

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();
            await CheckAgentOnceAsync(agent, options, now, ct);
        }
    }

    private async Task CheckAgentOnceAsync(
        string agent, QuotaResetNotifierOptions options, DateTimeOffset now, CancellationToken ct)
    {
        ResetSpendAdvice advice;
        try
        {
            advice = await _advisor!.AdviseAsync(new ResetAdviceRequest { Agent = agent }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota reset notifier: advice query for {Agent} threw; skipping until next tick", agent);
            return;
        }

        if (!advice.ShouldSpend)
            return;

        ResetOptimalNotifiedState? last;
        lock (_stateLock) _notifiedByAgent.TryGetValue(agent, out last);

        if (!ResetOptimalNotifyPolicy.ShouldNotify(advice, last, now, options.Cooldown))
            return;

        var bankedCredits = await TryReadBankedCreditsAsync(agent, ct);
        var details = QuotaResetOptimalDetails.FromAdvice(advice, bankedCredits);
        var evt = new WebhookEvent
        {
            Event = QuotaResetOptimalEvents.ResetOptimal,
            OccurredAt = now,
            EmittedAt = now,
            Details = details,
        };

        try
        {
            await _dispatcher.PublishAsync(evt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota reset notifier: webhook publish for {Agent} threw; will retry on the next optimal tick", agent);
            return;
        }

        lock (_stateLock)
        {
            _notifiedByAgent[agent] = new ResetOptimalNotifiedState
            {
                Agent = advice.Agent,
                OptimalUntil = details.OptimalUntil,
                NotifiedAt = now,
            };
        }

        _logger.LogInformation(
            "Quota reset notifier: published {Event} for {Agent} (reason={Reason}, optimalUntil={OptimalUntil:O})",
            QuotaResetOptimalEvents.ResetOptimal,
            advice.Agent,
            advice.Reason,
            details.OptimalUntil);
    }

    private async Task<int?> TryReadBankedCreditsAsync(string agent, CancellationToken ct)
    {
        if (_estimator is null)
            return null;

        try
        {
            var report = await _estimator.EstimateAsync(new ResetCreditExpiryQuery { Agent = agent }, ct);
            return report.Credits.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Quota reset notifier: credit-count query for {Agent} threw; publishing without a count", agent);
            return null;
        }
    }

    private void ReloadOptions()
    {
        var section = _scopedConfig;
        var next = section is null
            ? new QuotaResetNotifierOptions()
            : QuotaResetNotifierOptions.FromConfiguration(section);

        lock (_stateLock)
        {
            _options = next;
        }
    }

    public ValueTask DisposeAsync()
    {
        _configChangeRegistration?.Dispose();
        _configChangeRegistration = null;
        return ValueTask.CompletedTask;
    }
}
