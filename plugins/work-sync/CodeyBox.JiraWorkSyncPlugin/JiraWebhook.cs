using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.JiraWorkSyncPlugin;

/// <summary>
/// Jira webhook payload handling. Unlike Linear and Plane, Jira Cloud
/// webhooks carry <strong>no signature</strong>: delivery authentication is a
/// shared-secret token the operator embeds in the delivery URL query string
/// (e.g. <c>https://host/webhooks/jira?token=&lt;unguessable&gt;</c>). The
/// hosting endpoint MUST verify that token BEFORE calling
/// <c>ParseVerifiedWebhookBody</c> — this type only assigns meaning to
/// already-authenticated bytes.
/// </summary>
public static class JiraWebhook
{
    /// <summary>Maximum webhook body accepted (64 KB, enforced before buffering).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Query parameter carrying the shared-secret delivery token.</summary>
    public const string TokenQueryParameter = "token";

    /// <summary>Tag embedded in surfaced question comments so replies can be attributed.</summary>
    public const string QuestionTagPrefix = "<!-- codeybox-question:";

    /// <summary>
    /// Verifies a Jira webhook delivery by comparing the token presented in
    /// the request query string against the configured secret with a
    /// constant-time comparison. Returns false for missing/empty tokens —
    /// never throws.
    /// </summary>
    /// <param name="requestQuery">The raw request query string (with or without a leading <c>?</c>).</param>
    /// <param name="secret">The shared secret from the credential chain.</param>
    public static bool VerifyDelivery(string? requestQuery, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(requestQuery))
            return false;
        var presented = ExtractToken(requestQuery);
        if (string.IsNullOrEmpty(presented))
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes(presented));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Interprets an already-authenticated Jira webhook body. Returns null
    /// when the payload carries no candidate work (unsupported event, issue
    /// deletion, unknown project, missing issue key, or malformed JSON).
    /// Never throws for malformed input: unparseable bodies yield null so the
    /// endpoint can acknowledge without acting.
    /// </summary>
    /// <param name="verifiedBody">The authenticated raw body.</param>
    /// <param name="projectMap">Jira project key to CodeyBox project id.</param>
    public static JiraWebhookEvent? Parse(
        string verifiedBody, IReadOnlyDictionary<string, string> projectMap)
    {
        if (string.IsNullOrEmpty(verifiedBody) || verifiedBody.Length > MaxBodyBytes * 4)
            return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(verifiedBody);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var evt = Prop(root, "webhookEvent");
            if (string.IsNullOrWhiteSpace(evt))
                return null;

            if (string.Equals(evt, "jira:issue_deleted", StringComparison.OrdinalIgnoreCase))
            {
                var deletedKey = IssueKeyOf(root);
                return new JiraWebhookEvent(
                    Kind: JiraWebhookEventKind.SignalRemovedHint,
                    IssueKey: deletedKey,
                    ProjectKey: string.Empty,
                    ProjectId: null,
                    Title: string.Empty,
                    Body: string.Empty,
                    PresentSignals: [],
                    HasSignal: false,
                    ActorLogin: ActorOf(root),
                    CommentId: null);
            }

            if (evt.StartsWith("comment_", StringComparison.OrdinalIgnoreCase))
            {
                if (!evt.EndsWith("_created", StringComparison.OrdinalIgnoreCase)
                    && !evt.EndsWith("_updated", StringComparison.OrdinalIgnoreCase))
                    return null;
                return CommentEvent(root, projectMap);
            }

            if (evt.StartsWith("jira:issue_", StringComparison.OrdinalIgnoreCase))
                return IssueEvent(root, projectMap);

            return null;
        }
    }

    /// <summary>
    /// Extracts a question answer from an operator's reply comment. The reply
    /// protocol (documented in the plugin README): the comment starts with
    /// <c>{questionId}:</c> (e.g. <c>q-001: use forward-only</c>), matched by
    /// exact id against the work item's open questions. Returns null when the
    /// comment follows no known question. Bodies carrying the CodeyBox marker
    /// are ours and never parse as replies.
    /// </summary>
    public static (string QuestionId, string Answer)? TryExtractQuestionReply(
        string commentBody, IReadOnlySet<string> openQuestionIds)
    {
        if (string.IsNullOrWhiteSpace(commentBody) || openQuestionIds.Count == 0)
            return null;
        if (commentBody.Contains("codeybox-work-item:", StringComparison.Ordinal))
            return null;
        var trimmed = commentBody.Trim();
        foreach (var id in openQuestionIds)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (trimmed.StartsWith(id + ":", StringComparison.OrdinalIgnoreCase))
            {
                var answer = trimmed[(id.Length + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(answer))
                    return null;
                return (id, answer.Length > 4000 ? answer[..4000] : answer);
            }
        }
        return null;
    }

    /// <summary>Builds the tag embedded in a surfaced question comment.</summary>
    public static string QuestionTag(string questionId) => $"{QuestionTagPrefix}{questionId} -->";

    private static string? ExtractToken(string requestQuery)
    {
        var query = requestQuery.Trim();
        if (query.StartsWith('?'))
            query = query[1..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
                continue;
            var name = Uri.UnescapeDataString(pair[..equals].Trim());
            if (!string.Equals(name, TokenQueryParameter, StringComparison.Ordinal))
                continue;
            return Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return null;
    }

    private static JiraWebhookEvent? IssueEvent(
        JsonElement root, IReadOnlyDictionary<string, string> projectMap)
    {
        if (!root.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object)
            return null;
        var key = Str(issue, "key");
        if (string.IsNullOrWhiteSpace(key))
            return null;
        issue.TryGetProperty("fields", out var fields);

        var projectKey = fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty("project", out var project)
            && project.ValueKind == JsonValueKind.Object
                ? Str(project, "key") : string.Empty;
        projectMap.TryGetValue(projectKey, out var codeyBoxProject);
        if (string.IsNullOrWhiteSpace(codeyBoxProject))
            return null;

        return new JiraWebhookEvent(
            Kind: JiraWebhookEventKind.Issue,
            IssueKey: key,
            ProjectKey: projectKey,
            ProjectId: codeyBoxProject,
            Title: fields.ValueKind == JsonValueKind.Object ? Str(fields, "summary") : string.Empty,
            Body: fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("description", out var desc)
                ? AdfText.FromValue(desc) : string.Empty,
            PresentSignals: JiraSignals.FromFields(fields),
            HasSignal: false,
            ActorLogin: ActorOf(root),
            CommentId: null);
    }

    private static JiraWebhookEvent? CommentEvent(
        JsonElement root, IReadOnlyDictionary<string, string> projectMap)
    {
        if (!root.TryGetProperty("comment", out var comment) || comment.ValueKind != JsonValueKind.Object)
            return null;
        var body = comment.TryGetProperty("body", out var bodyEl)
            ? AdfText.FromValue(bodyEl) : string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // Comments never ingest directly: only a question-id reply prefix can
        // answer a surfaced question, and content alone never triggers work.
        var key = IssueKeyOf(root);
        string? codeyBoxProject = null;
        if (root.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Object
            && issue.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object)
        {
            var projectKey = Str(project, "key");
            if (!string.IsNullOrWhiteSpace(projectKey))
                projectMap.TryGetValue(projectKey, out codeyBoxProject);
        }

        var author = comment.TryGetProperty("author", out var authorEl)
            && authorEl.ValueKind == JsonValueKind.Object ? authorEl : (JsonElement?)null;
        return new JiraWebhookEvent(
            Kind: JiraWebhookEventKind.Comment,
            IssueKey: key,
            ProjectKey: string.Empty,
            ProjectId: codeyBoxProject,
            Title: string.Empty,
            Body: body,
            PresentSignals: [],
            HasSignal: false,
            ActorLogin: UserLogin(author),
            CommentId: Str(comment, "id"));
    }

    private static string IssueKeyOf(JsonElement root) =>
        root.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Object
            ? Str(issue, "key")
            : Str(root, "issueKey");

    private static string? ActorOf(JsonElement root)
    {
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var login = UserLogin(user);
            if (!string.IsNullOrWhiteSpace(login))
                return login;
        }
        return null;
    }

    private static string? UserLogin(JsonElement? user)
    {
        if (user is null || user.Value.ValueKind != JsonValueKind.Object)
            return null;
        var el = user.Value;
        foreach (var key in new[] { "emailAddress", "displayName", "accountId" })
        {
            var value = Str(el, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Str(JsonElement el, string name) => Prop(el, name);

    /// <summary>Reads the signal-bearing fields embedded in an issue payload.</summary>
    public static class JiraSignals
    {
        public sealed record SignalDatum(string Kind, string Value);

        public static IReadOnlyList<SignalDatum> FromFields(JsonElement fields)
        {
            var present = new List<SignalDatum>();
            if (fields.ValueKind != JsonValueKind.Object)
                return present;
            if (fields.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in labels.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String)
                        continue;
                    var name = e.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        present.Add(new SignalDatum("Label", name));
                }
            }
            if (fields.TryGetProperty("assignee", out var assignee) && assignee.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "accountId", "emailAddress", "displayName" })
                {
                    var value = Str(assignee, key);
                    if (!string.IsNullOrWhiteSpace(value))
                        present.Add(new SignalDatum("Assignee", value));
                }
            }
            if (fields.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
            {
                var name = Str(status, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    present.Add(new SignalDatum("Status", name));
            }
            return present;
        }
    }
}

/// <summary>Kinds of Jira webhook deliveries the plugin acts on.</summary>
public enum JiraWebhookEventKind
{
    /// <summary>Issue created or updated — a possible ingestion candidate.</summary>
    Issue,
    /// <summary>Comment created or updated — a possible question reply (or loop-guard skip).</summary>
    Comment,
    /// <summary>
    /// Issue deleted — a hint that the ingestion signal is gone. The host
    /// routes this to signal-removal handling for the tracked item.
    /// </summary>
    SignalRemovedHint,
}

/// <summary>Parsed Jira webhook delivery, before ingestion mapping.</summary>
public sealed record JiraWebhookEvent(
    JiraWebhookEventKind Kind,
    string IssueKey,
    string ProjectKey,
    string? ProjectId,
    string Title,
    string Body,
    IReadOnlyList<JiraWebhook.JiraSignals.SignalDatum> PresentSignals,
    bool HasSignal,
    string? ActorLogin,
    string? CommentId);
