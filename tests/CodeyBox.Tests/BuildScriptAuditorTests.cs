using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class BuildScriptAuditorTests : IDisposable
{
    private readonly string _workspace;

    public BuildScriptAuditorTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-build-script-audit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task PresentAndPasses_ReturnsPassedWithCapturedOutput()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(0, "build ok\n", ""));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx());

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal("build ok\n", result.RawOutput);
        Assert.Contains(sandbox.Executed, argv => argv.SequenceEqual(["sh", "-c", "./build.sh"]));
    }

    [Fact]
    public async Task PresentAndFails_EmitsBlockingBuildFailedFindingWithOutput()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(2, "compile stdout\n", "compile stderr\n"));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("build failed", finding.Title);
        Assert.Contains("build.sh exited with code 2", finding.Description);
        Assert.Contains("compile stdout", finding.Description);
        Assert.Contains("compile stderr", finding.Description);
        Assert.Contains("compile stdout", result.RawOutput);
        Assert.Contains("compile stderr", result.RawOutput);
    }

    [Fact]
    public async Task AbsentAndOptional_SkipsWithoutBlocking()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(1, "", "")
            : new SandboxExecResult(99, "should not run", ""));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx(required: false));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal("build.sh absent; auditor skipped", result.RawOutput);
        Assert.DoesNotContain(sandbox.Executed, argv => argv.SequenceEqual(["sh", "-c", "./build.sh"]));
    }

    [Fact]
    public async Task AbsentAndRequired_IsBlockingMissingScriptFinding()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(1, "", "")
            : new SandboxExecResult(99, "should not run", ""));

        var result = await new BuildScriptAuditor().RunAsync(
            sandbox,
            "/work/repo",
            Ctx(required: true));

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("build.sh missing", finding.Title);
    }

    [Fact]
    public async Task Exit127_ThrowsCouldNotVerifyInsteadOfFinding()
    {
        var sandbox = new StubSandbox(exec => IsPresenceCheck(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(127, "", "command not found\n"));

        var ex = await Assert.ThrowsAsync<BuildScriptAuditUnavailableException>(() =>
            new BuildScriptAuditor().RunAsync(
                sandbox,
                "/work/repo",
                Ctx()));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("exit 127", ex.Message);
        Assert.Equal(127, ex.ExitCode);
        Assert.Contains("command not found", ex.Output);
    }

    [Fact]
    public async Task Timeout_ThrowsCouldNotVerifyInsteadOfFinding()
    {
        var sandbox = new TimeoutSandbox();

        var ex = await Assert.ThrowsAsync<BuildScriptAuditUnavailableException>(() =>
            new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 1 }).RunAsync(
                sandbox,
                "/work/repo",
                Ctx()));

        Assert.Contains("could-not-verify", ex.Message);
        Assert.Contains("timed out", ex.Message);
        Assert.Contains(sandbox.Executed, argv => argv.SequenceEqual(["sh", "-c", "./build.sh"]));
    }

    [Fact]
    public async Task Pipeline_Exit127_FailsInfrastructureAndDoesNotPersistCodeFinding()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new BuildScriptAuditor(new BuildScriptAuditorOptions { TimeoutSeconds = 5 })],
            maxAuditIterations: 1,
            auditReportStore: reports,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        var item = NewItem("feature/build-script-exit-127") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitBuildScriptToBareBranchAsync(
            tp.GitHost.GetRepoPath(repoId),
            item.WorkBranch!,
            """
            #!/bin/sh
            echo missing tool >&2
            exit 127
            """);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("could-not-verify", final.LastError);
        Assert.Contains("exit 127", final.LastError);
        Assert.DoesNotContain(reports.Reports, r => r.AuditorName == BuildScriptAuditor.AuditorName);
    }

    [Fact]
    public async Task ProjectRepository_MapsBuildScriptRequired_DefaultsAndProjectOverride()
    {
        var options = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Audit = new ProjectAuditConfig
                {
                    BuildScriptRequired = true,
                    Languages = [],
                    AuditTypes = [],
                },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "inherits",
                    RepositoryUrl = "https://example.com/inherits.git",
                },
                new ProjectConfig
                {
                    Id = "overrides",
                    RepositoryUrl = "https://example.com/overrides.git",
                    Audit = new ProjectAuditConfig
                    {
                        BuildScriptRequired = false,
                        Languages = [],
                        AuditTypes = [],
                    },
                },
            ],
        };
        using var repo = new ProjectRepository(
            new StaticOptionsMonitor<ProjectsOptions>(options),
            NullLogger<ProjectRepository>.Instance);

        var inherits = await repo.GetAsync(new ProjectId("inherits"));
        var overrides = await repo.GetAsync(new ProjectId("overrides"));

        Assert.True(inherits!.Audit.BuildScriptRequired);
        Assert.False(overrides!.Audit.BuildScriptRequired);
    }

    private static AuditContext Ctx(bool required = false) => new(
        WorkItemId.New(),
        WorkBranch: "work",
        BaseBranch: "main",
        Iteration: 1,
        OriginalPrompt: "prompt",
        BuildScriptRequired: required);

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "build script audit",
        Prompt = "change",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private static bool IsPresenceCheck(SandboxExec exec)
        => exec.Argv.SequenceEqual(["sh", "-c", "test -f ./build.sh"]);

    private static async Task CommitBuildScriptToBareBranchAsync(
        string barePath,
        string branch,
        string script)
    {
        var clone = Directory.CreateTempSubdirectory("codeybox-build-script-branch-").FullName;
        try
        {
            await TestSupport.RunGit(clone, "clone", barePath, ".");
            await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");

            var path = Path.Combine(clone, "build.sh");
            await File.WriteAllTextAsync(path, script.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            await TestSupport.RunGit(clone, "add", "build.sh");
            await TestSupport.RunGit(clone, "commit", "-m", "add build script");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        }
        finally
        {
            try { Directory.Delete(clone, recursive: true); } catch { }
        }
    }

    private sealed class StubSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _handler;
        public List<IReadOnlyList<string>> Executed { get; } = [];

        public StubSandbox(Func<SandboxExec, SandboxExecResult> handler)
            => _handler = handler;

        public string Id => "stub";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Executed.Add(exec.Argv.ToArray());
            return Task.FromResult(_handler(exec));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimeoutSandbox : ISandbox
    {
        public List<IReadOnlyList<string>> Executed { get; } = [];
        public string Id => "timeout";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Executed.Add(exec.Argv.ToArray());
            if (IsPresenceCheck(exec))
                return new SandboxExecResult(0, "", "");

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new SandboxExecResult(0, "should not run", "");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingAuditReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(
            string workItemId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>(
                Reports.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<string?> GetRawOutputAsync(
            string workItemId,
            int iteration,
            string auditorName,
            CancellationToken ct = default)
            => Task.FromResult(Reports.FirstOrDefault(r =>
                    r.WorkItemId == workItemId &&
                    r.Iteration == iteration &&
                    r.AuditorName == auditorName)
                ?.RawOutput);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            var removed = Reports.RemoveAll(r => r.StartedAt < cutoff);
            return Task.FromResult(removed);
        }
    }
}
