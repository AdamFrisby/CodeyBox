using System.Text.RegularExpressions;
using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class DiffPatternAuditorTests
{
    private sealed class StubSandbox : ISandbox
    {
        public string Id => "stub";
        public string DiffStdout { get; init; } = "";
        public int DiffExitCode { get; init; }
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(DiffExitCode, DiffStdout, ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Pass_WhenDiffIsClean()
    {
        var auditor = new DiffPatternAuditor(new DiffPatternAuditorOptions
        {
            Name = "test",
            Patterns = [new DiffPattern { Regex = new Regex("EVIL"), Description = "evil marker" }],
        });
        var sandbox = new StubSandbox
        {
            DiffStdout = "+++ b/a.cs\n@@ -1,1 +1,1 @@\n+var ok = 1;\n",
        };
        var ctx = new AuditContext(WorkItemId.New(), "feature", "main", 1, "do x");
        var result = await auditor.RunAsync(sandbox, "/work", ctx);
        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Fail_WhenDiffContainsForbiddenPattern()
    {
        var auditor = new DiffPatternAuditor(new DiffPatternAuditorOptions
        {
            Name = "cheating",
            Patterns = [new DiffPattern { Regex = new Regex("@ts-ignore"), Description = "ts type-check suppression" }],
        });
        var sandbox = new StubSandbox
        {
            DiffStdout = "+++ b/src/x.ts\n@@ -1,1 +1,2 @@\n+// @ts-ignore\n+const x: number = 'oops';\n",
        };
        var ctx = new AuditContext(WorkItemId.New(), "feature", "main", 1, "do x");
        var result = await auditor.RunAsync(sandbox, "/work", ctx);
        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal("ts type-check suppression", result.Findings[0].Title);
        Assert.Contains("src/x.ts", result.Findings[0].Location ?? "");
    }

    [Fact]
    public async Task IgnoresRemovedLines()
    {
        // Removed lines (-) should not trigger findings — only added (+) lines do.
        var auditor = new DiffPatternAuditor(new DiffPatternAuditorOptions
        {
            Name = "lint",
            Patterns = [new DiffPattern { Regex = new Regex(@"# noqa"), Description = "noqa suppression" }],
        });
        var sandbox = new StubSandbox
        {
            DiffStdout = "+++ b/x.py\n@@ -1,1 +1,1 @@\n-import foo  # noqa\n+import foo\n",
        };
        var ctx = new AuditContext(WorkItemId.New(), "feature", "main", 1, "do x");
        var result = await auditor.RunAsync(sandbox, "/work", ctx);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task SeverityLevelHonoured_WhenSpecified()
    {
        var auditor = new DiffPatternAuditor(new DiffPatternAuditorOptions
        {
            Name = "todo",
            Patterns = [new DiffPattern
            {
                Regex = new Regex("TODO"),
                Description = "TODO marker",
                Severity = AuditSeverity.Warning,
            }],
        });
        var sandbox = new StubSandbox
        {
            DiffStdout = "+++ b/x.cs\n@@ -1,1 +1,1 @@\n+// TODO: fix this later\n",
        };
        var ctx = new AuditContext(WorkItemId.New(), "feature", "main", 1, "do x");
        var result = await auditor.RunAsync(sandbox, "/work", ctx);
        // Findings exist but severity is Warning, so AuditResult.Passed is
        // not strictly true — note that Passed reflects the internal "no
        // findings at all" check; severity gating happens in PipelineRunner.
        Assert.NotEmpty(result.Findings);
        Assert.Equal(AuditSeverity.Warning, result.Findings[0].Severity);
    }
}
