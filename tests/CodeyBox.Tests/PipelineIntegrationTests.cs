using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end pipeline test using the Process sandbox + a project + a
/// scripted agent that handles both the work and merge phases. Exercises:
///   - project resolution from in-memory repo
///   - work phase: agent writes a file → commit → push workBranch
///   - merge phase: agent runs `git merge --no-ff origin/&lt;workBranch&gt;` →
///     orchestrator verifies + pushes baseBranch
///   - final state Done with merged history in the bare repo
///
/// Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;
    public PipelineIntegrationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-pipeline-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task EndToEnd_RunsWorkAndAgentMergePhases()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello world\n"));

        var item = NewItem("feature/hello");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", "main:hello.txt");
        Assert.Equal("hello world\n", blob);

        var (_, branches, _) = await TestSupport.RunGit(barePath, "branch", "--list");
        Assert.Contains("feature/hello", branches);
    }

    [Fact]
    public async Task WorkPhasePush_WithStaleBareRepoWorkBranch_RebasesAndRetriesOnce()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fresh.txt", "fresh run\n"));

        var item = NewItem("feature/retry");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "stale.txt", "prior attempt\n", "prior attempt");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var (_, staleOnWorkBranch, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:stale.txt");
        var (_, freshOnWorkBranch, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:fresh.txt");
        var (_, staleOnMain, _) = await TestSupport.RunGit(barePath, "show", "main:stale.txt");
        var (_, freshOnMain, _) = await TestSupport.RunGit(barePath, "show", "main:fresh.txt");

        Assert.Equal("prior attempt\n", staleOnWorkBranch);
        Assert.Equal("fresh run\n", freshOnWorkBranch);
        Assert.Equal("prior attempt\n", staleOnMain);
        Assert.Equal("fresh run\n", freshOnMain);
    }

    [Fact]
    public async Task WorkPhasePush_WithStaleBareRepoWorkBranchConflict_FailsWithClearError()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "fresh run\n"));

        var item = NewItem("feature/retry-conflict");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "README.md", "prior attempt\n", "prior conflicting attempt");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains(
            "sandbox rebase conflict while reconciling push of work branch 'feature/retry-conflict'; manual resolution required",
            final.LastError);

        var (_, readmeOnWorkBranch, _) = await TestSupport.RunGit(barePath, "show", $"{item.WorkBranch}:README.md");
        var (_, readmeOnMain, _) = await TestSupport.RunGit(barePath, "show", "main:README.md");
        Assert.Equal("prior attempt\n", readmeOnWorkBranch);
        Assert.Equal("seed\n", readmeOnMain);
    }

    [Fact]
    public async Task AgentNoChange_FailsWorkItem()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        // No WorkPlan entries → ScriptedAgent throws → pipeline catches and fails.

        var item = NewItem("feature/x");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
    }

    [Fact]
    public async Task WorkBranchEqualsBaseBranch_FailsBeforeSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("x", "y"));

        var item = NewItem("main");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("must differ from baseBranch", final.LastError);
    }

    [Fact]
    public async Task MergeAgentDoesNothing_PipelineFailsVerification()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            mergeStrategy: [MergeStrategy.NoOp]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hi\n"));

        var item = NewItem("feature/hello");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("merge agent", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoWorkItems_DoNotShareBareRepoVisibility()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-iso-" + Guid.NewGuid().ToString("N")[..8]);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalGitHost>.Instance);

        var idA = WorkItemId.New();
        var idB = WorkItemId.New();
        var repoA = await gitHost.EnsureRepositoryAsync(idA, seed);
        var repoB = await gitHost.EnsureRepositoryAsync(idB, seed);
        Assert.NotEqual(repoA, repoB);

        var access = gitHost.GetSandboxAccess(repoA);
        Assert.Single(access.Mounts);
        Assert.Equal(LocalGitHost.SandboxRepoMountPath, access.Mounts[0].SandboxPath);
        Assert.Contains(repoA, access.Mounts[0].HostPath!);
        Assert.DoesNotContain(repoB, access.Mounts[0].HostPath!);
    }

    private async Task CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "stale-branch-" + Guid.NewGuid().ToString("N")[..8]);
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
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };
}
