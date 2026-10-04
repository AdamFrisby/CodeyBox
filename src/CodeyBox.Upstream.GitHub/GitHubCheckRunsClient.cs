using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Low-level GitHub Checks API transport for audit check runs, implementing
/// <see href="https://docs.github.com/en/rest/checks/runs">the check-runs
/// contract</see> (API version <c>2022-11-28</c>). Uses the same protected
/// credential plumbing as <see cref="GitHubUpstreamRemote"/>: the token is
/// resolved per call from <see cref="IGitHubTokenProvider"/>, carried only as
/// a per-request header, and never logged.
///
/// Error mapping: 401 and non-rate-limit 403 become
/// <see cref="AuditCheckAuthException"/> (actionable blocked state, never
/// retried); 429 and rate-limit 403 become
/// <see cref="AuditCheckRateLimitedException"/> honoring <c>Retry-After</c>;
/// 422 becomes <see cref="AuditCheckValidationException"/>; 5xx and network
/// failures become <see cref="AuditCheckTransientException"/>. Caller
/// cancellation is never wrapped — it propagates unchanged.
/// </summary>
public sealed class GitHubCheckRunsClient
{
    /// <summary>GitHub REST API version this client implements against.</summary>
    public const string GitHubApiVersion = "2022-11-28";

    private const int MaxListPages = 5;
    private const int ListPageSize = 100;
    private const int MaxErrorBodyChars = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubTokenProvider _tokenProvider;
    private readonly string _owner;
    private readonly string _repository;
    private readonly string _apiBase;
    private readonly ILogger _log;

    /// <param name="apiBaseUrl">
    /// API base override (e.g. a GitHub Enterprise Server <c>https://host/api/v3</c>
    /// or an isolated loopback fixture in tests). Defaults to
    /// <c>https://api.github.com</c>.
    /// </param>
    public GitHubCheckRunsClient(
        IHttpClientFactory httpClientFactory,
        IGitHubTokenProvider tokenProvider,
        string owner,
        string repository,
        ILogger? log = null,
        string? apiBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _owner = owner;
        _repository = repository;
        _apiBase = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "https://api.github.com"
            : apiBaseUrl.TrimEnd('/');
        _log = log ?? NullLogger<GitHubCheckRunsClient>.Instance;
    }

