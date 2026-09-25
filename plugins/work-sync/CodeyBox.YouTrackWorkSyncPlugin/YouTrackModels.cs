using System.Text.Json;

namespace CodeyBox.YouTrackWorkSyncPlugin;

/// <summary>
/// YouTrack issue shape used by the work-sync plugin. YouTrack descriptions
/// are plain text (Markdown in the UI); unknown fields are ignored so
/// additive YouTrack schema changes do not break ingestion.
/// <para>The ingestion key is the human-readable issue id
/// (<c>idReadable</c>, e.g. <c>PROJ-123</c>): stable, readable, and accepted
/// everywhere the REST API takes an issue id.</para>
/// <para>YouTrack's field model is per-project configurable: the state and
/// assignee live in custom fields whose names the operator declares via
/// <see cref="YouTrackWorkSyncOptions.StateFieldName"/> and
/// <see cref="YouTrackWorkSyncOptions.AssigneeFieldName"/>. Nothing here
/// assumes a fixed field set.</para>
/// </summary>
public sealed record YouTrackIssue
{
    /// <summary>Database entity id (e.g. <c>2-123</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable issue id (e.g. <c>PROJ-123</c>). The ingestion key.</summary>
    public required string IdReadable { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>YouTrack project short name (e.g. <c>PROJ</c>), for the operator's project map.</summary>
    public string ProjectKey { get; init; } = string.Empty;

    /// <summary>Tag names applied to the issue (the label signal).</summary>
    public IReadOnlyList<string> TagNames { get; init; } = [];

    /// <summary>Logins/full names of users in the configured assignee field.</summary>
    public IReadOnlyList<string> AssigneeLogins { get; init; } = [];

    /// <summary>Current value name of the configured state field.</summary>
    public string StateName { get; init; } = string.Empty;

    /// <summary>Login of the user who last updated the issue, when reported.</summary>
    public string? LastActorLogin { get; init; }

    /// <summary>
    /// Parses one issue object from a list/detail payload. Unknown fields are
    /// ignored so additive YouTrack schema changes do not break ingestion.
    /// Returns null when the node carries no readable id.
    /// </summary>
    public static YouTrackIssue? FromNode(JsonElement node, YouTrackWorkSyncOptions options)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        var idReadable = Str(node, "idReadable");
        if (string.IsNullOrWhiteSpace(idReadable))
            return null;

        var tags = TagNamesOf(Get(node, "tags"));
        var fields = Get(node, "customFields");
        var assignees = YouTrackSignals.UserValues(
            CustomFieldValue(fields, options.AssigneeFieldName));
        var state = CustomFieldValue(fields, options.StateFieldName) is { } stateValue
            ? YouTrackSignals.FirstValue(stateValue) : string.Empty;

        return new YouTrackIssue
        {
            Id = Str(node, "id"),
            IdReadable = idReadable,
            Title = Str(node, "summary"),
            Description = Str(node, "description"),
            ProjectKey = ProjectKeyOf(node),
            TagNames = tags,
            AssigneeLogins = assignees,
            StateName = state,
            LastActorLogin = UserLogin(Get(node, "updater")),
        };
    }

    private static string ProjectKeyOf(JsonElement node)
    {
        var project = Get(node, "project");
        return project.ValueKind == JsonValueKind.Object ? Str(project, "shortName") : string.Empty;
    }

    private static IReadOnlyList<string> TagNamesOf(JsonElement tags)
    {
        var names = new List<string>();
        if (tags.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var e in tags.EnumerateArray())
        {
            var name = e.ValueKind == JsonValueKind.Object ? Str(e, "name") : Str(e);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Reads the <c>value</c> of the custom field named <paramref name="fieldName"/>
    /// (ordinal-ignore-case). Returns null when absent.
    /// </summary>
    internal static JsonElement? CustomFieldValue(JsonElement fields, string fieldName)
    {
        if (fields.ValueKind != JsonValueKind.Array || string.IsNullOrWhiteSpace(fieldName))
            return null;
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                continue;
            if (!string.Equals(Str(field, "name"), fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            return field.TryGetProperty("value", out var value) ? value : null;
        }
        return null;
    }

    internal static string? UserLogin(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "login", "fullName", "name" })
        {
            var value = Str(user, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    internal static JsonElement Get(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    internal static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Str(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Shared extraction of ingestion-signal values from YouTrack's tag list and
/// custom-field values. A field value may be a scalar, a bundle element
/// (<c>{"name": …}</c>), a user (<c>{"login": …, "fullName": …}</c>), or an
/// array of any of those (multi-value fields) — all shapes are read.
/// </summary>
public static class YouTrackSignals
{
    /// <summary>
    /// All string values a custom-field value element carries: scalar text,
    /// bundle-element names, or user identifiers (login plus full name, so
    /// operators may declare either). Arrays yield every element's values.
    /// </summary>
    public static IReadOnlyList<string> Values(JsonElement? value)
    {
        var found = new List<string>();
        if (value is null)
            return found;
        Collect(value.Value, found);
        return found;
    }

    /// <summary>User-identifying values (login, full name) only — for the assignee signal.</summary>
    public static IReadOnlyList<string> UserValues(JsonElement? value)
    {
        var found = new List<string>();
        if (value is null)
            return found;
        CollectUsers(value.Value, found);
        return found;
    }

    /// <summary>First string value of a field value element, or empty.</summary>
    public static string FirstValue(JsonElement value) =>
        Values(value) is { Count: > 0 } all ? all[0] : string.Empty;

    private static void Collect(JsonElement el, List<string> found)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    Collect(item, found);
                return;
            case JsonValueKind.Object:
                foreach (var key in new[] { "name", "login", "fullName" })
                {
                    var s = YouTrackIssue.Str(el, key);
                    if (!string.IsNullOrWhiteSpace(s))
                        found.Add(s);
                }
                return;
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
        }
    }

    private static void CollectUsers(JsonElement el, List<string> found)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                CollectUsers(item, found);
            return;
        }
        // A bare string in an assignee-field context is a user reference
        // (login or display name) — the webhook app may send it either way.
        if (el.ValueKind == JsonValueKind.String)
        {
            var text = el.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                found.Add(text);
            return;
        }
        if (el.ValueKind != JsonValueKind.Object)
            return;
        // Only treat objects that look like users ($type or login present) as
        // users; a bundle element with just a name is not an assignee.
        var looksLikeUser = el.TryGetProperty("$type", out var t)
            && t.ValueKind == JsonValueKind.String
            && t.GetString()?.Contains("User", StringComparison.Ordinal) == true;
        var login = YouTrackIssue.Str(el, "login");
        if (!looksLikeUser && string.IsNullOrWhiteSpace(login))
            return;
        if (!string.IsNullOrWhiteSpace(login))
            found.Add(login);
        var fullName = YouTrackIssue.Str(el, "fullName");
        if (!string.IsNullOrWhiteSpace(fullName))
            found.Add(fullName);
    }
}
