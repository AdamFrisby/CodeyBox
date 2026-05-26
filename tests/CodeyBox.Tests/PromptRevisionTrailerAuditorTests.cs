using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PromptRevisionTrailerAuditor"/> — the deterministic
/// audit pass that verifies an agent commit carries a
/// <c>CodeyBox-Prompt-Revision: N</c> trailer matching the revision the
/// orchestrator snapshotted at iteration-dispatch time.
/// </summary>
public sealed class PromptRevisionTrailerAuditorTests
{
    private static AuditContext Ctx(int? dispatched) => new(
        WorkItemId.New(), "work", "main", Iteration: 1, OriginalPrompt: "p",
        PromptRevisionAtDispatch: dispatched);

    [Fact]
    public async Task NoDispatchedRevision_PassesWithWarningFinding()
    {
        // Legacy item with no iteration row recorded — auditor must not block
        // (Passed=true) but must surface a non-blocking Warning so the missing
        // dispatch row is visible to operators rather than silently disabling
        // the check.
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: null));
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task MatchingTrailer_Passes()
    {
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "5", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 5));
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task MissingTrailer_FailsWithErrorFinding()
    {
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 5));
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("missing", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleTrailer_FailsWithMismatchFinding()
    {
        // Agent committed with revision 1; orchestrator dispatched at revision 2.
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "1", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 2));
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("stale", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("found 1", finding.Title);
        Assert.Contains("expected 2", finding.Title);
    }

    [Fact]
    public async Task NonIntegerTrailer_FailsWithParseFinding()
    {
        var sandbox = new StubSandbox(_ => new SandboxExecResult(0, "not-a-number", ""));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 2));
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("not an integer", finding.Title);
    }

    [Fact]
    public async Task GitFails_SurfacesAsErrorFinding()
    {
        var sandbox = new StubSandbox(_ => new SandboxExecResult(128, "", "fatal: bad object"));
        var result = await new PromptRevisionTrailerAuditor()
            .RunAsync(sandbox, "/work", Ctx(dispatched: 1));
        Assert.False(result.Passed);
        Assert.Single(result.Findings);
    }

    private sealed class StubSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _handler;
        public StubSandbox(Func<SandboxExec, SandboxExecResult> handler) => _handler = handler;
        public string Id => "stub";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(_handler(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
