using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IQuotaRetryNotifier
{
    Task NotifyQuotaFailureAsync(WorkItem item);
}

public interface IQuotaResetResolver
{
    Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct);
}

public interface IQuotaWaitParker
{
    Task<DateTimeOffset> ResolveResetAtAsync(
        WorkItem item,
        Project? project,
        DateTimeOffset? detectedResetAt,
        CancellationToken ct = default);

    Task ParkAsync(QuotaWaitParkRequest request, CancellationToken ct = default);
}

public sealed record QuotaWaitParkRequest(
    WorkItem Item,
    string Reason,
    string Phase,
    DateTimeOffset? QuotaResetAt,
    Project? Project = null,
    int? Iteration = null);

public sealed class QuotaWaitParker : IQuotaWaitParker
{
    private readonly IWorkItemStore _store;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IQuotaRetryNotifier? _retryNotifier;
    private readonly IProjectRepository? _projects;
    private readonly IQuotaResetResolver? _resetResolver;
    private readonly ILogger<QuotaWaitParker> _log;
    private readonly TimeProvider _time;

    internal static readonly TimeSpan DefaultQuotaFailurePause = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxParsedQuotaResetWindow = TimeSpan.FromHours(24);

    public QuotaWaitParker(
        IWorkItemStore store,
        IWebhookDispatcher? webhooks = null,
        IQuotaRetryNotifier? retryNotifier = null,
        IProjectRepository? projects = null,
        IQuotaResetResolver? resetResolver = null,
        ILogger<QuotaWaitParker>? log = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _webhooks = webhooks;
        _retryNotifier = retryNotifier;
        _projects = projects;
        _resetResolver = resetResolver;
        _log = log ?? NullLogger<QuotaWaitParker>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<DateTimeOffset> ResolveResetAtAsync(
        WorkItem item,
        Project? project,
        DateTimeOffset? detectedResetAt,
        CancellationToken ct = default)
    {
        var resetAt = ClampQuotaReset(detectedResetAt, _time);
        if (resetAt is not null)
            return resetAt.Value;

        if (_resetResolver is not null)
        {
            try
            {
                var effectiveProject = project;
                if (effectiveProject is null && _projects is not null)
                    effectiveProject = await _projects.GetAsync(item.ProjectId, ct);

                resetAt = await _resetResolver.ComputeEarliestExhaustedResetAsync(item, effectiveProject, ct);
                if (resetAt is not null)
                    return resetAt.Value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Failed to compute quota reset fallback for failed work item {Id}",
                    item.Id);
            }
        }

        return _time.GetUtcNow().Add(DefaultQuotaFailurePause);
    }

    public async Task ParkAsync(QuotaWaitParkRequest request, CancellationToken ct = default)
    {
        var expectedState = request.Item.State;
        if (WorkItemDependencies.TerminalStates.Contains(expectedState))
        {
            _log.LogInformation(
                "Work item {Id} is already terminal ({State}); skipping WaitingForQuotaReset transition",
                request.Item.Id,
                expectedState);
            return;
        }

        var current = await _store.GetAsync(request.Item.Id, ct);
        if (current is null)
        {
            _log.LogInformation(
                "Work item {Id} no longer exists; skipping WaitingForQuotaReset transition",
                request.Item.Id);
            return;
        }

        if (current.State != expectedState)
        {
            _log.LogInformation(
                "Work item {Id} state changed from {ExpectedState} to {CurrentState}; skipping WaitingForQuotaReset transition",
                request.Item.Id,
                expectedState,
                current.State);
            return;
        }

        var effectiveResetAt = await ResolveResetAtAsync(
            current,
            request.Project,
            request.QuotaResetAt,
            ct);

        var next = current.With(
            WorkItemState.WaitingForQuotaReset,
            request.Reason,
            failureKind: "quota",
            quotaResetAt: effectiveResetAt) with
        {
            NextQuotaRetryAt = effectiveResetAt,
            QuotaRetryFrom = RetryFromForQuotaPhase(request.Phase),
        };

        var updated = await _store.TryUpdateIfStateAsync(next, expectedState, ct);
        if (!updated)
        {
            _log.LogInformation(
                "Work item {Id} state changed concurrently; skipping WaitingForQuotaReset transition",
                request.Item.Id);
            return;
        }

        AuditLog.WorkItemTransitioned(request.Item.Id, WorkItemState.WaitingForQuotaReset.ToString());

        if (_retryNotifier is not null)
            await _retryNotifier.NotifyQuotaFailureAsync(next);

        if (_webhooks is not null)
        {
            var effectiveProject = request.Project ?? new Project
            {
                Id = request.Item.ProjectId,
                DisplayName = request.Item.ProjectId.Value,
                RepositoryUrl = string.Empty,
            };

            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.waiting_for_quota_reset",
                WorkItem = next,
                Project = effectiveProject,
                Details = new AgentFallbackDetails(
                    WorkItemId: request.Item.Id.ToString(),
                    Phase: request.Phase,
                    Iteration: request.Iteration,
                    FromAgent: (request.Item.Agent ?? effectiveProject.DefaultAgent).Value,
                    FromModel: request.Item.ModelId,
                    ToAgent: null,
                    ToModel: null,
                    Reason: request.Reason),
            }, ct);
        }
    }

    internal static DateTimeOffset? ClampQuotaReset(DateTimeOffset? resetAt, TimeProvider? timeProvider = null)
    {
        if (resetAt is null)
            return null;

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var ceiling = now + MaxParsedQuotaResetWindow;
        if (resetAt.Value <= ceiling)
            return resetAt.Value;
        return ceiling;
    }

    private static string RetryFromForQuotaPhase(string phase) => phase switch
    {
        "audit" => "audit",
        "merge" => "merge",
        "upstream" => "upstream",
        _ => "work",
    };
}
