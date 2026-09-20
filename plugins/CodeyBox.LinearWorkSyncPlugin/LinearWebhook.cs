using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.LinearWorkSyncPlugin;

/// <summary>
/// Linear webhook payload handling. Linear signs deliveries with
/// <c>Linear-Signature: &lt;hex HMAC-SHA256 over the raw body, keyed by the
/// webhook secret&gt;</c>. The hosting endpoint MUST verify the signature
/// over the raw bytes BEFORE calling <c>ParseVerifiedWebhookBody</c> — this
/// type only assigns meaning to already-authenticated bytes.
/// </summary>
public static class LinearWebhook
{
    public const string SignatureHeader = "Linear-Signature";

    /// <summary>Maximum webhook body accepted (64 KB, enforced before buffering).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Tag embedded in surfaced question comments so replies can be attributed.</summary>
    public const string QuestionTagPrefix = "<!-- codeybox-question:";

    /// <summary>
    /// Verifies a Linear webhook signature with a constant-time comparison.
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
    /// Interprets an already-authenticated Linear webhook body. Returns null
    /// when the payload carries no candidate work (unsupported resource type,
    /// issue removal, unknown team, or malformed JSON). Never throws for
    /// malformed input: unparseable bodies yield null so the endpoint can
    /// acknowledge without acting.
    /// </summary>
    /// <param name="verifiedBody">The authenticated raw body.</param>
    /// <param name="teamProjectMap">Linear team key to CodeyBox project id.</param>
    public static LinearWebhookEvent? Parse(
        string verifiedBody, IReadOnlyDictionary<string, string> teamProjectMap)
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
            var type = Prop(root, "type");
            var action = Prop(root, "action");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            if (string.Equals(type, "Issue", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
                    return new LinearWebhookEvent(
                        Kind: LinearWebhookEventKind.SignalRemovedHint,
                        Identifier: Prop(data, "identifier"),
                        IssueId: Prop(data, "id"),
                        TeamKey: TeamOf(data),
                        ProjectId: null,
                        Title: Prop(data, "title"),
                        Body: string.Empty,
                        PresentSignals: [],
                        HasSignal: false,
                        ActorLogin: ActorOf(root, data),
                        CommentId: null,
                        CreatedAt: null);
                return IssueEvent(data, root, teamProjectMap);
            }

            if (string.Equals(type, "Comment", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
                    return null;
                return CommentEvent(data, root, teamProjectMap);
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

    private static LinearWebhookEvent? IssueEvent(
        JsonElement data, JsonElement root, IReadOnlyDictionary<string, string> teamProjectMap)
    {
        var identifier = Prop(data, "identifier");
        if (string.IsNullOrWhiteSpace(identifier))
            return null;
        var teamKey = TeamOf(data);
        teamProjectMap.TryGetValue(teamKey, out var projectId);
        if (string.IsNullOrWhiteSpace(projectId))
            return null;

        var signals = LinearSignals.FromIssueData(data);
        return new LinearWebhookEvent(
            Kind: LinearWebhookEventKind.Issue,
            Identifier: identifier,
            IssueId: Prop(data, "id"),
            TeamKey: teamKey,
            ProjectId: projectId,
            Title: Prop(data, "title"),
            Body: Prop(data, "description"),
            PresentSignals: signals,
            HasSignal: false,
            ActorLogin: ActorOf(root, data),
            CommentId: null,
            CreatedAt: null);
    }

    private static LinearWebhookEvent? CommentEvent(
        JsonElement data, JsonElement root, IReadOnlyDictionary<string, string> teamProjectMap)
    {
        var body = Prop(data, "body");
        var issue = data.TryGetProperty("issue", out var i) && i.ValueKind == JsonValueKind.Object
            ? i : (JsonElement?)null;
        var identifier = issue.HasValue ? Prop(issue.Value, "identifier") : Prop(data, "issueIdentifier");
        if (string.IsNullOrWhiteSpace(identifier))
            return null;
        var teamKey = issue.HasValue ? TeamOf(issue.Value) : string.Empty;
        teamProjectMap.TryGetValue(teamKey, out var projectId);
        return new LinearWebhookEvent(
            Kind: LinearWebhookEventKind.Comment,
            Identifier: identifier,
            IssueId: issue.HasValue ? Prop(issue.Value, "id") : string.Empty,
            TeamKey: teamKey,
            ProjectId: string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            Title: issue.HasValue ? Prop(issue.Value, "title") : string.Empty,
            Body: body,
            PresentSignals: [],
            HasSignal: false,
            ActorLogin: ActorOf(root, data),
            CommentId: Prop(data, "id"),
            CreatedAt: Prop(data, "createdAt"));
    }

    private static string TeamOf(JsonElement issueData)
    {
        if (issueData.TryGetProperty("team", out var team) && team.ValueKind == JsonValueKind.Object)
            return Prop(team, "key");
        return Prop(issueData, "teamKey");
    }

    private static string? ActorOf(JsonElement root, JsonElement data)
    {
        foreach (var scope in new[] { root, data })
        {
            foreach (var key in new[] { "actor", "user", "creator" })
            {
                if (scope.TryGetProperty(key, out var actor) && actor.ValueKind == JsonValueKind.Object)
                {
                    var login = Prop(actor, "email");
                    if (!string.IsNullOrWhiteSpace(login))
                        return login;
                    login = Prop(actor, "name");
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

    /// <summary>Reads the signal-bearing fields embedded in an issue payload.</summary>
    public static class LinearSignals
    {
        public sealed record SignalDatum(string Kind, string Value);

        public static IReadOnlyList<SignalDatum> FromIssueData(JsonElement data)
        {
            var present = new List<SignalDatum>();
            if (data.TryGetProperty("labels", out var labels))
            {
                foreach (var name in LabelNames(labels))
                    present.Add(new SignalDatum("Label", name));
            }
            if (data.TryGetProperty("assignee", out var assignee) && assignee.ValueKind == JsonValueKind.Object)
            {
                var id = Prop(assignee, "id");
                var email = Prop(assignee, "email");
                var name = Prop(assignee, "name");
                if (!string.IsNullOrWhiteSpace(id))
                    present.Add(new SignalDatum("Assignee", id));
                if (!string.IsNullOrWhiteSpace(email))
                    present.Add(new SignalDatum("Assignee", email));
                if (!string.IsNullOrWhiteSpace(name))
                    present.Add(new SignalDatum("Assignee", name));
            }
            var state = data.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                ? Prop(st, "name") : Prop(data, "stateName");
            if (!string.IsNullOrWhiteSpace(state))
                present.Add(new SignalDatum("Status", state));
            return present;
        }

        public static IEnumerable<string> LabelNames(JsonElement labels)
        {
            if (labels.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in labels.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                    {
                        var s = e.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            yield return s;
                    }
                    else if (e.ValueKind == JsonValueKind.Object)
                    {
                        var n = Prop(e, "name");
                        if (!string.IsNullOrWhiteSpace(n))
                            yield return n;
                    }
                }
            }
            else if (labels.ValueKind == JsonValueKind.Object
                && labels.TryGetProperty("nodes", out var nodes)
                && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in nodes.EnumerateArray())
                {
                    var n = Prop(e, "name");
                    if (!string.IsNullOrWhiteSpace(n))
                        yield return n;
                }
            }
        }
    }
}

/// <summary>Kinds of Linear webhook deliveries the plugin acts on.</summary>
public enum LinearWebhookEventKind
{
    /// <summary>Issue created or updated — a possible ingestion candidate.</summary>
    Issue,
    /// <summary>Comment created — a possible question reply (or loop-guard skip).</summary>
    Comment,
    /// <summary>
    /// Issue removed — a hint that the ingestion signal is gone. The host
    /// routes this to signal-removal handling for the tracked item.
    /// </summary>
    SignalRemovedHint,
}

/// <summary>Parsed Linear webhook delivery, before ingestion mapping.</summary>
public sealed record LinearWebhookEvent(
    LinearWebhookEventKind Kind,
    string Identifier,
    string IssueId,
    string TeamKey,
    string? ProjectId,
    string Title,
    string Body,
    IReadOnlyList<LinearWebhook.LinearSignals.SignalDatum> PresentSignals,
    bool HasSignal,
    string? ActorLogin,
    string? CommentId,
    string? CreatedAt);
