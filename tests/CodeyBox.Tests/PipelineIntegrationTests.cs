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
