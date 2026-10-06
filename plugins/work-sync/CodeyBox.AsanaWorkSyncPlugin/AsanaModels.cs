using System.Text.Json;

namespace CodeyBox.AsanaWorkSyncPlugin;

/// <summary>
/// Asana task shape used by the work-sync plugin. Asana notes are plain text;
/// unknown fields are ignored so additive API changes do not break ingestion.
/// <para>The ingestion key is the task <c>gid</c>: the immutable,
/// globally-unique identifier. Names, project names, and display names are
/// user-editable and are never used as identity.</para>
/// </summary>
public sealed record AsanaTask
{
    /// <summary>Immutable task GID (numeric string). The ingestion key.</summary>
    public required string Gid { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    /// <summary>Project GIDs the task belongs to, in API order.</summary>
    public IReadOnlyList<string> ProjectGids { get; init; } = [];

    /// <summary>Assignee user GID, when assigned.</summary>
    public string AssigneeGid { get; init; } = string.Empty;

    /// <summary>Assignee display name (editable; matched exactly but never used as identity).</summary>
    public string AssigneeName { get; init; } = string.Empty;

    /// <summary>Tag GIDs applied to the task.</summary>
    public IReadOnlyList<string> TagGids { get; init; } = [];

    /// <summary>Tag names applied to the task.</summary>
    public IReadOnlyList<string> TagNames { get; init; } = [];

    /// <summary>Section names the task sits in, across its projects.</summary>
    public IReadOnlyList<string> SectionNames { get; init; } = [];

    /// <summary>True when the task is marked complete.</summary>
    public bool Completed { get; init; }

    /// <summary>
    /// Parses one task object from a list payload (<c>GET /tasks</c> with
    /// <c>opt_fields</c>). Unknown fields are ignored. Returns null when the
    /// node carries no usable GID.
    /// </summary>
    public static AsanaTask? FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        var gid = Str(node, "gid");
        if (!AsanaGids.IsGid(gid))
            return null;

        var assignee = Get(node, "assignee");
        var tags = Get(node, "tags");
        var tagGids = new List<string>();
        var tagNames = new List<string>();
        if (tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Object)
                    continue;
                var tagGid = Str(tag, "gid");
                if (AsanaGids.IsGid(tagGid))
                    tagGids.Add(tagGid);
                var tagName = Str(tag, "name");
                if (!string.IsNullOrWhiteSpace(tagName))
                    tagNames.Add(tagName);
            }
        }

        var projectGids = new List<string>();
        if (Get(node, "projects") is { ValueKind: JsonValueKind.Array } projects)
        {
            foreach (var project in projects.EnumerateArray())
            {
                if (project.ValueKind != JsonValueKind.Object)
                    continue;
                var projectGid = Str(project, "gid");
                if (AsanaGids.IsGid(projectGid))
                    projectGids.Add(projectGid);
            }
        }

        var sectionNames = new List<string>();
        if (Get(node, "memberships") is { ValueKind: JsonValueKind.Array } memberships)
        {
            foreach (var membership in memberships.EnumerateArray())
            {
                if (membership.ValueKind != JsonValueKind.Object)
                    continue;
                var section = Get(membership, "section");
                if (section.ValueKind != JsonValueKind.Object)
                    continue;
                var sectionName = Str(section, "name");
                if (!string.IsNullOrWhiteSpace(sectionName))
                    sectionNames.Add(sectionName);
            }
        }

        return new AsanaTask
        {
            Gid = gid,
            Name = Str(node, "name"),
            Notes = Str(node, "notes"),
            ProjectGids = projectGids,
            AssigneeGid = assignee.ValueKind == JsonValueKind.Object ? Str(assignee, "gid") : string.Empty,
            AssigneeName = assignee.ValueKind == JsonValueKind.Object ? Str(assignee, "name") : string.Empty,
            TagGids = tagGids,
            TagNames = tagNames,
            SectionNames = sectionNames,
            Completed = Bool(node, "completed"),
        };
    }

    internal static JsonElement Get(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    internal static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static bool Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True;
}

/// <summary>
/// Asana story (comment or system event) shape, used only for duplicate
/// detection before outbound writes. Only the text is read; unknown fields
/// are ignored.
/// </summary>
public sealed record AsanaStory
{
    public string Gid { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Parses one story object from a <c>GET /tasks/{gid}/stories</c> payload.
    /// Nodes without text still parse (system stories carry no text); only a
    /// non-object node yields null.
    /// </summary>
    public static AsanaStory? FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        return new AsanaStory
        {
            Gid = AsanaTask.Str(node, "gid"),
            Type = AsanaTask.Str(node, "type"),
            Text = AsanaTask.Str(node, "text"),
        };
    }
}

/// <summary>
/// GID validation applied at every sink that accepts an Asana identifier.
/// Asana GIDs are immutable numeric strings; anything else is a caller or
/// configuration error and is refused before a request is built — never
/// interpolated into a URL unvalidated.
/// </summary>
public static class AsanaGids
{
    /// <summary>True when <paramref name="value"/> is a non-empty all-digit string.</summary>
    public static bool IsGid(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit);
}
