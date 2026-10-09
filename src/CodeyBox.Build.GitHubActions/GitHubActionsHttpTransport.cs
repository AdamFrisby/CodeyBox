using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Upstream.GitHub;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Credential seam so the HTTP transport reuses the existing GitHub
/// credential identity (<see cref="IGitHubTokenProvider"/>) without
/// duplicating forge plumbing. The token is carried only as a per-request
/// header and never logged.
/// </summary>
public interface IGitHubActionsCredentialProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken ct = default);
}

/// <summary>Wraps the shared GitHub token provider (App installation token or PAT source).</summary>
public sealed class GitHubTokenCredentialAdapter(IGitHubTokenProvider inner) : IGitHubActionsCredentialProvider
{
    public async ValueTask<string> GetTokenAsync(CancellationToken ct = default) =>
        await inner.GetTokenAsync(ct).ConfigureAwait(false);
}

/// <summary>
/// Production <see cref="IGitHubActionsTransport"/> over the GitHub Actions
/// REST API (https://docs.github.com/en/rest/actions/workflows,
/// /workflow-runs, /artifacts; version <c>2022-11-28</c>).
///
/// Dispatch answers carry no run id: any 2xx (200/201/202/204 — the actual
/// code is recorded, never assumed to be the historical 204) yields an
/// uncertain outcome that the framework reconciles by correlation before any
/// retry. Rate limits surface as <see cref="ExternalBuildRateLimitedException"/>
/// honoring Retry-After; 401/non-rate-limit 403 as auth (blocked, not
/// retried); 404/422 as validation; 5xx/network/timeout as transient.
/// Caller cancellation propagates unchanged. No log or annotation endpoint
/// is called by any route here.
/// </summary>
public sealed class GitHubActionsHttpTransport : IGitHubActionsTransport
{
    private const int MaxListPages = 3;
    private const int ListPageSize = 30;
    private const int MaxErrorBodyChars = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubActionsCredentialProvider _credentials;
    private readonly Func<GitHubActionsExternalBuildOptions> _options;
    private readonly TimeProvider _clock;
    private readonly HttpMessageHandler? _downloadHandler;

    public GitHubActionsHttpTransport(
        IHttpClientFactory httpClientFactory,
        IGitHubActionsCredentialProvider credentials,
        Func<GitHubActionsExternalBuildOptions> options,
        TimeProvider? clock = null,
        HttpMessageHandler? downloadHandler = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        _downloadHandler = downloadHandler;
    }

    public async Task<GitHubActionsDispatchOutcome> DispatchAsync(
        GitHubActionsDispatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var opts = _options();
        using var timeout = TimeoutScope(opts.DispatchTimeoutSeconds, ct);
        var call = timeout.Token;
        var url = $"{Base(opts)}/repos/{request.Owner}/{request.Repository}"
            + $"/actions/workflows/{Uri.EscapeDataString(request.WorkflowPath)}/dispatches";
        var inputs = new Dictionary<string, string>(request.Inputs, StringComparer.Ordinal)
        {
            ["codeybox_correlation_id"] = request.RequestCorrelationId,
            ["codeybox_candidate_ref"] = request.CandidateRef,
            ["codeybox_expected_head_sha"] = request.ExpectedHeadSha,
        };
        if (request.ExpectedMergeSha is not null)
            inputs["codeybox_expected_merge_sha"] = request.ExpectedMergeSha;
        using var req = await BuildRequestAsync(HttpMethod.Post, url, call).ConfigureAwait(false);
        req.Content = JsonContent.Create(new { @ref = request.Ref, inputs });
        using var resp = await SendAsync(req, "POST dispatches", call).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return new GitHubActionsDispatchOutcome(
                false, null,
                $"dispatch accepted (HTTP {(int)resp.StatusCode}); run id withheld, reconciling by correlation",
                true, (int)resp.StatusCode);
        throw await MapFailureAsync(resp, "POST dispatches", call).ConfigureAwait(false);
    }

