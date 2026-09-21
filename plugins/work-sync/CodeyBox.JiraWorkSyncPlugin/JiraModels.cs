using System.Text;
using System.Text.Json;

namespace CodeyBox.JiraWorkSyncPlugin;

/// <summary>
/// Jira issue shape used by the work-sync plugin. Jira Cloud v3 carries rich
/// text as Atlassian Document Format (ADF) objects; parsing extracts plain
/// text and ignores unknown fields so additive Jira schema changes do not
/// break ingestion.
/// <para>The ingestion key is the human issue key (<c>PROJ-123</c>): stable,
/// readable, and never a UUID.</para>
/// </summary>
public sealed record JiraIssue
{
    /// <summary>Human issue key (e.g. <c>PROJ-123</c>). The ingestion key.</summary>
    public required string Key { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Jira project key (e.g. <c>PROJ</c>), for the operator's project map.</summary>
    public string ProjectKey { get; init; } = string.Empty;

    public IReadOnlyList<string> LabelNames { get; init; } = [];

    public IReadOnlyList<string> AssigneeLogins { get; init; } = [];

    public string StatusName { get; init; } = string.Empty;

    public string? LastActorLogin { get; init; }

    /// <summary>
    /// Parses one issue object from a search/detail payload. Unknown fields
    /// are ignored so additive Jira schema changes do not break ingestion.
    /// </summary>
    public static JiraIssue FromNode(JsonElement node)
    {
        node.TryGetProperty("fields", out var fields);
        var fieldsObj = fields.ValueKind == JsonValueKind.Object ? fields : node;

        return new JiraIssue
        {
            Key = Str(node, "key"),
            Title = Str(fieldsObj, "summary"),
            Description = AdfText.FromValue(Get(fieldsObj, "description")),
            ProjectKey = ProjectKeyOf(fieldsObj),
            LabelNames = LabelNamesOf(fieldsObj),
            AssigneeLogins = AssigneeLoginsOf(Get(fieldsObj, "assignee")),
            StatusName = StatusNameOf(Get(fieldsObj, "status")),
            LastActorLogin = null,
        };
    }

    private static string ProjectKeyOf(JsonElement fields)
    {
        var project = Get(fields, "project");
        return project.ValueKind == JsonValueKind.Object ? Str(project, "key") : string.Empty;
    }

    private static IReadOnlyList<string> LabelNamesOf(JsonElement fields)
    {
        var names = new List<string>();
        if (fields.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in labels.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    var s = e.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        names.Add(s);
                }
            }
        }
        return names;
    }

    private static IReadOnlyList<string> AssigneeLoginsOf(JsonElement assignee)
    {
        var logins = new List<string>();
        if (assignee.ValueKind != JsonValueKind.Object)
            return logins;
        foreach (var key in new[] { "accountId", "emailAddress", "displayName" })
        {
            var value = Str(assignee, key);
            if (!string.IsNullOrWhiteSpace(value))
                logins.Add(value);
        }
        return logins;
    }

    private static string StatusNameOf(JsonElement status) =>
        status.ValueKind == JsonValueKind.Object ? Str(status, "name") : string.Empty;

    private static JsonElement Get(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Plain-text extraction from Atlassian Document Format (ADF). v3 search and
/// issue payloads carry <c>description</c> as an ADF object (or a plain string
/// on older Server/DC versions); webhook comment bodies arrive the same way.
/// </summary>
public static class AdfText
{
    /// <summary>
    /// Extracts plain text from an ADF value: plain strings pass through,
    /// ADF objects yield their text nodes joined by newlines, anything else
    /// yields empty. Never throws.
    /// </summary>
    public static string FromValue(JsonElement value)
    {
        try
        {
            return Extract(value);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Builds an ADF document body from plain text for comment posts.</summary>
    public static object ToDocument(string text)
    {
        var paragraphs = new List<object>();
        foreach (var chunk in ChunkParagraphs(text))
        {
            paragraphs.Add(new Dictionary<string, object?>
            {
                ["type"] = "paragraph",
                ["content"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = chunk },
                },
            });
        }
        if (paragraphs.Count == 0)
        {
            paragraphs.Add(new Dictionary<string, object?>
            {
                ["type"] = "paragraph",
                ["content"] = Array.Empty<object>(),
            });
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "doc",
            ["version"] = 1,
            ["content"] = paragraphs,
        };
    }

    private static string Extract(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Object)
            return string.Empty;
        var sb = new StringBuilder();
        Walk(value, sb);
        return sb.ToString().Trim();
    }

    private static void Walk(JsonElement node, StringBuilder sb)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        if (string.Equals(type, "text", StringComparison.Ordinal)
            && node.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            sb.Append(text.GetString());
            return;
        }
        if (string.Equals(type, "hardBreak", StringComparison.Ordinal))
        {
            sb.Append('\n');
            return;
        }
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        var block = string.Equals(type, "paragraph", StringComparison.Ordinal)
            || string.Equals(type, "heading", StringComparison.Ordinal)
            || string.Equals(type, "listItem", StringComparison.Ordinal);
        foreach (var child in content.EnumerateArray())
            Walk(child, sb);
        if (block)
            sb.Append('\n');
    }

    private static IEnumerable<string> ChunkParagraphs(string text)
    {
        const int maxChunk = 4000;
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length <= maxChunk)
            {
                yield return paragraph;
                continue;
            }
            for (var i = 0; i < paragraph.Length; i += maxChunk)
                yield return paragraph.Substring(i, Math.Min(maxChunk, paragraph.Length - i));
        }
    }
}

/// <summary>A reachable Jira workflow transition for one issue.</summary>
public sealed record JiraTransition(string Id, string Name, string ToName);

/// <summary>Jira webhook registration (site-level, 30-day expiry, renewable).</summary>
public sealed record JiraWebhookRegistration(string Id, string Url, long ExpirationDate);
