using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public interface IWorkItemTerminalTransition
{
    Task<WorkItemTerminalTransitionResult> TransitionFailedAsync(
        WorkItem item,
        string error,
        WorkItemTerminalFailureTransitionCommand command,
        CancellationToken ct);
}

public interface IWorkItemTerminalRevisionBuilder
{
    Task<TerminalRevisionAttribution?> BuildTerminalRevisionAsync(WorkItem item, CancellationToken ct);
}

public sealed record WorkItemTerminalFailureTransitionCommand
{
    public string? FailureKind { get; init; }
    public WorkItemAuthFailureScope? AuthFailureScope { get; init; }

    /// <summary>
    /// Failed agent attribution. When set, the transition rewrites
    /// <see cref="WorkItem.Agent"/> to this value and clears
    /// <see cref="WorkItem.AgentInstanceId"/> if the prior instance belonged to
    /// a different agent.
    /// </summary>
    public AgentKind? Agent { get; init; }

    /// <summary>
    /// Clears <see cref="WorkItem.Agent"/> and <see cref="WorkItem.AgentInstanceId"/>.
    /// Takes precedence over <see cref="Agent"/> when both are set.
    /// </summary>
    public bool ClearAgent { get; init; }
    public DateTimeOffset? QuotaResetAt { get; init; }
    public string? CancellationSource { get; init; }
    public IReadOnlyCollection<WorkItemState>? ExpectedStates { get; init; }
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }
    public WorkItemTransientRetryExhaustion? TransientRetryExhaustion { get; init; }
}

public sealed record WorkItemTerminalTransitionResult(
    bool Updated,
    WorkItem? FailedWorkItem,
    WorkItem? CurrentWorkItem);

public sealed record WorkItemTransientRetryExhaustion(
    string Reason,
    DateTimeOffset? FirstFailedAt);

public sealed record WorkItemTransientRetryExhaustedDetails(
    string workItemId,
    string? failureKind,
    string reason,
    int transientRetryAttempts);

