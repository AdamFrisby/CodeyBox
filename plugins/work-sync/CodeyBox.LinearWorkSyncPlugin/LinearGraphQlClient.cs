using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.LinearWorkSyncPlugin;

/// <summary>
/// Thrown when the Linear API returns an error payload or a non-success
/// status. Carries the status code and the (truncated) error summary only —
/// never tokens, secrets, or request bodies.
/// </summary>
public sealed class LinearApiException : Exception
{
    public System.Net.HttpStatusCode? StatusCode { get; }

    public LinearApiException(string message, System.Net.HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Minimal typed GraphQL client for the Linear operations the work-sync
/// plugin needs: listing recently-updated issues, resolving an identifier to
/// the immutable issue id, posting comments, moving workflow state, and
/// managing webhook registrations. All queries are fixed strings — no caller
/// input is interpolated into GraphQL source; caller values travel only as
/// JSON variables.
/// </summary>
public sealed class LinearGraphQlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LinearTokenProvider _tokens;

    public LinearGraphQlClient(HttpClient http, LinearTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// Lists issues page by page. Each yielded page is already deserialized;
    /// the caller enforces <c>MaxItemsPerPoll</c> before buffering items.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<LinearIssue>> ListIssuesPagedAsync(
        LinearWorkSyncOptions options,
        int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const string query = """
            query($first: Int, $after: String) {
              issues(first: $first, after: $after) {
                nodes {
                  id identifier title description
                  team { id key }
                  labels { nodes { name } }
                  assignee { id email name }
                  state { id name }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
            """;

        string? cursor = null;
        while (true)
        {
            var doc = await PostAsync(options, query,
                new Dictionary<string, object?> { ["first"] = pageSize, ["after"] = cursor }, ct)
                .ConfigureAwait(false);
            var issues = doc.GetProperty("data").GetProperty("issues");
            var nodes = issues.GetProperty("nodes").EnumerateArray()
                .Select(LinearIssue.FromNode)
                .ToList();
            var pageInfo = issues.GetProperty("pageInfo");
            yield return nodes;
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean())
                yield break;
            cursor = pageInfo.TryGetProperty("endCursor", out var end) && end.ValueKind == JsonValueKind.String
                ? end.GetString() : null;
            if (string.IsNullOrEmpty(cursor))
                yield break;
        }
    }

