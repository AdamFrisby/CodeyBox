using CodeyBox.Core;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

public sealed class GitGenericUpstreamRemoteTests
{
    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature Y",
        Description = "Automated via CodeyBox",
    };

    [Fact]
    public async Task CompleteAsync_PushesBaseBranchAndReturnsBranchPushed()
    {
        var gitHost = new FakeGitHost();
        var opts = new GitGenericUpstreamOptions { UpstreamUrl = "https://git.example.com/repo.git" };
        var remote = new GitGenericUpstreamRemote(gitHost, opts);

        var outcome = await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestUrl);
        Assert.Null(outcome.MergedSha);
        Assert.Single(gitHost.Pushes);
        Assert.Equal(SampleRequest.BaseBranch, gitHost.Pushes[0].Branch);
        Assert.Equal(opts.UpstreamUrl, gitHost.Pushes[0].Url);
        Assert.Equal(UpstreamPushReconcileStrategy.Merge, gitHost.Pushes[0].ReconcileStrategy);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesExceptionForRetry()
    {
        var gitHost = new ThrowingGitHost();
        var opts = new GitGenericUpstreamOptions { UpstreamUrl = "https://git.example.com/repo.git" };
        var remote = new GitGenericUpstreamRemote(gitHost, opts);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            remote.CompleteAsync(SampleRequest, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_UrlWithEmbeddedCredentials_ScrubbedFromExceptionMessage()
    {
        // Verifies that an operator-supplied URL with embedded user:pass does not
        // surface credentials in the exception message that reaches the orchestrator log.
        var gitHost = new ThrowingGitHost();
        var urlWithCreds = "https://user:secret@git.example.com/repo.git";
        var opts = new GitGenericUpstreamOptions { UpstreamUrl = urlWithCreds };
        var remote = new GitGenericUpstreamRemote(gitHost, opts);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.CompleteAsync(SampleRequest, CancellationToken.None));

        Assert.DoesNotContain("secret", ex.Message);
        Assert.Contains("git.example.com", ex.Message); // host still present for diagnostics
    }
}

internal sealed class ThrowingGitHost : IGitHost
{
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => throw new InvalidOperationException("simulated push failure");
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}
