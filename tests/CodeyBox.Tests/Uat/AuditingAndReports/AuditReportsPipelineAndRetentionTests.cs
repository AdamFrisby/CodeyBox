using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests.Uat.AuditingAndReports;

/// <summary>
/// UAT coverage for audit iteration reporting, finding identity, and report retention.
/// Plan anchor: docs/uat/00-plan.md#auditing-and-reports
/// </summary>
[Collection("Pipeline integration")]
public sealed class AuditReportsPipelineAndRetentionTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-audit-reports-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task ParallelAuditors_PersistIndividualReportsAndNonBlockingFindingsStillPass()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var captureStore = new CapturingAuditReportStore();
        var first = new BlockingAuditor("security:llm-review", AuditSeverity.Warning, "Review note", "security raw");
        var second = new BlockingAuditor("quality:llm-review", AuditSeverity.Info, "Quality note", "quality raw");
        using var pipeline = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: TestAuditGates.WithPassedBuildAndTest([first, second]),
            auditReportStore: captureStore,
            maxLlmAuditorParallelism: 2);
        pipeline.Agent.WorkPlan.Enqueue(new FileWrite("result.txt", "done\n"));
        var item = AuditingAndReportsHelpers.NewItem();
        await pipeline.Store.CreateAsync(item);

        var run = pipeline.Pipeline.RunAsync(item, CancellationToken.None);
        await Task.WhenAll(first.Started.Task, second.Started.Task);
        first.Release.SetResult();
        second.Release.SetResult();
        await run;

        var final = await pipeline.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var llmReports = captureStore.Reports
            .Where(r => r.AuditorName.EndsWith(":llm-review", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["quality:llm-review", "security:llm-review"],
            llmReports.Select(r => r.AuditorName).Order(StringComparer.Ordinal).ToArray());
        Assert.All(llmReports, report =>
        {
            Assert.Equal(item.Id.ToString(), report.WorkItemId);
            Assert.Equal(1, report.Iteration);
            Assert.Single(report.Findings);
            Assert.Equal(["src/A.cs"], report.Findings[0].Files);
            Assert.Equal([42], report.Findings[0].LineHints);
        });
    }

    [Fact]
    public async Task PipelineReportPersistence_RedactsAndBoundsLargeRawOutput()
    {
        const int MaxRawBytes = 256 * 1024;
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var captureStore = new CapturingAuditReportStore();
        var rawOutput = "token=ghp_abcdefghijklmnopqrstuvwxyz1234567890\n" + new string('x', MaxRawBytes + 4096);
        using var pipeline = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new StaticAuditor("security:gitleaks", new AuditResult(true, [], RawOutput: rawOutput))],
            auditReportStore: captureStore);
        pipeline.Agent.WorkPlan.Enqueue(new FileWrite("result.txt", "done\n"));
        var item = AuditingAndReportsHelpers.NewItem("feature/raw-output");
        await pipeline.Store.CreateAsync(item);

        await pipeline.Pipeline.RunAsync(item, CancellationToken.None);

        var report = Assert.Single(captureStore.Reports);
        Assert.DoesNotContain("ghp_", report.RawOutput, StringComparison.Ordinal);
        Assert.Contains("token=***", report.RawOutput, StringComparison.Ordinal);
        Assert.EndsWith("[...truncated]", report.RawOutput, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(report.RawOutput!) <= MaxRawBytes);
    }

    [Fact]
    public void StableFindingId_IncludesAuditorTitleAndFilesButIgnoresFileOrdering()
    {
        var first = FindingIdComputer.Compute("security:semgrep", "Unsafe call in src/A.cs line 42", ["src/B.cs", "src/A.cs"]);
        var reordered = FindingIdComputer.Compute("security:semgrep", "Unsafe call", ["src/A.cs", "src/B.cs"]);
        var otherAuditor = FindingIdComputer.Compute("security:gitleaks", "Unsafe call", ["src/A.cs", "src/B.cs"]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, otherAuditor);
        Assert.Matches("^f-[0-9a-f]{8}$", first);
    }

    [Fact]
    public async Task SqliteRetention_DeletesOnlyRowsStrictlyOlderThanCutoff()
    {
        var dbPath = Path.Combine(_workspace, $"audit-retention-{Guid.NewGuid():N}.db");
        var workItemId = WorkItemId.New();
        await AuditingAndReportsHelpers.SeedWorkItemAsync(dbPath, workItemId);
        using var store = new SqliteAuditReportStore(dbPath);
        var cutoff = new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero);
        var old = AuditingAndReportsHelpers.Report(workItemId.ToString(), 1, "old", startedAt: cutoff.AddTicks(-1));
        var atCutoff = AuditingAndReportsHelpers.Report(workItemId.ToString(), 1, "at-cutoff", startedAt: cutoff);
        var fresh = AuditingAndReportsHelpers.Report(workItemId.ToString(), 1, "fresh", startedAt: cutoff.AddTicks(1));
        await store.CreateAsync(old);
        await store.CreateAsync(atCutoff);
        await store.CreateAsync(fresh);

        var deleted = await store.DeleteOlderThanAsync(cutoff);

        var remaining = await store.GetByWorkItemAsync(workItemId.ToString());
        Assert.Equal(1, deleted);
        Assert.Equal(["at-cutoff", "fresh"], remaining.Select(r => r.AuditorName).ToArray());
    }

    [Fact]
    public async Task RetentionService_RunsImmediateStartupSweepWithConfiguredUtcCutoff()
    {
        var store = new StartupSweepStore(deleteResult: 2);
        var logger = new CapturingLogger<AuditReportRetentionService>();
        var service = new AuditReportRetentionService(store, retainedDays: 14, logger);
        var before = DateTimeOffset.UtcNow.AddDays(-14).AddSeconds(-1);

        await service.StartAsync(CancellationToken.None);
        var cutoff = await store.CutoffObserved.Task;
        await service.StopAsync(CancellationToken.None);
        var after = DateTimeOffset.UtcNow.AddDays(-14).AddSeconds(1);

        Assert.InRange(cutoff, before, after);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("deleted 2 rows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetentionService_LogsWarningAndKeepsHostAliveWhenStoreDeleteFails()
    {
        var store = new StartupSweepStore(deleteResult: 0, exception: new InvalidOperationException("boom"));
        var logger = new CapturingLogger<AuditReportRetentionService>();
        var service = new AuditReportRetentionService(store, retainedDays: 30, logger);

        await service.StartAsync(CancellationToken.None);
        await store.CutoffObserved.Task;
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("sweep failed", StringComparison.Ordinal));
    }

    private sealed class StaticAuditor(string name, AuditResult result) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
