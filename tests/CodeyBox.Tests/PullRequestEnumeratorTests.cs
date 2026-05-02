using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="GitHubPullRequestEnumerator"/>.
/// Uses a fake HTTP handler to simulate the GitHub API.
/// </summary>
public sealed class PullRequestEnumeratorTests
{
    private static GitHubPullRequestEnumerator Build(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var factory = new RoutingClientFactory(handler);
        return new GitHubPullRequestEnumerator(
            factory, NullLogger<GitHubPullRequestEnumerator>.Instance);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_ParsesMergePrCommitMessages()
    {
        var compareResponse = BuildCompareResponse([
            "Merge pull request #10 from feature/foo",
            "Merge pull request #11 from bugfix/bar",
            "Just a regular commit",
        ]);
        var pr10 = BuildPrResponse(10, "Feature Foo", "body foo", "2026-04-01T00:00:00Z");
        var pr11 = BuildPrResponse(11, "Bugfix Bar", "body bar", "2026-04-02T00:00:00Z");

        var enumerator = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/compare/"))
                return Ok(compareResponse);
            if (req.RequestUri.AbsolutePath.Contains("/pulls/10"))
                return Ok(pr10);
            if (req.RequestUri.AbsolutePath.Contains("/pulls/11"))
                return Ok(pr11);
            return NotFound();
        });

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.False(result.WasCapped);
        Assert.Equal(2, result.PullRequests.Count);
        Assert.Equal(10, result.PullRequests[0].Number);
        Assert.Equal("Feature Foo", result.PullRequests[0].Title);
        Assert.Equal(11, result.PullRequests[1].Number);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_ParsesSquashMergeFormat()
    {
        // GitHub squash-merge commit messages include "(#N)" at the end.
        var compareResponse = BuildCompareResponse([
            "Add timeline UI (#16)",
            "Fix queue race condition (#17)",
        ]);
        var pr16 = BuildPrResponse(16, "Timeline UI", "body", "2026-04-01T00:00:00Z");
        var pr17 = BuildPrResponse(17, "Queue Fix", "body", "2026-04-02T00:00:00Z");

        var enumerator = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/compare/")) return Ok(compareResponse);
            if (req.RequestUri.AbsolutePath.Contains("/pulls/16")) return Ok(pr16);
            if (req.RequestUri.AbsolutePath.Contains("/pulls/17")) return Ok(pr17);
            return NotFound();
        });

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.Equal(2, result.PullRequests.Count);
        Assert.Equal(16, result.PullRequests[0].Number);
        Assert.Equal(17, result.PullRequests[1].Number);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_DeduplicatesPrNumbers()
    {
        var compareResponse = BuildCompareResponse([
            "Merge pull request #5 from feature/x",
            "Merge pull request #5 from feature/x (revert)",
        ]);
        var pr5 = BuildPrResponse(5, "Feature X", "body", "2026-04-01T00:00:00Z");
        int fetchCount = 0;

        var enumerator = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/compare/")) return Ok(compareResponse);
            if (req.RequestUri.AbsolutePath.Contains("/pulls/5")) { fetchCount++; return Ok(pr5); }
            return NotFound();
        });

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.Single(result.PullRequests);
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_SkipsMissingPrs()
    {
        var compareResponse = BuildCompareResponse([
            "Merge pull request #100 from feature/gone",
        ]);

        var enumerator = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/compare/")) return Ok(compareResponse);
            return NotFound();
        });

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.Empty(result.PullRequests);
        Assert.False(result.WasCapped);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_CapsAt200Prs()
    {
        // Build 210 merge commits.
        var messages = Enumerable.Range(1, 210)
            .Select(n => $"Merge pull request #{n} from branch/{n}")
            .ToList();
        var compareResponse = BuildCompareResponse(messages);

        var enumerator = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/compare/")) return Ok(compareResponse);
            // Extract PR number from path like /repos/owner/repo/pulls/42
            var segs = req.RequestUri.AbsolutePath.Split('/');
            if (segs[^2] == "pulls" && int.TryParse(segs[^1], out var n))
                return Ok(BuildPrResponse(n, $"PR {n}", "body", "2026-04-01T00:00:00Z"));
            return NotFound();
        });

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.Equal(200, result.PullRequests.Count);
        Assert.True(result.WasCapped);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_ReturnsEmpty_WhenCompareApiFails()
    {
        var enumerator = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.Empty(result.PullRequests);
        Assert.False(result.WasCapped);
    }

    [Fact]
    public async Task ListMergedBetweenAsync_Pagination_CommitsPresentInSinglePage()
    {
        var compareResponse = BuildCompareResponse([
            "Merge pull request #7 from feature/y",
            "Merge pull request #8 from feature/z",
        ]);
        var pr7 = BuildPrResponse(7, "PR 7", "body", "2026-04-01T00:00:00Z");
        var pr8 = BuildPrResponse(8, "PR 8", "body", "2026-04-02T00:00:00Z");
        string? capturedUrl = null;

        var enumerator = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/compare/"))
            {
                capturedUrl = req.RequestUri.ToString();
                return Ok(compareResponse);
            }
            if (req.RequestUri.AbsolutePath.Contains("/pulls/7")) return Ok(pr7);
            if (req.RequestUri.AbsolutePath.Contains("/pulls/8")) return Ok(pr8);
            return NotFound();
        });

        var result = await enumerator.ListMergedBetweenAsync(
            "owner", "repo", "token", "v1.0.0", "v1.1.0", CancellationToken.None);

        Assert.Equal(2, result.PullRequests.Count);
        // Verify the compare request included per_page parameter.
        Assert.Contains("per_page=250", capturedUrl);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildCompareResponse(IEnumerable<string> messages)
    {
        var commits = messages.Select(m => new { commit = new { message = m } });
        return JsonSerializer.Serialize(new { commits });
    }

    private static string BuildPrResponse(int number, string title, string body, string mergedAt)
        => JsonSerializer.Serialize(new
        {
            number,
            title,
            body,
            merged_at = mergedAt,
        });

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

// ── Routing fake HTTP factory ─────────────────────────────────────────────────

internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
    public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_route(request));
}

internal sealed class RoutingClientFactory : IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
    public RoutingClientFactory(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;
    public HttpClient CreateClient(string name) => new(new RoutingHandler(_route), disposeHandler: true);
}
