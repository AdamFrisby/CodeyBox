using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when the orchestrator has made no state transitions for
/// a configurable number of minutes. Reads the shared
/// <see cref="OrchestratorProgressClock"/> singleton.
/// </summary>
public sealed class OrchestratorStallCondition : ICondition, IDisposable
{
    private readonly OrchestratorProgressClock _clock;
    private readonly TimeSpan _stallThreshold;

    public string Id => "orchestrator_stall";

    public OrchestratorStallCondition(OrchestratorProgressClock clock, TimeSpan stallThreshold)
    {
        _clock = clock;
        _stallThreshold = stallThreshold;
    }

    public Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var lastTransition = _clock.LastTransition;
        if (lastTransition == DateTimeOffset.MinValue)
            return Task.FromResult(false);

        var elapsed = DateTimeOffset.UtcNow - lastTransition;
        return Task.FromResult(elapsed >= _stallThreshold);
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the orchestrator_stall condition.
/// </summary>
public sealed class OrchestratorStallNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => "orchestrator_stall";

    private readonly TimeSpan _stallThreshold;

    public OrchestratorStallNotificationBuilder(TimeSpan stallThreshold)
    {
        _stallThreshold = stallThreshold;
    }

    public Notification Build(DateTimeOffset evaluatedAt) => new()
    {
        ConditionId = "orchestrator_stall",
        Title = $"Orchestrator stalled — no progress for {_stallThreshold.TotalMinutes:F0} min",
        Summary = "The orchestrator has not made any state transitions within the configured stall threshold.",
        Body = $"At {evaluatedAt:R}, the orchestrator had made no state transitions for " +
               $"{_stallThreshold.TotalMinutes:F0} minutes (configured threshold). " +
               "This may indicate a deadlock, resource exhaustion, or host issue. " +
               "Check the admin dashboard and host metrics.",
        Severity = NotificationSeverity.Critical,
        Timestamp = evaluatedAt,
        Fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stallThresholdMinutes"] = _stallThreshold.TotalMinutes.ToString("F0"),
        },
    };
}
