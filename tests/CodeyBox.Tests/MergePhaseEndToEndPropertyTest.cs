using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class MergePhaseEndToEndPropertyTest : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-property-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MainNeverLosesCommitsSilently(int mainCommitCount)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, $"seed-{mainCommitCount}");
        for (var i = 0; i < mainCommitCount; i++)
            await CommitAsync(seed, $"main-before-{i}.txt", $"main before {i}\n", $"main before {i}");
        var mainCommitsBeforeMerge = (await TestSupport.RunGit(seed, "rev-list", "main"))
            .stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite($"work-{mainCommitCount}.txt", $"work {mainCommitCount}\n"));
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = $"property {mainCommitCount}",
            Prompt = "write work",
            WorkBranch = $"feature/property-{mainCommitCount}",
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        foreach (var commit in mainCommitsBeforeMerge)
            await TestSupport.RunGit(barePath, "merge-base", "--is-ancestor", commit, "main");
    }

    private static async Task CommitAsync(string repo, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, path), content);
        await TestSupport.RunGit(repo, "add", path);
        await TestSupport.RunGit(repo, "commit", "-m", message);
    }
}
