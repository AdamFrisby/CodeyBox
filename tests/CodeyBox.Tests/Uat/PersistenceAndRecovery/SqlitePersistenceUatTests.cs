using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.PersistenceAndRecovery;

/// <summary>
/// UAT coverage for SQLite durable state from the Persistence And Recovery section.
/// Plan anchor:
/// docs/uat/00-plan.md#sqlite-work-item-and-auxiliary-stores---persists-durable-pipeline-state-and-related-records
/// </summary>
public sealed class SqlitePersistenceUatTests : IDisposable
{
    private readonly PersistenceAndRecoveryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void FirstStartup_CreatesStateDirectoryTablesAndIndexesForAllPersistenceStores()
    {
        var dbPath = Path.Combine(_workspace.Root, "missing", "nested", "state.db");

        using var releaseStore = new SqliteReleaseStore(dbPath);
        using var workItemStore = new SqliteWorkItemStore(dbPath);
        using var questionStore = new SqliteWorkItemQuestionStore(dbPath);
        using var suggestionStore = new SqliteSuggestionStore(dbPath);
        using var auditReportStore = new SqliteAuditReportStore(dbPath);
        using var timingStore = new SqliteTimingStore(dbPath);
        using var costStore = new SqliteWorkItemCostStore(dbPath);
        using var streamSummaryStore = new SqliteAgentStreamSummaryStore(dbPath);

        Assert.True(File.Exists(dbPath));
        var tables = PersistenceAndRecoveryHelpers.GetTableNames(dbPath);
        Assert.Contains("work_items", tables);
        Assert.Contains("work_item_questions", tables);
        Assert.Contains("suggestions", tables);
        Assert.Contains("audit_reports", tables);
        Assert.Contains("work_item_timings", tables);
        Assert.Contains("work_item_costs", tables);
        Assert.Contains("releases", tables);
        Assert.Contains("release_audit_iterations", tables);
        Assert.Contains("agent_stream_summaries", tables);

        var indexes = PersistenceAndRecoveryHelpers.GetIndexNames(dbPath);
        Assert.Contains("idx_work_items_state", indexes);
        Assert.Contains("idx_work_items_external_id_per_project", indexes);
        Assert.Contains("idx_questions_unique", indexes);
        Assert.Contains("idx_suggestions_state_project", indexes);
        Assert.Contains("idx_audit_reports_workitem_iter", indexes);
    }

    [Fact]
    public async Task LegacyWorkItemDatabase_RunsAdditiveMigrationsIdempotentlyAndKeepsDefaults()
    {
        var dbPath = _workspace.NewDatabasePath();
        var legacyItem = PersistenceAndRecoveryHelpers.Item(WorkItemState.WorkComplete);
        PersistenceAndRecoveryHelpers.CreateLegacyWorkItemsDatabase(dbPath, legacyItem);

        using (var firstStartup = new SqliteWorkItemStore(dbPath))
        {
            var migrated = await firstStartup.GetAsync(legacyItem.Id);
            Assert.NotNull(migrated);
            Assert.Empty(migrated!.DependsOn);
            Assert.Null(migrated.FailureKind);
            Assert.Null(migrated.QuotaResetAt);
            Assert.Null(migrated.ReleaseId);
            Assert.Equal(0, migrated.RecoveryAttempts);
            Assert.Null(migrated.PreemptCheckpoint);
            Assert.Null(migrated.AuditorProfile);
            Assert.Equal(95, migrated.MinModelScore);
        }

        using var secondStartup = new SqliteWorkItemStore(dbPath);
        var columns = PersistenceAndRecoveryHelpers.GetColumnNames(dbPath, "work_items");
        Assert.Contains("failure_kind", columns);
        Assert.Contains("quota_reset_at", columns);
        Assert.Contains("release_id", columns);
        Assert.Contains("recovery_attempts", columns);
        Assert.Contains("preempted_at", columns);
        Assert.Contains("preempt_checkpoint", columns);
        Assert.Contains("auditor_profile", columns);
        Assert.NotNull(await secondStartup.GetAsync(legacyItem.Id));
    }

    [Fact]
    public async Task ExternalIdIndex_AllowsMultipleNullExternalIdsButRejectsDuplicateProjectValue()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var projectItem = PersistenceAndRecoveryHelpers.Item();
        var secondNullExternalId = PersistenceAndRecoveryHelpers.Item();
        var withExternalId = PersistenceAndRecoveryHelpers.Item() with { ExternalId = "UAT-123" };
        var duplicateExternalId = PersistenceAndRecoveryHelpers.Item() with { ExternalId = "UAT-123" };
        var otherProjectSameExternalId = PersistenceAndRecoveryHelpers.Item() with
        {
            ProjectId = new ProjectId("uat-persistence-other"),
            ExternalId = "UAT-123",
        };

