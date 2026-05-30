using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when a leaked sandbox is detected by the reaper.
/// Transient — fires per new leak batch and clears on next evaluation.
/// </summary>
public sealed class SandboxLeakReapedCondition : ICondition, IDisposable
{
    private readonly LeakDetectionSink _sink;
    private long _lastKnownCount;

    public string Id => "sandbox_leak_reaped";

    public SandboxLeakReapedCondition(LeakDetectionSink sink)
    {
        _sink = sink;
        _lastKnownCount = sink.DetectionCount;
    }

    public Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var current = _sink.DetectionCount;
        var last = Interlocked.Read(ref _lastKnownCount);
        if (current > last)
        {
            Interlocked.Exchange(ref _lastKnownCount, current);
            return Task.FromResult(true);
        }

        Interlocked.Exchange(ref _lastKnownCount, current);
        return Task.FromResult(false);
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the sandbox_leak_reaped condition.
/// </summary>
public sealed class SandboxLeakReapedNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => "sandbox_leak_reaped";

    public Notification Build(DateTimeOffset evaluatedAt) => new()
    {
        ConditionId = "sandbox_leak_reaped",
        Title = "Sandbox leak detected",
        Summary = "One or more leaked sandboxes were detected by the leak reaper.",
        Body = $"At {evaluatedAt:R}, the sandbox leak reaper detected one or more " +
               "orphaned sandboxes. Check GET /sandboxes/leaked for details. " +
               "If AutoDispose is enabled, the reaper will automatically dispose them.",
        Severity = NotificationSeverity.Warning,
        Timestamp = evaluatedAt,
    };
}
