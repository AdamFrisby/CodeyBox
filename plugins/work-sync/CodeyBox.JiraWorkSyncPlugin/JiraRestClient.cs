using System.Net;
using System.Text.Json;

namespace CodeyBox.JiraWorkSyncPlugin;

/// <summary>
/// Thrown when the Jira API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class JiraApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public JiraApiException(string message, HttpStatusCode? statusCode = null)
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
/// Minimal typed REST client for the Jira operations the work-sync plugin
/// needs: searching recently-updated issues per project, posting comments,
/// listing and executing workflow transitions, and managing webhook
/// registrations with their 30-day renewal lifecycle.
/// <para>Search uses the current Cloud <c>/search/jql</c> endpoint and falls
/// back to the legacy <c>/search</c> endpoint on 404 so Server/DC instances
/// keep working. Webhook payloads tolerate both the list and the
/// registration-result response shapes.</para>
/// </summary>
public sealed class JiraRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<string> IssueFields =
        ["summary", "description", "labels", "assignee", "status", "project"];

    private readonly HttpClient _http;
    private readonly JiraTokenProvider _tokens;

    public JiraRestClient(HttpClient http, JiraTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Searches issues page by page for one Jira project key, newest first.
    /// Each yielded page is already deserialized; the caller enforces
    /// <c>MaxItemsPerPoll</c> before buffering items.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<JiraIssue>> SearchIssuesPagedAsync(
        JiraWorkSyncOptions options,
        string projectKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var jql = $"project = {QuoteJqlOperand(projectKey)} ORDER BY updated DESC";
        string? nextPageToken = null;
        var useLegacy = false;
        var legacyStartAt = 0;
        while (true)
        {
            if (useLegacy)
            {
                var legacyPage = await SearchLegacyAsync(options, jql, legacyStartAt, ct).ConfigureAwait(false);
                yield return legacyPage.Issues;
                if (legacyPage.Issues.Count == 0 || legacyStartAt + legacyPage.Issues.Count >= legacyPage.Total)
                    yield break;
                legacyStartAt += legacyPage.Issues.Count;
                continue;
            }
            JiraSearchPage page;
            try
            {
                page = await SearchJqlAsync(options, jql, nextPageToken, ct).ConfigureAwait(false);
            }
            catch (JiraApiException ex) when (ex.IsCapabilityGap || ex.StatusCode == HttpStatusCode.BadRequest)
            {
                useLegacy = true;
                continue;
            }
            yield return page.Issues;
            if (string.IsNullOrEmpty(page.NextPageToken))
                yield break;
            nextPageToken = page.NextPageToken;
        }
    }

    /// <summary>
    /// Lists the transitions reachable from the issue's current state, for
    /// resolving a declared status name to the transition id mutations
    /// require. Throws <see cref="JiraApiException"/> with 404 when the issue
    /// does not exist.
    /// </summary>
    public async Task<IReadOnlyList<JiraTransition>> ListTransitionsAsync(
        JiraWorkSyncOptions options, string issueKey, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/rest/api/3/issue/{Esc(issueKey)}/transitions";
        using var doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("transitions", out var transitions)
            || transitions.ValueKind != JsonValueKind.Array)
            return [];
        return transitions.EnumerateArray()
            .Where(n => n.ValueKind == JsonValueKind.Object)
            .Select(n => new JiraTransition(
                Str(n, "id"),
                Str(n, "name"),
                n.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object
                    ? Str(to, "name") : string.Empty))
            .Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
            .ToList();
    }

    /// <summary>
    /// Executes a reachable transition on the issue. A 400 means Jira rejected
    /// the transition (guard, screen, or permission) — a real outcome the
    /// caller reports, never a silent skip.
    /// </summary>
    public async Task ExecuteTransitionAsync(
        JiraWorkSyncOptions options,
        string issueKey,
        string transitionId,
        CancellationToken ct = default)
    {
        var url = $"{Base(options)}/rest/api/3/issue/{Esc(issueKey)}/transitions";
        var payload = JsonSerializer.Serialize(
            new { transition = new { id = transitionId } }, JsonOptions);
        using var _ = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Posts a comment on the issue. Returns the comment id.</summary>
    public async Task<string?> CreateCommentAsync(
        JiraWorkSyncOptions options,
        string issueKey,
        string body,
        CancellationToken ct = default)
    {
        var url = $"{Base(options)}/rest/api/3/issue/{Esc(issueKey)}/comment";
        var payload = JsonSerializer.Serialize(
            new { body = AdfText.ToDocument(body) }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() : null;
    }

    /// <summary>Lists the site webhook registrations visible to the credential.</summary>
    public async Task<IReadOnlyList<JiraWebhookRegistration>> ListWebhooksAsync(
        JiraWorkSyncOptions options, CancellationToken ct = default)
    {
        var url = $"{Base(options)}/rest/api/3/webhook";
        JsonDocument doc;
        try
        {
            doc = await GetAsync(options, url, ct).ConfigureAwait(false);
        }
        catch (JiraApiException ex) when (ex.IsCapabilityGap)
        {
            return [];
        }
        using (doc)
        {
            var root = doc.RootElement;
            var values = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("values", out var v)
                && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();
            return values
                .Where(n => n.ValueKind == JsonValueKind.Object)
                .Select(n => new JiraWebhookRegistration(
                    IdOf(n),
                    Str(n, "url"),
                    n.TryGetProperty("expirationDate", out var exp)
                    && exp.ValueKind == JsonValueKind.Number
                    && exp.TryGetInt64(out var ms) ? ms : 0))
                .Where(w => !string.IsNullOrEmpty(w.Id))
                .ToList();
        }
    }

    /// <summary>
    /// Registers a site webhook for issue and comment events. Returns the
    /// registration id. Jira registrations expire after 30 days — the caller
    /// renews them via <see cref="RefreshWebhooksAsync"/> instead of assuming
    /// they persist.
    /// </summary>
    public async Task<string?> CreateWebhookAsync(
        JiraWorkSyncOptions options, string url, CancellationToken ct = default)
    {
        var endpoint = $"{Base(options)}/rest/api/3/webhook";
        var payload = JsonSerializer.Serialize(new
        {
            name = "CodeyBox work sync",
            url,
            events = new[]
            {
                "jira:issue_created",
                "jira:issue_updated",
                "jira:issue_deleted",
                "comment_created",
                "comment_updated",
            },
            excludeBody = false,
        }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, endpoint, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("webhookRegistrationResult", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object)
                    continue;
                var id = IdOf(r);
                if (!string.IsNullOrEmpty(id))
                    return id;
            }
        }
        return IdOf(root);
    }

    /// <summary>
    /// Renews webhook registrations, extending their 30-day expiry. Returns
    /// the ids Jira accepted. Unknown ids report empty, never throw.
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshWebhooksAsync(
        JiraWorkSyncOptions options, IReadOnlyList<string> webhookIds, CancellationToken ct = default)
    {
        if (webhookIds.Count == 0)
            return [];
        var endpoint = $"{Base(options)}/rest/api/3/webhook/refresh";
        var payload = JsonSerializer.Serialize(new { webhookIds }, JsonOptions);
        try
        {
            using var doc = await SendAsync(options, HttpMethod.Put, endpoint, payload, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("webhookIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                return ids.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
            return webhookIds;
        }
        catch (JiraApiException ex) when (ex.IsCapabilityGap)
        {
            return [];
        }
    }

    /// <summary>Removes site webhook registrations. Unknown ids report false, never throw.</summary>
    public async Task<bool> DeleteWebhooksAsync(
        JiraWorkSyncOptions options, IReadOnlyList<string> webhookIds, CancellationToken ct = default)
    {
        if (webhookIds.Count == 0)
            return false;
        var endpoint = $"{Base(options)}/rest/api/3/webhook";
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { webhookIds }, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        try
        {
            using var _ = await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
            return true;
        }
        catch (JiraApiException)
        {
            return false;
        }
    }

    private sealed record JiraSearchPage(IReadOnlyList<JiraIssue> Issues, string? NextPageToken, long Total);

    private async Task<JiraSearchPage> SearchJqlAsync(
        JiraWorkSyncOptions options, string jql, string? nextPageToken, CancellationToken ct)
    {
        var url = $"{Base(options)}/rest/api/3/search/jql";
        var payload = JsonSerializer.Serialize(new
        {
            jql,
            maxResults = options.PageSize,
            fields = IssueFields,
            nextPageToken,
        }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement.Clone();
        var issues = root.TryGetProperty("issues", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.Object)
                .Select(JiraIssue.FromNode)
                .ToList()
            : (IReadOnlyList<JiraIssue>)[];
        var token = root.TryGetProperty("nextPageToken", out var nt) && nt.ValueKind == JsonValueKind.String
            ? nt.GetString() : null;
        var isLast = root.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True;
        return new JiraSearchPage(issues, isLast ? null : token, issues.Count);
    }

    private async Task<JiraSearchPage> SearchLegacyAsync(
        JiraWorkSyncOptions options, string jql, int startAt, CancellationToken ct)
    {
        var url = $"{Base(options)}/rest/api/3/search";
        var payload = JsonSerializer.Serialize(new
        {
            jql,
            startAt,
            maxResults = options.PageSize,
            fields = IssueFields,
        }, JsonOptions);
        using var doc = await SendAsync(options, HttpMethod.Post, url, payload, ct).ConfigureAwait(false);
        var root = doc.RootElement.Clone();
        var issues = root.TryGetProperty("issues", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.Object)
                .Select(JiraIssue.FromNode)
                .ToList()
            : (IReadOnlyList<JiraIssue>)[];
        var totalCount = root.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
            && totalEl.TryGetInt64(out var totalValue) ? totalValue : issues.Count;
        return new JiraSearchPage(issues, null, totalCount);
    }

    private static string QuoteJqlOperand(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static string Base(JiraWorkSyncOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OAuthCloudId))
            return $"https://api.atlassian.com/ex/jira/{options.OAuthCloudId.Trim()}";
        return string.IsNullOrEmpty(options.ApiBaseUrl)
            ? "https://example.atlassian.net"
            : options.ApiBaseUrl.TrimEnd('/');
    }

    private static string Esc(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string IdOf(JsonElement el)
    {
        foreach (var key in new[] { "id", "createdWebhookId" })
        {
            if (el.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
                else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                {
                    return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        return string.Empty;
    }

    private async Task<JsonDocument> GetAsync(
        JiraWorkSyncOptions options, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(
        JiraWorkSyncOptions options, HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return await SendDocumentAsync(options, request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendDocumentAsync(
        JiraWorkSyncOptions options, HttpRequestMessage request, CancellationToken ct)
    {
        var credential = await _tokens.GetCredentialAsync(options, ct).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            credential.Scheme, credential.Value);
        request.Headers.UserAgent.ParseAdd("CodeyBox-JiraWorkSync/1.0");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonDocument.Parse("{}");
        if (!response.IsSuccessStatusCode)
            throw new JiraApiException(
                $"Jira API returned {(int)response.StatusCode} {response.ReasonPhrase} for {request.Method} {request.RequestUri?.AbsolutePath}.",
                response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
