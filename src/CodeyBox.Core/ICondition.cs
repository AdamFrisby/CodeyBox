namespace CodeyBox.Core;

/// <summary>
/// Evaluates a named condition against the current system state. Conditions
/// return true when the condition is currently active (e.g. "queue is empty").
/// Implementations must be thread-safe and should be fast — conditions are
/// evaluated on a periodic sweep.
/// </summary>
public interface ICondition
{
    /// <summary>Stable condition identifier, e.g. "queue_empty", "all_quotas_exhausted".</summary>
    string Id { get; }

    /// <summary>Evaluates the condition against current system state.</summary>
    Task<bool> EvaluateAsync(CancellationToken ct);
}

/// <summary>
/// Produces a <see cref="Notification"/> when an <see cref="ICondition"/>
/// evaluates to true. Each condition has a companion builder so the
/// notification body can include structured detail from the evaluation.
/// </summary>
public interface INotificationBuilder
{
    /// <summary>Build a notification for a condition activation.</summary>
    Notification Build(DateTimeOffset evaluatedAt);
}
