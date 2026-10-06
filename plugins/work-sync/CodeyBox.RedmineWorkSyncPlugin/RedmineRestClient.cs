using System.Net;
using System.Text.Json;

namespace CodeyBox.RedmineWorkSyncPlugin;

/// <summary>
/// Thrown when the Redmine API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never the API key, secrets, or request bodies.
/// </summary>
public sealed class RedmineApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// True when the request may have reached the server despite the failure
    /// (client-side timeout). The caller must reconcile before repeating a
    /// write — the outcome is uncertain, not a proven rejection.
    /// </summary>
    public bool IsTimeout { get; init; }

    public RedmineApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>True when the failure is a rejected state change (unknown status, disallowed transition).</summary>
    public bool IsStatusRejected => StatusCode == HttpStatusCode.UnprocessableEntity;

    /// <summary>True when the instance answered "no such endpoint" — a version/capability gap, not a failure.</summary>
    public bool IsCapabilityGap =>
        StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
        or HttpStatusCode.Gone or HttpStatusCode.NotImplemented;
}

/// <summary>
/// Minimal typed REST client for the Redmine operations the work-sync plugin
/// needs: listing recently-updated issues per project, resolving status
/// names to ids, reading issue journals (for write reconciliation and
/// question replies), and updating issues with notes and status changes.
/// <para>Targets the documented Redmine JSON REST API (Redmine 4.x–6.x):
/// <c>GET /issues.json</c>, <c>GET /issues/{id}.json?include=journals</c>,
/// <c>GET /issue_statuses.json</c>, <c>PUT /issues/{id}.json</c>. Status
/// changes honor the instance workflow server-side: a transition the
/// workflow disallows surfaces as a <see cref="RedmineApiException"/> the
/// caller reports, never a bypassed write. No admin endpoints are used —
/// statuses, workflows, and projects stay operator-managed in Redmine.</para>
/// </summary>
public sealed class RedmineRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maximum upstream error text surfaced in exception detail.</summary>
    internal const int MaxErrorChars = 300;

    /// <summary>Upper bound on an honored Retry-After delay (seconds).</summary>
    internal const int MaxRetryAfterSeconds = 30;

    /// <summary>Upper bound on the exponential backoff delay (milliseconds).</summary>
    internal const int MaxBackoffMs = 5000;

    /// <summary>Read chunk size (bytes) for bounded response-body streaming.</summary>
    internal const int ReadChunkBytes = 16 * 1024;

    /// <summary>Upper bound (bytes) on an error body inspected for a detail summary.</summary>
    internal const int MaxErrorBodyBytes = 64 * 1024;

    /// <summary>Product token sent as User-Agent — no version, so it cannot drift.</summary>
    internal const string UserAgentProduct = "CodeyBox-RedmineWorkSync";

    private readonly HttpClient _http;
    private readonly RedmineTokenProvider _tokens;

    public RedmineRestClient(HttpClient http, RedmineTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Lists issues page by page for one Redmine project identifier, newest
    /// first (<c>status_id=*</c> includes closed issues so signal changes on
    /// them are visible). Each yielded page is already deserialized; the
    /// caller enforces <c>MaxItemsPerPoll</c> before buffering items.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<RedmineIssue>> SearchIssuesPagedAsync(
        RedmineWorkSyncOptions options,
        string projectIdentifier,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(options.PageSize, 1, 100);
        var maxPages = Math.Max(1, options.MaxPagesPerPoll);
        var offset = 0;
        // The page count is bounded independently of MaxItemsPerPoll: a
        // misbehaving upstream returning perpetually full pages of items
        // that fail to parse must not keep the poll issuing requests.
        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            var url = $"{Base(options)}/issues.json"
                + $"?project_id={Uri.EscapeDataString(projectIdentifier)}"
                + "&status_id=*&sort=updated_on:desc"
                + $"&limit={pageSize}&offset={offset}";
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            // The continuation decision uses the upstream item count, not the
            // parsed count: an issue that fails to parse must not truncate the
            // walk or double-skip the offset window.
            var nodes = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("issues", out var issues)
                && issues.ValueKind == JsonValueKind.Array
                    ? issues.EnumerateArray().ToList() : [];
            IReadOnlyList<RedmineIssue> page = nodes
                .Select(n => RedmineIssue.FromNode(n))
                .Where(i => i is not null)
                .Cast<RedmineIssue>()
                .ToList();
            yield return page;
            if (nodes.Count < pageSize)
                yield break;
            offset += nodes.Count;
        }
    }

    /// <summary>
    /// Resolves an issue status name to its numeric id via
    /// <c>GET /issue_statuses.json</c> (ordinal-ignore-case exact match —
    /// never substring). Returns null when no status bears that name: the
    /// caller reports the unmapped/rejected state, never a guessed id.
    /// </summary>
    public async Task<int?> ResolveStatusIdAsync(
        RedmineWorkSyncOptions options, string statusName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusName);
        var url = $"{Base(options)}/issue_statuses.json";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("issue_statuses", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            if (!string.Equals(RedmineIssue.Str(entry, "name"), statusName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.Number
                && idEl.TryGetInt32(out var id) && id > 0)
                return id;
        }
        return null;
    }

    /// <summary>
    /// Reads an issue's journal entries (<c>include=journals</c>) for write
    /// reconciliation and question-reply observation. Throws
    /// <see cref="ArgumentException"/> for a non-numeric id — the guard
    /// lives AT this sink so a malformed external id can never reach the URL.
    /// </summary>
    public async Task<IReadOnlyList<RedmineJournalEntry>> GetIssueJournalsAsync(
        RedmineWorkSyncOptions options, string externalId, CancellationToken ct = default)
    {
        var id = ParseIssueId(externalId);
        var url = $"{Base(options)}/issues/{id}.json?include=journals";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("issue", out var issue)
            || issue.ValueKind != JsonValueKind.Object)
            return [];
        return RedmineJournalEntry.ParseList(issue);
    }

    /// <summary>
    /// Updates an issue with a note and, optionally, a status change in one
    /// <c>PUT /issues/{id}.json</c> call. Any 2xx is success (Redmine answers
    /// updates with an empty body). A 422 (unknown status id, disallowed
    /// workflow transition, validation failure) surfaces as a <see
    /// cref="RedmineApiException"/> the caller reports — the workflow stays
    /// authoritative, never bypassed.
    /// <para>Exactly one attempt: a note write of uncertain outcome (timeout,
    /// cancellation, transport failure) must reconcile the issue journal
    /// before any repetition, so this method never retries internally.</para>
    /// </summary>
    public async Task UpdateIssueAsync(
        RedmineWorkSyncOptions options,
        string externalId,
        string notes,
        int? statusId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(notes);
        var id = ParseIssueId(externalId);
        var url = $"{Base(options)}/issues/{id}.json";
        object issue = statusId.HasValue
            ? new { notes, status_id = statusId.Value }
            : new { notes };
        var payload = JsonSerializer.Serialize(new { issue }, JsonOptions);
        await SendSingleAsync(options, HttpMethod.Put, url, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the statuses the instance reports (<c>GET
    /// /issue_statuses.json</c>) — the minimal live probe that the base URL
    /// and credential work. Redmine exposes no REST version endpoint, so
    /// capability is discovered per-request instead. A missing or forbidden
    /// endpoint returns null rather than throwing.
    /// </summary>
    public async Task<int?> GetIssueStatusCountAsync(
        RedmineWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/issue_statuses.json";
        try
        {
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("issue_statuses", out var list)
                || list.ValueKind != JsonValueKind.Array)
                return null;
            return list.GetArrayLength();
        }
        catch (RedmineApiException ex) when (ex.IsCapabilityGap
            || ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the login of the authenticated API-key owner
    /// (<c>GET /users/current.json</c>) — the minimal live smoke check that
    /// a credential works.
    /// </summary>
    public async Task<string?> GetAuthenticatedUserLoginAsync(
        RedmineWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/users/current.json";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("user", out var user)
            || user.ValueKind != JsonValueKind.Object)
            return null;
        var login = RedmineIssue.Str(user, "login");
        return string.IsNullOrWhiteSpace(login) ? null : login;
    }

    /// <summary>
    /// Validates a caller-supplied external id into a Redmine issue number
    /// AT the sink: positive decimal digits only. A malformed id throws
    /// <see cref="ArgumentException"/> before anything is sent — it can
    /// never reach the request URL.
    /// </summary>
    internal static int ParseIssueId(string externalId)
    {
        if (!int.TryParse(externalId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id)
            || id <= 0)
            throw new ArgumentException(
                $"Redmine external id '{externalId}' is not a positive issue number",
                nameof(externalId));
        return id;
    }

    /// <summary>
    /// The configured API origin. An unset, non-http(s), or plaintext-http
    /// value is an operator misconfiguration and throws before a request is
    /// built — the API key must never be aimed at a placeholder, a non-HTTP
    /// target, or a cleartext channel. <c>http://</c> requires the explicit
    /// dev-only <see cref="RedmineWorkSyncOptions.AllowUnsafeHttp"/> opt-in.
    /// </summary>
    private static string Base(RedmineWorkSyncOptions options)
    {
        var raw = options.ApiBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && options.AllowUnsafeHttp)))
            throw new InvalidOperationException(
                "Redmine ApiBaseUrl is not configured or is not an absolute https URL; " +
                "set it in the plugin configuration before enabling work sync " +
                "(plaintext http:// would send the API key unencrypted and requires " +
                "the dev-only AllowUnsafeHttp=true opt-in).");
        return raw;
    }

    private async Task<JsonDocument> GetAsync(
        RedmineWorkSyncOptions options, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendWithRetryAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task SendSingleAsync(
        RedmineWorkSyncOptions options, HttpMethod method, string url, string jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        };
        using var _ = await SendOnceAsync(options, request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an idempotent (GET) request with a bounded retry: 429/502/503/
    /// 504 are retried up to <c>MaxAttempts</c> honoring the upstream
    /// <c>Retry-After</c> hint (capped); anything else fails fast.
    /// Transport failures are retried the same way. Cancellation propagates.
    /// </summary>
    private async Task<JsonDocument> SendWithRetryAsync(
        RedmineWorkSyncOptions options, HttpRequestMessage request, CancellationToken ct)
    {
        var attempts = Math.Clamp(options.MaxAttempts, 1, 5);
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            TimeSpan hinted = TimeSpan.Zero;
            try
            {
                using var attemptRequest = CloneGetRequest(request);
                return await SendOnceAsync(options, attemptRequest, ct).ConfigureAwait(false);
            }
            catch (RedmineApiException ex) when (IsRetryable(ex.StatusCode) && attempt < attempts)
            {
                if (ex.Data["Retry-After"] is TimeSpan hintedDelay)
                    hinted = hintedDelay;
                lastFailure = ex;
            }
            catch (HttpRequestException ex) when (attempt < attempts)
            {
                lastFailure = ex;
            }
            await Task.Delay(RetryDelay(options, attempt, hinted), ct).ConfigureAwait(false);
        }
        throw lastFailure ?? new RedmineApiException("Redmine API request failed.");
    }

    private static bool IsRetryable(HttpStatusCode? status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(RedmineWorkSyncOptions options, int attempt, TimeSpan hinted)
    {
        var backoffMs = Math.Min(
            (long)Math.Max(0, options.RetryBaseDelayMs) * (1L << Math.Min(attempt - 1, 4)),
            MaxBackoffMs);
        var total = TimeSpan.FromMilliseconds(backoffMs) + hinted;
        var cap = TimeSpan.FromSeconds(MaxRetryAfterSeconds) + TimeSpan.FromMilliseconds(MaxBackoffMs);
        return total > cap ? cap : total;
    }

    /// <summary>
    /// Clones an idempotent GET request for a retry attempt. Only
    /// content-free requests flow through the retry path (PUTs are never
    /// retried — an uncertain note reconciles the journal instead), so a
    /// body here throws rather than being silently dropped.
    /// </summary>
    private static HttpRequestMessage CloneGetRequest(HttpRequestMessage request)
    {
        if (request.Content is not null)
            throw new InvalidOperationException(
                "Only content-free GET requests may be cloned for retry; refusing to drop a body.");
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private async Task<JsonDocument> SendOnceAsync(
        RedmineWorkSyncOptions options, HttpRequestMessage request, CancellationToken ct)
    {
        // Per-request timeout from the live option, so TimeoutSeconds edits
        // hot-reload; the client-level timeout is disabled on the client this
        // plugin owns. Covers send and body read.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var requestCt = timeout.Token;

        var apiKey = _tokens.GetApiKey(options);
        request.Headers.TryAddWithoutValidation("X-Redmine-API-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);

        TimeSpan? retryAfter = null;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, requestCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new RedmineApiException(
                $"Redmine API request to {request.Method} {request.RequestUri?.AbsolutePath} timed out.")
            {
                IsTimeout = true,
            };
        }
        using (response)
        {
            if (RedmineHttpClients.IsRedirect(response.StatusCode))
                throw new RedmineApiException(
                    $"Redmine API redirected {request.Method} {request.RequestUri?.AbsolutePath} " +
                    $"({(int)response.StatusCode}); configure the canonical ApiBaseUrl — " +
                    "redirects are refused rather than followed with credentials.");
            retryAfter = ReadRetryAfter(response);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return JsonDocument.Parse("{}");
            var body = await ReadBoundedStringAsync(
                response.Content, options.MaxResponseBytes, requestCt).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = ErrorDetail(body) is { } d ? $": {d}" : string.Empty;
                var ex = new RedmineApiException(
                    $"Redmine API returned {(int)response.StatusCode} {response.ReasonPhrase} " +
                    $"for {request.Method} {request.RequestUri?.AbsolutePath}{detail}.",
                    response.StatusCode);
                if (retryAfter.HasValue)
                    ex.Data["Retry-After"] = retryAfter.Value;
                throw ex;
            }
            if (string.IsNullOrWhiteSpace(body))
                return JsonDocument.Parse("{}");
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new RedmineApiException(
                    $"Redmine API returned malformed JSON for {request.Method} " +
                    $"{request.RequestUri?.AbsolutePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the upstream <c>Retry-After</c> hint for bounded retries.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;
        if (int.TryParse(raw, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfterSeconds));
        if (DateTimeOffset.TryParse(raw, out var date))
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay <= TimeSpan.Zero ? TimeSpan.Zero
                : delay > TimeSpan.FromSeconds(MaxRetryAfterSeconds)
                    ? TimeSpan.FromSeconds(MaxRetryAfterSeconds) : delay;
        }
        return null;
    }

    /// <summary>
    /// Reads a response body with a hard byte cap — the upstream is a
    /// less-trusted dependency, so its output is bounded before buffering:
    /// the declared Content-Length is checked first, then the stream is read
    /// in chunks and rejected the moment it exceeds <paramref
    /// name="maxBytes"/>. Throws <see cref="RedmineApiException"/> on
    /// overflow.
    /// </summary>
    internal static async Task<string> ReadBoundedStringAsync(
        HttpContent? content, long maxBytes, CancellationToken ct)
    {
        if (content is null)
            return string.Empty;
        if (content.Headers.ContentLength > maxBytes)
            throw new RedmineApiException(
                $"Redmine response body exceeds the {maxBytes}-byte cap " +
                $"(declared {content.Headers.ContentLength} bytes).");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(chunk, 0, read);
            if (memory.Length > maxBytes)
                throw new RedmineApiException(
                    $"Redmine response body exceeds the {maxBytes}-byte cap.");
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>
    /// Extracts a bounded, single-line error summary from a Redmine error
    /// body (<c>{"errors":[…]}</c>). Never throws, never echoes credentials
    /// — the response body is instance text only.
    /// </summary>
    private static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxErrorBodyBytes)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("errors", out var errors))
            {
                string? detail = errors.ValueKind switch
                {
                    JsonValueKind.Array => string.Join("; ", errors.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))),
                    JsonValueKind.String => errors.GetString(),
                    _ => errors.ToString(),
                };
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    var singleLine = detail.Replace('\n', ' ').Replace('\r', ' ');
                    return Clip(singleLine);
                }
            }
            var fallback = FirstNonEmpty(root, "error", "message");
            if (!string.IsNullOrWhiteSpace(fallback))
                return Clip(fallback.Replace('\n', ' ').Replace('\r', ' '));
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            var s = RedmineIssue.Str(el, name);
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    private static string Clip(string value) =>
        value.Length <= MaxErrorChars ? value : value[..MaxErrorChars];
}
