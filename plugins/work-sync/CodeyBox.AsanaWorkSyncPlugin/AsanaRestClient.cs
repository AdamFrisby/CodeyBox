using System.Net;
using System.Text.Json;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.AsanaWorkSyncPlugin;

/// <summary>
/// Thrown when the Asana API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class AsanaApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public AsanaApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public AsanaApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What the authenticated identity reports about itself (<c>GET /users/me</c>).</summary>
public sealed record AsanaUserInfo(string Gid, string Name);

/// <summary>
/// Minimal typed REST client for the Asana operations the work-sync plugin
/// needs: listing recently-modified tasks per project, reading recent
/// stories (duplicate detection), posting stories (progress, questions,
/// outcomes), marking tasks complete, and writing explicitly-mapped custom
/// fields — against the Asana REST API <c>/api/1.0</c>.
/// <para>List reads request only the identity/signal fields they use via
/// <c>opt_fields</c> (<see cref="TaskFields"/>); pagination follows the
/// opaque <c>next_page.offset</c> token, never a numeric skip. Retries on
/// 429/5xx are bounded by the live options and honour <c>Retry-After</c>
/// within its cap.</para>
/// </summary>
public sealed class AsanaRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Task fields requested on poll reads — bounded to what ingestion uses:
    /// identity (gid, name, notes) and signal fields (assignee, tags,
    /// completion, section memberships, project membership).
    /// </summary>
    internal const string TaskFields =
        "gid,name,notes,assignee.gid,assignee.name,tags.gid,tags.name," +
        "completed,modified_at,projects.gid,memberships.section.gid,memberships.section.name";

    /// <summary>Story fields requested for duplicate detection.</summary>
    internal const string StoryFields = "gid,type,text,created_by.gid,created_by.name";

    /// <summary>Maximum upstream error text surfaced in exception detail.</summary>
    internal const int MaxErrorChars = 300;

    /// <summary>Product token sent as User-Agent — no version, so it cannot drift.</summary>
    internal const string UserAgentProduct = "CodeyBox-AsanaWorkSync";

    private readonly HttpClient _http;
    private readonly AsanaTokenProvider _tokens;
    private readonly TimeProvider _clock;

    public AsanaRestClient(HttpClient http, AsanaTokenProvider tokens, TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Lists tasks page by page for one Asana project GID, following the
    /// opaque <c>next_page.offset</c> token. Each yielded page is already
    /// deserialized; the caller enforces <c>MaxItemsPerPoll</c> before
    /// buffering items. The project GID is validated numeric at this sink —
    /// never interpolated into a URL unvalidated.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<AsanaTask>> ListTasksPagedAsync(
        AsanaWorkSyncOptions options,
        string projectGid,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireGid(projectGid, "project");
        var maxPages = Math.Max(1, options.MaxPagesPerPoll);
        var limit = Math.Clamp(options.PageSize, 1, 100);
        string? offset = null;
        // The page count is bounded independently of MaxItemsPerPoll: an
        // upstream returning perpetually non-terminating pages must not keep
        // the poll issuing requests.
        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{Base(options)}/tasks?project={Uri.EscapeDataString(projectGid)}"
                + $"&opt_fields={Uri.EscapeDataString(TaskFields)}"
                + $"&limit={limit}"
                + ModifiedSinceQuery(options)
                + (offset is null ? string.Empty : $"&offset={Uri.EscapeDataString(offset)}");
            using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var data = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind == JsonValueKind.Array
                    ? dataEl : default;
            IReadOnlyList<AsanaTask> page = data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Select(AsanaTask.FromNode)
                    .Where(t => t is not null)
                    .Cast<AsanaTask>()
                    .ToList()
                : [];
            yield return page;
            offset = NextOffset(root);
            if (offset is null)
                yield break;
        }
    }

    /// <summary>
    /// Reads the most recent stories on a task for duplicate detection,
    /// bounded to <c>DedupScanLimit</c> (a single page: the API maximum page
    /// is 100 and the scan limit is clamped to it).
    /// </summary>
    public async Task<IReadOnlyList<AsanaStory>> ListRecentStoriesAsync(
        AsanaWorkSyncOptions options, string taskGid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireGid(taskGid, "task");
        var limit = Math.Clamp(options.DedupScanLimit, 1, 100);
        var url = $"{Base(options)}/tasks/{taskGid}/stories"
            + $"?opt_fields={Uri.EscapeDataString(StoryFields)}"
            + $"&limit={limit}";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != JsonValueKind.Array)
            return [];
        return dataEl.EnumerateArray()
            .Select(AsanaStory.FromNode)
            .Where(s => s is not null)
            .Cast<AsanaStory>()
            .ToList();
    }

    /// <summary>Posts a story (comment) on the task. Returns the story GID.</summary>
    public async Task<string?> CreateStoryAsync(
        AsanaWorkSyncOptions options, string taskGid, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(text);
        RequireGid(taskGid, "task");
        var url = $"{Base(options)}/tasks/{taskGid}/stories";
        var payload = JsonSerializer.Serialize(new { data = new { text } }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("gid", out var gid)
            && gid.ValueKind == JsonValueKind.String
                ? gid.GetString() : null;
    }

    /// <summary>
    /// Marks a task complete or incomplete. The decision (which external
    /// status means complete) is made by the caller from the operator's
    /// explicit state mapping; this method applies exactly what it is told.
    /// </summary>
    public async Task SetCompletedAsync(
        AsanaWorkSyncOptions options, string taskGid, bool completed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireGid(taskGid, "task");
        var url = $"{Base(options)}/tasks/{taskGid}";
        var payload = JsonSerializer.Serialize(
            new { data = new { completed } }, JsonOptions);
        using var _ = await SendAsync(options, HttpMethod.Put, url, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one custom-field enum value on a task. Both GIDs are validated
    /// numeric at this sink. Called only for caller-resolved external
    /// statuses present in the operator's explicit
    /// <c>StatusCustomFieldMap</c> — never guessed.
    /// </summary>
    public async Task SetCustomFieldAsync(
        AsanaWorkSyncOptions options, string taskGid, string fieldGid, string enumGid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireGid(taskGid, "task");
        RequireGid(fieldGid, "custom field");
        RequireGid(enumGid, "custom-field enum option");
        var url = $"{Base(options)}/tasks/{taskGid}";
        var payload = JsonSerializer.Serialize(
            new { data = new { custom_fields = new Dictionary<string, string> { [fieldGid] = enumGid } } },
            JsonOptions);
        using var _ = await SendAsync(options, HttpMethod.Put, url, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the authenticated identity (<c>GET /users/me</c>) — the minimal
    /// live smoke check that a credential works.
    /// </summary>
    public async Task<AsanaUserInfo?> GetAuthenticatedUserAsync(
        AsanaWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/users/me?opt_fields={Uri.EscapeDataString("gid,name")}";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            return null;
        var gid = AsanaTask.Str(data, "gid");
        if (!AsanaGids.IsGid(gid))
            return null;
        return new AsanaUserInfo(gid, AsanaTask.Str(data, "name"));
    }

    /// <summary>
    /// Requires a numeric Asana GID at the request-building sink. GIDs are
    /// immutable numeric strings; anything else is refused before a URL is
    /// built — never interpolated unvalidated.
    /// </summary>
    internal static void RequireGid(string value, string role)
    {
        if (!AsanaGids.IsGid(value))
            throw new ArgumentException(
                $"Asana {role} GID '{value}' is not a valid numeric GID.", nameof(value));
    }

    /// <summary>
    /// Parses an explicit custom-field mapping value in
    /// <c>fieldGid:enumGid</c> form. Returns false (never throws for shape)
    /// when the value is not two numeric GIDs.
    /// </summary>
    internal static bool TryParseCustomFieldMapping(
        string mapping, out string fieldGid, out string enumGid)
    {
        fieldGid = string.Empty;
        enumGid = string.Empty;
        if (string.IsNullOrWhiteSpace(mapping))
            return false;
        var parts = mapping.Split(':');
        if (parts.Length != 2)
            return false;
        var field = parts[0].Trim();
        var option = parts[1].Trim();
        if (!AsanaGids.IsGid(field) || !AsanaGids.IsGid(option))
            return false;
        fieldGid = field;
        enumGid = option;
        return true;
    }

    private string ModifiedSinceQuery(AsanaWorkSyncOptions options)
    {
        if (options.ModifiedSinceHours <= 0)
            return string.Empty;
        var since = _clock.GetUtcNow().AddHours(-options.ModifiedSinceHours);
        return $"&modified_since={Uri.EscapeDataString(since.ToString("o"))}";
    }

    private static string? NextOffset(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("next_page", out var next)
            || next.ValueKind != JsonValueKind.Object
            || !next.TryGetProperty("offset", out var offset)
            || offset.ValueKind != JsonValueKind.String)
            return null;
        var token = offset.GetString();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// The configured API origin including the version path. An unset,
    /// non-http(s), or plaintext-http value is an operator misconfiguration
    /// and throws before a request is built — the bearer credential must
    /// never be aimed at a placeholder, a non-HTTP target, or a cleartext
    /// channel. <c>http://</c> requires the explicit dev-only <see
    /// cref="AsanaWorkSyncOptions.AllowUnsafeHttp"/> opt-in.
    /// </summary>
    private static string Base(AsanaWorkSyncOptions options)
    {
        var raw = options.ApiBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && options.AllowUnsafeHttp)))
            throw new InvalidOperationException(
                "Asana ApiBaseUrl is not configured or is not an absolute https URL; " +
                "set it in the plugin configuration before enabling work sync " +
                "(plaintext http:// would send credentials unencrypted and requires " +
                "the dev-only AllowUnsafeHttp=true opt-in).");
        return raw;
    }

    private static string? FirstErrorMessage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object)
                    continue;
                var message = AsanaTask.Str(error, "message");
                if (!string.IsNullOrWhiteSpace(message))
                    return message;
            }
        }
        return null;
    }

    private static string Clip(string value) =>
        value.Length <= MaxErrorChars ? value : value[..MaxErrorChars];

    private async Task<JsonDocument> GetAsync(
        AsanaWorkSyncOptions options, string url, CancellationToken ct)
    {
        return await SendWithRetryAsync(
            options, () => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(
        AsanaWorkSyncOptions options, HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        return await SendWithRetryAsync(options, () =>
        {
            var request = new HttpRequestMessage(method, url);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            return request;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one request with bounded retries on 429 and 502/503/504/500
    /// (Asana documents 500s as sometimes load-related). <c>Retry-After</c>
    /// is honoured in seconds or HTTP-date form within its cap; otherwise an
    /// exponential backoff from the live option applies. Every bound comes
    /// from options, never literals, so edits hot-reload.
    /// </summary>
    private async Task<JsonDocument> SendWithRetryAsync(
        AsanaWorkSyncOptions options, Func<HttpRequestMessage> buildRequest, CancellationToken ct)
    {
        var maxAttempts = Math.Max(0, options.RetryMaxAttempts);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Per-attempt timeout from the live option, so TimeoutSeconds
            // edits hot-reload; the client-level timeout is disabled on the
            // client this plugin owns. Covers credential acquisition, send,
            // and body read.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            var requestCt = timeout.Token;

            var credential = await _tokens.GetCredentialAsync(options, requestCt).ConfigureAwait(false);
            using var request = buildRequest();
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                credential.Scheme, credential.Value);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd(UserAgentProduct);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, requestCt).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AsanaApiException(
                    $"Asana API request timed out after {Math.Max(1, options.TimeoutSeconds)}s " +
                    $"for {request.Method} {request.RequestUri?.AbsolutePath}.", ex);
            }

            using (response)
            {
                // Credential-bearing traffic never follows redirects: the
                // owned client disables auto-redirect, and this explicit
                // refusal covers any injected client whose handler follows
                // them. A 3xx is untrusted upstream output — following it
                // would re-send the bearer token to the redirect target —
                // so it fails the request instead of being retried.
                if (CredentialHttp.IsRedirect(response.StatusCode))
                    throw new AsanaApiException(
                        $"Asana API returned redirect {(int)response.StatusCode} {response.ReasonPhrase} " +
                        $"for {request.Method} {request.RequestUri?.AbsolutePath}: redirects are refused " +
                        "and never followed with credentials.",
                        response.StatusCode);
                if (!IsRetryable(response.StatusCode) || attempt >= maxAttempts)
                    return await ReadSuccessAsync(options, request, response, requestCt).ConfigureAwait(false);

                var delay = RetryDelay(response, attempt, options);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is (HttpStatusCode)429
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt, AsanaWorkSyncOptions options)
    {
        var cap = TimeSpan.FromSeconds(Math.Max(1, options.RetryMaxDelaySeconds));
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta >= TimeSpan.Zero && delta <= cap)
                return delta;
            if (retryAfter.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until >= TimeSpan.Zero && until <= cap)
                    return until;
            }
        }
        var baseMs = Math.Max(0, options.RetryBaseDelayMs);
        var backoffMs = (double)baseMs * (1L << Math.Min(attempt, 10));
        var cappedMs = Math.Min(backoffMs, cap.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(0, cappedMs));
    }

    private async Task<JsonDocument> ReadSuccessAsync(
        AsanaWorkSyncOptions options,
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken requestCt)
    {
        var body = await ReadBoundedStringAsync(
            response.Content, options.MaxResponseBytes, requestCt).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ErrorDetail(body) is { } d ? $": {d}" : string.Empty;
            throw new AsanaApiException(
                $"Asana API returned {(int)response.StatusCode} {response.ReasonPhrase} " +
                $"for {request.Method} {request.RequestUri?.AbsolutePath}{detail}.",
                response.StatusCode);
        }
        if (string.IsNullOrWhiteSpace(body))
            return JsonDocument.Parse("{\"data\":[]}");
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AsanaApiException(
                $"Asana API returned malformed JSON for {request.Method} {request.RequestUri?.AbsolutePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a response body with a hard byte cap — the upstream is a
    /// less-trusted dependency, so its output is bounded before buffering:
    /// the declared Content-Length is checked first, then the stream is read
    /// in chunks and rejected the moment it exceeds <paramref
    /// name="maxBytes"/>. Throws <see cref="AsanaApiException"/> on
    /// overflow.
    /// </summary>
    internal static async Task<string> ReadBoundedStringAsync(
        HttpContent? content, long maxBytes, CancellationToken ct)
    {
        if (content is null)
            return string.Empty;
        if (content.Headers.ContentLength > maxBytes)
            throw new AsanaApiException(
                $"Asana response body exceeds the {maxBytes}-byte cap " +
                $"(declared {content.Headers.ContentLength} bytes).");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(chunk, 0, read);
            if (memory.Length > maxBytes)
                throw new AsanaApiException(
                    $"Asana response body exceeds the {maxBytes}-byte cap.");
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>
    /// Extracts a bounded, single-line error summary from an Asana error body
    /// (<c>{"errors":[{"message":…}]}</c>). Never throws, never echoes
    /// credentials — the response body is instance text only.
    /// </summary>
    private static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var detail = FirstErrorMessage(doc.RootElement);
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
