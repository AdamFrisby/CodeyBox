using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <see cref="SqliteTransitionHealthDataSource"/> against a real
/// SQLite file populated by the production stores (work item / audit report /
/// agent involvement). Confirms the source pulls the same shape the
/// classifier expects, that the window predicate is honoured by the SQL, and
/// that the absent-table fast path doesn't throw.
/// </summary>
public sealed class SqliteTransitionHealthDataSourceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-transition-health-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    [Fact]
    public async Task Empty_database_returns_empty_snapshot()
    {
        // No tables exist yet — the source must not throw.
        var source = new SqliteTransitionHealthDataSource(_dbPath);
        var snapshot = await source.LoadAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 1000);
        Assert.Empty(snapshot.Involvements);
        Assert.Empty(snapshot.AuditReports);
        Assert.Empty(snapshot.TerminalFailures);
    }

    [Fact]
    public async Task Reads_involvement_audit_and_terminal_rows_within_window()
    {
        using var workItems = new SqliteWorkItemStore(_dbPath);
        using var involvement = new SqliteAgentInvolvementStore(_dbPath);
        using var auditReports = new SqliteAuditReportStore(_dbPath);

        var now = DateTimeOffset.UtcNow;
        var workItemId = WorkItemId.New();

        // The audit_reports table foreign-keys to work_items; create the
        // parent row first.
        var item = new WorkItem
        {
            Id = workItemId,
            ProjectId = new ProjectId("project-a"),
            Title = "test",
            Prompt = "p",
            Agent = new AgentKind("claude"),
            State = WorkItemState.Failed,
            FailureKind = "infrastructure",
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddMinutes(-5),
        };
        await workItems.CreateAsync(item);

        // Finalized involvement: one successful work, one failure:agent rework
        // both inside the window.
        var ok = new AgentInvolvement(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            AgentKind: new AgentKind("claude"),
            ModelId: "claude-opus-4-7",
            Phase: "work",
            StartedAt: now.AddMinutes(-30),
            EndedAt: null,
            Iteration: null,
            Outcome: null);
        await involvement.RecordStartAsync(ok);
        await involvement.FinalizeAsync(ok.Id, now.AddMinutes(-29), "success");

        var bad = ok with { Id = Guid.NewGuid(), Phase = "rework", Iteration = 2 };
        await involvement.RecordStartAsync(bad);
        await involvement.FinalizeAsync(bad.Id, now.AddMinutes(-10), "failure:agent");

        // Out-of-window finalize — must NOT be returned.
        var stale = ok with { Id = Guid.NewGuid() };
        await involvement.RecordStartAsync(stale);
        await involvement.FinalizeAsync(stale.Id, now.AddHours(-25), "success");

        // Audit report with a real-finding row (LEGITIMATE) and one with an
        // auditor-failed row (INFRA).
        await auditReports.CreateAsync(new AuditReport
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId.ToString(),
            Iteration = 1,
            AuditorName = "review:quality",
            AuditorKind = "llm",
            WorstSeverity = "Error",
            StartedAt = now.AddMinutes(-20),
            EndedAt = now.AddMinutes(-19),
            DurationMs = 60_000,
            Findings = [new AuditReportFinding(
                Id: "f-1", Severity: "Error", Title: "Unused variable in foo.cs",
                Message: "details", Files: [], LineHints: [])],
            RawOutput = null,
        });
        await auditReports.CreateAsync(new AuditReport
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId.ToString(),
            Iteration = 1,
            Target = AuditTarget.Plan,
            AuditorName = "review:quality",
            AuditorKind = "llm",
            WorstSeverity = "Error",
            StartedAt = now.AddMinutes(-4),
            EndedAt = now.AddMinutes(-3),
            DurationMs = 5_000,
            Findings = [new AuditReportFinding(
                Id: "f-plan", Severity: "Error", Title: "plan-only finding",
                Message: "not a code-audit health transition", Files: [], LineHints: [])],
            RawOutput = null,
        });
        await auditReports.CreateAsync(new AuditReport
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId.ToString(),
            Iteration = 2,
            AuditorName = "review:quality",
            AuditorKind = "llm",
            WorstSeverity = "Error",
            StartedAt = now.AddMinutes(-3),
            EndedAt = now.AddMinutes(-2),
            DurationMs = 5_000,
            Findings = [new AuditReportFinding(
                Id: "f-2", Severity: "Error", Title: "review agent failed to run",
                Message: "agent exit 137", Files: [], LineHints: [])],
            RawOutput = null,
        });

        var source = new SqliteTransitionHealthDataSource(_dbPath);
        var snapshot = await source.LoadAsync(
            now.AddHours(-1), now, 1000);

        // 2 in-window involvement, 1 stale dropped.
        Assert.Equal(2, snapshot.Involvements.Count);
        Assert.Contains(snapshot.Involvements, r => r.Phase == "work" && r.Outcome == "success");
        Assert.Contains(snapshot.Involvements, r => r.Phase == "rework" && r.Outcome == "failure:agent");

        Assert.Equal(2, snapshot.AuditReports.Count);
        Assert.DoesNotContain(snapshot.AuditReports, report =>
            report.FindingTitles.Contains("plan-only finding", StringComparer.Ordinal));
        var infraReport = snapshot.AuditReports.First(r => r.Iteration == 2);
        Assert.Contains("review agent failed to run", infraReport.FindingTitles);

        // The Failed item, with state=Failed (100), is in the window.
        Assert.Single(snapshot.TerminalFailures);
        Assert.Equal((int)WorkItemState.Failed, snapshot.TerminalFailures[0].State);
        Assert.Equal("infrastructure", snapshot.TerminalFailures[0].FailureKind);

        // End-to-end: the classifier reads this snapshot and produces a
        // report with 1 audit-infra, 1 audit-legit, 1 rework-infra, 1
        // work-legit, 1 terminal-infra.
        var report = TransitionHealthClassifier.Compute(
            snapshot, now,
            new TransitionHealthOptions { Enabled = true, Window = TimeSpan.FromHours(1) });
        Assert.Equal(5, report.TotalTransitions);
        Assert.Equal(2, report.LegitimateTransitions);
        Assert.Equal(3, report.InfraFailureTransitions);
    }

    [Fact]
    public async Task Terminal_failure_query_includes_failed_mcrf_and_abandoned_states()
    {
        // The terminal-failure SQL filter must follow the WorkItemState enum,
        // not magic ints. Pins the three INFRA terminal states (Failed,
        // MergeConflictResolutionFailed, AbandonedAfterRecoveryAttempts) as
        // included, and Done / Cancelled / AuditFailed as excluded. If a
        // future renumber of the enum decouples from the SQL, this test
        // breaks instead of the metric silently bucketing the wrong items.
        using var workItems = new SqliteWorkItemStore(_dbPath);
        var now = DateTimeOffset.UtcNow;

        async Task<WorkItemId> Insert(WorkItemState state, string? failureKind = null)
        {
            var id = WorkItemId.New();
            await workItems.CreateAsync(new WorkItem
            {
                Id = id,
                ProjectId = new ProjectId("project-a"),
                Title = "t",
                Prompt = "p",
                Agent = new AgentKind("claude"),
                State = state,
                FailureKind = failureKind,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddMinutes(-5),
            });
            return id;
        }

        var failed = await Insert(WorkItemState.Failed, failureKind: "infrastructure");
        var mcrf = await Insert(WorkItemState.MergeConflictResolutionFailed);
        var abandoned = await Insert(WorkItemState.AbandonedAfterRecoveryAttempts);
        var done = await Insert(WorkItemState.Done);
        var cancelled = await Insert(WorkItemState.Cancelled);
        var auditFailed = await Insert(WorkItemState.AuditFailed);

        var source = new SqliteTransitionHealthDataSource(_dbPath);
        var snapshot = await source.LoadAsync(now.AddHours(-1), now, 1000);

        var returnedStates = snapshot.TerminalFailures
            .Select(r => r.State)
            .OrderBy(s => s)
            .ToArray();
        Assert.Equal(
            new[]
            {
                (int)WorkItemState.Failed,
                (int)WorkItemState.AbandonedAfterRecoveryAttempts,
                (int)WorkItemState.MergeConflictResolutionFailed,
            }
            .OrderBy(s => s)
            .ToArray(),
            returnedStates);
    }

    [Fact]
    public void ExtractFindingTitles_pulls_only_titles_and_tolerates_malformed_json()
    {
        var valid = JsonSerializer.Serialize(new[]
        {
            new { Id = "a", Severity = "Error", Title = "alpha" },
            new { Id = "b", Severity = "Error", Title = "beta" },
        });
        var titles = SqliteTransitionHealthDataSource.ExtractFindingTitles(valid);
        Assert.Equal(["alpha", "beta"], titles);

        // Real persisted shape uses camelCase via JsonSerializerDefaults.Web.
        var webShape = "[{\"id\":\"a\",\"severity\":\"Error\",\"title\":\"gamma\"}]";
        var webTitles = SqliteTransitionHealthDataSource.ExtractFindingTitles(webShape);
        Assert.Equal(["gamma"], webTitles);

        Assert.Empty(SqliteTransitionHealthDataSource.ExtractFindingTitles(null));
        Assert.Empty(SqliteTransitionHealthDataSource.ExtractFindingTitles(""));
        Assert.Empty(SqliteTransitionHealthDataSource.ExtractFindingTitles("not-json"));
        Assert.Empty(SqliteTransitionHealthDataSource.ExtractFindingTitles("{\"not\":\"an-array\"}"));
    }
}
