using System.Net;
using System.Text.Json;

namespace CodeyBox.YouTrackWorkSyncPlugin;

/// <summary>
/// Thrown when the YouTrack API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class YouTrackApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public YouTrackApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>True when the instance answered "no such endpoint" — a version/capability gap, not a failure.</summary>
    public bool IsCapabilityGap =>
        StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
        or HttpStatusCode.Gone or HttpStatusCode.NotImplemented;
}

/// <summary>What an instance reports about itself, probed via <c>GET /api/config</c>.</summary>
public sealed record YouTrackInstanceInfo(string Version, string Build);

/// <summary>
/// Minimal typed REST client for the YouTrack operations the work-sync plugin
/// needs: querying recently-updated issues per project, posting comments,
/// applying commands (the only supported way to change state — YouTrack has
/// no field-write endpoint for bundle fields), and probing the instance.
/// <para>Works against both YouTrack Cloud and self-hosted Server — the same
/// <c>/api</c> surface serves both. Commands apply the documented
/// <c>{field} {value}</c> syntax against the operator-declared field
/// names; a rejected command surfaces as a <see cref="YouTrackApiException"/>
/// the caller reports, never a guessed write.</para>
/// </summary>
public sealed class YouTrackRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Issue fields requested on poll reads — bounded to what ingestion uses.</summary>
    internal const string IssueFields =
        "idReadable,summary,description,updated,project(shortName)," +
        "tags(name),customFields(name,value(name,login,$type),$type)," +
        "updater(login)";

    /// <summary>Maximum upstream error text surfaced in exception detail.</summary>
    internal const int MaxErrorChars = 300;

    /// <summary>Product token sent as User-Agent — no version, so it cannot drift.</summary>
    internal const string UserAgentProduct = "CodeyBox-YouTrackWorkSync";

    private readonly HttpClient _http;
    private readonly YouTrackTokenProvider _tokens;

    public YouTrackRestClient(HttpClient http, YouTrackTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Queries issues page by page for one YouTrack project short name,
    /// newest first. Each yielded page is already deserialized; the caller
    /// enforces <c>MaxItemsPerPoll</c> before buffering items. The project
    /// key is brace-quoted inside the query string — never interpolated raw.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<YouTrackIssue>> SearchIssuesPagedAsync(
        YouTrackWorkSyncOptions options,
        string projectKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var query = $"project: {QuoteQueryValue(projectKey)} sort by: updated desc";
        var maxPages = Math.Max(1, options.MaxPagesPerPoll);
        var skip = 0;
        // The page count is bounded independently of MaxItemsPerPoll: a
        // misbehaving upstream returning perpetually full pages of items
        // that fail to parse must not keep the poll issuing requests.
        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            var url = $"{Base(options)}/api/issues"
                + $"?query={Uri.EscapeDataString(query)}"
                + $"&fields={Uri.EscapeDataString(IssueFields)}"
                + $"&$top={options.PageSize}&$skip={skip}";
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            // The continuation decision uses the upstream item count, not the
            // parsed count: an issue that fails to parse must not truncate the
            // walk or double-skip the $skip window.
            var upstreamCount = root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : 0;
            IReadOnlyList<YouTrackIssue> page = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(n => YouTrackIssue.FromNode(n, options))
                    .Where(i => i is not null)
                    .Cast<YouTrackIssue>()
                    .ToList()
                : [];
            yield return page;
            if (upstreamCount < options.PageSize)
                yield break;
            skip += upstreamCount;
        }
    }

    /// <summary>Posts a comment on the issue. Returns the comment id.</summary>
    public async Task<string?> CreateCommentAsync(
        YouTrackWorkSyncOptions options, string issueId, string text, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/issues/{Esc(issueId)}/comments?fields=id";
        var payload = JsonSerializer.Serialize(new { text }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() : null;
    }

    /// <summary>
    /// Applies a YouTrack state command to the issue (e.g. <c>{State} {In
    /// Progress}</c>). YouTrack state changes are commands, not field writes:
    /// the state-machine bundle and workflow rules decide whether the value
    /// applies, and a rejection surfaces here as a
    /// <see cref="YouTrackApiException"/> — the caller reports it, never a
    /// guessed write. Both operands are brace-quoted and validated at this
    /// sink (<see cref="QuoteQueryValue"/>), so a field name or value the
    /// command language cannot express throws <see
    /// cref="ArgumentException"/> before anything is sent — raw command text
    /// is never accepted.
    /// </summary>
    /// <param name="fieldName">The configured state field name (e.g. <c>State</c>).</param>
    /// <param name="value">The state value to apply (e.g. <c>In Progress</c>).</param>
    public async Task ApplyCommandAsync(
        YouTrackWorkSyncOptions options, string issueId, string fieldName, string value, CancellationToken ct = default)
    {
        var commandQuery = $"{QuoteQueryValue(fieldName)} {QuoteQueryValue(value)}";
        var url = $"{Base(options)}/api/commands?fields=id,issues(idReadable)";
        var payload = JsonSerializer.Serialize(new
        {
            query = commandQuery,
            issues = new[] { new { idReadable = issueId } },
            silent = true,
        }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        // A 200 can still carry a per-issue failure in some versions; surface
        // anything the payload reports rather than treating transport success
        // as applied.
        var root = doc.RootElement;
        var reported = FirstNonEmpty(root, "error", "error_description", "localizedError");
        if (!string.IsNullOrEmpty(reported))
            throw new YouTrackApiException($"YouTrack rejected the command for '{issueId}': {Clip(reported)}");
    }

    /// <summary>
    /// Probes the instance for observability: <c>GET /api/config</c> carries
    /// version/build on both cloud and self-hosted. A missing or forbidden
    /// endpoint (older Server, low-privilege token) returns null rather than
    /// throwing.
    /// </summary>
    public async Task<YouTrackInstanceInfo?> GetInstanceInfoAsync(
        YouTrackWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/config";
        try
        {
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return new YouTrackInstanceInfo(
                YouTrackIssue.Str(root, "version"), YouTrackIssue.Str(root, "build"));
        }
        catch (YouTrackApiException ex) when (ex.IsCapabilityGap
            || ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }
    }

    /// <summary>
    /// Quotes one operand for the YouTrack query/command language with brace
    /// quoting (<c>{…}</c>). Operands containing characters the language
    /// cannot express inside braces are rejected — a silently mangled
    /// operand is worse than a refused write, and an unguarded brace would
    /// break out of the quoting into query syntax.
    /// </summary>
    internal static string QuoteQueryValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.AsSpan().IndexOfAny(['{', '}', '"', '\'', '\n', '\r', '\0']) >= 0)
            throw new ArgumentException(
                $"YouTrack query operand cannot be safely quoted (contains a brace, quote, or control character)",
                nameof(value));
        return $"{{{value.Trim()}}}";
    }

    /// <summary>
    /// Returns the login of the authenticated service account
    /// (<c>GET /api/users/me</c>) — the minimal live smoke check that a
    /// credential works.
    /// </summary>
    public async Task<string?> GetAuthenticatedUserLoginAsync(
        YouTrackWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/users/me?fields=login";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var login = YouTrackIssue.Str(doc.RootElement, "login");
        return string.IsNullOrWhiteSpace(login) ? null : login;
    }

    /// <summary>
    /// The configured API origin. An unset, non-http(s), or plaintext-http
    /// value is an operator misconfiguration and throws before a request is
    /// built — the bearer credential must never be aimed at a placeholder,
    /// a non-HTTP target, or a cleartext channel. <c>http://</c> requires the
    /// explicit dev-only <see cref="YouTrackWorkSyncOptions.AllowUnsafeHttp"/>
    /// opt-in.
    /// </summary>
    private static string Base(YouTrackWorkSyncOptions options)
    {
        var raw = options.ApiBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && options.AllowUnsafeHttp)))
            throw new InvalidOperationException(
                "YouTrack ApiBaseUrl is not configured or is not an absolute https URL; " +
                "set it in the plugin configuration before enabling work sync " +
                "(plaintext http:// would send credentials unencrypted and requires " +
                "the dev-only AllowUnsafeHttp=true opt-in).");
        return raw;
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string? FirstNonEmpty(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            var s = YouTrackIssue.Str(el, name);
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    private static string Clip(string value) =>
        value.Length <= MaxErrorChars ? value : value[..MaxErrorChars];

    private async Task<JsonDocument> GetAsync(
        YouTrackWorkSyncOptions options, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(
        YouTrackWorkSyncOptions options, HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendDocumentAsync(
        YouTrackWorkSyncOptions options, HttpRequestMessage request, CancellationToken ct)
    {
        // Per-request timeout from the live option, so TimeoutSeconds edits
        // hot-reload; the client-level timeout is disabled on the client this
        // plugin owns. Covers credential acquisition, send, and body read.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var requestCt = timeout.Token;

        var credential = await _tokens.GetCredentialAsync(options, requestCt).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            credential.Scheme, credential.Value);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);

        using var response = await _http.SendAsync(request, requestCt).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonDocument.Parse("{}");
        var body = await ReadBoundedStringAsync(
            response.Content, options.MaxResponseBytes, requestCt).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ErrorDetail(body) is { } d
                ? $": {d}"
                : string.Empty;
            throw new YouTrackApiException(
                $"YouTrack API returned {(int)response.StatusCode} {response.ReasonPhrase} for {request.Method} {request.RequestUri?.AbsolutePath}{detail}.",
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
            throw new YouTrackApiException(
                $"YouTrack API returned malformed JSON for {request.Method} {request.RequestUri?.AbsolutePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a response body with a hard byte cap — the upstream is a
    /// less-trusted dependency, so its output is bounded before buffering:
    /// the declared Content-Length is checked first, then the stream is read
    /// in chunks and rejected the moment it exceeds <paramref
    /// name="maxBytes"/>. Throws <see cref="YouTrackApiException"/> on
    /// overflow.
    /// </summary>
    internal static async Task<string> ReadBoundedStringAsync(
        HttpContent? content, long maxBytes, CancellationToken ct)
    {
        if (content is null)
            return string.Empty;
        if (content.Headers.ContentLength > maxBytes)
            throw new YouTrackApiException(
                $"YouTrack response body exceeds the {maxBytes}-byte cap " +
                $"(declared {content.Headers.ContentLength} bytes).");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(chunk, 0, read);
            if (memory.Length > maxBytes)
                throw new YouTrackApiException(
                    $"YouTrack response body exceeds the {maxBytes}-byte cap.");
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>
    /// Extracts a bounded, single-line error summary from a YouTrack error
    /// body (<c>{"error":…, "error_description":…}</c>). Never throws, never
    /// echoes credentials — the response body is instance text only.
    /// </summary>
    private static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var detail = FirstNonEmpty(doc.RootElement, "error_description", "localized_error", "localizedError", "error", "message");
            if (string.IsNullOrWhiteSpace(detail))
                return null;
            var singleLine = detail.Replace('\n', ' ').Replace('\r', ' ');
            return Clip(singleLine);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
