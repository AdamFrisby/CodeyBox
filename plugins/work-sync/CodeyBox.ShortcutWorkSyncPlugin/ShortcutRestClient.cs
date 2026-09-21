using System.Net;
using System.Text.Json;

namespace CodeyBox.ShortcutWorkSyncPlugin;

/// <summary>
/// Thrown when the Shortcut API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class ShortcutApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public ShortcutApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Minimal typed REST client for the Shortcut operations the work-sync plugin
/// needs: searching stories page by page, listing epics, resolving members for
/// assignee-signal matching, resolving workflow-state names to ids, posting
/// comments, and moving story workflow state.
/// <para>Request paths are fixed strings — caller values travel only as JSON
/// bodies or as numeric path segments validated by parsing, never as
/// interpolated query text.</para>
/// </summary>
public sealed class ShortcutRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ShortcutTokenProvider _tokens;

    public ShortcutRestClient(HttpClient http, ShortcutTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Searches stories page by page via <c>POST /api/v3/stories/search</c>.
    /// Each yielded page is already deserialized; the caller enforces
    /// <c>MaxItemsPerPoll</c> before buffering items.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<ShortcutStory>> SearchStoriesPagedAsync(
        ShortcutWorkSyncOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? next = null;
        while (true)
        {
            using var page = await PostSearchPageAsync(options, next, ct).ConfigureAwait(false);
            var root = page.RootElement;
            var nodes = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(ShortcutStory.FromNode).ToList()
                : new List<ShortcutStory>();
            yield return nodes;
            next = root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String
                ? nextProp.GetString() : null;
            if (string.IsNullOrEmpty(next))
                yield break;
        }
    }

    /// <summary>
    /// Lists epics via <c>GET /api/v3/epics</c>. Shortcut returns the full
    /// array; the caller enforces <c>MaxItemsPerPoll</c> before buffering.
    /// </summary>
    public async Task<IReadOnlyList<ShortcutEpic>> ListEpicsAsync(
        ShortcutWorkSyncOptions options, CancellationToken ct = default)
    {
        using var doc = await GetAsync(options, $"{Base(options)}/api/v3/epics", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return [];
        return root.EnumerateArray().Select(ShortcutEpic.FromNode).ToList();
    }

    /// <summary>
    /// Lists workspace members for assignee-signal matching (owner id → email /
    /// mention name) and loop-guard attribution. Returns an empty list when the
    /// endpoint cannot be read; callers treat unknown owners as unmatched, never
    /// as a signal.
    /// </summary>
    public async Task<IReadOnlyList<ShortcutMember>> ListMembersAsync(
        ShortcutWorkSyncOptions options, CancellationToken ct = default)
    {
        using var doc = await GetAsync(options, $"{Base(options)}/api/v3/members", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return [];
        var members = new List<ShortcutMember>();
        foreach (var node in root.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;
            var id = Str(node, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            string mention = string.Empty, email = string.Empty, name = string.Empty;
            if (node.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                mention = Str(profile, "mention_name");
                email = Str(profile, "email_address");
                name = Str(profile, "name");
            }
            members.Add(new ShortcutMember(id, mention, email, name));
        }
        return members;
    }

    /// <summary>
    /// Lists all workflow states across workflows, for resolving a configured
    /// state name to the id mutations require. Name matching is exact
    /// (ordinal-ignore-case) — never substring.
    /// </summary>
    public async Task<IReadOnlyList<ShortcutWorkflowState>> ListWorkflowStatesAsync(
        ShortcutWorkSyncOptions options, CancellationToken ct = default)
    {
        using var doc = await GetAsync(options, $"{Base(options)}/api/v3/workflows", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return [];
        var states = new List<ShortcutWorkflowState>();
        foreach (var workflow in root.EnumerateArray())
        {
            if (workflow.ValueKind != JsonValueKind.Object
                || !workflow.TryGetProperty("states", out var list)
                || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var state in list.EnumerateArray())
            {
                if (state.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Str(state, "name");
                var id = state.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                    && idProp.TryGetInt64(out var n) ? n : 0;
                if (id != 0 && !string.IsNullOrWhiteSpace(name))
                    states.Add(new ShortcutWorkflowState(id, name));
            }
        }
        return states;
    }

    /// <summary>Posts a comment on a story. Returns the comment id.</summary>
    public async Task<string?> CreateStoryCommentAsync(
        ShortcutWorkSyncOptions options, long storyId, string text, CancellationToken ct = default)
    {
        using var doc = await PostAsync(
            options, $"{Base(options)}/api/v3/stories/{storyId}/comments",
            new Dictionary<string, object?> { ["text"] = text }, ct).ConfigureAwait(false);
        return CommentIdOf(doc.RootElement);
    }

    /// <summary>Posts a comment on an epic. Returns the comment id.</summary>
    public async Task<string?> CreateEpicCommentAsync(
        ShortcutWorkSyncOptions options, long epicId, string text, CancellationToken ct = default)
    {
        using var doc = await PostAsync(
            options, $"{Base(options)}/api/v3/epics/{epicId}/comments",
            new Dictionary<string, object?> { ["text"] = text }, ct).ConfigureAwait(false);
        return CommentIdOf(doc.RootElement);
    }

    /// <summary>Moves a story to the workflow state with the given id.</summary>
    public async Task UpdateStoryStateAsync(
        ShortcutWorkSyncOptions options, long storyId, long stateId, CancellationToken ct = default)
    {
        using var request = await SendAsync(
            options,
            HttpMethod.Put,
            $"{Base(options)}/api/v3/stories/{storyId}",
            new Dictionary<string, object?> { ["workflow_state_id"] = stateId },
            ct).ConfigureAwait(false);
        request.EnsureSuccessStatusCode();
    }

    private async Task<JsonDocument> PostSearchPageAsync(
        ShortcutWorkSyncOptions options, string? next, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["page_size"] = options.PageSize };
        if (!string.IsNullOrEmpty(next))
            body["next"] = next;
        return await PostAsync(options, $"{Base(options)}/api/v3/stories/search", body, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetAsync(
        ShortcutWorkSyncOptions options, string url, CancellationToken ct)
    {
        using var response = await SendAsync(options, HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ShortcutApiException(
                $"Shortcut API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostAsync(
        ShortcutWorkSyncOptions options,
        string url,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken ct)
    {
        using var response = await SendAsync(options, HttpMethod.Post, url, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ShortcutApiException(
                $"Shortcut API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        ShortcutWorkSyncOptions options,
        HttpMethod method,
        string url,
        IReadOnlyDictionary<string, object?>? body,
        CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(options, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Shortcut-Token", token);
        request.Headers.UserAgent.ParseAdd("CodeyBox-ShortcutWorkSync/1.0");
        if (body is not null)
        {
            var payload = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        }
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static string? CommentIdOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n))
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        return null;
    }

    private static string Base(ShortcutWorkSyncOptions options) => options.ApiBaseUrl.TrimEnd('/');

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}
