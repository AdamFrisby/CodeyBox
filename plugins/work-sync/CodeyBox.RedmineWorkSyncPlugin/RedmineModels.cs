using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.RedmineWorkSyncPlugin;

/// <summary>
/// Redmine issue shape used by the work-sync plugin, per the documented
/// <c>/issues.json</c> resource: numeric id, subject, description, project
/// identifier, status name, assignee name, and scalar custom-field values.
/// Unknown fields are ignored so additive Redmine schema changes do not
/// break ingestion.
/// <para>The ingestion key is the issue number as a decimal string (e.g.
/// <c>"123"</c>): immutable across renames and accepted everywhere the REST
/// API takes an issue id. The provider namespace qualifies it, so the stored
/// external id is stable and collision-free per plugin instance.</para>
/// <para>Redmine has no native labels and no immutable user login on issues
/// (list payloads carry only display names): status names, assignee display
/// names, and every scalar custom-field value are surfaced as signals, and
/// the operator's exact-match signal selects among them.</para>
/// </summary>
public sealed record RedmineIssue
{
    /// <summary>Redmine issue number. The ingestion key (as a decimal string).</summary>
    public required int Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Redmine project identifier (URL-safe key, e.g. <c>my-app</c>), for the operator's project map.</summary>
    public string ProjectIdentifier { get; init; } = string.Empty;

    /// <summary>Current issue status name (the status signal).</summary>
    public string StatusName { get; init; } = string.Empty;

    /// <summary>Display name of the assignee, when assigned (the assignee signal).</summary>
    public string? AssigneeName { get; init; }

    /// <summary>Scalar custom-field values (each a label-kind signal).</summary>
    public IReadOnlyList<string> CustomFieldValues { get; init; } = [];

    /// <summary>Display name of the issue author, when reported (best-effort loop-guard attribution).</summary>
    public string? AuthorName { get; init; }

    /// <summary>External id for this issue: the bare number as a decimal string.</summary>
    public string ExternalId => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses one issue object from a list/detail payload. Unknown fields
    /// are ignored. Returns null when the node carries no positive numeric id.
    /// </summary>
    public static RedmineIssue? FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;
        var id = idEl.TryGetInt32(out var parsed) ? parsed : 0;
        if (id <= 0)
            return null;

        var projectIdentifier = string.Empty;
        if (node.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object)
            projectIdentifier = Str(project, "identifier");

        var statusName = string.Empty;
        if (node.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
            statusName = Str(status, "name");

        string? assigneeName = null;
        if (node.TryGetProperty("assigned_to", out var assignee) && assignee.ValueKind == JsonValueKind.Object)
        {
            var name = Str(assignee, "name");
            if (!string.IsNullOrWhiteSpace(name))
                assigneeName = name;
        }

        string? authorName = null;
        if (node.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
        {
            var name = Str(author, "name");
            if (!string.IsNullOrWhiteSpace(name))
                authorName = name;
        }

        return new RedmineIssue
        {
            Id = id,
            Title = Str(node, "subject"),
            Description = Str(node, "description"),
            ProjectIdentifier = projectIdentifier,
            StatusName = statusName,
            AssigneeName = assigneeName,
            CustomFieldValues = CustomFieldValuesOf(Get(node, "custom_fields")),
            AuthorName = authorName,
        };
    }

    private static IReadOnlyList<string> CustomFieldValuesOf(JsonElement fields)
    {
        var values = new List<string>();
        if (fields.ValueKind != JsonValueKind.Array)
            return values;
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                continue;
            if (!field.TryGetProperty("value", out var value))
                continue;
            CollectScalar(value, values);
        }
        return values;
    }

    private static void CollectScalar(JsonElement el, List<string> found)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var text = el.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    found.Add(text);
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                found.Add(el.ToString());
                return;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectScalar(item, found);
                return;
            default:
                return;
        }
    }

    internal static JsonElement Get(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    internal static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Journal (note) entries on a Redmine issue, read with
/// <c>GET /issues/{id}.json?include=journals</c>. Used for exactly two
/// purposes: reconciling an uncertain note write before any repetition, and
/// observing operator question replies on the poll path (Redmine offers no
/// webhooks, so replies arrive as journal notes).
/// </summary>
public sealed record RedmineJournalEntry
{
    public int Id { get; init; }

    public string Notes { get; init; } = string.Empty;

    public string? AuthorName { get; init; }

    /// <summary>
    /// Parses the <c>journals</c> array of an issue-detail payload. Entries
    /// with empty notes (pure status changes) are kept with empty notes so
    /// callers can distinguish "no journals" from "no notes".
    /// </summary>
    public static IReadOnlyList<RedmineJournalEntry> ParseList(JsonElement issueNode)
    {
        var entries = new List<RedmineJournalEntry>();
        var journals = RedmineIssue.Get(issueNode, "journals");
        if (journals.ValueKind != JsonValueKind.Array)
            return entries;
        foreach (var journal in journals.EnumerateArray())
        {
            if (journal.ValueKind != JsonValueKind.Object)
                continue;
            var id = journal.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                && idEl.TryGetInt32(out var parsed) ? parsed : 0;
            string? authorName = null;
            if (journal.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                var name = RedmineIssue.Str(user, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    authorName = name;
            }
            entries.Add(new RedmineJournalEntry
            {
                Id = id,
                Notes = RedmineIssue.Str(journal, "notes"),
                AuthorName = authorName,
            });
        }
        return entries;
    }
}

/// <summary>
/// Question-reply protocol over Redmine journal notes. Surfaced questions
/// end with <see cref="WorkSyncQuestions.TagFor"/> output; an operator
/// answers by posting a note that starts with <c>{questionId}:</c>. Notes
/// carrying the CodeyBox work-item marker are ours and never parse as
/// replies.
/// </summary>
public static class RedmineNotes
{
    /// <summary>Builds the tag embedded in a surfaced question note.</summary>
    public static string QuestionTag(string questionId) => WorkSyncQuestions.TagFor(questionId);

    /// <summary>
    /// Extracts a question answer from an operator's journal note, matched
    /// by exact id against <paramref name="openQuestionIds"/>. Returns null
    /// when the note follows no known question.
    /// </summary>
    public static (string QuestionId, string Answer)? TryExtractQuestionReply(
        string notes, IReadOnlySet<string> openQuestionIds) =>
        WorkSyncQuestions.TryExtractReply(notes, openQuestionIds);
}