    public async Task<GitHubActionsRun?> FindRunByCorrelationAsync(
        string owner, string repository, string workflowPath,
        string correlationId, string? headBranch, DateTimeOffset? createdAfter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var opts = _options();
        using var timeout = TimeoutScope(opts.PollTimeoutSeconds, ct);
        var call = timeout.Token;
        // WHY branch+time instead of the correlation id: the runs API does
        // not echo workflow_dispatch inputs, so the exact id is unqueryable
        // over HTTP. The host names each temporary candidate branch uniquely
        // per idempotency key, making exact-branch + event + freshness an
        // exact match in practice. The fake transport matches the id exactly.
        // A miss is null, never "the latest run".
        GitHubActionsRun? newest = null;
        for (var page = 1; page <= MaxListPages; page++)
        {
            var url = $"{Base(opts)}/repos/{owner}/{repository}"
                + $"/actions/workflows/{Uri.EscapeDataString(workflowPath)}/runs"
                + $"?per_page={ListPageSize}&page={page}&event=workflow_dispatch";
            using var req = await BuildRequestAsync(HttpMethod.Get, url, call).ConfigureAwait(false);
            using var resp = await SendAsync(req, "GET workflow runs", call).ConfigureAwait(false);
            await ThrowIfFailedAsync(resp, "GET workflow runs", call).ConfigureAwait(false);
            var runs = await ReadRunsAsync(resp, call).ConfigureAwait(false);
            foreach (var run in runs)
            {
                if (headBranch is not null
                    && !string.Equals(run.HeadBranch, headBranch, StringComparison.Ordinal))
                    continue;
                if (createdAfter.HasValue && run.CreatedAt < createdAfter.Value)
                    continue;
                if (newest is null || run.CreatedAt > newest.CreatedAt)
                    newest = run;
            }
            if (runs.Count < ListPageSize)
                break;
        }
        return newest;
    }

