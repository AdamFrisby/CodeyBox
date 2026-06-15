using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public interface IWorkItemTerminalTransition
{
    Task<TerminalRevisionDetails?> BuildTerminalRevisionAsync(WorkItem item, CancellationToken ct);

    Task<WorkItemTerminalTransitionResult> TransitionFailedAsync(
        WorkItem item,
        string error,
        WorkItemTerminalFailureTransitionOptions options,
        CancellationToken ct);
}

public sealed record WorkItemTerminalFailureTransitionOptions
{
    public Project? Project { get; init; }
    public string? FailureKind { get; init; }
    public DateTimeOffset? QuotaResetAt { get; init; }
    public string? CancellationSource { get; init; }
    public IReadOnlyCollection<WorkItemState>? ExpectedStates { get; init; }
    public Func<WorkItem, WorkItem>? PrepareFailedItem { get; init; }
    public object? Details { get; init; }
    public Func<WorkItem, object?>? DetailsFactory { get; init; }
    public bool ResolveProjectWhenMissing { get; init; }
    public bool FallbackProjectWhenMissing { get; init; } = true;
    public bool SwallowPublishExceptions { get; init; }
}

public sealed record WorkItemTerminalTransitionResult(
    bool Updated,
    WorkItem? FailedWorkItem,
    WorkItem? CurrentWorkItem);

/// <summary>
/// Shared terminal-failure state transition and event attribution path.
/// Pipeline workers and retry schedulers both route through this service so
/// terminal webhooks get identical revision fields and audit-log behavior.
/// </summary>
public sealed class WorkItemTerminalTransition : IWorkItemTerminalTransition
{
    private readonly IWorkItemStore _store;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IProjectRepository? _projects;
    private readonly ILogger<WorkItemTerminalTransition> _log;

    public WorkItemTerminalTransition(
        IWorkItemStore store,
        IWebhookDispatcher? webhooks,
        IProjectRepository? projects,
        ILogger<WorkItemTerminalTransition> log)
    {
        _store = store;
        _webhooks = webhooks;
        _projects = projects;
        _log = log;
    }

    public async Task<TerminalRevisionDetails?> BuildTerminalRevisionAsync(WorkItem item, CancellationToken ct)
    {
        if (!WorkItemDependencies.TerminalStates.Contains(item.State))
            return null;

        var iterations = await _store.GetIterationsAsync(item.Id, ct);
        int? lastDispatched = iterations.Count == 0
            ? null
            : iterations.OrderByDescending(i => i.Iteration).First().PromptRevisionAtDispatch;

        return new TerminalRevisionDetails(
            PromptRevision: item.PromptRevision,
            RevisionAtCompletion: lastDispatched,
            RevisionMatches: lastDispatched is { } r ? r == item.PromptRevision : null);
    }

    public async Task<WorkItemTerminalTransitionResult> TransitionFailedAsync(
        WorkItem item,
        string error,
        WorkItemTerminalFailureTransitionOptions options,
        CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        if (options.ExpectedStates is not null && !options.ExpectedStates.Contains(current.State))
        {
            return new WorkItemTerminalTransitionResult(
                Updated: false,
                FailedWorkItem: null,
                CurrentWorkItem: current);
        }

        var failed = current.With(
            WorkItemState.Failed,
            error,
            failureKind: options.FailureKind,
            quotaResetAt: options.QuotaResetAt,
            cancellationSource: options.CancellationSource);

        if (string.Equals(options.FailureKind, "quota", StringComparison.OrdinalIgnoreCase))
        {
            failed = failed with { NextQuotaRetryAt = options.QuotaResetAt };
        }

        if (options.PrepareFailedItem is not null)
        {
            failed = options.PrepareFailedItem(failed);
        }

        var updated = await _store.TryUpdateIfStateAsync(failed, current.State, ct);
        if (!updated)
        {
            return new WorkItemTerminalTransitionResult(
                Updated: false,
                FailedWorkItem: null,
                CurrentWorkItem: current);
        }

        AuditLog.WorkItemFailed(failed.Id, error);
        await PublishFailedAsync(failed, options);

        return new WorkItemTerminalTransitionResult(
            Updated: true,
            FailedWorkItem: failed,
            CurrentWorkItem: current);
    }

    private async Task PublishFailedAsync(
        WorkItem failed,
        WorkItemTerminalFailureTransitionOptions options)
    {
        if (_webhooks is null)
            return;

        if (!options.SwallowPublishExceptions)
        {
            await PublishFailedCoreAsync(failed, options);
            return;
        }

        try
        {
            await PublishFailedCoreAsync(failed, options);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Work item {Id} reached terminal Failed state, but failure webhook delivery failed",
                failed.Id);
        }
    }

    private async Task PublishFailedCoreAsync(
        WorkItem failed,
        WorkItemTerminalFailureTransitionOptions options)
    {
        var project = await ResolveProjectAsync(failed, options);
        var revision = await BuildTerminalRevisionAsync(failed, CancellationToken.None);
        await _webhooks!.PublishAsync(new WebhookEvent
        {
            Event = "work_item.failed",
            WorkItem = failed,
            Project = project,
            PromptRevision = revision?.PromptRevision,
            RevisionAtCompletion = revision?.RevisionAtCompletion,
            RevisionMatches = revision?.RevisionMatches,
            Details = options.DetailsFactory?.Invoke(failed) ?? options.Details,
        }, CancellationToken.None);
    }

    private async Task<Project?> ResolveProjectAsync(
        WorkItem item,
        WorkItemTerminalFailureTransitionOptions options)
    {
        if (options.Project is not null)
            return options.Project;

        if (options.ResolveProjectWhenMissing && _projects is not null)
            return await _projects.GetAsync(item.ProjectId, CancellationToken.None);

        return options.FallbackProjectWhenMissing
            ? new Project
            {
                Id = item.ProjectId,
                DisplayName = item.ProjectId.Value,
                RepositoryUrl = string.Empty,
            }
            : null;
    }
}

/// <summary>
/// Carrier for revision-attribution fields lifted onto webhook
/// payloads at terminal-state transitions (Done / Failed / Cancelled /
/// AuditFailed / MergeConflictResolutionFailed). The fields themselves are
/// serialised at the TOP LEVEL of the webhook payload (see
/// <see cref="WebhookEvent.PromptRevision"/> et al.) so trackers like
/// JobTrack can read <c>payload.promptRevision</c> directly; this record is
/// just the in-process plumbing.
/// </summary>
public sealed record TerminalRevisionDetails(
    int PromptRevision,
    int? RevisionAtCompletion,
    bool? RevisionMatches);
