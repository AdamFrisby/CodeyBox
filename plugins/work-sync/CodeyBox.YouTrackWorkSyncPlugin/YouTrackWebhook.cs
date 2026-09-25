using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.YouTrackWorkSyncPlugin;

/// <summary>
/// YouTrack Webhook Triggers app payload handling. The app authenticates
/// deliveries with a shared token carried in a configurable request header
/// (default <c>X-YouTrack-Token</c>) — no HMAC. The hosting endpoint MUST
/// verify that header against the configured secret via <see
/// cref="VerifyDelivery"/> BEFORE calling <c>Parse</c>: this type only
/// assigns meaning to already-authenticated bytes.
/// <para>Issue payloads carry no tag/custom-field values — only a
/// <c>changedFields</c> list on updates — so signal extraction reads what
/// the delivery actually reports (a field that just gained the signal value
/// is a positive observation) and polling remains the source of truth for
/// the full signal set.</para>
/// </summary>
public static class YouTrackWebhook
{
    /// <summary>Maximum webhook body accepted (64 KB, enforced before buffering).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Tag embedded in surfaced question comments so replies can be attributed.</summary>
    public const string QuestionTagPrefix = WorkSyncQuestions.TagPrefix;

    /// <summary>changedFields names treated as the tag list (label signal).</summary>
    private static readonly HashSet<string> TagFieldNames =
        new(StringComparer.OrdinalIgnoreCase) { "tag", "tags" };

