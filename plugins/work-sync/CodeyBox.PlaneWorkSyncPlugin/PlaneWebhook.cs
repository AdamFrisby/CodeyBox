using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.PlaneWorkSyncPlugin;

/// <summary>
/// Plane webhook payload handling (v2 dot-notation events). Plane signs every
/// delivery with <c>X-Plane-Signature: &lt;hex HMAC-SHA256 over the raw body,
/// keyed by the webhook secret&gt;</c>. The hosting endpoint MUST verify the
/// signature over the raw bytes BEFORE calling <c>ParseVerifiedWebhookBody</c> —
/// this type only assigns meaning to already-authenticated bytes.
/// </summary>
public static class PlaneWebhook
{
    public const string SignatureHeader = "X-Plane-Signature";

    /// <summary>Maximum webhook body accepted (64 KB, enforced before buffering).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Verifies a Plane webhook signature with a constant-time comparison.
    /// Returns false for missing/empty signatures and malformed hex — never throws.
    /// </summary>
    public static bool VerifySignature(byte[] rawBody, string? signatureHeader, string secret)
    {
        if (rawBody is null || string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;
        var signature = signatureHeader.Trim();
        byte[] expected;
        try
        {
            expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
        }
        catch (Exception)
        {
            return false;
        }
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>
    /// Interprets an already-authenticated Plane webhook body (v2). Returns null
    /// when the payload carries no candidate work (unsupported event, issue
    /// deletion, unknown project, missing human key, or malformed JSON). Never
    /// throws for malformed input: unparseable bodies yield null so the endpoint
    /// can acknowledge without acting.
    /// </summary>
    /// <param name="verifiedBody">The authenticated raw body.</param>
    /// <param name="projectMap">Plane project id (UUID) to CodeyBox project id.</param>
    public static PlaneWebhookEvent? Parse(
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
            var evt = Prop(root, "event");
            if (string.IsNullOrWhiteSpace(evt))
                return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            if (evt.StartsWith("workitem.comment.", StringComparison.OrdinalIgnoreCase))
            {
                if (!evt.EndsWith(".created", StringComparison.OrdinalIgnoreCase))
                    return null;
                return CommentEvent(data, root, projectMap);
            }

            if (evt.StartsWith("workitem.", StringComparison.OrdinalIgnoreCase))
            {
                if (evt.EndsWith(".deleted", StringComparison.OrdinalIgnoreCase)
                    || evt.EndsWith(".archived", StringComparison.OrdinalIgnoreCase))
                    return new PlaneWebhookEvent(
                        Kind: PlaneWebhookEventKind.SignalRemovedHint,
                        Key: string.Empty,
                        IssueUuid: Prop(data, "id"),
                        ProjectId: ProjectOf(data),
                        CodeyBoxProjectId: null,
                        Title: string.Empty,
                        Body: string.Empty,
                        PresentSignals: [],
                        HasSignal: false,
                        ActorLogin: ActorOf(root, data),
                        CommentId: null);
                if (evt.EndsWith(".created", StringComparison.OrdinalIgnoreCase)
                    || evt.EndsWith(".updated", StringComparison.OrdinalIgnoreCase))
                    return IssueEvent(data, root, projectMap);
                return null;
            }

            return null;
        }
    }

    /// <summary>
    /// Extracts a question answer from an operator's reply comment. The reply
    /// protocol (documented in the plugin README): the comment starts with
    /// <c>{questionId}:</c> (e.g. <c>q-001: use forward-only</c>), matched by
    /// exact id against the work item's open questions. Returns null when the
    /// comment follows no known question. Bodies carrying the CodeyBox marker
    /// are ours and never parse as replies. An @mention of the Plane agent bot
    /// alone is content, never a trigger: only the question-id prefix answers.
    /// </summary>
    public static (string QuestionId, string Answer)? TryExtractQuestionReply(
        string commentBody, IReadOnlySet<string> openQuestionIds) =>
        WorkSyncQuestions.TryExtractReply(commentBody, openQuestionIds);

    /// <summary>Builds the tag embedded in a surfaced question comment.</summary>
    public static string QuestionTag(string questionId) => WorkSyncQuestions.TagFor(questionId);

    private static PlaneWebhookEvent? IssueEvent(
        JsonElement data, JsonElement root, IReadOnlyDictionary<string, string> projectMap)
    {
        var projectId = ProjectOf(data);
        if (string.IsNullOrWhiteSpace(projectId))
            return null;
        projectMap.TryGetValue(projectId, out var codeyBoxProject);
        if (string.IsNullOrWhiteSpace(codeyBoxProject))
            return null;

        var identifier = ProjectIdentifierOf(data);
        var sequence = SequenceOf(data);
        var key = PlaneIssue.BuildKey(identifier, sequence);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return new PlaneWebhookEvent(
            Kind: PlaneWebhookEventKind.Issue,
            Key: key,
            IssueUuid: Prop(data, "id"),
            ProjectId: projectId,
            CodeyBoxProjectId: codeyBoxProject,
            Title: FirstNonEmpty(Prop(data, "name"), Prop(data, "title")),
            Body: FirstNonEmpty(Prop(data, "description"), Prop(data, "description_text")),
            PresentSignals: PlaneSignals.FromIssueData(data),
            HasSignal: false,
            ActorLogin: ActorOf(root, data),
            CommentId: null);
    }

    private static PlaneWebhookEvent? CommentEvent(
        JsonElement data, JsonElement root, IReadOnlyDictionary<string, string> projectMap)
    {
        var body = FirstNonEmpty(
            Prop(data, "comment_text"),
            Prop(data, "comment_html"),
            Prop(data, "body"));
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // The comment payload references its issue by UUID or embedded object;
        // the human key is resolved by the caller when needed. Comments never
        // ingest directly (HasSignal false): only the question-id prefix in the
        // body can answer a surfaced question.
        string issueUuid = string.Empty;
        string projectId = ProjectOf(data);
        string key = string.Empty;
        if (data.TryGetProperty("issue", out var issue))
        {
            if (issue.ValueKind == JsonValueKind.String)
            {
                issueUuid = issue.GetString() ?? string.Empty;
            }
            else if (issue.ValueKind == JsonValueKind.Object)
            {
                issueUuid = Prop(issue, "id");
                if (string.IsNullOrWhiteSpace(projectId))
                    projectId = ProjectOf(issue);
                key = PlaneIssue.BuildKey(ProjectIdentifierOf(issue), SequenceOf(issue));
            }
        }
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = ProjectOf(data);
        if (string.IsNullOrWhiteSpace(key))
            key = FirstNonEmpty(Prop(data, "issue_key"), Prop(data, "issue_identifier"));

        string? codeyBoxProject = null;
        if (!string.IsNullOrWhiteSpace(projectId))
            projectMap.TryGetValue(projectId, out codeyBoxProject);

        return new PlaneWebhookEvent(
            Kind: PlaneWebhookEventKind.Comment,
            Key: key,
            IssueUuid: issueUuid,
            ProjectId: projectId,
            CodeyBoxProjectId: codeyBoxProject,
            Title: string.Empty,
            Body: body,
            PresentSignals: [],
            HasSignal: false,
            ActorLogin: ActorOf(root, data),
            CommentId: Prop(data, "id"));
    }

    private static string ProjectOf(JsonElement el)
    {
        foreach (var key in new[] { "project", "project_id" })
        {
            var value = Prop(el, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        if (el.TryGetProperty("project_detail", out var pd) && pd.ValueKind == JsonValueKind.Object)
        {
            var id = Prop(pd, "id");
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        return string.Empty;
    }

    private static string ProjectIdentifierOf(JsonElement el)
    {
        if (el.TryGetProperty("project_detail", out var pd) && pd.ValueKind == JsonValueKind.Object)
        {
            var identifier = Prop(pd, "identifier");
            if (!string.IsNullOrWhiteSpace(identifier))
                return identifier;
        }
        return FirstNonEmpty(Prop(el, "project_identifier"), Prop(el, "identifier_prefix"));
    }

    private static int SequenceOf(JsonElement el)
    {
        if (el.TryGetProperty("sequence_id", out var seq))
        {
            if (seq.ValueKind == JsonValueKind.Number && seq.TryGetInt32(out var n))
                return n;
        }
        return 0;
    }

    private static string? ActorOf(JsonElement root, JsonElement data)
    {
        foreach (var scope in new[] { root, data })
        {
            foreach (var key in new[] { "actor", "user", "updated_by_detail", "created_by_detail" })
            {
                if (scope.TryGetProperty(key, out var actor) && actor.ValueKind == JsonValueKind.Object)
                {
                    var login = Prop(actor, "email");
                    if (!string.IsNullOrWhiteSpace(login))
                        return login;
                    login = Prop(actor, "display_name");
                    if (!string.IsNullOrWhiteSpace(login))
                        return login;
                }
            }
        }
        return null;
    }

    private static string Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return string.Empty;
    }

    /// <summary>Reads the signal-bearing fields embedded in an issue payload.</summary>
    public static class PlaneSignals
    {
        public sealed record SignalDatum(string Kind, string Value);

        public static IReadOnlyList<SignalDatum> FromIssueData(JsonElement data)
        {
            var present = new List<SignalDatum>();
            if (data.TryGetProperty("label_details", out var labels) && labels.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in labels.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = Prop(e, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                        present.Add(new SignalDatum("Label", name));
                }
            }
            if (data.TryGetProperty("assignee_details", out var assignees) && assignees.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in assignees.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var key in new[] { "id", "email", "display_name" })
                    {
                        var value = Prop(e, key);
                        if (!string.IsNullOrWhiteSpace(value))
                            present.Add(new SignalDatum("Assignee", value));
                    }
                }
            }
            if (data.TryGetProperty("state_detail", out var state) && state.ValueKind == JsonValueKind.Object)
            {
                var name = Prop(state, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    present.Add(new SignalDatum("Status", name));
                var group = Prop(state, "group");
                if (!string.IsNullOrWhiteSpace(group))
                    present.Add(new SignalDatum("Status", group));
            }
            else
            {
                var name = FirstNonEmpty(Prop(data, "state_name"), Prop(data, "state"));
                if (!string.IsNullOrWhiteSpace(name))
                    present.Add(new SignalDatum("Status", name));
            }
            return present;
        }
    }
}

/// <summary>Kinds of Plane webhook deliveries the plugin acts on.</summary>
public enum PlaneWebhookEventKind
{
    /// <summary>Work item created or updated — a possible ingestion candidate.</summary>
    Issue,
    /// <summary>Comment created — a possible question reply (or loop-guard skip).</summary>
    Comment,
    /// <summary>
    /// Work item deleted/archived — a hint that the ingestion signal is gone. The host
    /// routes this to signal-removal handling for the tracked item.
    /// </summary>
    SignalRemovedHint,
}

/// <summary>Parsed Plane webhook delivery, before ingestion mapping.</summary>
public sealed record PlaneWebhookEvent(
    PlaneWebhookEventKind Kind,
    string Key,
    string IssueUuid,
    string ProjectId,
    string? CodeyBoxProjectId,
    string Title,
    string Body,
    IReadOnlyList<PlaneWebhook.PlaneSignals.SignalDatum> PresentSignals,
    bool HasSignal,
    string? ActorLogin,
    string? CommentId);
