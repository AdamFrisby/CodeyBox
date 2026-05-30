using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when the orchestrator has made no state transitions for
/// a configurable number of minutes. Reads the shared
/// <see cref="OrchestratorProgressClock"/> singleton and obtains the
/// stall threshold from the current <see cref="NotificationRuleOptions"/>
/// via <c>IOptionsMonitor</c> so operators can adjust it without restart.
/// </summary>
public sealed class OrchestratorStallCondition : ICondition, IDisposable
{
    private readonly OrchestratorProgressClock _clock;
    private readonly IOptionsMonitor<NotificationsOptions> _optsMonitor;

    public string Id => "orchestrator_stall";

    public OrchestratorStallCondition(OrchestratorProgressClock clock, IOptionsMonitor<NotificationsOptions> optsMonitor)
    {
        _clock = clock;
        _optsMonitor = optsMonitor;
    }

    public Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var lastTransition = _clock.LastTransition;
        if (lastTransition == DateTimeOffset.MinValue)
            return Task.FromResult(false);

        var threshold = GetStallThreshold();
        var elapsed = DateTimeOffset.UtcNow - lastTransition;
        return Task.FromResult(elapsed >= threshold);
    }

    private TimeSpan GetStallThreshold()
    {
        var rule = _optsMonitor.CurrentValue.Rules
            .FirstOrDefault(r => string.Equals(r.Condition, Id, StringComparison.Ordinal));
        return TimeSpan.FromMinutes(rule?.StallThresholdMinutes ?? 15);
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the orchestrator_stall condition.
/// </summary>
public sealed class OrchestratorStallNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => "orchestrator_stall";

    private readonly IOptionsMonitor<NotificationsOptions> _optsMonitor;

    public OrchestratorStallNotificationBuilder(IOptionsMonitor<NotificationsOptions> optsMonitor)
    {
        _optsMonitor = optsMonitor;
    }

    public Notification Build(DateTimeOffset evaluatedAt)
    {
        var threshold = GetStallThreshold();
        var minutes = threshold.TotalMinutes.ToString("F0");
        return new Notification
        {
            ConditionId = "orchestrator_stall",
            Title = $"Orchestrator stalled — no progress for {minutes} min",
            Summary = "The orchestrator has not made any state transitions within the configured stall threshold.",
            Body = $"At {evaluatedAt:R}, the orchestrator had made no state transitions for " +
                   $"{minutes} minutes (configured threshold). " +
                   "This may indicate a deadlock, resource exhaustion, or host issue. " +
                   "Check the admin dashboard and host metrics.",
            Severity = NotificationSeverity.Critical,
            Timestamp = evaluatedAt,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stallThresholdMinutes"] = minutes,
            },
        };
    }

    private TimeSpan GetStallThreshold()
    {
        var rule = _optsMonitor.CurrentValue.Rules
            .FirstOrDefault(r => string.Equals(r.Condition, ConditionId, StringComparison.Ordinal));
        return TimeSpan.FromMinutes(rule?.StallThresholdMinutes ?? 15);
    }
}
