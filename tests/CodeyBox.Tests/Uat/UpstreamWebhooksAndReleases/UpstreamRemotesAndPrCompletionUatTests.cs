using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Tests;
using CodeyBox.Upstream;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.UpstreamWebhooksAndReleases;

/// <summary>
/// UAT coverage for "Upstream remotes and PR completion - Pushes merged work
/// to noop, generic git, or GitHub remotes".
/// Plan anchor: docs/uat/00-plan.md#upstream-webhooks-and-releases
/// </summary>
public sealed class UpstreamRemotesAndPrCompletionUatTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-upstream-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task NoopUpstream_CompletesAsSkippedWithoutBranchPush()
    {
        var remote = new NoopUpstreamRemote();

        var outcome = await remote.CompleteAsync(UpstreamWebhooksAndReleasesHelpers.Request());

        Assert.True(outcome.Skipped);
        Assert.False(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestUrl);
    }

    [Fact]
    public async Task GenericGitUpstream_PushesMergedBaseBranchToConfiguredBareRemote()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "generic-seed");
        var upstreamBare = Path.Combine(_workspace, "upstream.git");
        await TestSupport.RunGit(_workspace, "clone", "--bare", "--local", seed, upstreamBare);

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos") },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), upstreamBare, "main");
        await UpstreamWebhooksAndReleasesHelpers.CommitToBareBranchAsync(
            _workspace,
            gitHost.GetRepoPath(repoId),
            "main",
            "merged.txt",
            "merged by host\n",
            "test: merged upstream change");

        var remote = new GitGenericUpstreamRemote(
            gitHost,
            new GitGenericUpstreamOptions
            {
                UpstreamUrl = upstreamBare,
                ExtraEnvironment = new Dictionary<string, string>
                {
                    ["CODEYBOX_UAT_UPSTREAM"] = "1",
                },
            });

        var outcome = await remote.CompleteAsync(
            UpstreamWebhooksAndReleasesHelpers.Request(repositoryId: repoId, baseBranch: "main"));

        Assert.True(outcome.BranchPushed);
        var (_, pushedFile, _) = await TestSupport.RunGit(upstreamBare, "show", "main:merged.txt");
        Assert.Equal("merged by host\n", pushedFile);
    }

    [Fact]
    public async Task GitHubUpstream_PushesWorkBranchOpensPrAndAutoMergesWithConfiguredShape()
    {
        var gitHost = new CapturingGitHost();
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.Created,
            """{"number":17,"html_url":"https://github.com/owner/repo/pull/17"}"""));
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.OK,
            """
            [
              {
                "commit": {
                  "message": "feat: complete UAT upstream change\n\nCodeyBox-Prompt-Revision: 2\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"
                }
              }
            ]
            """));
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.OK,
            """{"sha":"remote-merge-sha","merged":true}"""));
        var remote = UpstreamWebhooksAndReleasesHelpers.GitHubRemote(
            gitHost,
            handler,
            UpstreamWebhooksAndReleasesHelpers.GitHubOptions() with
            {
                AutoMerge = true,
                MergeMethod = "squash",
                PullRequestTitleTemplate = "[codeybox] {title} ({branch})",
            });

        var outcome = await remote.CompleteAsync(UpstreamWebhooksAndReleasesHelpers.Request());

        var push = Assert.Single(gitHost.Pushes);
        Assert.Equal("feature/uat-upstream", push.Branch);
        Assert.Equal("https://github.com/owner/repo.git", push.UpstreamUrl);
        Assert.Contains(push.Environment, kv => kv.Key == "GIT_ASKPASS");

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("/repos/owner/repo/pulls", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Contains("/repos/owner/repo/pulls/17/commits", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);
        Assert.Contains("/repos/owner/repo/pulls/17/merge", handler.Requests[2].RequestUri!.PathAndQuery);

        using var prBody = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("[codeybox] UAT upstream change (feature/uat-upstream)",
            prBody.RootElement.GetProperty("title").GetString());
        Assert.Equal("feature/uat-upstream", prBody.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", prBody.RootElement.GetProperty("base").GetString());

        using var mergeBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("squash", mergeBody.RootElement.GetProperty("merge_method").GetString());
        Assert.Equal("[codeybox] UAT upstream change (feature/uat-upstream) (#17)",
            mergeBody.RootElement.GetProperty("commit_title").GetString());
        Assert.Contains("CodeyBox-Prompt-Revision: 2",
            mergeBody.RootElement.GetProperty("commit_message").GetString());
        Assert.Equal("https://github.com/owner/repo/pull/17", outcome.PullRequestUrl);
        Assert.Equal(17, outcome.PullRequestNumber);
        Assert.Equal("remote-merge-sha", outcome.MergedSha);
    }

    [Fact]
    public async Task GitHubUpstream_PrAlreadyExistsReturnsPartialOutcomeRatherThanDuplicateFailure()
    {
        var gitHost = new CapturingGitHost();
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(UpstreamWebhooksAndReleasesHelpers.Json(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"A pull request already exists"}]}"""));
        var remote = UpstreamWebhooksAndReleasesHelpers.GitHubRemote(gitHost, handler);

        var outcome = await remote.CompleteAsync(UpstreamWebhooksAndReleasesHelpers.Request());

        Assert.True(outcome.BranchPushed);
        Assert.Null(outcome.PullRequestNumber);
        Assert.Contains("422", outcome.Notes);
        Assert.Single(gitHost.Pushes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void GitHubUpstream_MissingTokenFailsBeforeAnyPushAttempt()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            UpstreamWebhooksAndReleasesHelpers.GitHubRemote(
                new CapturingGitHost(),
                new SequenceHttpMessageHandler(),
                UpstreamWebhooksAndReleasesHelpers.GitHubOptions() with { Token = "" }));

        Assert.Contains("PAT", ex.Message);
    }
}
