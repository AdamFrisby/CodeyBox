using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true while a bake-capable sandbox provider reports no usable
/// baseline (consecutive baseline-bake failures). Backed by the provider's
/// <see cref="IBaselineProvisioningBlockedStatusProvider"/> snapshot so the
/// alert clears as soon as a bake succeeds — no restart required. The rules
/// engine's edge-trigger + rule cooldown rate-limits the loud alert while
/// the condition stays true.
/// </summary>
public sealed class BaselineProvisioningBlockedCondition : ICondition, IDisposable
{
    public const string ConditionId = "baseline_provisioning_blocked";

    private readonly IBaselineProvisioningBlockedStatusProvider? _statusSource;

    public string Id => ConditionId;

    public BaselineProvisioningBlockedCondition(IBaselineProvisioningBlockedStatusProvider? statusSource)
    {
        _statusSource = statusSource;
    }

    public Task<bool> EvaluateAsync(CancellationToken ct) =>
        Task.FromResult(_statusSource?.GetProvisioningBlockedStatus() is not null);

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the baseline_provisioning_blocked condition.
/// </summary>
public sealed class BaselineProvisioningBlockedNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => BaselineProvisioningBlockedCondition.ConditionId;

    private readonly IBaselineProvisioningBlockedStatusProvider? _statusSource;

    public BaselineProvisioningBlockedNotificationBuilder(IBaselineProvisioningBlockedStatusProvider? statusSource)
    {
        _statusSource = statusSource;
    }

    public Notification Build(DateTimeOffset evaluatedAt)
    {
        var status = _statusSource?.GetProvisioningBlockedStatus();
        var since = status?.BlockedSince.ToString("R") ?? evaluatedAt.ToString("R");
        var cause = string.IsNullOrWhiteSpace(status?.LastBakeCause) ? "(unknown bake failure)" : status!.LastBakeCause;
        var failures = status?.ConsecutiveFailures.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1";
        return new Notification
        {
            ConditionId = BaselineProvisioningBlockedCondition.ConditionId,
            Title = "No usable baseline — all provisioning blocked",
            Summary = $"No usable baseline; all provisioning blocked since {since}; last bake failure: {cause}",
            Body = $"At {evaluatedAt:R}, no usable baseline exists and all sandbox provisioning is blocked since {since} " +
                   $"({failures} consecutive bake failure(s)). Last bake failure: {cause}. " +
                   "Fix the bake failure (see sandbox.provisioning_deferred events for the sanitized cause), then provisioning resumes automatically.",
            Severity = NotificationSeverity.Critical,
            Timestamp = evaluatedAt,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["blockedSince"] = since,
                ["consecutiveFailures"] = failures,
                ["lastBakeCause"] = cause,
                ["provider"] = status?.Provider ?? "unknown",
                ["baseline"] = status?.BaselineName ?? "unknown",
            },
        };
    }
}
