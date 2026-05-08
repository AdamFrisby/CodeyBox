using CodeyBox.Core;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class WorkBranchRebaseOnPickupTests : IDisposable
{
    private readonly string _workspace;

    public WorkBranchRebaseOnPickupTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-work-branch-rebase-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task RebaseRetryWithFreshMain()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("codeybox/rebase-clean") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);

        var (oldB, oldC) = await CommitTwoWorkBranchCommitsAsync(barePath, item.WorkBranch!);
        await CommitToSeedAsync(seed, "main.txt", "main advanced\n", "main advanced");
        var advancedMain = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var rebasedC = await RevParseAsync(barePath, item.WorkBranch!);
        var rebasedB = await RevParseAsync(barePath, $"{item.WorkBranch}~1");
        var rebasedBase = await RevParseAsync(barePath, $"{item.WorkBranch}~2");
        Assert.Equal(advancedMain, rebasedBase);
        Assert.NotEqual(oldB, rebasedB);
        Assert.NotEqual(oldC, rebasedC);
        Assert.Equal("work B\n", await ShowAsync(barePath, $"{item.WorkBranch}:b.txt"));
        Assert.Equal("work C\n", await ShowAsync(barePath, $"{item.WorkBranch}:c.txt"));
    }

    [Fact]
    public async Task NoRebaseOnFirstPickup()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "first pickup\n"));

        var item = NewItem("codeybox/first-pickup");
        var baseTip = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("first pickup\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task RebaseConflictRoutesToScopeFenceFailureState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("codeybox/rebase-conflict") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var originalTip = await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "README.md",
            "work branch change\n",
            "work changes readme");

        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("pickup-time rebase", final.LastError);
        Assert.Equal(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal("work branch change\n", await ShowAsync(barePath, $"{item.WorkBranch}:README.md"));
    }

    [Fact]
    public async Task NoBaseAdvanceIsNoop()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("codeybox/rebase-noop") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var (_, originalTip) = await CommitTwoWorkBranchCommitsAsync(barePath, item.WorkBranch!);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
    }

    [Fact]
    public async Task PreservesAuthorship()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("codeybox/rebase-authorship") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "authored.txt",
            "authored work\n",
            "authored work",
            authorName: "Original Author",
            authorEmail: "original@example.com");
        await CommitToSeedAsync(seed, "main.txt", "main advanced\n", "main advanced");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var log = await GitStdoutAsync(barePath, "log", "-1", "--format=%an <%ae>%n%B", item.WorkBranch!);
        Assert.Contains("Original Author <original@example.com>", log);
        Assert.Contains(CodeyBoxTrailers.CoAuthoredBy, log);
    }

    [Fact]
    public async Task RetryAfterMainAdvancesEndToEnd()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("codeybox/rebase-agent-view");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "prior.txt", "prior attempt\n", "prior attempt");
        await CommitToSeedAsync(seed, "dependency.txt", "dependency landed\n", "dependency landed");

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var observed = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "git -C \"$1\" log -1 --format=%s origin/main > \"$1/observed-origin-main-subject.txt\" && git -C \"$1\" merge-base --is-ancestor origin/main HEAD",
                    "sh",
                    workingDirectory,
                ],
            }, ct);
            if (!observed.Success)
                throw new InvalidOperationException($"agent did not see rebased branch: {observed.Stderr}");
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent saw fresh main\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal("dependency landed\n", await ShowAsync(barePath, "main:observed-origin-main-subject.txt"));
        Assert.Equal("prior attempt\n", await ShowAsync(barePath, "main:prior.txt"));
        Assert.Equal("agent saw fresh main\n", await ShowAsync(barePath, "main:agent.txt"));
    }

    private async Task<(string B, string C)> CommitTwoWorkBranchCommitsAsync(string barePath, string branch)
    {
        var clone = await CloneForCommitAsync(barePath);
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        await WriteAndCommitAsync(clone, "b.txt", "work B\n", "work B");
        var b = await RevParseAsync(clone, "HEAD");
        await WriteAndCommitAsync(clone, "c.txt", "work C\n", "work C");
        var c = await RevParseAsync(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return (b, c);
    }

    private async Task<string> CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject,
        string authorName = "Test",
        string authorEmail = "test@test.com")
    {
        var clone = await CloneForCommitAsync(barePath);
        await TestSupport.RunGit(clone, "config", "user.email", authorEmail);
        await TestSupport.RunGit(clone, "config", "user.name", authorName);
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        await WriteAndCommitAsync(clone, fileName, contents, subject);
        var sha = await RevParseAsync(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return sha;
    }

    private async Task<string> CloneForCommitAsync(string barePath)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        return clone;
    }

    private static async Task WriteAndCommitAsync(string repoPath, string path, string content, string subject)
    {
        var fullPath = Path.Combine(repoPath, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task<string> ShowAsync(string repoPath, string rev)
        => await GitStdoutAsync(repoPath, "show", rev);

    private static async Task<string> RevParseAsync(string repoPath, string rev)
        => (await GitStdoutAsync(repoPath, "rev-parse", rev)).Trim();

    private static async Task<string> GitStdoutAsync(string repoPath, params string[] args)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, args);
        return stdout;
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