    /// <summary>
    /// Resolves a human identifier (<c>ENG-123</c>) to the immutable issue id
    /// required for mutations, via exact match on the search result.
    /// Returns null when no issue carries that identifier.
    /// </summary>
    public async Task<string?> ResolveIssueIdAsync(
        LinearWorkSyncOptions options, string identifier, CancellationToken ct = default)
    {
        const string query = """
            query($term: String!) {
              searchIssues(term: $term, first: 10) {
                nodes { id identifier }
              }
            }
            """;
        var doc = await PostAsync(options, query,
            new Dictionary<string, object?> { ["term"] = identifier }, ct).ConfigureAwait(false);
        foreach (var node in doc.GetProperty("data").GetProperty("searchIssues").GetProperty("nodes").EnumerateArray())
        {
            var found = node.TryGetProperty("identifier", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString() : null;
            if (string.Equals(found, identifier, StringComparison.OrdinalIgnoreCase)
                && node.TryGetProperty("id", out var uuid) && uuid.ValueKind == JsonValueKind.String)
                return uuid.GetString();
        }
        return null;
    }

    /// <summary>Posts a comment on the issue with the given Linear id. Returns the comment id.</summary>
    public async Task<string?> CreateCommentAsync(
        LinearWorkSyncOptions options, string issueId, string body, CancellationToken ct = default)
    {
        const string query = """
            mutation($input: CommentCreateInput!) {
              commentCreate(input: $input) { success comment { id } }
            }
            """;
        var doc = await PostAsync(options, query,
            new Dictionary<string, object?>
            {
                ["input"] = new Dictionary<string, object?> { ["issueId"] = issueId, ["body"] = body },
            }, ct).ConfigureAwait(false);
        var payload = doc.GetProperty("data").GetProperty("commentCreate");
        if (!payload.GetProperty("success").GetBoolean())
            throw new LinearApiException("Linear commentCreate reported success=false.");
        return payload.TryGetProperty("comment", out var comment)
            && comment.ValueKind == JsonValueKind.Object
            && comment.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            ? id.GetString() : null;
    }

    /// <summary>Moves the issue to the workflow state with the given id.</summary>
    public async Task UpdateIssueStateAsync(
        LinearWorkSyncOptions options, string issueId, string stateId, CancellationToken ct = default)
    {
        const string query = """
            mutation($id: String!, $input: IssueUpdateInput!) {
              issueUpdate(id: $id, input: $input) { success }
            }
            """;
        var doc = await PostAsync(options, query,
            new Dictionary<string, object?>
            {
                ["id"] = issueId,
                ["input"] = new Dictionary<string, object?> { ["stateId"] = stateId },
            }, ct).ConfigureAwait(false);
        if (!doc.GetProperty("data").GetProperty("issueUpdate").GetProperty("success").GetBoolean())
            throw new LinearApiException("Linear issueUpdate reported success=false.");
    }

    /// <summary>
    /// Lists the workflow states of the team owning the issue, for resolving
    /// a configured state name to the id mutations require. Name matching is
    /// exact (ordinal-ignore-case) — never substring.
    /// </summary>
    public async Task<IReadOnlyList<LinearWorkflowState>> ListWorkflowStatesAsync(
        LinearWorkSyncOptions options, string issueId, CancellationToken ct = default)
    {
        const string query = """
            query($id: String!) {
              issue(id: $id) {
                team { states { nodes { id name } } }
              }
            }
            """;
        var doc = await PostAsync(options, query,
            new Dictionary<string, object?> { ["id"] = issueId }, ct).ConfigureAwait(false);
        var states = doc.GetProperty("data").GetProperty("issue").GetProperty("team").GetProperty("states");
        return states.GetProperty("nodes").EnumerateArray()
            .Select(n => new LinearWorkflowState(
                n.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                n.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty))
            .Where(s => !string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(s.Name))
            .ToList();
    }

    /// <summary>Lists the workspace webhook registrations visible to the token.</summary>
    public async Task<IReadOnlyList<LinearWebhookRegistration>> ListWebhooksAsync(
        LinearWorkSyncOptions options, CancellationToken ct = default)
    {
        const string query = """
            query { webhooks { nodes { id url enabled resourceTypes } } }
            """;
        var doc = await PostAsync(options, query, new Dictionary<string, object?>(), ct).ConfigureAwait(false);
        return doc.GetProperty("data").GetProperty("webhooks").GetProperty("nodes").EnumerateArray()
            .Select(n => new LinearWebhookRegistration(
                n.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                n.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                n.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                n.TryGetProperty("resourceTypes", out var rt) && rt.ValueKind == JsonValueKind.Array
                    ? rt.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                    : (IReadOnlyList<string>)[]))
            .Where(w => !string.IsNullOrEmpty(w.Id))
            .ToList();
    }

    /// <summary>Registers a workspace webhook for issue and comment events. Returns the webhook id.</summary>
    public async Task<string?> CreateWebhookAsync(
        LinearWorkSyncOptions options, string url, string secret, CancellationToken ct = default)
    {
        const string query = """
            mutation($input: WebhookCreateInput!) {
              webhookCreate(input: $input) { success webhook { id } }
            }
            """;
        var doc = await PostAsync(options, query,
            new Dictionary<string, object?>
            {
                ["input"] = new Dictionary<string, object?>
                {
                    ["url"] = url,
                    ["secret"] = secret,
                    ["resourceTypes"] = new[] { "Issue", "Comment" },
                },
            }, ct).ConfigureAwait(false);
        var payload = doc.GetProperty("data").GetProperty("webhookCreate");
        if (!payload.GetProperty("success").GetBoolean())
            throw new LinearApiException("Linear webhookCreate reported success=false.");
        return payload.TryGetProperty("webhook", out var hook)
            && hook.ValueKind == JsonValueKind.Object
            && hook.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            ? id.GetString() : null;
    }

    /// <summary>Removes a workspace webhook registration. Unknown ids report false, never throw.</summary>
    public async Task<bool> DeleteWebhookAsync(
        LinearWorkSyncOptions options, string webhookId, CancellationToken ct = default)
    {
        const string query = """
            mutation($id: String!) {
              webhookDelete(id: $id) { success }
            }
            """;
        try
        {
            var doc = await PostAsync(options, query,
                new Dictionary<string, object?> { ["id"] = webhookId }, ct).ConfigureAwait(false);
            return doc.GetProperty("data").GetProperty("webhookDelete").GetProperty("success").GetBoolean();
        }
        catch (LinearApiException)
        {
            return false;
        }
    }

    private async Task<JsonElement> PostAsync(
        LinearWorkSyncOptions options,
        string query,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(options, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new { query, variables }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("CodeyBox-LinearWorkSync/1.0");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LinearApiException(
                $"Linear API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var first = errors.EnumerateArray().FirstOrDefault();
            var message = first.ValueKind != JsonValueKind.Undefined
                && first.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String
                ? msg.GetString() : "unknown GraphQL error";
            throw new LinearApiException($"Linear API error: {WorkSyncText.Truncate(message ?? "unknown GraphQL error", 500)}.");
        }
        return root;
    }

}
