using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when a project's effective git identity changes between two
/// work items — e.g. the project gains a per-project override after the first
/// item was processed — each item's commits are authored under the identity
/// that was active when that item ran.
/// </summary>
[Collection("Pipeline integration")]
public sealed class RetryAcrossIdentityChangeTests : IDisposable
{
    private readonly string _workspace;
    public RetryAcrossIdentityChangeTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-idchange-").FullName;
    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace); }

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

    /// <summary>
    /// Item 1 runs with only the host identity active (no project override).
    /// Item 2 runs against a project that has a per-project override set.
    /// The two work items must produce commits with different author identities.
    /// </summary>
    [Fact]
    public async Task TwoItems_DifferentEffectiveIdentity_ProduceDifferentCommitAuthors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var hostId = new HostGitIdentity("Host Author", "host@idchange.test");

        // ── Item 1: no project override → commits use host identity ──────────
        using var tp1 = TestSupport.BuildPipeline(_workspace, seed,
            hostGitIdentity: hostId);
        tp1.Agent.WorkPlan.Enqueue(new FileWrite("item1.txt", "item1\n"));
        var item1 = NewItem("feature/item1");
        await tp1.Store.CreateAsync(item1);
        await tp1.Pipeline.RunAsync(item1, CancellationToken.None);

        var final1 = await tp1.Store.GetAsync(item1.Id);
        Assert.Equal(WorkItemState.Done, final1!.State);

        var barePath1 = Path.Combine(tp1.GitRoot, item1.Id + ".git");
        var (_, authorLog1, _) = await TestSupport.RunGit(barePath1, "log", "--format=%an|%ae", "--all");
        Assert.Contains("Host Author|host@idchange.test", authorLog1);

        // ── Item 2: project override set → commits use project identity ───────
        using var tp2 = TestSupport.BuildPipeline(_workspace, seed,
            hostGitIdentity: hostId,
            projectGitAuthor: ("Project Override Author", "override@idchange.test"));
        tp2.Agent.WorkPlan.Enqueue(new FileWrite("item2.txt", "item2\n"));
        var item2 = NewItem("feature/item2");
        await tp2.Store.CreateAsync(item2);
        await tp2.Pipeline.RunAsync(item2, CancellationToken.None);

        var final2 = await tp2.Store.GetAsync(item2.Id);
        Assert.Equal(WorkItemState.Done, final2!.State);

        var barePath2 = Path.Combine(tp2.GitRoot, item2.Id + ".git");
        var (_, authorLog2, _) = await TestSupport.RunGit(barePath2, "log", "--format=%an|%ae", "--all");
        Assert.Contains("Project Override Author|override@idchange.test", authorLog2);
        Assert.DoesNotContain("Host Author", authorLog2);

        // The two items must have produced commits under different identities.
        Assert.NotEqual(authorLog1.Trim(), authorLog2.Trim());
    }
}
