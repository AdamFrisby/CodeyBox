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
        "id,idReadable,summary,description,updated,project(id,shortName)," +
        "tags(name),customFields(name,value(name,login,fullName,$type),$type)," +
        "updater(login,fullName)";

    /// <summary>Maximum upstream error text surfaced in exception detail.</summary>
    internal const int MaxErrorChars = 300;

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
        var skip = 0;
        while (true)
        {
            var url = $"{Base(options)}/api/issues"
                + $"?query={Uri.EscapeDataString(query)}"
                + $"&fields={Uri.EscapeDataString(IssueFields)}"
                + $"&$top={options.PageSize}&$skip={skip}";
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            IReadOnlyList<YouTrackIssue> page = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                    .Select(n => YouTrackIssue.FromNode(n, options))
                    .Where(i => i is not null)
                    .Cast<YouTrackIssue>()
                    .ToList()
                : [];
            yield return page;
            if (page.Count < options.PageSize)
                yield break;
            skip += page.Count;
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
    /// Applies a YouTrack command to the issue (e.g. <c>{State} {In
    /// Progress}</c>). YouTrack state changes are commands, not field writes:
    /// the state-machine bundle and workflow rules decide whether the value
    /// applies, and a rejection surfaces here as a
    /// <see cref="YouTrackApiException"/> — the caller reports it, never a
    /// guessed write. The command text is built by the caller through
    /// <see cref="YouTrackWorkSyncPlugin.BuildStateCommand"/> which quotes and
    /// validates both operands.
    /// </summary>
    public async Task ApplyCommandAsync(
        YouTrackWorkSyncOptions options, string issueId, string commandQuery, CancellationToken ct = default)
    {
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
    /// Probes what the instance actually exposes: <c>GET /api/config</c>
    /// carries version/build on both cloud and self-hosted. A missing or
    /// forbidden endpoint (older Server, low-privilege token) reports
    /// <c>HasRestApi=false</c> rather than throwing — capability is declared
    /// from what the instance answers, not assumed.
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
                Str(root, "version"), Str(root, "build"));
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

    private static string Base(YouTrackWorkSyncOptions options) =>
        string.IsNullOrEmpty(options.ApiBaseUrl)
            ? "https://example.youtrack.cloud"
            : options.ApiBaseUrl.TrimEnd('/');

    private static string Esc(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string? FirstNonEmpty(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            var s = Str(el, name);
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
        var credential = await _tokens.GetCredentialAsync(options, ct).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            credential.Scheme, credential.Value);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodeyBox-YouTrackWorkSync/1.0");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonDocument.Parse("{}");
        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
