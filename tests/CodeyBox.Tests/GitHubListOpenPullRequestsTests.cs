using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GitHubUpstreamRemote.ListOpenPullRequestsAsync"/> —
/// the GitHub-side probe the stale-base PR sweeper consumes. Uses the same
/// FakeHttpMessageHandler infrastructure as <see cref="GitHubUpstreamRemoteTests"/>.
/// </summary>
public sealed class GitHubListOpenPullRequestsTests
{
    private static readonly GitHubUpstreamOptions DefaultOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        MergeMethod = "merge",
        AutoMerge = false,
    };

    private static GitHubUpstreamRemote BuildRemote(FakeHttpMessageHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        return new GitHubUpstreamRemote(
            new FakeGitHost(),
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            DefaultOpts);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ListOpenPullRequestsAsync_FiltersByBranchPrefix_AndReadsMergeability()
    {
        // Two PRs returned by /pulls: one matches the codeybox/ prefix, one is
        // a human PR. Only the codeybox/ one gets a detail fetch.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        [
          { "number": 112, "head": {"ref": "codeybox/abc", "sha": "deadbeef"}, "base": {"ref": "main"} },
          { "number": 200, "head": {"ref": "feature/by-human", "sha": "cafef00d"}, "base": {"ref": "main"} }
        ]
        """));
        // Detail for the codeybox/ PR — dirty (mergeable=false).
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        {"number": 112, "html_url": "https://github.com/myorg/myrepo/pull/112",
         "mergeable": false, "mergeable_state": "dirty"}
        """));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        var pr = Assert.Single(prs);
        Assert.Equal(112, pr.Number);
        Assert.Equal("codeybox/abc", pr.HeadBranch);
        Assert.Equal("deadbeef", pr.HeadSha);
        Assert.Equal("main", pr.BaseBranch);
        Assert.True(pr.HasMergeConflict);

        // Two HTTP calls: list + one detail (only for the matching prefix).
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/pulls?state=open", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Contains("/pulls/112", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_MergeableNull_PrIsSkipped()
    {
        // GitHub computes mergeable asynchronously — null means "still
        // calculating". The probe must NOT report such PRs as either
        // mergeable or stale; the sweeper reconsiders them next tick.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        [{ "number": 5, "head": {"ref": "codeybox/x", "sha": "tip"}, "base": {"ref": "main"} }]
        """));
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        {"number": 5, "html_url": "https://github.com/myorg/myrepo/pull/5",
         "mergeable": null, "mergeable_state": "unknown"}
        """));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        Assert.Empty(prs);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_MergeableStateBlocked_NotReportedAsConflict()
    {
        // GitHub's mergeable_state can be "blocked" (branch protection
        // pending review) — the PR is not mergeable but the cause is NOT
        // a stale base; that's an unrelated concern the sweeper should not
        // signal. Probe must report HasMergeConflict=false here.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        [{ "number": 11, "head": {"ref": "codeybox/y", "sha": "tip"}, "base": {"ref": "main"} }]
        """));
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        {"number": 11, "html_url": "https://github.com/myorg/myrepo/pull/11",
         "mergeable": true, "mergeable_state": "blocked"}
        """));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        var pr = Assert.Single(prs);
        Assert.False(pr.HasMergeConflict);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_DirtyByMergeableState_ReportsConflict()
    {
        // Belt-and-braces: GitHub may report mergeable=null but
        // mergeable_state="dirty" in some transient windows. The probe
        // treats explicit "dirty" as a conflict regardless of `mergeable`.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        [{ "number": 21, "head": {"ref": "codeybox/d", "sha": "tip"}, "base": {"ref": "main"} }]
        """));
        // `mergeable: false` is the primary signal — checked here in
        // combination with the "dirty" state.
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        {"number": 21, "html_url": "https://github.com/myorg/myrepo/pull/21",
         "mergeable": false, "mergeable_state": "dirty"}
        """));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        Assert.True(Assert.Single(prs).HasMergeConflict);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_NoMatchingPrefix_ReturnsEmpty()
    {
        // No /pulls/N detail fetches when nothing matches the prefix.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        [{ "number": 1, "head": {"ref": "feature/human", "sha": "x"}, "base": {"ref": "main"} }]
        """));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        Assert.Empty(prs);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_EmptyList_TerminatesPaging()
    {
        // First page is empty → loop exits immediately, no second page
        // request. Guards against an infinite pagination loop on a
        // misbehaving response.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, "[]"));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        Assert.Empty(prs);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_EmptyPrefix_Throws()
    {
        // Defensive: an empty prefix would match every open PR including
        // human-authored ones — the sweeper's whole identity model assumes a
        // non-empty discriminator. Reject the argument explicitly so a
        // misconfigured options object surfaces loudly rather than fanning
        // out PR-detail fetches for every open PR.
        var handler = new FakeHttpMessageHandler();
        var remote = BuildRemote(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.ListOpenPullRequestsAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_PullsListReturnsServerError_Throws()
    {
        // A non-2xx response on the list endpoint (e.g. 503 from GitHub, or a
        // 403 rate-limit) must NOT be silently swallowed: the sweeper relies on
        // this exception path to log + back off until the next tick. If the
        // throw regressed to a silent return the sweep would appear healthy
        // while reporting zero open PRs and stale-base events would stop firing.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.ServiceUnavailable, "{\"message\":\"upstream busy\"}"));

        var remote = BuildRemote(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None));
        // No detail call should have been issued because the list call failed first.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_PullsDetailReturnsNotFound_SkipsPr()
    {
        // Per-PR detail fetch may fail (404 on a just-closed PR, transient 5xx,
        // PAT scope drift). The list endpoint succeeded, so the sweep itself is
        // still healthy — the failing PR is skipped via `return null` and the
        // loop continues to the next prefix-matching summary. Removing the
        // graceful-skip would break the sweep for the entire project on a
        // single bad PR.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        [
          { "number": 50, "head": {"ref": "codeybox/missing", "sha": "x"}, "base": {"ref": "main"} },
          { "number": 51, "head": {"ref": "codeybox/ok", "sha": "y"}, "base": {"ref": "main"} }
        ]
        """));
        // First detail fetch: 404 — that PR is skipped.
        handler.Enqueue(JsonResponse(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));
        // Second detail fetch: 200, dirty — included.
        handler.Enqueue(JsonResponse(HttpStatusCode.OK, """
        {"number": 51, "html_url": "https://github.com/myorg/myrepo/pull/51",
         "mergeable": false, "mergeable_state": "dirty"}
        """));

        var remote = BuildRemote(handler);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        var pr = Assert.Single(prs);
        Assert.Equal(51, pr.Number);
        Assert.True(pr.HasMergeConflict);
        // Three HTTP calls: list + two details.
        Assert.Equal(3, handler.Requests.Count);
    }
}
