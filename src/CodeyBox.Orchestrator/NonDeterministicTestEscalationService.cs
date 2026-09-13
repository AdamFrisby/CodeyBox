using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of a NotDiffAttributable escalation attempt.
/// </summary>
public sealed record FlakeEscalationResult(
    bool Escalated,
    WorkItemId? ChildId = null,
    bool ReusedExisting = false,
    IReadOnlyList<string>? FlakyTests = null)
{
    public static FlakeEscalationResult NotEscalated { get; } = new(false);
}

/// <summary>
/// Spawns an isolated base-branch fix item for audit failures classified as
/// NotDiffAttributable, then parks the parent on a dependsOn gate so the
/// audit loop does not burn rework iterations on a failure the diff did not
/// cause. De-duplicates on the fully-qualified test-name set: an open
/// fix-task for the same tests is reused instead of spawning a duplicate.
/// </summary>
public sealed class NonDeterministicTestEscalationService
{
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue? _queue;
    private readonly NonDeterministicTestEscalationSnapshot? _options;
    private readonly TimeProvider _time;
    private readonly ILogger<NonDeterministicTestEscalationService> _log;

    public NonDeterministicTestEscalationService(
        IWorkItemStore store,
        ITaskQueue? queue = null,
        NonDeterministicTestEscalationSnapshot? options = null,
        TimeProvider? timeProvider = null,
        ILogger<NonDeterministicTestEscalationService>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLogger<NonDeterministicTestEscalationService>.Instance;
    }

    /// <summary>
    /// Attempts escalation for <paramref name="parent"/> given the audit
    /// iteration's <paramref name="attributions"/>. Returns an escalated
    /// result (with the child id) when the parent was parked, or
    /// <see cref="FlakeEscalationResult.NotEscalated"/> when attribution
    /// yields nothing actionable, the switch is off, or the parent cannot be
    /// parked. Never throws for store/queue faults: failures return
    /// NotEscalated so the audit loop falls back to normal rework.
    /// </summary>
    public async Task<FlakeEscalationResult> TryEscalateAsync(
        WorkItem parent,
        IReadOnlyList<TestFailureAttributionResult>? attributions,
        string baseBranch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var opts = _options?.Current ?? new NonDeterministicTestEscalationOptions();
        if (!opts.Enabled)
            return FlakeEscalationResult.NotEscalated;

        var tests = NonDeterministicTestEscalationPolicy.SelectActionableTests(
            attributions, opts.MaxTestsPerChild);
        if (tests.Count == 0)
            return FlakeEscalationResult.NotEscalated;

        try
        {
            return await EscalateCoreAsync(parent, tests, baseBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Flake escalation for work item {Id} failed; falling back to normal rework",
                parent.Id);
            return FlakeEscalationResult.NotEscalated;
        }
    }

    private async Task<FlakeEscalationResult> EscalateCoreAsync(
        WorkItem parent,
        IReadOnlyList<string> tests,
        string baseBranch,
        CancellationToken ct)
    {
        var allItems = new List<WorkItem>();
        await foreach (var item in _store.ListAsync(ct).ConfigureAwait(false))
            allItems.Add(item);

        var current = allItems.FirstOrDefault(i => i.Id == parent.Id) ?? parent;
        if (WorkItemDependencies.TerminalStates.Contains(current.State))
            return FlakeEscalationResult.NotEscalated;

        var dedupKey = NonDeterministicTestEscalationPolicy.ComputeDedupKey(tests);
        var existing = NonDeterministicTestEscalationPolicy.FindExistingFixTask(
            allItems, current.ProjectId, dedupKey);

        WorkItemId childId;
        var reused = false;
        if (existing is not null && existing.Id != current.Id)
        {
            var cycle = WorkItemDependencies.FindCycle(current.Id, [.. current.DependsOn, existing.Id], allItems);
            if (cycle is not null)
            {
                _log.LogWarning(
                    "Flake escalation for work item {Id} reuses no child: dependsOn {Child} would cycle ({Cycle})",
                    current.Id, existing.Id, cycle);
                return FlakeEscalationResult.NotEscalated;
            }
            childId = existing.Id;
            reused = true;
            _log.LogInformation(
                "Work item {Id} reusing open flake-fix item {Child} for {Count} test(s)",
                current.Id, childId, tests.Count);
        }
        else
        {
            var now = _time.GetUtcNow();
            var child = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = current.ProjectId,
                Title = NonDeterministicTestEscalationPolicy.BuildChildTitle(tests.Count, dedupKey),
                Prompt = NonDeterministicTestEscalationPolicy.BuildChildPrompt(
                    tests.Count, dedupKey, baseBranch, current.Title, current.Id),
                BaseBranch = string.IsNullOrWhiteSpace(current.BaseBranch) ? baseBranch : current.BaseBranch,
                DependsOn = [],
                QueuePosition = now.Ticks,
                Initiator = current.Initiator,
                ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [NonDeterministicTestEscalationPolicy.FixMarkerNamespace] = dedupKey,
                },
            };
            await _store.CreateAsync(child, ct).ConfigureAwait(false);
            AuditLog.WorkItemCreated(child.Id, child.ProjectId, child.Title, child.Initiator);
            _log.LogInformation(
                "Work item {Id} spawned flake-fix item {Child} for {Count} test(s)",
                current.Id, child.Id, tests.Count);
            childId = child.Id;
            allItems.Add(child);
            if (_queue is not null)
                await _queue.EnqueueAsync(childId, ct).ConfigureAwait(false);
        }

        if (current.DependsOn.Contains(childId))
            return new FlakeEscalationResult(true, childId, reused, tests);

        var newDeps = new List<WorkItemId>(current.DependsOn.Count + 1);
        newDeps.AddRange(current.DependsOn);
        newDeps.Add(childId);
        var cycleCheck = WorkItemDependencies.FindCycle(current.Id, newDeps, allItems);
        if (cycleCheck is not null)
        {
            _log.LogWarning(
                "Flake escalation for work item {Id} not parking: dependsOn {Child} would cycle ({Cycle})",
                current.Id, childId, cycleCheck);
            return FlakeEscalationResult.NotEscalated;
        }

        var update = await _store.UpdateDependsOnAsync(
            current.Id, newDeps, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (update.Outcome != DependsOnUpdateOutcome.Updated)
        {
            _log.LogWarning(
                "Flake escalation for work item {Id} could not park parent (outcome {Outcome}); falling back to rework",
                current.Id, update.Outcome);
            return FlakeEscalationResult.NotEscalated;
        }

        AuditLog.WorkItemDependenciesChanged(current.Id, update.OldDependsOn ?? [], newDeps);

        var parked = (update.Item ?? current) with
        {
            State = WorkItemState.Queued,
            UpdatedAt = _time.GetUtcNow(),
        };
        await _store.UpdateAsync(parked, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Work item {Id} parked on flake-fix item {Child}; audit resumes when it reaches Done",
            current.Id, childId);
        AuditLog.WorkItemTransitioned(current.Id, WorkItemState.Queued.ToString());

        return new FlakeEscalationResult(true, childId, reused, tests);
    }
}
