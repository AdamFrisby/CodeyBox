using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.ShortcutWorkSyncPlugin;

/// <summary>
/// Shortcut outgoing-webhook payload handling. Shortcut fires entity-level
/// deliveries (<c>story</c>, <c>epic</c>, <c>story-comment</c>,
/// <c>epic-comment</c> with <c>create</c>/<c>update</c>/<c>delete</c> actions),
/// which makes CodeyBox-authored comments distinguishable on the way back in:
/// our own comments carry the loop-guard marker and the service login, and
/// both are checked before any ingestion decision.
/// <para>Shortcut signs nothing itself. When a signing proxy fronts CodeyBox,
/// it adds <c>&lt;hex HMAC-SHA256 over the raw body&gt;</c> under the configured
/// header; the hosting endpoint MUST verify that signature over the raw bytes
/// BEFORE calling <c>ParseVerifiedWebhookBody</c> — this type only assigns
/// meaning to already-authenticated bytes.</para>
/// </summary>
public static class ShortcutWebhook
{
    /// <summary>Maximum webhook body accepted (64 KB, enforced before buffering).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Verifies a webhook signature with a constant-time comparison: hex
    /// HMAC-SHA256 over the raw body keyed by the shared secret. Returns false
    /// for missing/empty signatures and malformed hex — never throws.
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
    /// Interprets an already-authenticated Shortcut webhook body. Returns null
    /// when the payload carries no candidate work (unsupported entity type,
    /// deletion, or malformed JSON). Never throws for malformed input:
    /// unparseable bodies yield null so the endpoint can acknowledge without acting.
    /// <para>Accepted shape (see README for a full example): an object with
    /// <c>action</c> (<c>create</c>/<c>update</c>/<c>delete</c>),
    /// <c>entity_type</c> (<c>story</c>, <c>epic</c>, <c>story-comment</c>,
    /// <c>epic-comment</c>; <c>resource_type</c>/<c>type</c> are accepted as
    /// aliases), and the entity payload under the matching key
    /// (<c>story</c>, <c>epic</c>, or <c>comment</c>; <c>entity</c>/<c>data</c>
    /// are accepted as aliases).</para>
    /// </summary>
    /// <param name="verifiedBody">The authenticated raw body.</param>
    /// <param name="projectMap">Shortcut project id (numeric string) to CodeyBox project id.</param>
    /// <param name="defaultProjectId">Project for payloads that name no mapped project; null skips them.</param>
    public static ShortcutWebhookEvent? Parse(
        string verifiedBody,
        IReadOnlyDictionary<string, string> projectMap,
        string? defaultProjectId = null)
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
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var action = Prop(root, "action");
            var entityType = EntityTypeOf(root);
            if (string.IsNullOrWhiteSpace(entityType))
                return null;

