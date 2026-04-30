namespace CodeyBox.Core;

/// <summary>
/// Dispatches pipeline events to configured webhook endpoints. Constructed
/// by the orchestrator with a <see cref="WebhookEvent"/> and called fire-and-
/// forget — implementations are responsible for delivery and retries and
/// should not throw on failure.
/// </summary>
public interface IWebhookDispatcher
{
    Task PublishAsync(WebhookEvent evt, CancellationToken ct);
}

/// <summary>
/// Details payload for the <c>agent.smoke_failed</c> event, fired when a
/// credential smoke test fails at startup or at work-item pickup.
/// </summary>
public sealed record AgentSmokeFailedDetails
{
    public required string AgentKind { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Details payload for the <c>work_item.pull_request_opened</c> event,
/// surfaced via <see cref="WebhookEvent.Details"/>.
/// </summary>
public sealed record PullRequestOpenedDetails
{
    public required string WorkBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required int PullRequestNumber { get; init; }
    public required string PullRequestUrl { get; init; }
    public string? MergedSha { get; init; }
}
