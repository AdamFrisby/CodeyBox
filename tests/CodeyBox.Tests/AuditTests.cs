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
        var sandbox = new FakeSandbox(_ => new SandboxExecResult(1, "", "lint error: bad"));
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, result.Findings[0].Severity);
        Assert.Contains("lint error: bad", result.Findings[0].Description);
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

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