        await store.CreateAsync(projectItem);
        await store.CreateAsync(secondNullExternalId);
        await store.CreateAsync(withExternalId);
        await store.CreateAsync(otherProjectSameExternalId);
        var duplicate = await Assert.ThrowsAsync<WorkItemExternalIdConflictException>(
            () => store.CreateAsync(duplicateExternalId));

        Assert.NotNull(duplicate);
        Assert.Equal(withExternalId.Id, (await store.GetByExternalIdAsync(PersistenceAndRecoveryHelpers.ProjectId, "UAT-123"))!.Id);
        Assert.Equal(otherProjectSameExternalId.Id,
            (await store.GetByExternalIdAsync(new ProjectId("uat-persistence-other"), "UAT-123"))!.Id);
    }

    [Fact]
    public async Task ConditionalWorkItemUpdate_DropsStaleTransitionInsteadOfOverwritingNewState()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var item = PersistenceAndRecoveryHelpers.Item();
        await store.CreateAsync(item);

        var firstUpdate = await store.TryUpdateIfStateAsync(item.With(WorkItemState.Working), WorkItemState.Queued);
        var staleUpdate = await store.TryUpdateIfStateAsync(item.With(WorkItemState.Done), WorkItemState.Queued);

        Assert.True(firstUpdate);
        Assert.False(staleUpdate);
        Assert.Equal(WorkItemState.Working, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task WorkItemDependenciesQuestionsAndSuggestions_RoundTripStructuredJsonFields()
    {
        var dbPath = _workspace.NewDatabasePath();
        using var workItems = new SqliteWorkItemStore(dbPath);
        using var questions = new SqliteWorkItemQuestionStore(dbPath);
        using var suggestions = new SqliteSuggestionStore(dbPath);
        var parentA = PersistenceAndRecoveryHelpers.Item(WorkItemState.Done);
        var parentB = PersistenceAndRecoveryHelpers.Item(WorkItemState.Failed);
        var child = PersistenceAndRecoveryHelpers.Item() with { DependsOn = [parentA.Id, parentB.Id] };
        await workItems.CreateAsync(parentA);
        await workItems.CreateAsync(parentB);
        await workItems.CreateAsync(child);

        var question = new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = child.Id.ToString(),
            QuestionId = "q-001",
            QuestionText = "Which durable recovery path should be used?",
            AskedAt = DateTimeOffset.Parse("2026-05-14T01:00:00Z"),
        };
        var suggestion = new Suggestion
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceWorkItemId = child.Id.ToString(),
            ProjectId = child.ProjectId.Value,
            Title = "Add upgrade fixture",
            Rationale = "A real old database fixture would supplement synthetic migration coverage.",
            Category = "test-coverage",
            Severity = "minor",
            EstimatedEffort = "small",
            FilesReferenced = ["docs/uat/00-plan.md", "src/CodeyBox.Orchestrator/SqliteWorkItemStore.cs"],
            CreatedAt = DateTimeOffset.Parse("2026-05-14T01:05:00Z"),
        };

        Assert.True(await questions.CreateIfNotExistsAsync(question));
        Assert.False(await questions.CreateIfNotExistsAsync(question with
        {
            Id = Guid.NewGuid().ToString("N"),
            QuestionText = "Duplicate question should be ignored.",
        }));
        await suggestions.CreateAsync(suggestion);
        Assert.True(await suggestions.TryAcceptAsync(suggestion.Id, WorkItemId.New().ToString()));

        var storedChild = await workItems.GetAsync(child.Id);
        var storedQuestion = await questions.GetAsync(child.Id.ToString(), "q-001");
        var storedSuggestion = await suggestions.GetAsync(suggestion.Id);
        Assert.Equal([parentA.Id, parentB.Id], storedChild!.DependsOn);
        Assert.Equal(question.QuestionText, storedQuestion!.QuestionText);
        Assert.Equal("accepted", storedSuggestion!.State);
        Assert.Equal(suggestion.FilesReferenced, storedSuggestion.FilesReferenced);
    }

    [Fact]
    public async Task AuxiliaryStores_PersistRowsAcrossStoreReopen()
    {
        var dbPath = _workspace.NewDatabasePath();
        var release = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = PersistenceAndRecoveryHelpers.ProjectId,
            Name = "uat-release",
            Description = "Persistence UAT release",
            State = ReleaseState.Open,
            CreatedAt = DateTimeOffset.Parse("2026-05-14T02:00:00Z"),
            TargetTag = "v-uat",
        };
        var item = PersistenceAndRecoveryHelpers.Item(WorkItemState.Done) with { ReleaseId = release.Id };

        using (var releases = new SqliteReleaseStore(dbPath))
        using (var workItems = new SqliteWorkItemStore(dbPath))
        using (var reports = new SqliteAuditReportStore(dbPath))
        using (var timings = new SqliteTimingStore(dbPath))
        using (var costs = new SqliteWorkItemCostStore(dbPath))
        using (var summaries = new SqliteAgentStreamSummaryStore(dbPath))
        {
            await releases.CreateAsync(release);
            await workItems.CreateAsync(item);
            await reports.CreateAsync(new AuditReport
            {
                Id = "audit-uat",
                WorkItemId = item.Id.ToString(),
                Iteration = 1,
                AuditorName = "uat:deterministic",
                AuditorKind = "tool",
                WorstSeverity = "medium",
                StartedAt = DateTimeOffset.Parse("2026-05-14T02:01:00Z"),
                EndedAt = DateTimeOffset.Parse("2026-05-14T02:02:00Z"),
                DurationMs = 60000,
                Findings =
                [
                    new AuditReportFinding("finding-1", "medium", "Finding", "Message", ["src/file.cs"], [42]),
                ],
                RawOutput = "raw auditor output",
            });
            await timings.BeginAsync(new TimingRecord
            {
                Id = "timing-uat",
                WorkItemId = item.Id,
                Phase = "work",
                Iteration = null,
                Step = "agent.exec",
                StartedAt = DateTimeOffset.Parse("2026-05-14T02:03:00Z"),
                MetadataJson = """{"phase":"work"}""",
            });
            await timings.EndAsync("timing-uat", DateTimeOffset.Parse("2026-05-14T02:04:00Z"), 61000);
            await costs.RecordAsync(new WorkItemCost
            {
                Id = "cost-uat",
                WorkItemId = item.Id.ToString(),
                Phase = "work",
                AgentKind = "claude",
                ModelId = "claude-uat",
                InputTokens = 100,
                CachedInputTokens = 10,
                OutputTokens = 20,
                EstimatedUsd = 0.12,
                StartedAt = DateTimeOffset.Parse("2026-05-14T02:03:00Z"),
                EndedAt = DateTimeOffset.Parse("2026-05-14T02:04:00Z"),
                RawMetadataJson = """{"source":"uat"}""",
            });
            await summaries.UpsertAsync(new AgentStreamSummaryRow(
                item.Id,
                "work-1-abcdef.jsonl",
                "work",
                null,
                AgentKind.Claude,
                new AgentStreamSummary(
                    TimeSpan.FromSeconds(61),
                    TimeSpan.FromSeconds(2),
                    100,
                    20,
                    10,
                    0.12m,
                    [new ToolCallInvocation("tool-1", "shell", "dotnet test", null, null, null, true, 128)],
                    [new StallEvent(DateTimeOffset.Parse("2026-05-14T02:03:30Z"), TimeSpan.FromSeconds(30), "thinking", "tool", "tool_wait")],
                    "done"),
                DateTimeOffset.Parse("2026-05-14T02:05:00Z")));
        }

        using var reopenedReleases = new SqliteReleaseStore(dbPath);
        using var reopenedWorkItems = new SqliteWorkItemStore(dbPath);
        using var reopenedReports = new SqliteAuditReportStore(dbPath);
        using var reopenedTimings = new SqliteTimingStore(dbPath);
        using var reopenedCosts = new SqliteWorkItemCostStore(dbPath);
        using var reopenedSummaries = new SqliteAgentStreamSummaryStore(dbPath);

        Assert.Equal(release.Name, (await reopenedReleases.GetAsync(release.Id))!.Name);
        Assert.Equal(release.Id, (await reopenedWorkItems.GetAsync(item.Id))!.ReleaseId);
        Assert.Equal("Finding", Assert.Single(await reopenedReports.GetByWorkItemAsync(item.Id.ToString())).Findings[0].Title);
        Assert.Equal(61000, Assert.Single(await reopenedTimings.GetByWorkItemAsync(item.Id)).DurationMs);
        Assert.Equal(0.12, Assert.Single(await reopenedCosts.GetByWorkItemAsync(item.Id.ToString())).EstimatedUsd, precision: 2);
        Assert.Equal("done", Assert.Single(await reopenedSummaries.GetByWorkItemAsync(item.Id)).Summary.FinalAssistantMessage);
    }
}
