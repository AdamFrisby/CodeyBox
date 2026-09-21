using System.Net;
using System.Text.Json;

namespace CodeyBox.PlaneWorkSyncPlugin;

/// <summary>
/// Thrown when the Plane API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class PlaneApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public PlaneApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>True when the instance answered "no such endpoint" — a version/capability gap, not a failure.</summary>
    public bool IsCapabilityGap =>
        StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
        or HttpStatusCode.Gone or HttpStatusCode.NotImplemented;
}

/// <summary>
/// Minimal typed REST client for the Plane operations the work-sync plugin
/// needs: listing issues per project, resolving a human key to the immutable
/// issue UUID, posting comments, moving workflow state, and best-effort
/// webhook lifecycle management.
/// <para>Plane renamed <c>issues</c> to <c>work-items</c> in newer releases and
/// self-hosted versions vary: collection requests try the configured
/// <see cref="PlaneWorkSyncOptions.IssuesPath"/> first and fall back to the
/// other spelling on 404, so mixed-version fleets keep working. Instances
/// lacking an endpoint surface a <see cref="PlaneApiException"/> with
/// <see cref="PlaneApiException.IsCapabilityGap"/> set; callers degrade
/// honestly (report, never silently drop) instead of throwing.</para>
/// </summary>
public sealed class PlaneRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly PlaneTokenProvider _tokens;

    public PlaneRestClient(HttpClient http, PlaneTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Lists issues page by page for one project. Each yielded page carries the
    /// project identifier so callers can build human keys; the caller enforces
    /// <c>MaxItemsPerPoll</c> before buffering items.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<PlaneIssue>> ListIssuesPagedAsync(
        PlaneWorkSyncOptions options,
        string projectId,
        string projectIdentifier,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? cursor = null;
        while (true)
        {
            var page = await GetIssuePageAsync(options, projectId, cursor, ct).ConfigureAwait(false);
            yield return page.RawNodes
                .Select(n => PlaneIssue.FromNode(n, projectIdentifier))
                .ToList();
            if (!page.HasMore || string.IsNullOrEmpty(page.NextCursor))
                yield break;
            cursor = page.NextCursor;
        }
    }

    /// <summary>
    /// Resolves a project UUID to its human identifier (e.g. <c>WEB</c>) used
    /// for building ingestion keys. Returns empty when the project cannot be read.
    /// </summary>
    public async Task<string> GetProjectIdentifierAsync(
        PlaneWorkSyncOptions options, string projectId, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("identifier", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString() ?? string.Empty;
        return string.Empty;
    }

    /// <summary>
    /// Resolves a human key (<c>WEB-123</c>) to the immutable issue UUID required
    /// for mutations, by scanning list pages for an exact key match. The scan is
    /// bounded by <c>MaxResolvePages</c>. Returns null when no issue carries that key.
    /// </summary>
    public async Task<string?> ResolveIssueUuidAsync(
        PlaneWorkSyncOptions options,
        string projectId,
        string projectIdentifier,
        string key,
        CancellationToken ct = default)
    {
        var pages = 0;
        string? cursor = null;
        while (pages < Math.Max(1, options.MaxResolvePages))
        {
            pages++;
            var page = await GetIssuePageAsync(options, projectId, cursor, ct).ConfigureAwait(false);
            foreach (var node in page.RawNodes)
            {
                var issue = PlaneIssue.FromNode(node, projectIdentifier);
                if (string.Equals(issue.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(issue.Id))
                    return issue.Id;
            }
            if (!page.HasMore || string.IsNullOrEmpty(page.NextCursor))
                return null;
            cursor = page.NextCursor;
        }
        return null;
    }

    /// <summary>Posts a comment on the issue with the given UUID. Returns the comment id.</summary>
    public async Task<string?> CreateCommentAsync(
        PlaneWorkSyncOptions options,
        string projectId,
        string issueUuid,
        string body,
        CancellationToken ct = default)
    {
        var path = await IssueCollectionPathAsync(options, projectId, ct).ConfigureAwait(false);
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/{path}/{Esc(issueUuid)}/comments/";
        var payload = JsonSerializer.Serialize(
            new { comment_html = $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>" }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        return null;
    }

    /// <summary>Moves the issue to the workflow state with the given id.</summary>
    public async Task UpdateIssueStateAsync(
        PlaneWorkSyncOptions options,
        string projectId,
        string issueUuid,
        string stateId,
        CancellationToken ct = default)
    {
        var path = await IssueCollectionPathAsync(options, projectId, ct).ConfigureAwait(false);
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/{path}/{Esc(issueUuid)}/";
        var payload = JsonSerializer.Serialize(new { state = stateId }, JsonOptions);
        using var _ = await SendAsync(options, HttpMethod.Patch, url, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the workflow states of a project, for resolving a configured state
    /// name to the id mutations require. Name matching is exact
    /// (ordinal-ignore-case) — never substring. Returns empty when the instance
    /// lacks the states endpoint; callers report the gap instead of guessing.
    /// </summary>
    public async Task<IReadOnlyList<PlaneWorkflowState>> ListStatesAsync(
        PlaneWorkSyncOptions options, string projectId, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/states/";
        JsonDocument doc;
        try
        {
            doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        }
        catch (PlaneApiException ex) when (ex.IsCapabilityGap)
        {
            return [];
        }
        using (doc)
        {
            return ReadStateNodes(doc.RootElement).ToList();
        }
    }

    /// <summary>
    /// Lists workspace webhook registrations. Throws
    /// <see cref="PlaneApiException"/> with a capability gap when the instance
    /// lacks the webhooks API; callers fall back to polling.
    /// </summary>
    public async Task<IReadOnlyList<PlaneWebhookRegistration>> ListWebhooksAsync(
        PlaneWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/webhooks/";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var nodes = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
        return nodes
            .Where(n => n.ValueKind == JsonValueKind.Object)
            .Select(n => new PlaneWebhookRegistration(
                IdOf(n),
                Str(n, "url"),
                IsEnabled(n)))
            .Where(w => !string.IsNullOrEmpty(w.Id))
            .ToList();
    }

    /// <summary>
    /// Registers a workspace webhook for work-item and comment events.
    /// Best-effort: the payload uses the documented v2 fields and tolerates
    /// older instances by surfacing capability gaps to the caller. Returns the
    /// webhook id.
    /// </summary>
    public async Task<string?> CreateWebhookAsync(
        PlaneWorkSyncOptions options, string url, string secret, CancellationToken ct = default)
    {
        var endpoint = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/webhooks/";
        var payload = JsonSerializer.Serialize(new
        {
            title = options.WebhookTitle,
            url,
            secret_key = secret,
            is_active = true,
        }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, endpoint, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        return null;
    }

    /// <summary>Removes a workspace webhook registration. Unknown ids report false, never throw.</summary>
    public async Task<bool> DeleteWebhookAsync(
        PlaneWorkSyncOptions options, string webhookId, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/webhooks/{Esc(webhookId)}/";
        try
        {
            using var _ = await SendAsync(options, HttpMethod.Delete, url, null, ct).ConfigureAwait(false);
            return true;
        }
        catch (PlaneApiException ex) when (ex.IsCapabilityGap)
        {
            return false;
        }
    }

    private async Task<PlanePage> GetIssuePageAsync(
        PlaneWorkSyncOptions options, string projectId, string? cursor, CancellationToken ct)
    {
        var path = await IssueCollectionPathAsync(options, projectId, ct).ConfigureAwait(false);
        var query = $"per_page={options.PageSize}"
            + (string.IsNullOrEmpty(cursor) ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
        var url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/{path}/?{query}";
        JsonDocument doc;
        try
        {
            doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        }
        catch (PlaneApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && path == options.IssuesPath)
        {
            // Configured collection spelling is absent on this instance (rename
            // gap): retry once with the alternate spelling before giving up.
            url = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/{AlternatePath(options.IssuesPath)}/?{query}";
            doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        }
        using (doc)
        {
            var root = doc.RootElement.Clone();
            var nodes = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                    ? results.EnumerateArray().ToList()
                    : [];
            var next = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("next_cursor", out var nc)
                && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            var more = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("next_page_results", out var np)
                && np.ValueKind == JsonValueKind.True;
            return new PlanePage(nodes, next, more);
        }
    }

    private sealed record PlanePage(
        IReadOnlyList<JsonElement> RawNodes,
        string? NextCursor,
        bool HasMore);

    private static IEnumerable<PlaneWorkflowState> ReadStateNodes(JsonElement root)
    {
        var nodes = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
        foreach (var n in nodes)
        {
            if (n.ValueKind != JsonValueKind.Object)
                continue;
            var id = Str(n, "id");
            var name = Str(n, "name");
            var group = Str(n, "group");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                yield return new PlaneWorkflowState(id, name, group);
        }
    }

    private static string AlternatePath(string configured) =>
        string.Equals(configured, "work-items", StringComparison.OrdinalIgnoreCase) ? "issues" : "work-items";

    private readonly Dictionary<string, string> _collectionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pathLock = new();

    private async Task<string> IssueCollectionPathAsync(
        PlaneWorkSyncOptions options, string projectId, CancellationToken ct)
    {
        lock (_pathLock)
        {
            if (_collectionPaths.TryGetValue(projectId, out var cached))
                return cached;
        }
        // Probe the configured spelling with a one-item page; on 404 remember
        // the alternate spelling so every later call skips the failing path.
        var probe = $"{Base(options)}/api/v1/workspaces/{Esc(options.WorkspaceSlug)}/projects/{Esc(projectId)}/{options.IssuesPath}/?per_page=1";
        string resolved = options.IssuesPath;
        try
        {
            using var _ = await GetAsync(options, probe, ct).ConfigureAwait(false);
        }
        catch (PlaneApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            resolved = AlternatePath(options.IssuesPath);
        }
        lock (_pathLock)
        {
            _collectionPaths[projectId] = resolved;
        }
        return resolved;
    }

    private static string Base(PlaneWorkSyncOptions options) =>
        string.IsNullOrEmpty(options.ApiBaseUrl) ? "https://api.plane.so" : options.ApiBaseUrl.TrimEnd('/');

    private static string Esc(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string IdOf(JsonElement el)
    {
        foreach (var key in new[] { "id", "webhook_id" })
        {
            var s = Str(el, key);
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        return string.Empty;
    }

    private static bool IsEnabled(JsonElement el)
    {
        foreach (var key in new[] { "is_active", "enabled", "is_enabled" })
        {
            if (el.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.True)
                    return true;
                if (v.ValueKind == JsonValueKind.False)
                    return false;
            }
        }
        return true;
    }

    private async Task<JsonDocument> GetAsync(
        PlaneWorkSyncOptions options, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(
        PlaneWorkSyncOptions options, HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendDocumentAsync(
        PlaneWorkSyncOptions options, HttpRequestMessage request, CancellationToken ct)
    {
        var credential = await _tokens.GetCredentialAsync(options, ct).ConfigureAwait(false);
        if (string.Equals(credential.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value);
        else
            request.Headers.Add("X-API-Key", credential.Value);
        request.Headers.UserAgent.ParseAdd("CodeyBox-PlaneWorkSync/1.0");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonDocument.Parse("{}");
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException(
                $"Plane API returned {(int)response.StatusCode} {response.ReasonPhrase} for {request.Method} {request.RequestUri?.AbsolutePath}.",
                response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
