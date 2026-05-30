using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Notifications;

/// <summary>
/// Periodically evaluates configured notification rules and dispatches
/// to matching providers. Implements edge-triggered semantics:
/// <list type="bullet">
/// <item>Fire once when a condition transitions from false to true.</item>
/// <item>Suppress while the condition remains true.</item>
/// <item>Clear when the condition returns to false, allowing re-fire later.</item>
/// <item>Cooldown between fires even after re-trigger.</item>
/// </list>
/// </summary>
public sealed class NotificationRulesEngine : BackgroundService
{
    private readonly IOptionsMonitor<NotificationsOptions> _optsMonitor;
    private readonly IReadOnlyDictionary<string, ICondition> _conditions;
    private readonly IReadOnlyDictionary<string, INotificationBuilder> _builders;
    private readonly IReadOnlyDictionary<string, INotificationProvider> _providers;
    private readonly ILogger<NotificationRulesEngine> _log;

    // Per-condition state for edge-trigger + cooldown tracking.
    // conditionId -> (isCurrentlyActive, lastFireTime)
    private readonly ConcurrentDictionary<string, (bool Active, DateTimeOffset LastFired)> _state = new(StringComparer.Ordinal);
    private readonly TimeSpan _sweepInterval;

    public NotificationRulesEngine(
        IOptionsMonitor<NotificationsOptions> optsMonitor,
        IEnumerable<ICondition> conditions,
        IEnumerable<INotificationBuilder> builders,
        IEnumerable<INotificationProvider> providers,
        ILogger<NotificationRulesEngine> log,
        TimeSpan? sweepInterval = null)
    {
        _optsMonitor = optsMonitor;
        _log = log;

        _conditions = conditions.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _builders = builders.ToDictionary(
            b => (b as IConditionAwareBuilder)?.ConditionId ?? InferConditionId(b),
            StringComparer.Ordinal);
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        _sweepInterval = sweepInterval ?? TimeSpan.FromSeconds(15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PrimeInitialStateAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_sweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunSweepAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Evaluates every configured condition and captures its current state
    /// so the first real sweep fires only on edge transitions.
    /// </summary>
    internal async Task PrimeInitialStateAsync(CancellationToken ct)
    {
        var opts = _optsMonitor.CurrentValue;
        foreach (var rule in opts.Rules)
        {
            if (string.IsNullOrEmpty(rule.Condition)) continue;
            if (_conditions.TryGetValue(rule.Condition, out var condition))
            {
                try
                {
                    var active = await condition.EvaluateAsync(ct);
                    _state[rule.Condition] = (active, DateTimeOffset.MinValue);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Notifications: failed to evaluate initial state for condition {Condition}", rule.Condition);
                    _state[rule.Condition] = (false, DateTimeOffset.MinValue);
                }
            }
        }
    }

    /// <summary>
    /// Runs one evaluation sweep: evaluates every condition, applies
    /// edge-trigger + cooldown logic, and fires matching notifications.
    /// </summary>
    internal async Task RunSweepAsync(CancellationToken ct)
    {
        var currentOpts = _optsMonitor.CurrentValue;
        if (!currentOpts.Enabled || currentOpts.Rules.Count == 0)
            return;

        foreach (var rule in currentOpts.Rules)
        {
            if (string.IsNullOrEmpty(rule.Condition)) continue;
            if (!_conditions.TryGetValue(rule.Condition, out var condition))
                continue;

            var state = _state.GetOrAdd(rule.Condition, _ => (false, DateTimeOffset.MinValue));
            bool currentlyActive;
            try
            {
                currentlyActive = await condition.EvaluateAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Notifications: failed to evaluate condition {Condition}", rule.Condition);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var cooldown = GetCooldown(rule.Condition);

            if (currentlyActive && !state.Active)
            {
                if (cooldown > TimeSpan.Zero && state.LastFired != DateTimeOffset.MinValue)
                {
                    var sinceLastFire = now - state.LastFired;
                    if (sinceLastFire < cooldown)
                    {
                        _state[rule.Condition] = (true, state.LastFired);
                        continue;
                    }
                }

                try
                {
                    await FireAsync(rule.Condition, now, GetProviderNames(rule.Condition), ct);
                    _state[rule.Condition] = (true, now);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Notifications: dispatch failed for condition {Condition}", rule.Condition);
                }
            }
            else if (!currentlyActive && state.Active)
            {
                _state[rule.Condition] = (false, state.LastFired);
            }
            else
            {
                _state[rule.Condition] = (currentlyActive, state.LastFired);
            }
        }
    }

    private async Task FireAsync(
        string conditionId,
        DateTimeOffset now,
        IReadOnlyList<string> providerNames,
        CancellationToken ct)
    {
        if (!_builders.TryGetValue(conditionId, out var builder))
        {
            _log.LogWarning("Notifications: no builder registered for condition {Condition}", conditionId);
            return;
        }

        var notification = builder.Build(now);

        var opts = _optsMonitor.CurrentValue;
        IReadOnlyList<string>? recipients = null;
        foreach (var rule in opts.Rules)
        {
            if (!string.Equals(rule.Condition, conditionId, StringComparison.Ordinal)) continue;
            if (rule.Severity is not null
                && Enum.TryParse<NotificationSeverity>(rule.Severity, ignoreCase: true, out var sev))
            {
                notification = notification with { Severity = sev };
            }
            if (rule.Recipients is { Count: > 0 })
                recipients = rule.Recipients;
        }
        notification = notification with { Recipients = recipients };

        _log.LogInformation("Notifications: firing condition {Condition} ({Severity})",
            conditionId, notification.Severity);

        foreach (var name in providerNames)
        {
            if (!_providers.TryGetValue(name, out var provider))
            {
                _log.LogWarning("Notifications: provider '{Provider}' not registered (condition {Condition})",
                    name, conditionId);
                continue;
            }

            try
            {
                await provider.SendAsync(notification, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Notifications: provider '{Provider}' failed for condition {Condition}",
                    name, conditionId);
            }
        }
    }

    private TimeSpan GetCooldown(string conditionId)
    {
        var rule = _optsMonitor.CurrentValue.Rules
            .FirstOrDefault(r => string.Equals(r.Condition, conditionId, StringComparison.Ordinal));
        if (rule is null) return TimeSpan.Zero;
        return TimeSpan.TryParse(rule.Cooldown, out var cd) ? cd : TimeSpan.Zero;
    }

    private IReadOnlyList<string> GetProviderNames(string conditionId)
    {
        return _optsMonitor.CurrentValue.Rules
            .Where(r => string.Equals(r.Condition, conditionId, StringComparison.Ordinal))
            .SelectMany(r => r.Providers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string InferConditionId(INotificationBuilder builder)
    {
        if (builder is IConditionAwareBuilder aware)
            return aware.ConditionId;

        var name = builder.GetType().Name;
        if (name.EndsWith("NotificationBuilder", StringComparison.Ordinal))
            name = name[..^"NotificationBuilder".Length];
        return CamelToSnake(name);
    }

    private static string CamelToSnake(string name)
    {
        var chars = new List<char>(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                chars.Add('_');
            chars.Add(char.ToLowerInvariant(name[i]));
        }
        return new string(chars.ToArray());
    }
}

public interface IConditionAwareBuilder
{
    string ConditionId { get; }
}
