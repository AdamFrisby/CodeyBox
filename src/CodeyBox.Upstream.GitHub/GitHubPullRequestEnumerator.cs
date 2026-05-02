using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Enumerates merged pull requests between two git tags using the GitHub REST API.
/// Flow:
///   1. Compare API to get commits between fromTag and toTag.
///   2. Extract PR numbers from "Merge pull request #N" commit messages.
///   3. Fetch each PR's details (title, body, merged_at) up to the 200-PR cap.
/// </summary>
public sealed class GitHubPullRequestEnumerator : IPullRequestEnumerator
{
    private const int MaxPrs = 200;
    private const int ComparePerPage = 250;

    // Matches "Merge pull request #42 from …" and "(#42)" squash-merge format.
    private static readonly Regex MergePrPattern = new(
        @"(?:Merge pull request #(\d+)|(?:^|\s)\(#(\d+)\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubPullRequestEnumerator> _log;

    public GitHubPullRequestEnumerator(
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubPullRequestEnumerator> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<PullRequestEnumeratorResult> ListMergedBetweenAsync(
        string owner,
        string repo,
        string token,
        string fromTag,
        string toTag,
        CancellationToken ct)
    {
        // Step 1: get commit list between the two tags.
        var commits = await FetchCompareCommitsAsync(owner, repo, token, fromTag, toTag, ct);

        // Step 2: extract unique PR numbers from commit messages, preserving order.
        var prNumbers = ExtractPrNumbers(commits);

        bool wasCapped = false;
        if (prNumbers.Count > MaxPrs)
        {
            _log.LogWarning(
                "Release {Owner}/{Repo} {FromTag}→{ToTag} contains {Count} PRs, capping at {Max}",
                owner, repo, fromTag, toTag, prNumbers.Count, MaxPrs);
            prNumbers = prNumbers.Take(MaxPrs).ToList();
            wasCapped = true;
        }

        // Step 3: fetch each PR's details.
        var results = new List<MergedPullRequest>(prNumbers.Count);
        foreach (var number in prNumbers)
        {
            var pr = await FetchPullRequestAsync(owner, repo, token, number, ct);
            if (pr is not null)
                results.Add(pr);
        }

        return new PullRequestEnumeratorResult(results, wasCapped);
    }

    private async Task<List<GitHubCommit>> FetchCompareCommitsAsync(
        string owner, string repo, string token,
        string fromTag, string toTag, CancellationToken ct)
    {
        // GitHub Compare returns up to 250 commits. For very large releases this
        // may miss commits beyond the first page, but capping at 200 PRs means
        // 250 merge commits covers the vast majority of real releases.
        var url = $"https://api.github.com/repos/{owner}/{repo}/compare/{Uri.EscapeDataString(fromTag)}...{Uri.EscapeDataString(toTag)}?per_page={ComparePerPage}";
        using var req = BuildRequest(HttpMethod.Get, url, token);
        using var response = await SendAsync(req, ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "GitHub GET /compare returned {Status} for {Owner}/{Repo} {From}→{To}",
                (int)response.StatusCode, owner, repo, fromTag, toTag);
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("commits", out var commitsEl))
                return [];

            var list = new List<GitHubCommit>();
            foreach (var c in commitsEl.EnumerateArray())
            {
                var message = c.TryGetProperty("commit", out var commitEl)
                    && commitEl.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? ""
                    : "";
                list.Add(new GitHubCommit(message));
            }
            return list;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse GitHub compare response for {Owner}/{Repo}", owner, repo);
            return [];
        }
    }

    private static List<int> ExtractPrNumbers(IEnumerable<GitHubCommit> commits)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();
        foreach (var commit in commits)
        {
            foreach (Match m in MergePrPattern.Matches(commit.Message))
            {
                // Group 1 = "Merge pull request #N", group 2 = "(#N)" squash format
                var numStr = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(numStr, out var n) && seen.Add(n))
                    ordered.Add(n);
            }
        }
        return ordered;
    }

    private async Task<MergedPullRequest?> FetchPullRequestAsync(
        string owner, string repo, string token, int number, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls/{number}";
        using var req = BuildRequest(HttpMethod.Get, url, token);
        using var response = await SendAsync(req, ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogDebug(
                "GitHub GET /pulls/{Number} returned {Status} for {Owner}/{Repo}; skipping",
                number, (int)response.StatusCode, owner, repo);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var pr = JsonSerializer.Deserialize<GitHubPrDetail>(body, JsonOpts);
            if (pr is null) return null;

            return new MergedPullRequest(
                Number: pr.Number,
                Title: pr.Title ?? "",
                Body: pr.Body ?? "",
                MergedAt: pr.MergedAt ?? "",
                AuthorTrailers: [],
                ChangedFiles: []);
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "Failed to parse PR #{Number} detail for {Owner}/{Repo}; skipping", number, owner, repo);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => _httpClientFactory.CreateClient("github-upstream").SendAsync(req, ct);
}

// ── Internal DTOs ─────────────────────────────────────────────────────────────

internal sealed record GitHubCommit(string Message);

internal sealed class GitHubPrDetail
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("merged_at")] public string? MergedAt { get; set; }
}
