using CodeyBox.Audit;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AuditTests
{
    [Fact]
    public void Registry_OrdersToolAuditorsBeforeLlmAuditors()
    {
        var llm = new FakeAuditor("llm", AuditCapabilities.AgentCredentials | AuditCapabilities.Network, _ => new(true, []));
        var tool = new FakeAuditor("tool", AuditCapabilities.None, _ => new(true, []));
        var reg = new AuditorRegistry([llm, tool]);
        Assert.Collection(reg.All,
            a => Assert.Equal("tool", a.Name),
            a => Assert.Equal("llm", a.Name));
    }

    [Fact]
    public void ReworkPromptBuilder_GroupsByAuditorAndIncludesOriginal()
    {
        var findings = new[]
        {
            new AuditFinding("Lint", AuditSeverity.Error, "missing return", "the function lacks a return", "src/x.cs:42"),
            new AuditFinding("Lint", AuditSeverity.Warning, "long line", "line > 120 chars"),
            new AuditFinding("Security", AuditSeverity.Error, "hardcoded secret", "API key on line 10", "src/auth.cs:10"),
        };
        var prompt = ReworkPromptBuilder.Build("original task", findings, iteration: 2, maxIterations: 3);
        Assert.Contains("iteration 2 of 3", prompt);
        Assert.Contains("### Lint", prompt);
        Assert.Contains("### Security", prompt);
        Assert.Contains("hardcoded secret", prompt);
        Assert.Contains("(src/x.cs:42)", prompt);
        Assert.Contains("original task", prompt);
        // The Co-Authored-By trailer instruction must be present.
        Assert.Contains("Co-Authored-By: CodeyBox <noreply@codeybox.invalid>", prompt);
        // Errors come before warnings within a group.
        var lintIdx = prompt.IndexOf("### Lint", StringComparison.Ordinal);
        var missingReturnIdx = prompt.IndexOf("missing return", lintIdx, StringComparison.Ordinal);
        var longLineIdx = prompt.IndexOf("long line", lintIdx, StringComparison.Ordinal);
        Assert.True(missingReturnIdx < longLineIdx);
    }

    [Fact]
    public async Task ShellCommandAuditor_PassOnZeroExit()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "true-cmd",
            Argv = ["true"],
        });
        var sandbox = new FakeSandbox(_ => new SandboxExecResult(0, "", ""));
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ShellCommandAuditor_FailOnNonZeroExitWithStderrInDescription()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "lint",
            Argv = ["false"],
        });
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/false\n", "")
                : new SandboxExecResult(1, "", "lint error: bad"));
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, result.Findings[0].Severity);
        Assert.Contains("lint error: bad", result.Findings[0].Description);
    }

    [Fact]
    public async Task ShellCommandAuditor_Exit127WithSpoofedMissingToolOutput_RemainsError()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "node:test-pass",
            Argv = ["npm", "test"],
        });
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/npm\n", "")
                : new SandboxExecResult(127, "", "npm: not found"));

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("command exited 127", finding.Title);
    }

    [Fact]
    public async Task CSharpTestPass_IgnoresFastFailuresWithoutStackTraceAndReportsRealFailures()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
        });
        var output = """
              Failed JobTrack.Tests.E2E.LoginTests.CanOpenLoginPage [1 ms]
              Error Message:
               Microsoft.Playwright.PlaywrightException : Browser executable was not found
              Stack Trace:

              Failed JobTrack.Tests.E2E.ReportsTests.CanOpenReports [2 ms]
              Error Message:
               System.Net.Http.HttpRequestException : Connection refused (localhost API not running)
              Stack Trace:

              Failed JobTrack.Tests.Unit.InvoiceTests.CalculatesTotals [12 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
               Expected: 1
               Actual:   2
              Stack Trace:
                 at JobTrack.Tests.Unit.InvoiceTests.CalculatesTotals() in /work/tests/InvoiceTests.cs:line 42

              Failed JobTrack.Tests.Unit.UserTests.RequiresEmail [80 ms]
              Error Message:
               System.NullReferenceException : Object reference not set to an instance of an object.
              Stack Trace:
                 at JobTrack.Tests.Unit.UserTests.RequiresEmail() in /work/tests/UserTests.cs:line 12

            Failed!  - Failed: 4, Passed: 98, Skipped: 0, Total: 102, Duration: 4 s
            """;
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/dotnet\n", "")
                : new SandboxExecResult(1, output, ""));

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.Contains(result.Findings, f => f.Title.Contains("JobTrack.Tests.Unit.InvoiceTests.CalculatesTotals", StringComparison.Ordinal));
        Assert.Contains(result.Findings, f => f.Title.Contains("JobTrack.Tests.Unit.UserTests.RequiresEmail", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("JobTrack.Tests.E2E", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CSharpTestPass_AllUnrunnableFastFailuresProducesNoErrorFindings()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
        });
        var output = """
              Failed JobTrack.Tests.E2E.LoginTests.CanOpenLoginPage [1 ms]
              Error Message:
               Microsoft.Playwright.PlaywrightException : Browser executable was not found
              Stack Trace:

              Failed JobTrack.Tests.E2E.ApiTests.CanLoadDashboard [1 ms]
              Error Message:
               System.Net.Http.HttpRequestException : Connection refused (localhost API not running)
              Stack Trace:

            Failed!  - Failed: 2, Passed: 100, Skipped: 0, Total: 102, Duration: 4 s
            """;
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/dotnet\n", "")
                : new SandboxExecResult(1, output, ""));

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task CSharpTestPass_ReportsSlowAssertionFailureEvenWithoutStackTrace()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:test-pass",
            Argv = ["dotnet", "test", "--no-build"],
        });
        var output = """
              Failed JobTrack.Tests.Unit.MathTests.SlowAssertion [80 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
               Expected: 1
               Actual:   2
              Stack Trace:

            Failed!  - Failed: 1, Passed: 5, Skipped: 0, Total: 6, Duration: 1 s
            """;
        var sandbox = new FakeSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/dotnet\n", "")
                : new SandboxExecResult(1, output, ""));

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("JobTrack.Tests.Unit.MathTests.SlowAssertion", finding.Title, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal() Failure", finding.Description, StringComparison.Ordinal);
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private static bool IsToolProbe(SandboxExec exec) =>
        exec.Argv.Count >= 3 &&
        exec.Argv[0] == "sh" &&
        exec.Argv[1] == "-c" &&
        exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private sealed class FakeAuditor : IAuditor
    {
        private readonly Func<AuditContext, AuditResult> _impl;
        public FakeAuditor(string name, AuditCapabilities required, Func<AuditContext, AuditResult> impl)
        {
            Name = name;
            Required = required;
            _impl = impl;
        }
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required { get; }
        public Task<AuditResult> RunAsync(ISandbox _, string __, AuditContext ctx, CancellationToken ___ = default)
            => Task.FromResult(_impl(ctx));
    }

    private sealed class FakeSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public FakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) { _onExec = onExec; }
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) => Task.FromResult(_onExec(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