    /// <summary>
    /// Verifies a webhook delivery by comparing the token presented in the
    /// configured header against the shared secret with a constant-time
    /// comparison. Returns false for missing/empty values — never throws.
    /// </summary>
    /// <param name="presentedToken">The value of the <see cref="YouTrackWorkSyncOptions.WebhookTokenHeader"/> header.</param>
    /// <param name="secret">The shared webhook token from the credential chain.</param>
    public static bool VerifyDelivery(string? presentedToken, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(presentedToken))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(presentedToken.Trim()));
    }

    /// <summary>
    /// Interprets an already-authenticated webhook body. Returns null when
    /// the payload carries no candidate work (unsupported event type,
    /// missing identity, unmapped project, or malformed JSON). Never throws
    /// for malformed input: unparseable bodies yield null so the endpoint can
    /// acknowledge without acting.
    /// </summary>
    /// <param name="verifiedBody">The authenticated raw body.</param>
    /// <param name="options">Current plugin options (project map, field names).</param>
    public static YouTrackWebhookEvent? Parse(
        string verifiedBody, YouTrackWorkSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The bound is in bytes: measure the UTF-8 encoding so a body of
        // multi-byte characters cannot slip four times the cap past the guard.
        if (string.IsNullOrEmpty(verifiedBody)
            || Encoding.UTF8.GetByteCount(verifiedBody) > MaxBodyBytes)
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
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var evt = Str(root, "event");
            if (string.IsNullOrWhiteSpace(evt))
                return null;

            if (string.Equals(evt, "issueDeleted", StringComparison.OrdinalIgnoreCase))
            {
                return new YouTrackWebhookEvent(
                    Kind: YouTrackWebhookEventKind.SignalRemovedHint,
                    IssueId: IssueIdReadable(root),
                    ProjectKey: ProjectKeyOf(root),
                    Title: Str(root, "summary"),
                    Body: string.Empty,
                    PresentSignals: [],
                    ActorLogin: UserLoginOf(root, "updatedBy"),
                    CommentId: null);
            }

            if (evt.StartsWith("comment", StringComparison.OrdinalIgnoreCase))
            {
                if (!evt.EndsWith("Added", StringComparison.OrdinalIgnoreCase)
                    && !evt.EndsWith("Updated", StringComparison.OrdinalIgnoreCase))
                    return null;
                return CommentEvent(root);
            }

            if (evt.StartsWith("issue", StringComparison.OrdinalIgnoreCase))
                return IssueEvent(root, options);

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
        string commentBody, IReadOnlySet<string> openQuestionIds) =>
        WorkSyncQuestions.TryExtractReply(commentBody, openQuestionIds);

    /// <summary>Builds the tag embedded in a surfaced question comment.</summary>
    public static string QuestionTag(string questionId) => WorkSyncQuestions.TagFor(questionId);

    private static YouTrackWebhookEvent? IssueEvent(
        JsonElement root, YouTrackWorkSyncOptions options)
    {
        var issueId = IssueIdReadable(root);
        if (string.IsNullOrWhiteSpace(issueId))
            return null;
        var projectKey = ProjectKeyOf(root);
        if (string.IsNullOrWhiteSpace(projectKey))
            return null;

        var signals = new List<YouTrackSignalDatum>();
        // Top-level fields (present when an emitter includes them).
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
            {
                var name = t.ValueKind == JsonValueKind.Object ? Str(t, "name") : StringOf(t);
                if (!string.IsNullOrWhiteSpace(name))
                    signals.Add(new YouTrackSignalDatum(WorkSignalKind.Label, name));
            }
        }
        if (root.TryGetProperty("customFields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in YouTrackSignals.Values(
                         YouTrackIssue.CustomFieldValue(fields, options.StateFieldName)))
                signals.Add(new YouTrackSignalDatum(WorkSignalKind.Status, s));
            foreach (var s in YouTrackSignals.UserValues(
                         YouTrackIssue.CustomFieldValue(fields, options.AssigneeFieldName)))
                signals.Add(new YouTrackSignalDatum(WorkSignalKind.Assignee, s));
        }

        // What the Webhook Triggers app actually reports: the changed fields.
        if (root.TryGetProperty("changedFields", out var changed) && changed.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in changed.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Str(entry, "name");
                if (!entry.TryGetProperty("value", out var value))
                    continue;
                if (string.Equals(name, options.StateFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in YouTrackSignals.Values(value))
                        signals.Add(new YouTrackSignalDatum(WorkSignalKind.Status, s));
                }
                else if (string.Equals(name, options.AssigneeFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in YouTrackSignals.UserValues(value))
                        signals.Add(new YouTrackSignalDatum(WorkSignalKind.Assignee, s));
                }
                else if (TagFieldNames.Contains(name))
                {
                    foreach (var s in YouTrackSignals.Values(value))
                        signals.Add(new YouTrackSignalDatum(WorkSignalKind.Label, s));
                }
            }
        }

        return new YouTrackWebhookEvent(
            Kind: YouTrackWebhookEventKind.Issue,
            IssueId: issueId,
            ProjectKey: projectKey,
            Title: Str(root, "summary"),
            Body: Str(root, "description"),
            PresentSignals: signals,
            ActorLogin: UserLoginOf(root, "updatedBy") ?? UserLoginOf(root, "reporter"),
            CommentId: null);
    }

    private static YouTrackWebhookEvent? CommentEvent(JsonElement root)
    {
        var issueId = IssueIdReadable(root);
        if (string.IsNullOrWhiteSpace(issueId))
            return null;
        if (!root.TryGetProperty("comments", out var comments)
            || comments.ValueKind != JsonValueKind.Array)
            return null;

        string? text = null, commentId = null;
        string? author = null;
        foreach (var c in comments.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
                continue;
            var t = Str(c, "text");
            if (string.IsNullOrWhiteSpace(t))
                continue;
            text = t;
            commentId = Str(c, "id");
            if (c.TryGetProperty("author", out var a))
                author = YouTrackIssue.UserLogin(a);
        }
        if (text is null)
            return null;

        // Comments never ingest directly: only a question-id reply prefix can
        // answer a surfaced question, and content alone never triggers work.
        return new YouTrackWebhookEvent(
            Kind: YouTrackWebhookEventKind.Comment,
            IssueId: issueId,
            ProjectKey: ProjectKeyOf(root),
            Title: string.Empty,
            Body: text,
            PresentSignals: [],
            ActorLogin: author,
            CommentId: commentId);
    }

    /// <summary>
    /// The human-readable issue id: the payload's <c>idReadable</c> when
    /// present, else composed from the project short name and
    /// <c>numberInProject</c> (the documented Webhook Triggers shape carries
    /// the pair, not the composed id).
    /// </summary>
    private static string IssueIdReadable(JsonElement root)
    {
        var readable = Str(root, "idReadable");
        if (!string.IsNullOrWhiteSpace(readable))
            return readable;
        var project = ProjectKeyOf(root);
        if (string.IsNullOrWhiteSpace(project))
            return string.Empty;
        if (root.TryGetProperty("numberInProject", out var n))
        {
            if (n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var num) && num > 0)
                return $"{project}-{num}";
            if (n.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(n.GetString()))
                return $"{project}-{n.GetString()!.Trim()}";
        }
        return string.Empty;
    }

    private static string ProjectKeyOf(JsonElement root) =>
        root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
            ? Str(project, "shortName") : string.Empty;

    private static string? UserLoginOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var user) && user.ValueKind == JsonValueKind.Object
            ? YouTrackIssue.UserLogin(user) : null;

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string StringOf(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
}

/// <summary>Kinds of YouTrack webhook deliveries the plugin acts on.</summary>
public enum YouTrackWebhookEventKind
{
    /// <summary>Issue created or updated — a possible ingestion candidate.</summary>
    Issue,
    /// <summary>Comment added or updated — a possible question reply (or loop-guard skip).</summary>
    Comment,
    /// <summary>
    /// Issue deleted — the signal is gone. Parsed for completeness; the
    /// <c>IWorkSource</c> contract has no removal channel, so the plugin's
    /// <c>ParseVerifiedWebhookBody</c> drops this kind. Informational only —
    /// no signal-removal handling is reachable from here today.
    /// </summary>
    SignalRemovedHint,
}

/// <summary>One observed signal value from a webhook payload.</summary>
public sealed record YouTrackSignalDatum(WorkSignalKind Kind, string Value);

/// <summary>Parsed YouTrack webhook delivery, before ingestion mapping.</summary>
public sealed record YouTrackWebhookEvent(
    YouTrackWebhookEventKind Kind,
    string IssueId,
    string ProjectKey,
    string Title,
    string Body,
    IReadOnlyList<YouTrackSignalDatum> PresentSignals,
    string? ActorLogin,
    string? CommentId);
