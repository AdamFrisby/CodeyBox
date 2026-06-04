using CodeyBox.Audit;
using CodeyBox.Core;
using System.Diagnostics;

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
    public async Task PriorCommitMissingTrailer_HeadMatchingTrailerPasses()
    {
        // The deterministic auditor must be satisfiable by amending or adding
        // a correct HEAD commit. Earlier branch history may predate the prompt
        // trailer requirement and must not keep the item blocked forever.
        var repo = Directory.CreateTempSubdirectory("codeybox-prompt-rev-audit-").FullName;
        try
        {
            await TestSupport.RunGit(repo, "init", "-b", "main");
            await TestSupport.RunGit(repo, "config", "user.email", "test@example.invalid");
            await TestSupport.RunGit(repo, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(repo, "old.txt"), "old\n");
            await TestSupport.RunGit(repo, "add", "old.txt");
            await TestSupport.RunGit(repo, "commit", "-m", "old commit without prompt trailer");

            await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "new\n");
            await TestSupport.RunGit(repo, "add", "new.txt");
            await TestSupport.RunGit(repo, "commit", "-m",
                $"new commit with prompt trailer\n\n{CodeyBoxTrailers.PromptRevisionTrailerKey}: 7\n{CodeyBoxTrailers.CoAuthoredBy}");

            var result = await new PromptRevisionTrailerAuditor()
                .RunAsync(new ProcessExecSandbox(), repo, Ctx(dispatched: 7));

            Assert.True(result.Passed);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
        }
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

    private sealed class ProcessExecSandbox : ISandbox
    {
        public string Id => "process";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exec.Argv[0],
                WorkingDirectory = exec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = exec.Stdin is not null,
                UseShellExecute = false,
            };
            foreach (var arg in exec.Argv.Skip(1))
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            if (exec.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(exec.Stdin);
                await process.StandardInput.DisposeAsync();
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SandboxExecResult(process.ExitCode, stdout, stderr);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