    /// <summary>
    /// Rejects requests addressed to a different repository than this client
    /// is configured for. A fork or typo must never receive another project's
    /// audit verdict (and a green check must never land on the wrong repo).
    /// Comparison is ordinal case-insensitive, matching GitHub's own owner/repo
    /// handling; anything else is a validation failure, not a retry.
    /// </summary>
    public void ValidateRepositoryMatch(AuditCheckPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Owner, _owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Repository, _repository, StringComparison.OrdinalIgnoreCase))
            throw new AuditCheckValidationException(
                $"Audit check request targets {request.Owner}/{request.Repository} but this " +
                $"publisher is configured for {_owner}/{_repository}; refusing to publish " +
                "an audit verdict to a repository that was not audited.");
    }

    public async Task<GitHubCheckRun> CreateCheckRunAsync(
        GitHubCreateCheckRunRequest body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var url = $"{_apiBase}/repos/{_owner}/{_repository}/check-runs";
        using var req = await BuildRequestAsync(HttpMethod.Post, url, ct).ConfigureAwait(false);
        req.Content = JsonContent.Create(body);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(resp, "POST /check-runs", ct).ConfigureAwait(false);
        try
        {
            var run = await resp.Content.ReadFromJsonAsync<GitHubCheckRun>(ct).ConfigureAwait(false);
            return run ?? throw new AuditCheckTransientException("GitHub POST /check-runs returned an empty body.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or OperationCanceledException)
        {
            throw new AuditCheckTransientException(
                "GitHub POST /check-runs returned an unreadable body; the write may have landed.", ex);
        }
    }

    public async Task<GitHubCheckRun> UpdateCheckRunAsync(
        long checkRunId,
        GitHubUpdateCheckRunRequest body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (checkRunId <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkRunId), "Check-run id must be positive.");
        var url = $"{_apiBase}/repos/{_owner}/{_repository}/check-runs/{checkRunId}";
        using var req = await BuildRequestAsync(new HttpMethod("PATCH"), url, ct).ConfigureAwait(false);
        req.Content = JsonContent.Create(body);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(resp, $"PATCH /check-runs/{checkRunId}", ct).ConfigureAwait(false);
        try
        {
            var run = await resp.Content.ReadFromJsonAsync<GitHubCheckRun>(ct).ConfigureAwait(false);
            return run ?? throw new AuditCheckTransientException(
                $"GitHub PATCH /check-runs/{checkRunId} returned an empty body.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or OperationCanceledException)
        {
            throw new AuditCheckTransientException(
                $"GitHub PATCH /check-runs/{checkRunId} returned an unreadable body; the write may have landed.", ex);
        }
    }

    /// <summary>
    /// Lists check runs for a commit ref, newest pages first, bounded to
    /// <c>MaxListPages</c> so a pathological ref cannot fan out API calls.
    /// Used to reconcile the intended check run by <c>external_id</c> before
    /// retrying a create after a lost response.
    /// </summary>
    public async Task<IReadOnlyList<GitHubCheckRun>> ListCheckRunsForRefAsync(
        string headSha,
        string? checkName,
        CancellationToken ct = default)
    {
        AuditCheckPayloadBuilder.ValidateHeadSha(headSha);
        var runs = new List<GitHubCheckRun>();
        for (var page = 1; page <= MaxListPages; page++)
        {
            var url = $"{_apiBase}/repos/{_owner}/{_repository}/commits/{headSha}/check-runs" +
                $"?per_page={ListPageSize}&page={page}" +
                (string.IsNullOrWhiteSpace(checkName)
                    ? string.Empty
                    : $"&check_name={Uri.EscapeDataString(checkName)}");
            using var req = await BuildRequestAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            await ThrowIfFailedAsync(resp, $"GET /commits/{headSha}/check-runs", ct).ConfigureAwait(false);
            var page_result = await ReadJsonAsync<GitHubCheckRunsListResponse>(resp, $"GET /commits/{headSha}/check-runs", ct)
                .ConfigureAwait(false);
            var items = page_result?.CheckRuns ?? [];
            runs.AddRange(items);
            if (items.Count < ListPageSize)
                break;
        }
        return runs;
    }

    /// <summary>
    /// Lists annotations already stored on a check run (bounded pages). Used
    /// to reconcile ambiguous batch writes: batches proven present are
    /// skipped instead of duplicated.
    /// </summary>
    public async Task<IReadOnlyList<GitHubCheckAnnotationItem>> ListAnnotationsAsync(
        long checkRunId,
        CancellationToken ct = default)
    {
        if (checkRunId <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkRunId), "Check-run id must be positive.");
        var items = new List<GitHubCheckAnnotationItem>();
        for (var page = 1; page <= MaxListPages; page++)
        {
            var url = $"{_apiBase}/repos/{_owner}/{_repository}/check-runs/{checkRunId}/annotations" +
                $"?per_page={ListPageSize}&page={page}";
            using var req = await BuildRequestAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            await ThrowIfFailedAsync(resp, $"GET /check-runs/{checkRunId}/annotations", ct).ConfigureAwait(false);
            var batch = await ReadJsonAsync<GitHubCheckAnnotationItem[]>(resp, $"GET /check-runs/{checkRunId}/annotations", ct)
                .ConfigureAwait(false);
            if (batch is null || batch.Length == 0)
                break;
            items.AddRange(batch);
            if (batch.Length < ListPageSize)
                break;
        }
        return items;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        var token = await _tokenProvider.GetTokenAsync(ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await _httpClientFactory.CreateClient("github-upstream")
                .SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new AuditCheckTransientException(
                "GitHub Checks request timed out; the write may or may not have landed.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AuditCheckTransientException(
                $"GitHub Checks transport failure ({ex.Message}); the write may or may not have landed.", ex);
        }
    }

    private async Task ThrowIfFailedAsync(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        var status = (int)resp.StatusCode;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuditCheckAuthException(
                $"{operation} returned 401: bad or revoked credential. " +
                "Check the configured PAT/GitHub App installation; publication is blocked, not retrying. " +
                "Supported credentials: fine-grained PAT with Checks read+write on the repository, " +
                "classic PAT with repo scope, or a GitHub App installation token with checks:write.");

        var retryAfter = ParseRetryAfter(resp);
        var errorBody = await ReadErrorBodyAsync(resp, ct).ConfigureAwait(false);
        if (resp.StatusCode == (HttpStatusCode)429 || IsRateLimit(resp, errorBody))
            throw new AuditCheckRateLimitedException(
                $"{operation} was rate-limited (HTTP {status}). Backing off" +
                (retryAfter.HasValue ? $" for {retryAfter.Value.TotalSeconds:F0}s per Retry-After." : "."),
                retryAfter);

        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new AuditCheckAuthException(
                $"{operation} returned 403: the credential lacks Checks write permission on " +
                $"{_owner}/{_repository}, or the repository is inaccessible (SSO, suspension, or wrong " +
                "installation). Grant checks:write (App) or the Checks read+write repository permission " +
                "(fine-grained PAT); publication is blocked, not retrying.");

        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
            throw new AuditCheckValidationException(
                $"{operation} returned 422: {errorBody}");

        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            throw new AuditCheckValidationException(
                $"{operation} returned HTTP {status}: {errorBody}");

        if ((int)resp.StatusCode >= 500)
            throw new AuditCheckTransientException(
                $"{operation} returned HTTP {status} from the forge; retryable with backoff.");

        throw new AuditCheckTransientException($"{operation} returned unexpected HTTP {status}; retryable.");
    }

    private static bool IsRateLimit(HttpResponseMessage resp, string errorBody)
    {
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.Any(v => v.Trim() == "0"))
            return true;
        return errorBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("secondary rate", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or OperationCanceledException)
        {
            throw new AuditCheckTransientException($"{operation} returned an unreadable body.", ex);
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Retry-After", out var values))
            return null;
        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;
        if (int.TryParse(raw, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
        if (DateTimeOffset.TryParse(raw, out var date))
        {
            var delta = date - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Forge error bodies are untrusted: strip control characters and cap length.
            var clean = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
            return clean.Length > MaxErrorBodyChars ? clean[..MaxErrorBodyChars] : clean;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return "(unreadable error body)";
        }
    }
}

/// <summary>Forge check-run identity as returned by the GitHub Checks API.</summary>
public sealed record GitHubCheckRun(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("conclusion")] string? Conclusion = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("external_id")] string? ExternalId = null,
    [property: JsonPropertyName("head_sha")] string? HeadSha = null);

public sealed record GitHubCheckRunsListResponse(
    [property: JsonPropertyName("total_count")] int TotalCount = 0,
    [property: JsonPropertyName("check_runs")] IReadOnlyList<GitHubCheckRun>? CheckRuns = null);

public sealed record GitHubCheckAnnotationItem(
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("start_line")] int StartLine = 0,
    [property: JsonPropertyName("end_line")] int EndLine = 0,
    [property: JsonPropertyName("annotation_level")] string? Level = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record GitHubCreateCheckRunRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("head_sha")] string HeadSha,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("external_id")] string ExternalId,
    [property: JsonPropertyName("output")] GitHubCheckRunOutput Output,
    [property: JsonPropertyName("conclusion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Conclusion = null,
    [property: JsonPropertyName("started_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StartedAt = null,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CompletedAt = null,
    [property: JsonPropertyName("details_url")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DetailsUrl = null);

public sealed record GitHubUpdateCheckRunRequest(
    [property: JsonPropertyName("status")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Status = null,
    [property: JsonPropertyName("conclusion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Conclusion = null,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CompletedAt = null,
    [property: JsonPropertyName("output")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GitHubCheckRunOutput? Output = null,
    [property: JsonPropertyName("details_url")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DetailsUrl = null);

public sealed record GitHubCheckRunOutput(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("annotations")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GitHubCheckAnnotationRequest>? Annotations = null);

public sealed record GitHubCheckAnnotationRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("annotation_level")] string Level,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message);