    public async Task<GitHubActionsRun?> GetRunAsync(
        string owner, string repository, long runId, CancellationToken ct)
    {
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));
        var opts = _options();
        using var timeout = TimeoutScope(opts.PollTimeoutSeconds, ct);
        var call = timeout.Token;
        var url = $"{Base(opts)}/repos/{owner}/{repository}/actions/runs/{runId}";
        using var req = await BuildRequestAsync(HttpMethod.Get, url, call).ConfigureAwait(false);
        using var resp = await SendAsync(req, $"GET runs/{runId}", call).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfFailedAsync(resp, $"GET runs/{runId}", call).ConfigureAwait(false);
        return await ReadRunAsync(resp, call).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubActionsJob>> ListJobsAsync(
        string owner, string repository, long runId, CancellationToken ct)
    {
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));
        var opts = _options();
        using var timeout = TimeoutScope(opts.PollTimeoutSeconds, ct);
        var call = timeout.Token;
        var jobs = new List<GitHubActionsJob>();
        for (var page = 1; page <= MaxListPages && jobs.Count < opts.MaxJobsPerRun; page++)
        {
            var url = $"{Base(opts)}/repos/{owner}/{repository}/actions/runs/{runId}/jobs"
                + $"?per_page={Math.Min(100, opts.MaxJobsPerRun)}&page={page}";
            using var req = await BuildRequestAsync(HttpMethod.Get, url, call).ConfigureAwait(false);
            using var resp = await SendAsync(req, $"GET runs/{runId}/jobs", call).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound && page == 1)
                return [];
            await ThrowIfFailedAsync(resp, $"GET runs/{runId}/jobs", call).ConfigureAwait(false);
            var batch = await ReadJobsAsync(resp, call).ConfigureAwait(false);
            jobs.AddRange(batch);
            if (batch.Count == 0)
                break;
        }
        return jobs.Take(opts.MaxJobsPerRun).ToList();
    }

    public async Task CancelRunAsync(string owner, string repository, long runId, CancellationToken ct)
    {
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));
        var opts = _options();
        using var timeout = TimeoutScope(opts.PollTimeoutSeconds, ct);
        var call = timeout.Token;
        var url = $"{Base(opts)}/repos/{owner}/{repository}/actions/runs/{runId}/cancel";
        using var req = await BuildRequestAsync(HttpMethod.Post, url, call).ConfigureAwait(false);
        using var resp = await SendAsync(req, $"POST runs/{runId}/cancel", call).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubActionsValidationException($"cancel: run {runId} does not exist (404)");
        await ThrowIfFailedAsync(resp, $"POST runs/{runId}/cancel", call).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubActionsArtifactInfo>> ListArtifactsAsync(
        string owner, string repository, long runId, CancellationToken ct)
    {
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));
        var opts = _options();
        using var timeout = TimeoutScope(opts.PollTimeoutSeconds, ct);
        var call = timeout.Token;
        var artifacts = new List<GitHubActionsArtifactInfo>();
        for (var page = 1; page <= MaxListPages; page++)
        {
            var url = $"{Base(opts)}/repos/{owner}/{repository}/actions/runs/{runId}/artifacts"
                + "?per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture);
            using var req = await BuildRequestAsync(HttpMethod.Get, url, call).ConfigureAwait(false);
            using var resp = await SendAsync(req, $"GET runs/{runId}/artifacts", call).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound && page == 1)
                return [];
            await ThrowIfFailedAsync(resp, $"GET runs/{runId}/artifacts", call).ConfigureAwait(false);
            var batch = await ReadArtifactsAsync(resp, call).ConfigureAwait(false);
            artifacts.AddRange(batch);
            if (batch.Count == 0)
                break;
        }
        return artifacts;
    }

    public async Task<byte[]> DownloadArtifactAsync(
        GitHubActionsArtifactInfo artifact, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (artifact.Expired)
            throw new GitHubActionsEvidenceUnavailableException($"artifact '{artifact.Name}' expired");
        var opts = _options();
        using var timeout = TimeoutScope(opts.PollTimeoutSeconds, ct);
        var call = timeout.Token;
        var allowed = opts.AllowedArtifactHosts
            .Append(new Uri(Base(opts)).Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var urlError = ExternalBuildArtifactGuard.ValidateArtifactUrl(artifact.ArchiveUrl, allowed);
        if (urlError is not null)
            throw new GitHubActionsValidationException("artifact URL rejected: " + urlError);
        var url = artifact.ArchiveUrl;
        for (var redirect = 0; redirect <= opts.MaxRedirects; redirect++)
        {
            // The credential travels only to the API host, never to a
            // redirect target: storage hosts must not receive the token.
            var toApiHost = IsApiHost(url, opts);
            using var req = await BuildRequestAsync(HttpMethod.Get, url, call, includeToken: toApiHost).ConfigureAwait(false);
            using var resp = await SendNoRedirectAsync(req, call).ConfigureAwait(false);
            if (IsRedirect(resp.StatusCode))
            {
                var location = resp.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(location))
                    throw new GitHubActionsTransientException("artifact download redirected without a location");
                var next = location.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? location
                    : new Uri(new Uri(url), location).ToString();
                var nextError = ExternalBuildArtifactGuard.ValidateArtifactUrl(next, allowed);
                if (nextError is not null)
                    throw new GitHubActionsValidationException("artifact redirect rejected: " + nextError);
                url = next;
                continue;
            }
            await ThrowDownloadFailedAsync(resp, artifact.Name, call).ConfigureAwait(false);
            if (resp.Content.Headers.ContentLength is long declared && declared > maxBytes)
                throw new GitHubActionsEvidenceUnavailableException(
                    $"artifact '{artifact.Name}' declares {declared} bytes, exceeding the {maxBytes}-byte cap; refusing to buffer");
            var bytes = await resp.Content.ReadAsByteArrayAsync(call).ConfigureAwait(false);
            if (bytes.LongLength > maxBytes)
                throw new GitHubActionsEvidenceUnavailableException(
                    $"artifact '{artifact.Name}' is {bytes.LongLength} bytes, exceeding the {maxBytes}-byte cap");
            return bytes;
        }
        throw new GitHubActionsTransientException(
            $"artifact '{artifact.Name}' exceeded the {opts.MaxRedirects}-redirect bound");
    }

    private static string Base(GitHubActionsExternalBuildOptions opts) =>
        opts.ApiBaseUrl.TrimEnd('/');

    private static bool IsApiHost(string url, GitHubActionsExternalBuildOptions opts) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, new Uri(Base(opts)).Host, StringComparison.OrdinalIgnoreCase);

    private static CancellationTokenSource TimeoutScope(int seconds, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method, string url, CancellationToken ct, bool includeToken = true)
    {
        var req = new HttpRequestMessage(method, url);
        if (includeToken)
        {
            var token = await _credentials.GetTokenAsync(ct).ConfigureAwait(false);
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", GitHubActionsExternalBuildOptions.ApiVersion);
        req.Headers.UserAgent.ParseAdd("CodeyBox");
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string operation, CancellationToken ct)
    {
        try
        {
            return await _httpClientFactory.CreateClient("github-actions")
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new GitHubActionsTransientException(
                $"GitHub Actions {operation} timed out; the write may or may not have landed.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubActionsTransientException(
                $"GitHub Actions {operation} transport failure; retryable with backoff.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendNoRedirectAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_downloadHandler is not null)
        {
            // Test seam: the caller owns the handler lifetime.
            using var client = new HttpClient(_downloadHandler, disposeHandler: false);
            return await SendViaAsync(client, req, ct).ConfigureAwait(false);
        }
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var autoClient = new HttpClient(handler, disposeHandler: false);
        return await SendViaAsync(autoClient, req, ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendViaAsync(
        HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new GitHubActionsTransientException(
                "GitHub Actions artifact download timed out; retryable with backoff.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubActionsTransientException(
                "GitHub Actions artifact download transport failure; retryable.", ex);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308;

    private async Task<Exception> MapFailureAsync(HttpResponseMessage resp, string operation, CancellationToken ct) =>
        await MapFailureCoreAsync(resp, operation, ct).ConfigureAwait(false);

    private async Task ThrowIfFailedAsync(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;
        throw await MapFailureCoreAsync(resp, operation, ct).ConfigureAwait(false);
    }

    private async Task ThrowDownloadFailedAsync(HttpResponseMessage resp, string artifactName, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new GitHubActionsEvidenceUnavailableException(
                $"artifact '{artifactName}' is unavailable (HTTP {(int)resp.StatusCode}); expired artifacts never count as pass");
        throw await MapFailureCoreAsync(resp, "GET artifact download", ct).ConfigureAwait(false);
    }

    private async Task<Exception> MapFailureCoreAsync(
        HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return new GitHubActionsAuthException(
                $"{operation} returned 401: bad or revoked credential. Check the configured GitHub App "
                + "installation or PAT (actions:read/write, contents:read); blocked, not retrying.");
        var retryAfter = ParseRetryAfter(resp);
        var errorBody = await ReadErrorBodyAsync(resp, ct).ConfigureAwait(false);
        if (resp.StatusCode == (HttpStatusCode)429 || IsRateLimit(resp, errorBody))
            return new ExternalBuildRateLimitedException(
                $"{operation} was rate-limited (HTTP {status}). Backing off"
                + (retryAfter.HasValue ? $" for {retryAfter.Value.TotalSeconds:F0}s per Retry-After." : "."),
                retryAfter);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return new GitHubActionsAuthException(
                $"{operation} returned 403: the credential lacks Actions permission on this repository "
                + "(or the repository is inaccessible). Grant actions:read/write (App) or the Actions "
                + "repository permission (fine-grained PAT); blocked, not retrying.");
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity)
            return new GitHubActionsValidationException($"{operation} returned HTTP {status}: {errorBody}");
        if (status >= 500)
            return new GitHubActionsTransientException(
                $"{operation} returned HTTP {status} from the forge; retryable with backoff.");
        return new GitHubActionsTransientException($"{operation} returned unexpected HTTP {status}; retryable.");
    }

    private static bool IsRateLimit(HttpResponseMessage resp, string errorBody)
    {
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.Any(static v => v.Trim() == "0"))
            return true;
        return errorBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("secondary rate", StringComparison.OrdinalIgnoreCase);
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
            var wait = date - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero)
                return TimeSpan.Zero;
            return wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
        }
        return null;
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length <= MaxErrorBodyChars ? body : body[..MaxErrorBodyChars];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private async Task<GitHubActionsRun?> ReadRunAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return ParseRun(doc.RootElement, _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new GitHubActionsTransientException("GitHub Actions run body was unreadable; retryable.", ex);
        }
    }

    private async Task<List<GitHubActionsRun>> ReadRunsAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var list = new List<GitHubActionsRun>();
            if (doc.RootElement.TryGetProperty("workflow_runs", out var runs)
                && runs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in runs.EnumerateArray())
                {
                    try
                    {
                        list.Add(ParseRun(item, _clock.GetUtcNow()));
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        // Skip one malformed entry; the page guard still bounds the read.
                    }
                }
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new GitHubActionsTransientException("GitHub Actions runs body was unreadable; retryable.", ex);
        }
    }

    private static GitHubActionsRun ParseRun(JsonElement el, DateTimeOffset fallbackNow)
    {
        return new GitHubActionsRun(
            el.GetProperty("id").GetInt64(),
            el.TryGetProperty("run_attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number
                ? attempt.GetInt32() : 1,
            el.GetProperty("status").GetString() ?? "unknown",
            el.TryGetProperty("conclusion", out var conclusion) && conclusion.ValueKind == JsonValueKind.String
                ? conclusion.GetString() : null,
            el.GetProperty("head_sha").GetString() ?? string.Empty,
            el.GetProperty("head_branch").GetString() ?? string.Empty,
            el.TryGetProperty("head_repository", out var headRepo)
                && headRepo.TryGetProperty("full_name", out var headName)
                ? headName.GetString() ?? string.Empty : string.Empty,
            el.TryGetProperty("repository", out var repo) && repo.TryGetProperty("full_name", out var repoName)
                ? repoName.GetString() ?? string.Empty : string.Empty,
            el.GetProperty("path").GetString() ?? string.Empty,
            el.TryGetProperty("event", out var evt) ? evt.GetString() ?? string.Empty : string.Empty,
            el.TryGetProperty("created_at", out var created) && created.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(created.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdAt)
                ? createdAt : fallbackNow,
            el.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(updated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updatedAt)
                ? updatedAt : fallbackNow);
    }

    private static async Task<List<GitHubActionsJob>> ReadJobsAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var list = new List<GitHubActionsJob>();
            if (doc.RootElement.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in jobs.EnumerateArray())
                {
                    list.Add(new GitHubActionsJob(
                        item.GetProperty("id").GetInt64(),
                        item.GetProperty("name").GetString() ?? string.Empty,
                        item.GetProperty("status").GetString() ?? "unknown",
                        item.TryGetProperty("conclusion", out var conclusion)
                            && conclusion.ValueKind == JsonValueKind.String ? conclusion.GetString() : null));
                }
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new GitHubActionsTransientException("GitHub Actions jobs body was unreadable; retryable.", ex);
        }
    }

    private static async Task<List<GitHubActionsArtifactInfo>> ReadArtifactsAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var list = new List<GitHubActionsArtifactInfo>();
            if (doc.RootElement.TryGetProperty("artifacts", out var artifacts)
                && artifacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in artifacts.EnumerateArray())
                {
                    list.Add(new GitHubActionsArtifactInfo(
                        item.GetProperty("id").GetInt64(),
                        item.GetProperty("name").GetString() ?? string.Empty,
                        item.TryGetProperty("size_in_bytes", out var size)
                            && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
                        item.TryGetProperty("expired", out var expired)
                            && expired.ValueKind == JsonValueKind.True,
                        item.GetProperty("archive_download_url").GetString() ?? string.Empty,
                        item.TryGetProperty("expires_at", out var expires)
                            && expires.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(
                                expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt)
                            ? expiresAt : DateTimeOffset.UtcNow.AddHours(24)));
                }
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new GitHubActionsTransientException("GitHub Actions artifacts body was unreadable; retryable.", ex);
        }
    }
}
