using System.Net;
using System.Text.Json;

namespace CodeyBox.OpenProjectWorkSyncPlugin;

/// <summary>
/// Thrown when the OpenProject API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class OpenProjectApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public OpenProjectApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// True when the write lost a <c>lockVersion</c> race (HTTP 409): the
    /// work package changed since it was read. The caller re-reads and
    /// reconciles — never blindly retries.
    /// </summary>
    public bool IsLockConflict => StatusCode == HttpStatusCode.Conflict;

    /// <summary>True when the target does not exist (HTTP 404).</summary>
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;

    /// <summary>True for rejected credentials (HTTP 401/403) — redacted, never echoed.</summary>
    public bool IsAuthFailure =>
        StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// Minimal typed REST client for the OpenProject API v3 operations the
/// work-sync plugin needs: listing a project's work packages newest-first,
/// re-reading one work package (optimistic-locking reconcile), resolving the
/// admin-defined status list (host-resolved status mapping), patching a work
/// package's status with its current <c>lockVersion</c>, and posting/listing
/// activity comments for idempotent writes.
/// <para>Field ownership is explicit: the only mutation this client can make
/// is a status link change plus an activity comment. Subject, description,
/// assignee, and every other field are never written.</para>
/// <para>Rate-limit retries happen only when the server guarantees the
/// request was not processed (HTTP 429). A 503 or timeout on a mutating
/// request is ambiguous — the request is NOT repeated here; the caller
/// re-reads and reconciles before any repeat.</para>
/// </summary>
public sealed class OpenProjectRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Newest-first ordering for poll listings.</summary>
    internal const string UpdatedDescSort = """[["updatedAt","desc"]]""";

    /// <summary>Newest-first ordering for activity scans.</summary>
    internal const string ActivityDescSort = """[["id","desc"]]""";

    /// <summary>Maximum upstream error text surfaced in exception detail.</summary>
    internal const int MaxErrorChars = 300;

    /// <summary>Product token sent as User-Agent — no version, so it cannot drift.</summary>
    internal const string UserAgentProduct = "CodeyBox-OpenProjectWorkSync";

    private readonly HttpClient _http;
    private readonly OpenProjectTokenProvider _tokens;

    public OpenProjectRestClient(HttpClient http, OpenProjectTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Lists one project's work packages page by page, newest first. Each
    /// yielded page is already deserialized; the caller enforces
    /// <c>MaxItemsPerPoll</c> before buffering items. The project key
    /// (numeric id or identifier slug) travels as one escaped path segment —
    /// never interpolated raw.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<OpenProjectWorkPackage>> ListProjectWorkPackagesPagedAsync(
        OpenProjectWorkSyncOptions options,
        string projectKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var maxPages = Math.Max(1, options.MaxPagesPerPoll);
        var offset = 1;
        // The page count is bounded independently of MaxItemsPerPoll: a
        // misbehaving upstream returning perpetually full pages of items
        // that fail to parse must not keep the poll issuing requests.
        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            var url = $"{Base(options)}/api/v3/projects/{Esc(projectKey)}/work_packages"
                + $"?sortBy={Uri.EscapeDataString(UpdatedDescSort)}"
                + $"&pageSize={options.PageSize}&offset={offset}";
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            // The continuation decision uses the upstream item count, not the
            // parsed count: an item that fails to parse must not truncate the
            // walk or double-skip the offset window. Offsets are 1-based.
            var elements = ElementsOf(root);
            var count = CountOf(root);
            IReadOnlyList<OpenProjectWorkPackage> page = elements
                .Select(n => OpenProjectWorkPackage.FromNode(n))
                .Where(w => w is not null)
                .Cast<OpenProjectWorkPackage>()
                .ToList();
            yield return page;
            if (elements.Count < options.PageSize || count < options.PageSize)
                yield break;
            offset += elements.Count;
        }
    }

    /// <summary>
    /// Re-reads one work package by its immutable numeric id. The fresh
    /// <c>lockVersion</c> and status feed the reconcile-before-repeat path:
    /// after an ambiguous write the caller compares this read against the
    /// intended effect instead of repeating blindly.
    /// </summary>
    public async Task<OpenProjectWorkPackage?> GetWorkPackageAsync(
        OpenProjectWorkSyncOptions options, string workPackageId, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/v3/work_packages/{Esc(workPackageId)}";
        try
        {
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            return OpenProjectWorkPackage.FromNode(doc.RootElement);
        }
        catch (OpenProjectApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the instance's admin-defined status list. The caller matches
    /// the declared status name exactly (ordinal-ignore-case) against these
    /// host-supplied entries — the href applied on PATCH always comes from
    /// the host, never constructed or guessed.
    /// </summary>
    public async Task<IReadOnlyList<OpenProjectStatusEntry>> ListStatusesAsync(
        OpenProjectWorkSyncOptions options, CancellationToken ct = default)
    {
        var found = new List<OpenProjectStatusEntry>();
        var maxPages = Math.Max(1, options.MaxPagesPerPoll);
        var offset = 1;
        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            var url = $"{Base(options)}/api/v3/statuses?pageSize={options.PageSize}&offset={offset}";
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var elements = ElementsOf(root);
            foreach (var entry in elements
                .Select(OpenProjectStatusEntry.FromNode)
                .Where(e => e is not null)
                .Cast<OpenProjectStatusEntry>())
                found.Add(entry);
            if (elements.Count < options.PageSize || CountOf(root) < options.PageSize)
                break;
            offset += elements.Count;
        }
        return found;
    }

    /// <summary>
    /// Posts an activity comment on the work package. Returns the activity id.
    /// The payload carries comment text only — the only free-text field this
    /// integration owns alongside the status link.
    /// </summary>
    public async Task<string?> CreateActivityAsync(
        OpenProjectWorkSyncOptions options, string workPackageId, string text, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/v3/work_packages/{Esc(workPackageId)}/activities";
        var payload = JsonSerializer.Serialize(new { comment = new { raw = text } }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, idempotent: false, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var numeric)
            ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Applies a host-resolved status to the work package with optimistic
    /// locking: the body carries the <paramref name="lockVersion"/> observed
    /// on the immediately-preceding read plus the status href resolved from
    /// <c>GET /api/v3/statuses</c>. A 409 means the package changed under us —
    /// the caller re-reads and reconciles. Only the status link is sent; no
    /// other field is touched.
    /// </summary>
    /// <param name="statusHref">Host-supplied status href (e.g. <c>/api/v3/statuses/2</c>).</param>
    /// <param name="lockVersion">Lock version from the fresh read.</param>
    public async Task PatchWorkPackageStatusAsync(
        OpenProjectWorkSyncOptions options,
        string workPackageId,
        string statusHref,
        int lockVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusHref);
        var url = $"{Base(options)}/api/v3/work_packages/{Esc(workPackageId)}";
        var payload = JsonSerializer.Serialize(new
        {
            lockVersion,
            _links = new { status = new { href = statusHref } },
        }, JsonOptions);
        using var _ = await SendAsync(options, new HttpMethod("PATCH"), url, payload, idempotent: false, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Scans activities newest-first for an exact comment-body match. Used
    /// before every write (idempotent post: identical content already posted)
    /// and after every ambiguous failure (reconcile: did the write land?).
    /// Comparison is ordinal exact equality on <c>comment.raw</c> — never
    /// substring. The scan is bounded by <paramref name="maxScanned"/>.
    /// Returns the matching activity id, or null.
    /// </summary>
    public async Task<string?> FindActivityByExactCommentAsync(
        OpenProjectWorkSyncOptions options,
        string workPackageId,
        string exactBody,
        int maxScanned,
        CancellationToken ct = default)
    {
        var scanned = 0;
        var offset = 1;
        var pageSize = Math.Clamp(options.PageSize, 1, 500);
        while (scanned < maxScanned)
        {
            var url = $"{Base(options)}/api/v3/work_packages/{Esc(workPackageId)}/activities"
                + $"?sortBy={Uri.EscapeDataString(ActivityDescSort)}"
                + $"&pageSize={Math.Min(pageSize, maxScanned - scanned)}&offset={offset}";
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var elements = ElementsOf(root);
            if (elements.Count == 0)
                return null;
            foreach (var node in elements)
            {
                scanned++;
                var activity = OpenProjectActivity.FromNode(node);
                if (activity is not null
                    && string.Equals(activity.CommentRaw, exactBody, StringComparison.Ordinal))
                    return activity.Id;
                if (scanned >= maxScanned)
                    return null;
            }
            if (elements.Count < pageSize || CountOf(root) < pageSize)
                return null;
            offset += elements.Count;
        }
        return null;
    }

    /// <summary>
    /// The configured API origin plus <c>/api/v3</c> is never needed here:
    /// callers append it. An unset, non-http(s), or plaintext-http value is
    /// an operator misconfiguration and throws before a request is built —
    /// the API token must never be aimed at a placeholder, a non-HTTP
    /// target, or a cleartext channel. <c>http://</c> requires the explicit
    /// dev-only <see cref="OpenProjectWorkSyncOptions.AllowUnsafeHttp"/>
    /// opt-in.
    /// </summary>
    private static string Base(OpenProjectWorkSyncOptions options)
    {
        var raw = options.ApiBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && options.AllowUnsafeHttp)))
            throw new InvalidOperationException(
                "OpenProject ApiBaseUrl is not configured or is not an absolute https URL; " +
                "set it in the plugin configuration before enabling work sync " +
                "(plaintext http:// would send the API token unencrypted and requires " +
                "the dev-only AllowUnsafeHttp=true opt-in).");
        return raw;
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static List<JsonElement> ElementsOf(JsonElement root)
    {
        var elements = new List<JsonElement>();
        if (root.ValueKind != JsonValueKind.Object)
            return elements;
        if (root.TryGetProperty("_embedded", out var embedded)
            && embedded.ValueKind == JsonValueKind.Object
            && embedded.TryGetProperty("elements", out var array)
            && array.ValueKind == JsonValueKind.Array)
            elements.AddRange(array.EnumerateArray());
        return elements;
    }

    private static int CountOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("count", out var count)
        && count.ValueKind == JsonValueKind.Number
        && count.TryGetInt32(out var n) ? n : 0;

    private static string Clip(string value) =>
        value.Length <= MaxErrorChars ? value : value[..MaxErrorChars];

    private async Task<JsonDocument> GetAsync(
        OpenProjectWorkSyncOptions options, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendDocumentAsync(options, request, idempotent: true, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(
        OpenProjectWorkSyncOptions options,
        HttpMethod method,
        string url,
        string? jsonBody,
        bool idempotent,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return await SendDocumentAsync(options, request, idempotent, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendDocumentAsync(
        OpenProjectWorkSyncOptions options,
        HttpRequestMessage request,
        bool idempotent,
        CancellationToken ct)
    {
        // Per-request timeout from the live option, so TimeoutSeconds edits
        // hot-reload. Covers credential read, send, retries, and body read.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var requestCt = timeout.Token;

        var method = request.Method;
        var url = request.RequestUri?.ToString() ?? string.Empty;
        var hasBody = request.Content is not null;
        string? bodyText = hasBody ? await request.Content!.ReadAsStringAsync(requestCt).ConfigureAwait(false) : null;

        HttpResponseMessage? response = null;
        try
        {
            // Retried only when the server guarantees non-execution (429):
            // the rate limiter rejects before processing, so a repeat cannot
            // duplicate a write. Anything ambiguous (503, timeout) on a
            // mutating request is thrown to the caller, which re-reads and
            // reconciles before any repeat. Reads retry on 429 and 503.
            var maxAttempts = 1 + Math.Max(0, options.MaxRateLimitRetries);
            for (var attempt = 1; ; attempt++)
            {
                using var attemptRequest = CloneRequest(method, url, bodyText);
                await AuthenticateAsync(options, attemptRequest, requestCt).ConfigureAwait(false);
                try
                {
                    response?.Dispose();
                    response = await _http.SendAsync(attemptRequest, requestCt).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new OpenProjectApiException(
                        $"OpenProject API request failed for {method} {PathOf(url)}: {Clip(ex.Message)}.");
                }
                if (!IsRetryable(response.StatusCode, idempotent) || attempt >= maxAttempts)
                    break;
                var delay = RetryDelay(response, attempt, options.MaxRateLimitDelaySeconds);
                response.Dispose();
                response = null;
                await Task.Delay(delay, requestCt).ConfigureAwait(false);
            }

            if (response!.StatusCode == HttpStatusCode.NoContent)
                return JsonDocument.Parse("{}");
            var body = await ReadBoundedStringAsync(
                response.Content, options.MaxResponseBytes, requestCt).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = ErrorDetail(body) is { } d ? $": {d}" : string.Empty;
                throw new OpenProjectApiException(
                    $"OpenProject API returned {(int)response.StatusCode} {response.ReasonPhrase} " +
                    $"for {method} {PathOf(url)}{detail}.",
                    response.StatusCode);
            }
            if (string.IsNullOrWhiteSpace(body))
                return JsonDocument.Parse("{}");
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new OpenProjectApiException(
                    $"OpenProject API returned malformed JSON for {method} {PathOf(url)}: {ex.Message}");
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task AuthenticateAsync(
        OpenProjectWorkSyncOptions options, HttpRequestMessage request, CancellationToken ct)
    {
        var credential = await _tokens.GetCredentialAsync(options, ct).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            credential.Scheme, credential.Value);
        request.Headers.Accept.ParseAdd("application/hal+json, application/json");
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);
    }

    private static HttpRequestMessage CloneRequest(HttpMethod method, string url, string? bodyText)
    {
        var clone = new HttpRequestMessage(method, url);
        if (bodyText is not null)
            clone.Content = new StringContent(bodyText, System.Text.Encoding.UTF8, "application/json");
        return clone;
    }

    private static bool IsRetryable(HttpStatusCode status, bool idempotent) =>
        status == HttpStatusCode.TooManyRequests
        || (idempotent && status == HttpStatusCode.ServiceUnavailable);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt, int maxDelaySeconds)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta
            && delta >= TimeSpan.Zero
            && delta <= TimeSpan.FromSeconds(maxDelaySeconds))
            return delta;
        var backoff = Math.Min(1 << attempt, maxDelaySeconds);
        return TimeSpan.FromSeconds(backoff);
    }

    /// <summary>
    /// Reads a response body with a hard byte cap — the upstream is a
    /// less-trusted dependency, so its output is bounded before buffering:
    /// the declared Content-Length is checked first, then the stream is read
    /// in chunks and rejected the moment it exceeds <paramref
    /// name="maxBytes"/>. Throws <see cref="OpenProjectApiException"/> on
    /// overflow.
    /// </summary>
    internal static async Task<string> ReadBoundedStringAsync(
        HttpContent? content, long maxBytes, CancellationToken ct)
    {
        if (content is null)
            return string.Empty;
        if (content.Headers.ContentLength > maxBytes)
            throw new OpenProjectApiException(
                $"OpenProject response body exceeds the {maxBytes}-byte cap " +
                $"(declared {content.Headers.ContentLength} bytes).");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(chunk, 0, read);
            if (memory.Length > maxBytes)
                throw new OpenProjectApiException(
                    $"OpenProject response body exceeds the {maxBytes}-byte cap.");
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>
    /// Extracts a bounded, single-line error summary from an API v3 error
    /// body (<c>{"_type":"Error","message":…}</c>). Never throws, never
    /// echoes credentials — the response body is instance text only.
    /// </summary>
    private static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string? detail = null;
            if (root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                detail = message.GetString();
            detail ??= root.TryGetProperty("errorIdentifier", out var ident)
                && ident.ValueKind == JsonValueKind.String ? ident.GetString() : null;
            if (string.IsNullOrWhiteSpace(detail))
                return null;
            return Clip(detail.Replace('\n', ' ').Replace('\r', ' '));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string PathOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
}