/// <summary>
/// Shared terminal-failure state transition and event attribution path.
/// Pipeline workers and retry schedulers both route through this service so
/// terminal webhooks get identical revision fields and audit-log behavior.
/// </summary>
public sealed class WorkItemTerminalTransition : IWorkItemTerminalTransition, IWorkItemTerminalRevisionBuilder
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

    async Task<TerminalRevisionAttribution?> IWorkItemTerminalRevisionBuilder.BuildTerminalRevisionAsync(
        WorkItem item,
        CancellationToken ct)
        => await BuildTerminalRevisionCoreAsync(item, ct);

    private async Task<TerminalRevisionAttribution?> BuildTerminalRevisionCoreAsync(WorkItem item, CancellationToken ct)
    {
        if (!WorkItemDependencies.TerminalStates.Contains(item.State))
            return null;

        var iterations = await _store.GetIterationsAsync(item.Id, ct);
        int? lastDispatched = iterations.Count == 0
            ? null
            : iterations.OrderByDescending(i => i.Iteration).First().PromptRevisionAtDispatch;

        return new TerminalRevisionAttribution(
            PromptRevision: item.PromptRevision,
            RevisionAtCompletion: lastDispatched,
            RevisionMatches: lastDispatched is { } r ? r == item.PromptRevision : null);
    }

    public async Task<WorkItemTerminalTransitionResult> TransitionFailedAsync(
        WorkItem item,
        string error,
        WorkItemTerminalFailureTransitionCommand command,
        CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        if (command.ExpectedStates is not null && !command.ExpectedStates.Contains(current.State))
        {
            return new WorkItemTerminalTransitionResult(
                Updated: false,
                FailedWorkItem: null,
                CurrentWorkItem: current);
        }

        if (command.ExpectedUpdatedAt is { } expectedUpdatedAt
            && current.UpdatedAt != expectedUpdatedAt)
        {
            return new WorkItemTerminalTransitionResult(
                Updated: false,
                FailedWorkItem: null,
                CurrentWorkItem: current);
        }

        var attributed = command.ClearAgent
            ? current with
            {
                Agent = null,
                AgentInstanceId = null,
            }
            : command.Agent is { } agent
            ? current with
            {
                Agent = agent,
                AgentInstanceId = current.Agent == agent ? current.AgentInstanceId : null,
            }
            : current;

        var failed = attributed.With(
            WorkItemState.Failed,
            error,
            failureKind: command.FailureKind,
            quotaResetAt: command.QuotaResetAt,
            cancellationSource: command.CancellationSource,
            authFailureScope: command.AuthFailureScope);

        if (string.Equals(command.FailureKind, "quota", StringComparison.OrdinalIgnoreCase))
        {
            failed = failed with { NextQuotaRetryAt = command.QuotaResetAt };
        }

        if (command.TransientRetryExhaustion is { } transientRetryExhaustion)
        {
            failed = failed with
            {
                NextTransientRetryAt = null,
                TransientRetryFirstFailedAt =
                    transientRetryExhaustion.FirstFailedAt ?? failed.TransientRetryFirstFailedAt,
            };
        }

        var updated = command.ExpectedUpdatedAt is { } expectedForUpdate
            ? await _store.TryUpdateIfStateAndUpdatedAtAsync(failed, current.State, expectedForUpdate, ct)
            : await _store.TryUpdateIfStateAsync(failed, current.State, ct);
        if (!updated)
        {
            return new WorkItemTerminalTransitionResult(
                Updated: false,
                FailedWorkItem: null,
                CurrentWorkItem: current);
        }

        AuditLog.WorkItemFailed(failed.Id, error);
        await PublishFailedAsync(failed, command, ct);

        return new WorkItemTerminalTransitionResult(
            Updated: true,
            FailedWorkItem: failed,
            CurrentWorkItem: current);
    }

    private async Task PublishFailedAsync(
        WorkItem failed,
        WorkItemTerminalFailureTransitionCommand command,
        CancellationToken ct)
    {
        if (_webhooks is null)
            return;

        try
        {
            await PublishFailedCoreAsync(failed, command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        WorkItemTerminalFailureTransitionCommand command,
        CancellationToken ct)
    {
        var project = await ResolveProjectAsync(failed, ct);
        var revision = await BuildTerminalRevisionCoreAsync(failed, ct);
        await _webhooks!.PublishAsync(new WebhookEvent
        {
            Event = "work_item.failed",
            WorkItem = failed,
            Project = project,
            PromptRevision = revision?.PromptRevision,
            RevisionAtCompletion = revision?.RevisionAtCompletion,
            RevisionMatches = revision?.RevisionMatches,
            Details = BuildFailureDetails(failed, command),
        }, ct);
    }

    private static object? BuildFailureDetails(
        WorkItem failed,
        WorkItemTerminalFailureTransitionCommand command)
    {
        return command.TransientRetryExhaustion is { } transientRetryExhaustion
            ? new WorkItemTransientRetryExhaustedDetails(
                failed.Id.ToString(),
                failed.FailureKind,
                transientRetryExhaustion.Reason,
                failed.TransientRetryAttempts)
            : null;
    }

    private async Task<Project?> ResolveProjectAsync(
        WorkItem item,
        CancellationToken ct)
    {
        if (_projects is not null)
        {
            try
            {
                return await _projects.GetAsync(item.ProjectId, ct)
                    ?? FallbackProject(item);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Failed to resolve project {ProjectId} for terminal failure webhook; using fallback project shape",
                    item.ProjectId);
            }
        }

        return FallbackProject(item);
    }

    private static Project FallbackProject(WorkItem item) => new()
    {
        Id = item.ProjectId,
        DisplayName = item.ProjectId.Value,
        RepositoryUrl = string.Empty,
    };
}

/// <summary>
/// Domain value describing whether a terminal work item completed against the
/// prompt revision that was current when its last iteration was dispatched.
/// The values are published as top-level terminal webhook fields (see
/// <see cref="WebhookEvent.PromptRevision"/> et al.) so external trackers can
/// distinguish fresh completions from stale-prompt completions.
/// </summary>
public sealed record TerminalRevisionAttribution(
    int PromptRevision,
    int? RevisionAtCompletion,
    bool? RevisionMatches);
