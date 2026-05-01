using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <see cref="PipelineRunner"/> persists audit reports to
/// <see cref="IAuditReportStore"/> after each auditor invocation.
/// </summary>
public sealed class AuditReportCaptureTests : IDisposable
{
    private readonly string _workspace;

    public AuditReportCaptureTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-capture-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PipelineRunner_PersistsReport_AfterAuditorInvocation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var captureStore = new CapturingAuditReportStore();
        var finding = new AuditFinding("Scripted", AuditSeverity.Error, "Missing return", "no return stmt", "src/A.cs:10");
        var auditor = new KnownOutputAuditor([finding], rawOutput: "lint stdout here");

        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: [auditor], maxAuditIterations: 1, auditReportStore: captureStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Single(captureStore.Reports);
        var report = captureStore.Reports[0];
        Assert.Equal(item.Id.ToString(), report.WorkItemId);
        Assert.Equal(1, report.Iteration);
        Assert.Equal("KnownOutput", report.AuditorName);
        Assert.Equal("tool", report.AuditorKind);
        Assert.Equal("Error", report.WorstSeverity);
        Assert.Single(report.Findings);
        Assert.Equal("Missing return", report.Findings[0].Title);
        Assert.Equal("Error", report.Findings[0].Severity);
        Assert.Equal(["src/A.cs"], report.Findings[0].Files);
        Assert.Equal([10], report.Findings[0].LineHints);
        Assert.Equal("lint stdout here", report.RawOutput);
    }

    [Fact]
    public async Task PipelineRunner_PersistsReport_EvenWhenAuditorPasses()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var captureStore = new CapturingAuditReportStore();
        var auditor = new KnownOutputAuditor([], rawOutput: "all good");

        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: [auditor], auditReportStore: captureStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Single(captureStore.Reports);
        Assert.Equal("none", captureStore.Reports[0].WorstSeverity);
        Assert.Empty(captureStore.Reports[0].Findings);
        Assert.Equal("all good", captureStore.Reports[0].RawOutput);
    }

    [Fact]
    public async Task PipelineRunner_MultipleIterations_PersistsOneReportPerAuditorRun()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var captureStore = new CapturingAuditReportStore();
        var auditor = new KnownOutputAuditor(
        [
            new AuditFinding("KnownOutput", AuditSeverity.Error, "needs fix", "x"),
        ]);
        // plan: fail iter1, pass iter2
        auditor.PassOnSecondCall = true;

        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: [auditor], maxAuditIterations: 3, auditReportStore: captureStore);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(2, captureStore.Reports.Count);
        Assert.Equal(1, captureStore.Reports[0].Iteration);
        Assert.Equal(2, captureStore.Reports[1].Iteration);
    }

    [Fact]
    public async Task PipelineRunner_NullAuditReportStore_DoesNotThrow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new KnownOutputAuditor([]);

        // No auditReportStore → should silently skip persistence
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        var ex = await Record.ExceptionAsync(() => tp.Pipeline.RunAsync(item, CancellationToken.None));
        Assert.Null(ex);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "capture test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/capture",
        PushUpstream = false,
    };

    private sealed class CapturingAuditReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>(Reports.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class KnownOutputAuditor : IAuditor
    {
        private readonly IReadOnlyList<AuditFinding> _findings;
        private readonly string? _rawOutput;
        private int _callCount;

        public KnownOutputAuditor(IReadOnlyList<AuditFinding> findings, string? rawOutput = null)
        {
            _findings = findings;
            _rawOutput = rawOutput;
        }

        public bool PassOnSecondCall { get; set; }

        public string Name => "KnownOutput";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _callCount++;
            if (PassOnSecondCall && _callCount >= 2)
                return Task.FromResult(new AuditResult(true, [], RawOutput: _rawOutput));
            var passed = _findings.Count == 0;
            return Task.FromResult(new AuditResult(passed, _findings, RawOutput: _rawOutput));
        }
    }
}