            if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase))
                return null;

            if (IsCommentType(entityType))
            {
                if (!string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
                    return null;
                return CommentEvent(root, entityType, projectMap, defaultProjectId);
            }

            if (IsStoryType(entityType))
            {
                var story = EntityOf(root, "story");
                if (story is null)
                    return null;
                return StoryEvent(story.Value, projectMap, defaultProjectId, root);
            }

            if (IsEpicType(entityType))
            {
                var epic = EntityOf(root, "epic");
                if (epic is null)
                    return null;
                return EpicEvent(epic.Value, projectMap, defaultProjectId, root);
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
        string commentBody, IReadOnlySet<string> openQuestionIds) =>
        WorkSyncQuestions.TryExtractReply(commentBody, openQuestionIds);

    /// <summary>Builds the tag embedded in a surfaced question comment.</summary>
    public static string QuestionTag(string questionId) => WorkSyncQuestions.TagFor(questionId);

    private static ShortcutWebhookEvent? StoryEvent(
        JsonElement story, IReadOnlyDictionary<string, string> projectMap, string? defaultProjectId, JsonElement root)
    {
        var id = story.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
            && idProp.TryGetInt64(out var n) ? n : 0;
        if (id == 0)
            return null;
        var projectId = ResolveStoryProject(story, projectMap, defaultProjectId);
        if (string.IsNullOrWhiteSpace(projectId))
            return null;
        return new ShortcutWebhookEvent(
            Kind: ShortcutWebhookEventKind.Story,
            ExternalId: ShortcutWorkSyncPlugin.StoryKey(id),
            ProjectId: projectId,
            Title: Prop(story, "name"),
            Body: Prop(story, "description"),
            PresentSignals: ShortcutWorkSyncPlugin.SignalsOf(
                LabelNames(story), OwnerRefs(story), Prop(story, "workflow_state_name")),
            HasSignal: false,
            ActorLogin: ActorOf(root),
            CommentId: null);
    }

    private static ShortcutWebhookEvent? EpicEvent(
        JsonElement epic, IReadOnlyDictionary<string, string> projectMap, string? defaultProjectId, JsonElement root)
    {
        var id = epic.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
            && idProp.TryGetInt64(out var n) ? n : 0;
        if (id == 0)
            return null;
        var projectId = ResolveEpicProject(epic, projectMap, defaultProjectId);
        if (string.IsNullOrWhiteSpace(projectId))
            return null;
        return new ShortcutWebhookEvent(
            Kind: ShortcutWebhookEventKind.Epic,
            ExternalId: ShortcutWorkSyncPlugin.EpicKey(id),
            ProjectId: projectId,
            Title: Prop(epic, "name"),
            Body: Prop(epic, "description"),
            PresentSignals: ShortcutWorkSyncPlugin.SignalsOf(
                LabelNames(epic), OwnerRefs(epic), string.Empty),
            HasSignal: false,
            ActorLogin: ActorOf(root),
            CommentId: null);
    }

    private static ShortcutWebhookEvent? CommentEvent(
        JsonElement root, string entityType, IReadOnlyDictionary<string, string> projectMap, string? defaultProjectId)
    {
        var comment = EntityOf(root, "comment");
        if (comment is null)
            return null;
        var body = Prop(comment.Value, "text");
        if (string.IsNullOrWhiteSpace(body))
            body = Prop(comment.Value, "body");
        var story = EntityOf(root, "story");
        var epic = EntityOf(root, "epic");
        string externalId = string.Empty;
        string? projectId = null;
        string title = string.Empty;
        if (story is not null
            && story.Value.TryGetProperty("id", out var sid)
            && sid.ValueKind == JsonValueKind.Number
            && sid.TryGetInt64(out var storyId)
            && storyId != 0)
        {
            externalId = ShortcutWorkSyncPlugin.StoryKey(storyId);
            projectId = ResolveStoryProject(story.Value, projectMap, defaultProjectId);
            title = Prop(story.Value, "name");
        }
        else if (epic is not null
            && epic.Value.TryGetProperty("id", out var eid)
            && eid.ValueKind == JsonValueKind.Number
            && eid.TryGetInt64(out var epicId)
            && epicId != 0)
        {
            externalId = ShortcutWorkSyncPlugin.EpicKey(epicId);
            projectId = ResolveEpicProject(epic.Value, projectMap, defaultProjectId);
            title = Prop(epic.Value, "name");
        }
        if (string.IsNullOrWhiteSpace(externalId))
            return null;
        _ = entityType;
        return new ShortcutWebhookEvent(
            Kind: ShortcutWebhookEventKind.Comment,
            ExternalId: externalId,
            ProjectId: projectId,
            Title: title,
            Body: body,
            PresentSignals: [],
            HasSignal: false,
            ActorLogin: ActorOf(root),
            CommentId: CommentIdOf(comment.Value));
    }

    private static string? ResolveStoryProject(
        JsonElement story, IReadOnlyDictionary<string, string> projectMap, string? defaultProjectId)
    {
        string? raw = null;
        if (story.TryGetProperty("project_id", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt64(out var n))
            raw = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (raw is not null && projectMap.TryGetValue(raw, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped;
        return string.IsNullOrWhiteSpace(defaultProjectId) ? null : defaultProjectId;
    }

    private static string? ResolveEpicProject(
        JsonElement epic, IReadOnlyDictionary<string, string> projectMap, string? defaultProjectId)
    {
        if (epic.TryGetProperty("project_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var n))
                    continue;
                var raw = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (projectMap.TryGetValue(raw, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                    return mapped;
            }
        }
        if (epic.TryGetProperty("project_id", out var single) && single.ValueKind == JsonValueKind.Number
            && single.TryGetInt64(out var one))
        {
            var raw = one.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (projectMap.TryGetValue(raw, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                return mapped;
        }
        return string.IsNullOrWhiteSpace(defaultProjectId) ? null : defaultProjectId;
    }

    private static string EntityTypeOf(JsonElement root)
    {
        foreach (var key in new[] { "entity_type", "resource_type", "type" })
        {
            var value = Prop(root, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim().ToLowerInvariant();
        }
        return string.Empty;
    }

    private static bool IsStoryType(string entityType) =>
        string.Equals(entityType, "story", StringComparison.Ordinal);

    private static bool IsEpicType(string entityType) =>
        string.Equals(entityType, "epic", StringComparison.Ordinal);

    private static bool IsCommentType(string entityType) =>
        entityType is "story-comment" or "story_comment" or "epic-comment" or "epic_comment" or "comment";

    private static JsonElement? EntityOf(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var direct) && direct.ValueKind == JsonValueKind.Object)
            return direct;
        foreach (var alias in new[] { "entity", "data" })
        {
            if (root.TryGetProperty(alias, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                if (nested.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Object)
                    return inner;
                if (string.Equals(key, "comment", StringComparison.Ordinal)
                    && nested.TryGetProperty("text", out _))
                    return nested;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> LabelNames(JsonElement entity)
    {
        if (!entity.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var e in labels.EnumerateArray())
        {
            string? name = e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Object => Prop(e, "name"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    private static IReadOnlyList<string> OwnerRefs(JsonElement entity)
    {
        if (!entity.TryGetProperty("owner_ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string? ActorOf(JsonElement root)
    {
        foreach (var key in new[] { "actor", "author", "member", "user" })
        {
            if (root.TryGetProperty(key, out var actor) && actor.ValueKind == JsonValueKind.Object)
            {
                foreach (var id in new[] { "mention_name", "email", "email_address", "name" })
                {
                    var login = Prop(actor, id);
                    if (!string.IsNullOrWhiteSpace(login))
                        return login;
                }
            }
        }
        return null;
    }

    private static string? CommentIdOf(JsonElement comment)
    {
        if (comment.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n))
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        return null;
    }

    private static string Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}

/// <summary>Kinds of Shortcut webhook deliveries the plugin acts on.</summary>
public enum ShortcutWebhookEventKind
{
    /// <summary>Story created or updated — a possible ingestion candidate.</summary>
    Story,
    /// <summary>Epic created or updated — ingests only when <c>IngestEpics</c> is enabled.</summary>
    Epic,
    /// <summary>Comment created — a possible question reply (or loop-guard skip).</summary>
    Comment,
}

/// <summary>Parsed Shortcut webhook delivery, before ingestion mapping.</summary>
public sealed record ShortcutWebhookEvent(
    ShortcutWebhookEventKind Kind,
    string ExternalId,
    string? ProjectId,
    string Title,
    string Body,
    IReadOnlyList<ShortcutWebhookSignal> PresentSignals,
    bool HasSignal,
    string? ActorLogin,
    string? CommentId);

/// <summary>Signal-bearing datum read from a Shortcut webhook entity payload.</summary>
public sealed record ShortcutWebhookSignal(string Kind, string Value);
