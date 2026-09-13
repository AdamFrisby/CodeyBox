using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the delegation-phase contracts: retry-from normalization,
/// quota/transient/pause phase mapping, recovery maps, one-shot trigger
/// semantics on <see cref="WorkItem"/>, the delegation prompt shape, and the
/// sqlite round-trips for delegation fields and events. No sandbox or git.
/// </summary>
public sealed class DelegationUnitTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-delegation-unit-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public void RetryFromPolicy_Delegation_NormalizesAndResumes()
    {
        Assert.Equal(RetryFromPolicy.Delegation, RetryFromPolicy.NormalizeOrWork("delegation"));
        Assert.Equal(RetryFromPolicy.Delegation, RetryFromPolicy.NormalizeOrWork(" Delegation "));
        Assert.True(RetryFromPolicy.TryNormalize("delegation", out var normalized));
        Assert.Equal(RetryFromPolicy.Delegation, normalized);
        Assert.True(RetryFromPolicy.TryGetResumeState(RetryFromPolicy.Delegation, out var resume));
        Assert.Equal(WorkItemState.Delegating, resume);
    }

    [Fact]
    public void QuotaRetryPhasePolicy_Delegation_RoundTrips()
    {
        Assert.Equal("delegation", QuotaRetryPhasePolicy.NormalizePhase("delegation"));
        Assert.Equal(RetryFromPolicy.Delegation, QuotaRetryPhasePolicy.RetryFromForPhase("delegation"));
        Assert.Equal(WorkItemState.Delegating, QuotaRetryPhasePolicy.ResumeStateForRetryFrom("delegation"));
        Assert.Equal(
            (int)WorkItemState.Delegating,
            QuotaRetryPhasePolicy.OrderingStateForQuotaRetryCandidate("delegation", null));
    }

    [Fact]
    public void PipelineRunner_PhaseMappings_CoverDelegation()
    {
        Assert.Equal("delegation", PipelineRunner.PhaseForQuotaPark(WorkItemState.Delegating));
        Assert.Equal(
            RetryFromPolicy.Delegation,
            PipelineRunner.RetryFromForTransientPhase("delegation", WorkItemState.Delegating));
        Assert.Equal(
            RetryFromPolicy.Delegation,
            AgentPauseResumeMapper.RetryFromForState(WorkItemState.Delegating));
    }

    [Fact]
    public void RecoveryPolicy_RecoversDelegatingInPlace()
    {
        Assert.Contains(
            WorkItemState.Delegating,
            (IEnumerable<WorkItemState>)WorkItemRecoveryPolicy.WorkerOccupiedStates);
        Assert.Equal(
            WorkItemState.Delegating,
            WorkItemRecoveryPolicy.MapToRecoveryState(WorkItemState.Delegating));
        Assert.True(WorkItemRecoveryPolicy.HandlesRecoveryState(WorkItemState.Delegating));
        // Delegating -> WorkComplete after a mid-turn recovery counts as real
        // progress, so a stale recovery counter is cleared on the advance.
        var recovered = NewItem() with
        {
            State = WorkItemState.Delegating,
            RecoveryAttempts = 1,
            RecoveryAttemptSourceState = WorkItemState.Delegating,
        };
        var advanced = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
            recovered.With(WorkItemState.WorkComplete),
            WorkItemState.Delegating,
            WorkItemState.WorkComplete);
        Assert.Equal(0, advanced.RecoveryAttempts);
    }

    [Fact]
    public void WorkItem_With_ConsumesOneShotTriggerOffDelegating()
    {
        var item = NewItem() with
        {
            State = WorkItemState.Delegating,
            DelegationRequested = true,
            DelegationReason = "manual retry",
            DelegationAttempts = 1,
        };

        var parked = item.With(WorkItemState.NeedsOperatorInput, "reason");
        Assert.False(parked.DelegationRequested);
        Assert.Equal(1, parked.DelegationAttempts);
        Assert.Equal("manual retry", parked.DelegationReason);

        var staying = item.With(WorkItemState.Delegating);
        Assert.True(staying.DelegationRequested);
    }

    [Fact]
    public void PromptComposer_DelegationPrompt_CombinesBriefWithLatitude()
    {
        var composer = new PromptComposer();
        var prompt = composer.BuildDelegationPrompt("BRIEF: prior failures", "do the thing");

        Assert.Contains("BRIEF: prior failures", prompt);
        Assert.Contains("do the thing", prompt);
        Assert.Contains("latitude", prompt);
        Assert.Contains("do not amend", prompt);

        // Fence runs inside the brief are escaped so they cannot break the
        // quoted-data fences.
        var fenced = composer.BuildDelegationPrompt("has ``` fence", "do the thing");
        Assert.DoesNotContain("has ``` fence", fenced);
        Assert.Contains("has ` ` ` fence", fenced);
    }

    [Fact]
    public async Task SqliteWorkItemStore_RoundTripsDelegationFields()
    {
        var db = Path.Combine(_workspace, "state.db");
        using var store = new SqliteWorkItemStore(db);
        var item = NewItem() with
        {
            State = WorkItemState.Delegating,
            DelegationRequested = true,
            DelegationReason = "manual retry",
            DelegationAttempts = 2,
        };
        await store.CreateAsync(item);

        var loaded = await store.GetAsync(item.Id);
        Assert.NotNull(loaded);
        Assert.True(loaded.DelegationRequested);
        Assert.Equal("manual retry", loaded.DelegationReason);
        Assert.Equal(2, loaded.DelegationAttempts);

        await store.UpdateAsync(loaded.With(WorkItemState.WorkComplete) with { DelegationAttempts = 3 });
        var advanced = await store.GetAsync(item.Id);
        Assert.NotNull(advanced);
        Assert.False(advanced.DelegationRequested);
        Assert.Equal(3, advanced.DelegationAttempts);
    }

    [Fact]
    public async Task SqliteDelegationEventStore_RecordsAndBounds()
    {
        var db = Path.Combine(_workspace, "events.db");
        // The delegation table references work_items: the work item store
        // owns the schema lead, mirroring production registration order.
        using var workItems = new SqliteWorkItemStore(db);
        var workItemId = WorkItemId.New();
        await workItems.CreateAsync(new WorkItem
        {
            Id = workItemId,
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
        });
        using var events = new SqliteDelegationEventStore(db, () => new DelegationOptions
        {
            MaxResultDiffChars = 16,
            MaxDiffStatChars = 8,
            MaxReasonChars = 8,
        });
        await events.RecordAsync(new DelegationEvent
        {
            Id = "evt-1",
            WorkItemId = workItemId,
            Attempt = 1,
            Brief = "brief text",
            Agent = AgentKind.Claude,
            Model = "model-x",
            Outcome = DelegationOutcomes.Completed,
            Reason = "a much longer reason than eight chars",
            DiffStat = "a much longer stat",
            ResultDiff = "a much longer diff than sixteen chars",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        var listed = await events.ListByWorkItemAsync(workItemId);
        var recorded = Assert.Single(listed);
        Assert.Equal("evt-1", recorded.Id);
        Assert.Equal(1, recorded.Attempt);
        Assert.Equal(AgentKind.Claude, recorded.Agent);
        Assert.Equal("model-x", recorded.Model);
        Assert.Equal(DelegationOutcomes.Completed, recorded.Outcome);
        Assert.Equal(8, recorded.Reason!.Length);
        Assert.Equal(8, recorded.DiffStat.Length);
        Assert.Equal(16, recorded.ResultDiff.Length);

        Assert.Empty(await events.ListByWorkItemAsync(WorkItemId.New()));
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/test",
    };
}
