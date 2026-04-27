using CodeyBox.Core;
using CodeyBox.Git;

namespace CodeyBox.Tests;

public sealed class InMemoryPullRequestServiceTests
{
    [Fact]
    public async Task Open_ReturnsPrInOpenState()
    {
        var svc = new InMemoryPullRequestService();
        var pr = await svc.OpenAsync(new OpenPullRequest("repo", "feature", "main", "t", "d"));
        Assert.Equal(PullRequestStatus.Open, pr.Status);
        Assert.Equal("feature", pr.SourceBranch);
    }

    [Fact]
    public async Task MarkMerged_UpdatesStatusAndSha()
    {
        var svc = new InMemoryPullRequestService();
        var pr = await svc.OpenAsync(new OpenPullRequest("repo", "feature", "main", "t", "d"));
        await svc.MarkMergedAsync(pr.Id, "abc1234");
        var read = await svc.GetAsync(pr.Id);
        Assert.Equal(PullRequestStatus.Merged, read!.Status);
        Assert.Equal("abc1234", read.MergeCommitSha);
    }

    [Fact]
    public async Task MarkClosed_TransitionsStatus()
    {
        var svc = new InMemoryPullRequestService();
        var pr = await svc.OpenAsync(new OpenPullRequest("repo", "feature", "main", "t", "d"));
        await svc.MarkClosedAsync(pr.Id, "obsolete");
        var read = await svc.GetAsync(pr.Id);
        Assert.Equal(PullRequestStatus.Closed, read!.Status);
    }
}
