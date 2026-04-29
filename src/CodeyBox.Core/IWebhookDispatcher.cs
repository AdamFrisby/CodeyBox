namespace CodeyBox.Core;

/// <summary>
/// Dispatches webhook events to configured endpoints. Implementations are
/// responsible for delivery; the caller fire-and-forgets and does not retry.
/// A null-object implementation is used when no webhook endpoints are configured.
/// </summary>
public interface IWebhookDispatcher
{
    /// <summary>
    /// Fires an event to all registered webhook endpoints.
    /// Implementations should not throw on delivery failure.
    /// </summary>
    Task DispatchAsync(string eventName, object payload, CancellationToken ct = default);
}

/// <summary>No-op dispatcher used when no webhook endpoints are configured.</summary>
public sealed class NullWebhookDispatcher : IWebhookDispatcher
{
    public static readonly NullWebhookDispatcher Instance = new();

    public Task DispatchAsync(string eventName, object payload, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Payload for the <c>work_item.upstream_pushing</c> event.</summary>
public sealed record UpstreamPushingPayload
{
    public required string WorkItemId { get; init; }
    public required string ProjectId { get; init; }
}

/// <summary>Payload for the <c>work_item.pull_request_opened</c> event.</summary>
public sealed record PullRequestOpenedPayload
{
    public required string WorkItemId { get; init; }
    public required string ProjectId { get; init; }
    public required string WorkBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required int PullRequestNumber { get; init; }
    public required string PullRequestUrl { get; init; }
}

/// <summary>Payload for the <c>work_item.done</c> event.</summary>
public sealed record WorkItemDonePayload
{
    public required string WorkItemId { get; init; }
    public required string ProjectId { get; init; }
}
