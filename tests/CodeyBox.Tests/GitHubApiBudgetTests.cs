using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the shared GitHub REST budget (<see cref="GitHubApiBudgetTracker"/>,
/// <see cref="GitHubRateLimitHandler"/>) and the conditional-read cache
/// (<see cref="GitHubUpstreamReadCache"/>) that keeps the stale-PR sweep at
/// O(1) requests per tick instead of one detail call per open PR.
/// </summary>
public sealed class GitHubApiBudgetTests
{
    private const string Token = "test-token-not-a-real-pat";
    private static readonly string ShaA = new('1', 40);
    private static readonly string ShaB = new('2', 40);
    private static readonly string ShaC = new('3', 40);
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly GitHubUpstreamOptions DefaultOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = Token,
        MergeMethod = "merge",
        AutoMerge = false,
    };

    private static GitHubUpstreamRemote BuildRemote(
        HttpMessageHandler handler,
        GitHubUpstreamReadCache? readCache = null,
        GitHubApiBudgetTracker? apiBudget = null,
        TimeProvider? clock = null) =>
        new(
            new FakeGitHost(),
            new FakeHttpClientFactory(handler, userAgent: "codeybox"),
            NullLogger<GitHubUpstreamRemote>.Instance,
            DefaultOpts,
            apiBudget: apiBudget,
            readCache: readCache,
            clock: clock);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private static HttpResponseMessage JsonResponseWithETag(HttpStatusCode status, string json, string etag)
    {
        var response = JsonResponse(status, json);
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
        return response;
    }

    private static long EpochSeconds(DateTimeOffset instant) => instant.ToUnixTimeSeconds();

    private static HttpResponseMessage RateLimitHeaders(
        HttpResponseMessage response, int? remaining, DateTimeOffset? reset, string? path = null)
    {
        if (remaining is { } r)
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", r.ToString());
        if (reset is { } resetAt)
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", EpochSeconds(resetAt).ToString());
        return response;
    }

    // ---------------------------------------------------------------------
    // Conditional reads: steady-state sweep cost
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fake GitHub REST endpoint honouring If-None-Match: list pages, branch
    /// tips and PR details each carry an ETag; a matching If-None-Match gets a
    /// 304 (which does not count against the rate limit). The PR set is
    /// scriptable so tests can mutate head shas between sweeps.
    /// </summary>
    private sealed class ConditionalGitHubStub : HttpMessageHandler
    {
        public sealed record PrSummary(int Number, string HeadRef, string HeadSha, string BaseRef);

        public List<PrSummary> PullRequests { get; }
        public string BaseTip { get; set; } = "base-tip-1";
        public List<HttpRequestMessage> Requests { get; } = new();
        public int DetailRequestCount =>
            Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("/pulls/"));

        public ConditionalGitHubStub(int prCount)
        {
            PullRequests = Enumerable.Range(1, prCount)
                .Select(i => new PrSummary(i, $"codeybox/pr-{i}", $"head-sha-{i}", "main"))
                .ToList();
        }

        private static bool IsNotModified(HttpRequestMessage request, string etag) =>
            request.Headers.IfNoneMatch.Any(h => h.Tag == $"\"{etag}\"");

        private static int PageOf(string query)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "page" && int.TryParse(kv[1], out var page))
                    return page;
            }
            return 1;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;

            if (path.EndsWith("/pulls", StringComparison.Ordinal))
                return Task.FromResult(ListPage(request, query));
            if (path.Contains("/branches/", StringComparison.Ordinal))
                return Task.FromResult(BranchTip(request));
            if (path.Contains("/pulls/", StringComparison.Ordinal)
                && int.TryParse(path[(path.LastIndexOf('/') + 1)..], out var number))
                return Task.FromResult(PullRequestDetail(request, number));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private HttpResponseMessage ListPage(HttpRequestMessage request, string query)
        {
            var page = PageOf(query);
            const int perPage = 100;
            var slice = PullRequests.Skip((page - 1) * perPage).Take(perPage).ToList();
            var body = "[" + string.Join(",", slice.Select(pr =>
                $$$"""{"number":{{{pr.Number}}},"head":{"ref":"{{{pr.HeadRef}}}","sha":"{{{pr.HeadSha}}}"},"base":{"ref":"{{{pr.BaseRef}}}"}}""")) + "]";
            // Content-derived ETag: any change to the page body (e.g. a pushed
            // head sha) must change the validator or 304s would hide it.
            var etag = $"list-page-{page}-{body.GetHashCode():x8}";
            if (IsNotModified(request, etag))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            return JsonResponseWithETag(HttpStatusCode.OK, body, etag);
        }

        private HttpResponseMessage BranchTip(HttpRequestMessage request)
        {
            var etag = $"branch-tip-{BaseTip}";
            if (IsNotModified(request, etag))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            return JsonResponseWithETag(
                HttpStatusCode.OK, $$$"""{"commit":{"sha":"{{{BaseTip}}}"}}""", etag);
        }

        private HttpResponseMessage PullRequestDetail(HttpRequestMessage request, int number)
        {
            var pr = PullRequests.FirstOrDefault(p => p.Number == number);
            if (pr is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var etag = $"pr-{number}-{pr.HeadSha}-{BaseTip}";
            if (IsNotModified(request, etag))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            var body = $$$"""
                {"number":{{{number}}},"html_url":"https://github.com/myorg/myrepo/pull/{{{number}}}",
                 "mergeable":true,"mergeable_state":"clean",
                 "head":{"ref":"{{{pr.HeadRef}}}","sha":"{{{pr.HeadSha}}}"},
                 "base":{"ref":"{{{pr.BaseRef}}}","sha":"{{{BaseTip}}}"}}
                """;
            return JsonResponseWithETag(HttpStatusCode.OK, body, etag);
        }
    }

    [Fact]
    public async Task Probe_150UnchangedPrs_SecondSweepCostsOnlyListAndBranchCalls()
    {
        var stub = new ConditionalGitHubStub(prCount: 150);
        var readCache = new GitHubUpstreamReadCache();

        // First sweep: every PR needs its detail once (2 list pages + 1 branch
        // tip + 150 details).
        var remote1 = BuildRemote(stub, readCache: readCache);
        var first = await remote1.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);
        Assert.Equal(150, first.Count);
        Assert.Equal(153, stub.Requests.Count);
        Assert.Equal(150, stub.DetailRequestCount);

        // Second sweep through a NEW remote sharing only the read cache —
        // exactly what IUpstreamRemoteFactory.Create produces per tick. Head
        // shas and the base tip are unchanged, so zero detail requests are
        // issued; the two list pages and the branch tip revalidate as 304.
        stub.Requests.Clear();
        var remote2 = BuildRemote(stub, readCache: readCache);
        var second = await remote2.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        Assert.Equal(150, second.Count);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(0, stub.DetailRequestCount);
        Assert.All(stub.Requests, r =>
            Assert.NotNull(r.Headers.IfNoneMatch.FirstOrDefault()));
    }

    [Fact]
    public async Task Probe_ChangedHeadSha_RefetchesOnlyThatPr()
    {
        var stub = new ConditionalGitHubStub(prCount: 150);
        var readCache = new GitHubUpstreamReadCache();

        await BuildRemote(stub, readCache: readCache)
            .ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);
        stub.Requests.Clear();

        // Operator pushes to PR #7: its head sha changes, everything else is
        // identical — exactly one detail read should be re-issued.
        var pr7 = stub.PullRequests.Single(p => p.Number == 7);
        stub.PullRequests[stub.PullRequests.IndexOf(pr7)] = pr7 with { HeadSha = "head-sha-7-v2" };

        var prs = await BuildRemote(stub, readCache: readCache)
            .ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);

        Assert.Equal(150, prs.Count);
        Assert.Equal(7, Assert.Single(prs, p => p.HeadSha == "head-sha-7-v2").Number);
        // 2 list pages (one changed → 200, one unchanged → 304) + 1 branch tip
        // (304) + exactly 1 detail fetch.
        Assert.Equal(4, stub.Requests.Count);
        Assert.Equal(1, stub.DetailRequestCount);
        Assert.Contains("/pulls/7", stub.Requests[^1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Probe_MergeableStillComputing_IsNotCached()
    {
        // A detail response with mergeable=null must not be pinned into the
        // cache: the next sweep has to re-ask or the PR would stay invisible
        // until its revisions moved.
        var inner = new FakeHttpMessageHandler();
        inner.Enqueue(JsonResponseWithETag(HttpStatusCode.OK, """
            [{ "number": 9, "head": {"ref": "codeybox/x", "sha": "tip"}, "base": {"ref": "main"} }]
            """, "list-1"));
        inner.Enqueue(JsonResponseWithETag(HttpStatusCode.OK,
            """{"commit":{"sha":"basetip"}}""", "branch-1"));
        inner.Enqueue(JsonResponseWithETag(HttpStatusCode.OK, """
            {"number": 9, "html_url": "u", "mergeable": null, "mergeable_state": "unknown"}
            """, "pr-9"));

        var readCache = new GitHubUpstreamReadCache();
        var remote = BuildRemote(inner, readCache: readCache);
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);
        Assert.Empty(prs);

        // Second sweep: list page and branch tip revalidate (304s replayed
        // from cache), but the detail MUST be fetched again — it was never
        // cached because mergeable was still computing.
        inner.Enqueue(new HttpResponseMessage(HttpStatusCode.NotModified));
        inner.Enqueue(new HttpResponseMessage(HttpStatusCode.NotModified));
        inner.Enqueue(JsonResponseWithETag(HttpStatusCode.OK, """
            {"number": 9, "html_url": "u", "mergeable": true, "mergeable_state": "clean"}
            """, "pr-9-v2"));

        var prs2 = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);
        var pr = Assert.Single(prs2);
        Assert.Equal(9, pr.Number);
        Assert.False(pr.HasMergeConflict);
        // Six requests total: 3 per sweep. The detail is refetched on the
        // second sweep because mergeable=null was never cached.
        Assert.Equal(6, inner.Requests.Count);
    }

    // ---------------------------------------------------------------------
    // Shared budget: reserve, exhaustion, secondary limits
    // ---------------------------------------------------------------------

    private static (GitHubApiBudgetTracker Tracker, HttpMessageHandler Handler, FakeHttpMessageHandler Inner) BuildBudgetStack(
        ControllableTimeProvider time, int reserve = 100, CapturingLogger<GitHubApiBudgetTracker>? log = null)
    {
        var tracker = new GitHubApiBudgetTracker(
            () => new GitHubApiBudgetOptions { BackgroundReserveRemaining = reserve },
            time,
            log ?? new CapturingLogger<GitHubApiBudgetTracker>());
        var inner = new FakeHttpMessageHandler();
        var handler = new GitHubRateLimitHandler(tracker) { InnerHandler = inner };
        return (tracker, handler, inner);
    }

    [Fact]
    public async Task Budget_BelowReserve_BackgroundDefers_PipelineProceeds()
    {
        var time = new ControllableTimeProvider(T0);
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 100);
        var remote = BuildRemote(handler, apiBudget: tracker, clock: time);

        // Prime the budget: a normal response reporting 50 remaining (below
        // the 100 reserve, above zero).
        inner.Enqueue(RateLimitHeaders(
            JsonResponse(HttpStatusCode.OK, $$$"""{"commit":{"sha":"{{{ShaA}}}"}}"""),
            remaining: 50, reset: T0 + TimeSpan.FromHours(1)));
        var sha = await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        Assert.Equal(ShaA, sha);
        Assert.Single(inner.Requests);

        // The stale-PR probe is a background read: deferred before any HTTP.
        var deferred = await Assert.ThrowsAsync<UpstreamApiDeferredException>(
            () => remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None));
        Assert.Equal(T0 + TimeSpan.FromHours(1), deferred.DeferredUntil);
        Assert.Single(inner.Requests); // no request was sent

        // The gate reports the same deferral to latency-tolerant callers.
        var gate = Assert.IsAssignableFrom<IUpstreamBackgroundReadGate>(remote);
        var info = await gate.GetBackgroundReadDeferralAsync(CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(T0 + TimeSpan.FromHours(1), info!.DeferredUntil);

        // A pipeline-class call is NOT deferred by the reserve.
        inner.Enqueue(RateLimitHeaders(
            new HttpResponseMessage(HttpStatusCode.Created),
            remaining: 49, reset: T0 + TimeSpan.FromHours(1)));
        var merged = await remote.TryMergeUpstreamBranchAsync("main", "codeybox/x", CancellationToken.None);
        Assert.True(merged);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task Budget_OperatorPullRequestList_IgnoresBackgroundReserve()
    {
        var time = new ControllableTimeProvider(T0);
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 100);
        var remote = BuildRemote(handler, apiBudget: tracker, clock: time);

        inner.Enqueue(RateLimitHeaders(
            JsonResponse(HttpStatusCode.OK, $$$"""{"commit":{"sha":"{{{ShaA}}}"}}"""),
            remaining: 50, reset: T0 + TimeSpan.FromHours(1)));
        await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        Assert.Single(inner.Requests);

        await Assert.ThrowsAsync<UpstreamApiDeferredException>(
            () => remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None));
        Assert.Single(inner.Requests);

        inner.Enqueue(JsonResponse(HttpStatusCode.OK, "[]"));
        var prs = await remote.ListOpenPullRequestsForOperatorAsync("codeybox/", CancellationToken.None);

        Assert.Empty(prs);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task Budget_403WithRemainingZero_DefersUntilReset()
    {
        var time = new ControllableTimeProvider(T0);
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 0);
        var remote = BuildRemote(handler, apiBudget: tracker, clock: time);

        var reset = T0 + TimeSpan.FromMinutes(30);
        inner.Enqueue(RateLimitHeaders(
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"API rate limit exceeded"}"""),
            },
            remaining: 0, reset: reset));

        // First call goes out and gets the 403 — the tracker records it.
        var first = await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        Assert.Null(first);
        Assert.Single(inner.Requests);

        // Every subsequent call — pipeline included — is refused until reset:
        // sending now would just produce another 403.
        var deferred = await Assert.ThrowsAsync<UpstreamApiDeferredException>(
            () => remote.GetBranchHeadShaAsync("main", CancellationToken.None));
        Assert.Equal(reset, deferred.DeferredUntil);
        Assert.Single(inner.Requests);

        var deferredMerge = await Assert.ThrowsAsync<UpstreamApiDeferredException>(
            () => remote.TryMergeUpstreamBranchAsync("main", "codeybox/x", CancellationToken.None));
        Assert.Equal(reset, deferredMerge.DeferredUntil);
        Assert.Single(inner.Requests);

        // Past the reset the budget is usable again.
        time.Advance(TimeSpan.FromMinutes(31));
        inner.Enqueue(JsonResponse(HttpStatusCode.OK, $$$"""{"commit":{"sha":"{{{ShaB}}}"}}"""));
        var sha = await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        Assert.Equal(ShaB, sha);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task Budget_ReserveIgnoredOnceWindowReset()
    {
        var time = new ControllableTimeProvider(T0);
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 100);
        var remote = BuildRemote(handler, apiBudget: tracker, clock: time);

        // Prime the budget: 50 remaining (below the 100 reserve) in a window
        // that resets at T0+30m.
        inner.Enqueue(RateLimitHeaders(
            JsonResponse(HttpStatusCode.OK, """{"commit":{"sha":"x"}}"""),
            remaining: 50, reset: T0 + TimeSpan.FromMinutes(30)));
        await remote.GetBranchHeadShaAsync("main", CancellationToken.None);

        // While the window is live the background read defers.
        await Assert.ThrowsAsync<UpstreamApiDeferredException>(
            () => remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None));

        // Past the reset the stored Remaining is stale — the real budget is
        // unknown until the next response — so background reads must proceed
        // rather than defer forever.
        time.Advance(TimeSpan.FromMinutes(31));
        inner.Enqueue(JsonResponse(HttpStatusCode.OK, "[]"));
        var prs = await remote.ListOpenPullRequestsAsync("codeybox/", CancellationToken.None);
        Assert.Empty(prs);

        var gate = Assert.IsAssignableFrom<IUpstreamBackgroundReadGate>(remote);
        Assert.Null(await gate.GetBackgroundReadDeferralAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Budget_BackgroundGate_NeverResolvesToken_AndSharesAcrossRemoteInstances()
    {
        var time = new ControllableTimeProvider(T0);
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 100);
        var provider = new CountingTokenProvider("gate-token");
        var opts = DefaultOpts with { TokenProvider = provider };
        var remote = new GitHubUpstreamRemote(
            new FakeGitHost(),
            new FakeHttpClientFactory(handler, userAgent: "codeybox"),
            NullLogger<GitHubUpstreamRemote>.Instance,
            opts,
            apiBudget: tracker,
            clock: time);

        // Before any send the gate reports "proceed" WITHOUT resolving the
        // token — GetTokenAsync can POST an App installation-token exchange,
        // which the gate contract forbids.
        var gate = Assert.IsAssignableFrom<IUpstreamBackgroundReadGate>(remote);
        Assert.Null(await gate.GetBackgroundReadDeferralAsync(CancellationToken.None));
        Assert.Equal(0, provider.Calls);

        // A pipeline send registers the credential→budget-key mapping.
        inner.Enqueue(RateLimitHeaders(
            JsonResponse(HttpStatusCode.OK, """{"commit":{"sha":"x"}}"""),
            remaining: 50, reset: T0 + TimeSpan.FromHours(1)));
        await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        Assert.Equal(1, provider.Calls);

        // The gate now reports the deferral — still without touching the
        // token provider.
        var deferral = await gate.GetBackgroundReadDeferralAsync(CancellationToken.None);
        Assert.NotNull(deferral);
        Assert.Equal(T0 + TimeSpan.FromHours(1), deferral!.DeferredUntil);
        Assert.Equal(1, provider.Calls);

        // A NEW remote instance for the same credential (what the factory
        // produces per call) sees the same deferral — the mapping lives in
        // the shared tracker, not the remote.
        var provider2 = new CountingTokenProvider("gate-token");
        var remote2 = new GitHubUpstreamRemote(
            new FakeGitHost(),
            new FakeHttpClientFactory(handler, userAgent: "codeybox"),
            NullLogger<GitHubUpstreamRemote>.Instance,
            DefaultOpts with { TokenProvider = provider2 },
            apiBudget: tracker,
            clock: time);
        var gate2 = Assert.IsAssignableFrom<IUpstreamBackgroundReadGate>(remote2);
        Assert.NotNull(await gate2.GetBackgroundReadDeferralAsync(CancellationToken.None));
        Assert.Equal(0, provider2.Calls);
    }

    private sealed class CountingTokenProvider(string token) : IGitHubTokenProvider
    {
        public int Calls;
        public string CredentialFingerprint => "counting-provider";
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(token);
        }
    }

    [Fact]
    public void Budget_ObservedPaths_RedactCredentialAndIdSegments()
    {
        var tracker = new GitHubApiBudgetTracker();
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        // The App-manifest conversion exchange puts a caller-supplied code
        // in the path; it must never reach TopConsumers, logs, or the admin
        // endpoint.
        tracker.ObserveResponse("k", response, "/app-manifests/S3cr3tM4n1f3stC0d3XyZ9/conversions");
        // A commit sha path collapses the same way.
        tracker.ObserveResponse("k", response,
            "/repos/myorg/myrepo/commits/0123456789abcdef0123456789abcdef01234567/check-runs");
        // Ordinary endpoints keep their readable shape.
        tracker.ObserveResponse("k", response, "/repos/myorg/myrepo/pulls/112");

        var snapshot = Assert.Single(tracker.Snapshot());
        var paths = snapshot.TopConsumers.Select(kv => kv.Key).ToList();
        Assert.Contains("/app-manifests/#/conversions", paths);
        Assert.Contains("/repos/myorg/myrepo/commits/#/check-runs", paths);
        Assert.Contains("/repos/myorg/myrepo/pulls/#", paths);
        Assert.All(paths, p => Assert.DoesNotContain("S3cr3tM4n1f3stC0d3XyZ9", p));
        Assert.All(paths, p => Assert.DoesNotContain("0123456789abcdef", p));
    }

    [Fact]
    public async Task Budget_SecondaryLimit_RetryAfter_Defers()
    {
        var time = new ControllableTimeProvider(T0);
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 0);
        var remote = BuildRemote(handler, apiBudget: tracker, clock: time);

        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"message":"You have exceeded a secondary rate limit"}"""),
        };
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        inner.Enqueue(limited);

        await remote.GetBranchHeadShaAsync("main", CancellationToken.None);

        var deferred = await Assert.ThrowsAsync<UpstreamApiDeferredException>(
            () => remote.GetBranchHeadShaAsync("main", CancellationToken.None));
        Assert.Equal(T0 + TimeSpan.FromSeconds(120), deferred.DeferredUntil);
        Assert.Single(inner.Requests);

        time.Advance(TimeSpan.FromSeconds(121));
        inner.Enqueue(JsonResponse(HttpStatusCode.OK, $$$"""{"commit":{"sha":"{{{ShaC}}}"}}"""));
        Assert.Equal(ShaC, await remote.GetBranchHeadShaAsync("main", CancellationToken.None));
    }

    [Fact]
    public async Task Budget_LowBudgetWarning_LoggedOncePerEpisode()
    {
        var time = new ControllableTimeProvider(T0);
        var log = new CapturingLogger<GitHubApiBudgetTracker>();
        var (tracker, handler, inner) = BuildBudgetStack(time, reserve: 100, log: log);
        var remote = BuildRemote(handler, apiBudget: tracker, clock: time);

        // Two degraded observations in a row → exactly one warning.
        for (var i = 0; i < 2; i++)
        {
            inner.Enqueue(RateLimitHeaders(
                JsonResponse(HttpStatusCode.OK, """{"commit":{"sha":"x"}}"""),
                remaining: 40, reset: T0 + TimeSpan.FromHours(1)));
            await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        }

        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("40", warning.Message);
        Assert.Contains("top consumers", warning.Message, StringComparison.OrdinalIgnoreCase);

        // Budget recovers above the reserve, then drops again → a fresh warning.
        inner.Enqueue(RateLimitHeaders(
            JsonResponse(HttpStatusCode.OK, """{"commit":{"sha":"x"}}"""),
            remaining: 4000, reset: T0 + TimeSpan.FromHours(1)));
        await remote.GetBranchHeadShaAsync("main", CancellationToken.None);
        inner.Enqueue(RateLimitHeaders(
            JsonResponse(HttpStatusCode.OK, """{"commit":{"sha":"x"}}"""),
            remaining: 30, reset: T0 + TimeSpan.FromHours(1)));
        await remote.GetBranchHeadShaAsync("main", CancellationToken.None);

        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Warning));

        // The admin status endpoint reads Snapshot(): one bucket keyed by the
        // token hash (never the token), with remaining/reset/block state.
        var snapshot = Assert.Single(tracker.Snapshot());
        Assert.Equal(GitHubApiBudgetTracker.BudgetKeyForToken(Token), snapshot.BudgetKey);
        Assert.False(snapshot.BudgetKey.Contains(Token, StringComparison.Ordinal));
        Assert.Equal(30, snapshot.Remaining);
        Assert.Equal(T0 + TimeSpan.FromHours(1), snapshot.ResetAt);
        Assert.NotEmpty(snapshot.TopConsumers);
    }

    // ---------------------------------------------------------------------
    // Hot-reloadable CheckInterval
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sweeper_CheckIntervalChange_TakesEffectWithoutRestart()
    {
        var time = new ControllableTimeProvider(T0);
        var project = new Project
        {
            Id = new ProjectId("budget-project"),
            DisplayName = "budget-project",
            RepositoryUrl = "https://github.com/example/repo",
            Upstream = new ProjectUpstream
            {
                Kind = "github",
                GitHubOwner = "example",
                GitHubRepository = "repo",
                TokenEnvVar = "FAKE_TOKEN",
            },
        };
        var remote = new ScriptedUpstreamRemote();
        var opts = new StalePullRequestSweeperOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromSeconds(60),
            BranchPrefix = "codeybox/",
        };
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(project),
            new ScriptedUpstreamFactory(remote),
            new CapturingWebhookDispatcher(),
            () => opts,
            NullLogger<StalePullRequestSweeper>.Instance,
            time);

        using var cts = new CancellationTokenSource();
        await sweeper.StartAsync(cts.Token);
        try
        {
            // Stagger (5 s) then first sweep. The clock advances in 1 s
            // slices and each slice waits on the remote's call signal —
            // push-based, so a starved thread pool cannot race fake time
            // ahead of the sweep it is meant to observe.
            await AdvanceUntilAsync(time, remote.WaitForListCallAsync(1, cts.Token));
            Assert.Equal(1, remote.ListCallCount);

            // Shrink the interval mid-flight: the in-progress wait re-reads
            // options every slice (≤5 s), so the next sweep lands at
            // ~cycleStart+30s rather than ~cycleStart+60s. The assertion is on
            // the fake-clock instant the second sweep ran — a stale-interval
            // regression would land ~25 s later.
            opts.CheckInterval = TimeSpan.FromSeconds(30);
            await AdvanceUntilAsync(time, remote.WaitForListCallAsync(2, cts.Token));
            var secondSweepAt = time.GetUtcNow();
            Assert.True(
                secondSweepAt <= T0 + TimeSpan.FromSeconds(45),
                $"second sweep ran at {secondSweepAt:O}, expected ≤ {T0 + TimeSpan.FromSeconds(45):O} " +
                "(a stale 60 s interval would land near T0+65s)");

            // And it keeps the NEW cadence: another ~30 s → third sweep.
            await AdvanceUntilAsync(time, remote.WaitForListCallAsync(3, cts.Token));
            var thirdSweepAt = time.GetUtcNow();
            Assert.True(
                thirdSweepAt - secondSweepAt <= TimeSpan.FromSeconds(40),
                $"sweep cadence {(thirdSweepAt - secondSweepAt).TotalSeconds:F0}s exceeds the reloaded 30 s interval");
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Advances the fake clock in 1 s slices until <paramref name="signal"/>
    /// completes, yielding a short real-time window each slice so timer
    /// continuations can run and schedule their next delay. Stops advancing
    /// the moment the signal fires, so the fake instant observed afterwards
    /// is the one the code under test actually ran at.
    /// </summary>
    private static async Task AdvanceUntilAsync(
        ControllableTimeProvider time, Task signal, int maxSteps = 400)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (signal.IsCompleted) return;
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(signal, Task.Delay(50));
        }
        if (!signal.IsCompleted)
            throw new TimeoutException("Signal was not observed within the advanced fake-time window");
    }
}
