using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class MergePhaseEndToEndPropertyTest : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-property-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task MainNeverLosesCommitsSilently()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await CommitAsync(seed, "main-before.txt", "main before\n", "main before");
        var preMergeMain = (await TestSupport.RunGit(seed, "rev-parse", "main")).stdout.Trim();

        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "property",
            Prompt = "write work",
            WorkBranch = "feature/property",
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        await TestSupport.RunGit(barePath, "merge-base", "--is-ancestor", preMergeMain, "main");
    }

    private static async Task CommitAsync(string repo, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, path), content);
        await TestSupport.RunGit(repo, "add", path);
        await TestSupport.RunGit(repo, "commit", "-m", message);
    }
}
