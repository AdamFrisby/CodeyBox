using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the stale-audit-verdict cycle: a retry that returns a
/// parked item to a runnable state must invalidate its prior audit progress, a
/// row left in_progress/incomplete by an interrupted run must be superseded
/// rather than read as a verdict, a no-change rework driven by such a
/// superseded verdict must not feed the no-changes circuit breaker, and park
/// reasons must carry the verdict's age and status.
/// </summary>
[Collection("Pipeline integration")]
public sealed class StaleAuditVerdictTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("stale-audit-verdict");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-stale-audit-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private static AuditProgressSnapshot Snapshot(
        int iteration,
        int blocking,
        int nonBlocking,
        string status,
        DateTimeOffset? recordedAt = null) => new(
            Iteration: iteration,
            MaxIterations: 3,
            BlockingFindings: blocking,
            NonBlockingFindings: nonBlocking,
            BlockingFindingIds: blocking > 0 ? [$"scripted:finding-{iteration}"] : [],
            BlockingFindingsDetails: blocking > 0
                ? [new AuditProgressFinding("scripted", AuditSeverity.Error, $"finding-{iteration}", "stale finding")]
                : [],
            Findings: blocking > 0
                ? [new AuditProgressFinding("scripted", AuditSeverity.Error, $"finding-{iteration}", "stale finding")]
                : [],
            WorkBranchTip: "abc123",
            Status: status,
            RecordedAt: recordedAt);

    private static AuditProgressRecord Record(
        int iteration,
        int blocking,
        string status,
        DateTimeOffset? recordedAt = null) => new(
            Iteration: iteration,
            MaxIterations: 3,
            BlockingFindings: blocking,
            NonBlockingFindings: 0,
            BlockingFindingIds: blocking > 0 ? [$"scripted:finding-{iteration}"] : [],
            BlockingFindingsDetails: blocking > 0
                ? [new AuditProgressFinding("scripted", AuditSeverity.Error, $"finding-{iteration}", "stale finding")]
                : [],
            Findings: blocking > 0
                ? [new AuditProgressFinding("scripted", AuditSeverity.Error, $"finding-{iteration}", "stale finding")]
                : [],
            WorkBranchTip: "abc123",
            Status: status,
            RecordedAt: recordedAt);

    // ── Deliverable 1: retry invalidates prior audit progress ────────────────

    [Fact]
    public async Task RetryParkedItem_InvalidatesPriorAuditProgress_SoNextAuditWritesFreshRow()
    {
        using var store = new SqliteWorkItemStore(Path.Combine(_workspace, "state.db"));
        var queue = new InMemoryTaskQueue();
        var gitHost = new StubGitHost();
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "stale verdict",
            Prompt = "do the work",
            WorkBranch = "codeybox/stale-verdict",
            BaseBranch = "main",
            State = WorkItemState.Failed,
            LastError = "audit failed",
        };
        await store.CreateAsync(item);
        var attempt = DateTimeOffset.UtcNow.AddDays(-4);
        await store.RecordIterationDispatchAsync(item.Id, AuditProgressIterationNumbers.WorkPhase, item.PromptRevision, attempt);
        var recordedAt = DateTimeOffset.UtcNow.AddDays(-4);
        await store.RecordAuditProgressAsync(item.Id, attempt, Record(1, 3, AuditProgressStatuses.Complete, recordedAt), recordedAt);
        await store.RecordAuditProgressAsync(item.Id, attempt, Record(2, 1, AuditProgressStatuses.Complete, recordedAt), recordedAt);
        var retrier = new WorkItemRetrier(
            store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance, auditProgress: store);

        var result = await retrier.RetryAsync(item, from: "audit");

        Assert.True(result.Success, result.Error);
        Assert.Equal("audit", result.ActualFrom);
        Assert.Empty(await store.GetAuditProgressAsync(item.Id, attempt));

        // The next audit pickup re-evaluates from the current work branch and
        // writes a genuinely new iteration-1 row rather than reusing the stale
        // verdict's iterations.
        var freshAt = DateTimeOffset.UtcNow;
        await store.RecordAuditProgressAsync(item.Id, attempt, Record(1, 0, AuditProgressStatuses.Complete, freshAt), freshAt);
        var rows = await store.GetAuditProgressAsync(item.Id, attempt);
        var single = Assert.Single(rows);
        Assert.Equal(1, single.Iteration);
        Assert.Equal(0, single.BlockingFindings);
        Assert.Equal(freshAt, single.RecordedAt);
    }

    // ── Deliverable 2: interrupted rows are superseded, not verdicts ─────────

    [Fact]
    public void DropSupersededAuditVerdicts_KeepsCompleteInOrder_DropsInterrupted()
    {
        IReadOnlyList<AuditProgressSnapshot> history =
        [
            Snapshot(1, 2, 0, AuditProgressStatuses.Complete),
            Snapshot(2, 1, 0, AuditProgressStatuses.InProgress),
            Snapshot(3, 1, 1, AuditProgressStatuses.Incomplete),
            Snapshot(4, 0, 0, AuditProgressStatuses.Complete),
        ];

        var (kept, superseded) = PipelineRunner.DropSupersededAuditVerdicts(history);

        Assert.Equal(2, superseded);
        Assert.Equal([1, 4], kept.Select(s => s.Iteration));
        Assert.All(kept, s => Assert.True(s.IsComplete));
    }

    [Fact]
    public void SelectMergeGateVerdict_IgnoresInterruptedHigherIteration_SelectsLatestComplete()
    {
        IReadOnlyList<AuditProgressRecord> records =
        [
            Record(1, 0, AuditProgressStatuses.Complete),
            // Frozen by a host restart mid-audit: must never gate the merge.
            Record(2, 1, AuditProgressStatuses.InProgress),
        ];

        var verdict = PipelineRunner.SelectMergeGateVerdict(records);

        Assert.NotNull(verdict);
        Assert.Equal(1, verdict!.Iteration);
        Assert.Equal(0, verdict.BlockingFindings);
    }

    [Fact]
    public void SelectMergeGateVerdict_OnlyInterruptedRows_ReturnsNull()
    {
        IReadOnlyList<AuditProgressRecord> records =
        [
            Record(1, 1, AuditProgressStatuses.InProgress),
        ];

        Assert.Null(PipelineRunner.SelectMergeGateVerdict(records));
    }

    // ── Deliverable 3: superseded-driven empty rework skips the breaker ──────

    [Fact]
    public async Task EmptyReworkDrivenBySupersededVerdict_ParksWithoutIncrementingBreaker()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            availabilityRegistry: registry);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "superseded rework",
            Prompt = "change the repo",
            WorkBranch = "feature/superseded-rework",
            BaseBranch = "main",
        };
        await tp.Store.CreateAsync(item);
        var project = new Project
        {
            Id = item.ProjectId,
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 3 },
        };
        // A verdict frozen mid-audit days ago: the findings it carries were
        // never finished, so the empty rework says nothing about the agent.
        IReadOnlyList<AuditProgressSnapshot> history =
        [
            Snapshot(1, 1, 0, AuditProgressStatuses.InProgress, DateTimeOffset.UtcNow.AddDays(-4)),
        ];

        // The rework dispatch recorded its outcome before surfacing the empty
        // pass, exactly as RunAgentPhaseAsync does.
        registry.RecordNoChangesOutcome(AgentKind.Claude, item.Id);
        Assert.Equal(1, registry.Snapshot().Single(s => s.Agent == AgentKind.Claude).ConsecutiveNoChanges);

        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        var parked = await tp.Pipeline.HandleEmptyReworkAsync(
            item,
            project,
            new ReworkProducedNoChangesException(AgentKind.Claude, "Rework agent produced no changes"),
            history,
            auditIteration: 1,
            reworkIterationNumber: 2,
            maxIterations: 3,
            baseReworkPrompt: "fix the findings",
            dispatchAsync: _ => Task.FromException<string?>(
                new InvalidOperationException("superseded park must not redispatch")),
            reworkPhase: phase,
            reworkStart: DateTimeOffset.UtcNow,
            repoId: item.Id.ToString(),
            workBranch: item.WorkBranch!,
            ct: CancellationToken.None);

        Assert.True(parked);
        var snapshot = registry.Snapshot().Single(s => s.Agent == AgentKind.Claude);
        Assert.Equal(0, snapshot.ConsecutiveNoChanges);
        Assert.False(snapshot.Excluded);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("status in_progress", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("recorded", final.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeSupersededDrivenEmptyReworksInSequence_DoNotTripCircuitBreaker()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            availabilityRegistry: registry);

        for (var i = 1; i <= 3; i++)
        {
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = $"stale item {i}",
                Prompt = "change the repo",
                WorkBranch = $"feature/stale-item-{i}",
                BaseBranch = "main",
            };
            await tp.Store.CreateAsync(item);
            var project = new Project
            {
                Id = item.ProjectId,
                DisplayName = "Test Project",
                RepositoryUrl = seed,
                DefaultBaseBranch = "main",
                DefaultAgent = AgentKind.Claude,
                Audit = new ProjectAudit { MaxIterations = 3 },
            };
            IReadOnlyList<AuditProgressSnapshot> history =
            [
                Snapshot(1, 1, 0, AuditProgressStatuses.Incomplete, DateTimeOffset.UtcNow.AddDays(-i)),
            ];

            registry.RecordNoChangesOutcome(AgentKind.Claude, item.Id);

            using var phase = new PhaseCancellation("rework", CancellationToken.None);
            var parked = await tp.Pipeline.HandleEmptyReworkAsync(
                item,
                project,
                new ReworkProducedNoChangesException(AgentKind.Claude, "Rework agent produced no changes"),
                history,
                auditIteration: 1,
                reworkIterationNumber: 2,
                maxIterations: 3,
                baseReworkPrompt: "fix the findings",
                dispatchAsync: _ => Task.FromException<string?>(
                    new InvalidOperationException("superseded park must not redispatch")),
                reworkPhase: phase,
                reworkStart: DateTimeOffset.UtcNow,
                repoId: item.Id.ToString(),
                workBranch: item.WorkBranch!,
                ct: CancellationToken.None);

            Assert.True(parked);
            var mid = registry.Snapshot().Single(s => s.Agent == AgentKind.Claude);
            Assert.Equal(0, mid.ConsecutiveNoChanges);
            Assert.False(mid.Excluded);
        }

        var final = registry.Snapshot().Single(s => s.Agent == AgentKind.Claude);
        Assert.Equal(0, final.ConsecutiveNoChanges);
        Assert.False(final.Excluded);
        Assert.Null(final.Reason);
    }

    // ── Deliverable 4: park reasons carry age and status ─────────────────────

    [Fact]
    public void ParkMessages_SurfaceVerdictAgeAndStatus()
    {
        var recordedAt = new DateTimeOffset(2026, 9, 6, 15, 59, 55, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 10, 15, 59, 55, TimeSpan.Zero);
        var blocker = new AuditProgressFinding("scripted", AuditSeverity.Error, "still failing", "fix it");
        AuditProgressSnapshot Stale(int iteration, string status) => new(
            Iteration: iteration,
            MaxIterations: 3,
            BlockingFindings: 1,
            NonBlockingFindings: 0,
            BlockingFindingIds: ["scripted:still failing"],
            BlockingFindingsDetails: [blocker],
            Findings: [blocker],
            WorkBranchTip: "abc123",
            Status: status,
            RecordedAt: recordedAt);
        IReadOnlyList<AuditProgressSnapshot> history = [Stale(2, AuditProgressStatuses.Complete)];

        var maxIteration = PipelineRunner.BuildAuditMaxIterationEscalationMessage(history, now);
        Assert.Contains("audit iteration 2/3", maxIteration, StringComparison.Ordinal);
        Assert.Contains("status complete", maxIteration, StringComparison.Ordinal);
        Assert.Contains("recorded 4d ago", maxIteration, StringComparison.Ordinal);

        var emptyRework = PipelineRunner.BuildEmptyReworkEscalationMessage(
            history, AgentKind.Claude, reworkIterationNumber: 3, attempts: 0, converging: false, now: now);
        Assert.Contains("audit iteration 2/3", emptyRework, StringComparison.Ordinal);
        Assert.Contains("status complete", emptyRework, StringComparison.Ordinal);
        Assert.Contains("recorded 4d ago", emptyRework, StringComparison.Ordinal);

        // Snapshots that predate recorded-at tracking still report status and
        // iteration rather than a fabricated age.
        IReadOnlyList<AuditProgressSnapshot> unknownAge = [Stale(2, AuditProgressStatuses.InProgress) with { RecordedAt = null }];
        var unknown = PipelineRunner.BuildAuditMaxIterationEscalationMessage(unknownAge, now);
        Assert.Contains("status in_progress", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("ago", unknown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12, "12s")]
    [InlineData(5 * 60, "5m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(4 * 86400, "4d")]
    public void FormatVerdictAge_BucketsMatchLargestWholeUnit(int seconds, string expected)
    {
        Assert.Equal(expected, PipelineRunner.FormatVerdictAge(TimeSpan.FromSeconds(seconds)));
    }

    private sealed class StubGitHost : IGitHost
    {
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public Task<string> EnsureRepositoryAsync(
            WorkItemId id,
            string? seedFromUrl,
            string? baseBranch,
            CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
            => throw new NotSupportedException();

        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => Task.FromResult("main");

        public Task PushToUpstreamAsync(
            string repositoryId,
            string upstreamUrl,
            string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> BranchHasCommitsAheadAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult((string.Empty, string.Empty));
    }
}
