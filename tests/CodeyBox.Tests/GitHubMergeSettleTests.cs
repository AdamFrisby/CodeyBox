using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Post-push mergeability settle in <see cref="GitHubUpstreamRemote"/>:
/// GitHub recomputes a PR's mergeability asynchronously after a push, so a
/// merge issued ~1 s after the push observes the previous head and returns a
/// spurious 405. After the push the remote polls <c>GET /pulls/{n}</c> until
/// the reported head equals the pushed tip and <c>mergeable</c> is non-null,
/// then merges only when mergeable. Uses the shared fake
/// <see cref="HttpMessageHandler"/> (same assembly) so no real API is called.
/// </summary>
public sealed class GitHubMergeSettleTests
{
    private static readonly GitHubUpstreamOptions SettleOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        MergeMethod = "merge",
        AutoMerge = true,
    };

    private const string PushedSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StaleSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static UpstreamCompletionRequest SettleRequest() => new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/abc123",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Add feature X",
        Description = "Automated via CodeyBox",
        MergeSettleTimeout = TimeSpan.FromSeconds(5),
        MergeSettlePollInterval = TimeSpan.FromMilliseconds(10),
    };

    private static GitHubUpstreamRemote BuildRemote(
        FakeGitHost gitHost, FakeHttpMessageHandler handler) =>
        new(
            gitHost,
            new FakeHttpClientFactory(handler, userAgent: "codeybox"),
            NullLogger<GitHubUpstreamRemote>.Instance,
            SettleOpts);

    private static FakeGitHost GitHostWithTip() =>
        new() { ResolveCommits = { ["codeybox/abc123"] = PushedSha } };

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        JsonResponse(HttpStatusCode.Created,
            $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""");

    private static HttpResponseMessage PrDetailResponse(string headSha, bool? mergeable, string? state) =>
        JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            number = 99,
            html_url = "https://github.com/myorg/myrepo/pull/99",
            state = "open",
            merged = false,
            mergeable,
            mergeable_state = state,
            head = new { @ref = "codeybox/abc123", sha = headSha },
            @base = new { @ref = "main", sha = "basesha" },
        }));

    private static HttpResponseMessage MergeOkResponse(string sha) =>
        JsonResponse(HttpStatusCode.OK,
            $$"""{"sha":"{{sha}}","merged":true,"message":"Pull Request successfully merged"}""");

    private static bool IsDetailGet(HttpRequestMessage r) =>
        r.Method == HttpMethod.Get && r.RequestUri!.PathAndQuery == "/repos/myorg/myrepo/pulls/99";

    private static bool IsMergePut(HttpRequestMessage r) =>
        r.Method == HttpMethod.Put && r.RequestUri!.PathAndQuery == "/repos/myorg/myrepo/pulls/99/merge";

    [Fact]
    public async Task Settle_MergeableNullThenTrue_MergesOnceWithoutRace()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        handler.Enqueue(PrDetailResponse(PushedSha, null, "unknown"));
        handler.Enqueue(PrDetailResponse(PushedSha, true, "clean"));
        handler.Enqueue(MergeOkResponse("merge-sha-99"));

        var outcome = await BuildRemote(GitHostWithTip(), handler)
            .CompleteAsync(SettleRequest(), CancellationToken.None);

        Assert.Equal("merge-sha-99", outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);
        Assert.True(outcome.Mergeable);
        Assert.Equal("clean", outcome.MergeableState);
        // Two settle polls (unknown, then computed) before exactly one PUT.
        Assert.Equal(2, handler.Requests.Count(IsDetailGet));
        Assert.Single(handler.Requests, IsMergePut);
    }

    [Fact]
    public async Task Settle_SettledFalse_SkipsPutAndFlagsRaceWithVerdict()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        handler.Enqueue(PrDetailResponse(PushedSha, false, "dirty"));

        var outcome = await BuildRemote(GitHostWithTip(), handler)
            .CompleteAsync(SettleRequest(), CancellationToken.None);

        Assert.True(outcome.AutoMergeRaced);
        Assert.Null(outcome.MergedSha);
        Assert.False(outcome.Mergeable);
        Assert.Equal("dirty", outcome.MergeableState);
        Assert.Contains("mergeable", outcome.Notes, StringComparison.OrdinalIgnoreCase);
        // The doomed PUT is skipped: only create + the settle read.
        Assert.DoesNotContain(handler.Requests, IsMergePut);
    }

    [Fact]
    public async Task Settle_ComputedButStaleHead_WaitsForPushedHead()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        // First poll: mergeability computed, but for the PREVIOUS head — the
        // exact stale read that used to produce the spurious 405.
        handler.Enqueue(PrDetailResponse(StaleSha, true, "clean"));
        handler.Enqueue(PrDetailResponse(PushedSha, true, "clean"));
        handler.Enqueue(MergeOkResponse("merge-sha-99"));

        var outcome = await BuildRemote(GitHostWithTip(), handler)
            .CompleteAsync(SettleRequest(), CancellationToken.None);

        Assert.Equal("merge-sha-99", outcome.MergedSha);
        Assert.False(outcome.AutoMergeRaced);
        Assert.Equal(2, handler.Requests.Count(IsDetailGet));
        Assert.Single(handler.Requests, IsMergePut);
    }

    [Fact]
    public async Task Merge405_AfterSettledTrue_CarriesObservedVerdict()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        handler.Enqueue(PrDetailResponse(PushedSha, true, "clean"));
        handler.Enqueue(JsonResponse(HttpStatusCode.MethodNotAllowed,
            """{"message":"Pull Request is not mergeable"}"""));

        var outcome = await BuildRemote(GitHostWithTip(), handler)
            .CompleteAsync(SettleRequest(), CancellationToken.None);

        Assert.True(outcome.AutoMergeRaced);
        Assert.Null(outcome.MergedSha);
        Assert.True(outcome.Mergeable);
        Assert.Equal("clean", outcome.MergeableState);
        Assert.Contains("405", outcome.Notes);
    }

    [Fact]
    public async Task NoSettleTimeout_SkipsDetailPollEntirely()
    {
        // Legacy callers (no settle timeout on the request) keep the
        // historical create-then-merge sequence with no detail read.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(99, "https://github.com/myorg/myrepo/pull/99"));
        handler.Enqueue(MergeOkResponse("merge-sha-99"));

        var request = SettleRequest() with { MergeSettleTimeout = null, MergeSettlePollInterval = null };
        var outcome = await BuildRemote(GitHostWithTip(), handler)
            .CompleteAsync(request, CancellationToken.None);

        Assert.Equal("merge-sha-99", outcome.MergedSha);
        Assert.DoesNotContain(handler.Requests, IsDetailGet);
    }

    [Fact]
    public async Task GetPullRequestAsync_ExposesHeadAndMergeability()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrDetailResponse(PushedSha, false, "dirty"));

        var state = await BuildRemote(GitHostWithTip(), handler).GetPullRequestAsync(99);

        Assert.NotNull(state);
        Assert.Equal(PushedSha, state!.HeadSha);
        Assert.False(state.Mergeable);
        Assert.Equal("dirty", state.MergeableState);
    }
}
