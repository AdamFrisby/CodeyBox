using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// The required-build gate appends the NuGet fallback coverage report to a
/// passed verification output: shared versus fetched package counts after
/// restore. The gate result itself never depends on the diagnostic.
/// </summary>
public sealed class RequiredBuildCoverageTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-required-build-coverage-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task VerifyAsync_PassedOutput_ReportsFallbackCoverage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var item = NewItem("feature/fallback-coverage");
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(gitHost.GetRepoPath(repoId), item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var verifier = new SandboxRequiredBuildVerifier(
            new CoverageReportingSandboxProvider(),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });
        var verification = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, verification.Status);
        Assert.Contains(
            "NuGet fallback coverage: 1 shared, 1 fetched of 2 referenced (1 available)",
            verification.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_PassedOutput_OmitsCoverageWhenUndeterminable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddDotnetSolutionMarkerAsync(seed);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var item = NewItem("feature/no-coverage");
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CommitToBareBranchAsync(gitHost.GetRepoPath(repoId), item.WorkBranch!, "ok.txt", "ok\n", "branch exists");

        var verifier = new SandboxRequiredBuildVerifier(
            new SilentSandboxProvider(),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });
        var verification = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = item.BaseBranch,
            WorkBranch = item.WorkBranch!,
            Phase = "audit",
        }, CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Passed, verification.Status);
        Assert.DoesNotContain("NuGet fallback coverage", verification.Output, StringComparison.Ordinal);
    }

    private static async Task AddDotnetSolutionMarkerAsync(string repoPath)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, "CodeyBox.slnx"), "# solution marker\n");
        await TestSupport.RunGit(repoPath, "add", "CodeyBox.slnx");
        await TestSupport.RunGit(repoPath, "commit", "-m", "add solution marker");
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch);
        var path = Path.Combine(clone, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"{branch}:{branch}");
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "required build coverage test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private sealed class CoverageReportingSandboxProvider : ISandboxProvider
    {
        public string Name => "coverage-reporting";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new CoverageReportingSandbox());

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CoverageReportingSandbox : ISandbox
    {
        public string Id => "coverage-reporting-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 2 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c"
                && exec.Argv[2].Contains("NUGET-CENSUS", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0,
                    "CODEYBOX-NUGET-CENSUS-V1\n" +
                    "FALLBACK-VALUE:/fb\n" +
                    "DIR:/fb\n" +
                    "a/1.0.0\n" +
                    "END-DIR\n" +
                    "ASSETS\n" +
                    "/work/proj/project.assets.json\n" +
                    "END-ASSETS\n" +
                    "END-CENSUS\n",
                    string.Empty));
            }
            if (exec.Argv is ["cat", "--", "/work/proj/project.assets.json"])
            {
                return Task.FromResult(new SandboxExecResult(0,
                    """{"libraries":{"A/1.0.0":{"type":"package"},"C/3.0.0":{"type":"package"}}}""",
                    string.Empty));
            }
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentSandboxProvider : ISandboxProvider
    {
        public string Name => "silent";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new SilentSandbox());

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SilentSandbox : ISandbox
    {
        public string Id => "silent-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = exec;
            _ = ct;
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
