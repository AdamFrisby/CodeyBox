using System.Text.Json;

namespace CodeyBox.PlaneWorkSyncPlugin;

/// <summary>
/// Plane issue shape used by the work-sync plugin. Plane renamed
/// <c>issues</c> to <c>work items</c> in newer releases and self-hosted
/// versions vary, so parsing accepts both spellings (<c>name</c>/<c>title</c>,
/// <c>description_html</c>/<c>description</c>) and ignores unknown fields so
/// additive schema changes do not break ingestion.
/// <para>The ingestion key is the human key <c>{PROJECT}-{sequence_id}</c>
/// (e.g. <c>WEB-123</c>): stable, readable, and never a UUID. The immutable
/// issue UUID is used for mutations only.</para>
/// </summary>
public sealed record PlaneIssue
{
    /// <summary>Immutable Plane UUID. Used for mutations; never an ingestion key.</summary>
    public required string Id { get; init; }

    /// <summary>Human key (e.g. <c>WEB-123</c>). The ingestion key.</summary>
    public required string Key { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string ProjectIdentifier { get; init; } = string.Empty;

    public IReadOnlyList<string> LabelNames { get; init; } = [];

    public IReadOnlyList<string> AssigneeLogins { get; init; } = [];

    public string StateName { get; init; } = string.Empty;

    /// <summary>State group (<c>backlog</c>, <c>unstarted</c>, <c>started</c>, ...).</summary>
    public string StateGroup { get; init; } = string.Empty;

    public string? LastActorLogin { get; init; }

    /// <summary>
    /// Builds the human key from a project identifier and sequence id.
    /// Returns empty when either part is missing.
    /// </summary>
    public static string BuildKey(string projectIdentifier, int sequenceId) =>
        string.IsNullOrWhiteSpace(projectIdentifier) || sequenceId <= 0
            ? string.Empty
            : $"{projectIdentifier.Trim().ToUpperInvariant()}-{sequenceId}";

    /// <summary>
    /// Parses one issue element from a REST list/detail payload. Unknown
    /// fields are ignored so additive Plane schema changes do not break ingestion.
    /// </summary>
    /// <param name="node">The issue object.</param>
    /// <param name="projectIdentifier">
    /// Project identifier (e.g. <c>WEB</c>) for building the human key when the
    /// payload does not carry it. Empty falls back to any embedded value.
    /// </param>
    public static PlaneIssue FromNode(JsonElement node, string projectIdentifier = "")
    {
        var project = Str(node, "project");
        var sequence = 0;
        if (node.TryGetProperty("sequence_id", out var seq))
        {
            if (seq.ValueKind == JsonValueKind.Number && seq.TryGetInt32(out var n))
                sequence = n;
        }

        var embeddedIdentifier = string.Empty;
        if (node.TryGetProperty("project_detail", out var pd) && pd.ValueKind == JsonValueKind.Object)
            embeddedIdentifier = Str(pd, "identifier");
        if (string.IsNullOrEmpty(embeddedIdentifier)
            && node.TryGetProperty("project_identifier", out var pi)
            && pi.ValueKind == JsonValueKind.String)
            embeddedIdentifier = pi.GetString() ?? string.Empty;
        var identifier = !string.IsNullOrWhiteSpace(projectIdentifier)
            ? projectIdentifier
            : embeddedIdentifier;

        return new PlaneIssue
        {
            Id = Str(node, "id"),
            Key = BuildKey(identifier, sequence),
            Title = FirstNonEmpty(Str(node, "name"), Str(node, "title")),
            Description = FirstNonEmpty(
                PlainText(Str(node, "description_html")),
                Str(node, "description"),
                Str(node, "description_text")),
            ProjectId = project,
            ProjectIdentifier = identifier,
            LabelNames = LabelNamesOf(node),
            AssigneeLogins = AssigneeLoginsOf(node),
            StateName = StateNameOf(node),
            StateGroup = StateGroupOf(node),
            LastActorLogin = ActorOf(node),
        };
    }

    private static IReadOnlyList<string> LabelNamesOf(JsonElement node)
    {
        var names = new List<string>();
        if (node.TryGetProperty("label_details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in details.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Str(e, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        if (node.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in labels.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    var s = e.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        names.Add(s);
                }
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    var name = Str(e, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }
        }
        return names;
    }

    private static IReadOnlyList<string> AssigneeLoginsOf(JsonElement node)
    {
        var logins = new List<string>();
        if (node.TryGetProperty("assignee_details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in details.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var key in new[] { "id", "email", "display_name" })
                {
                    var value = Str(e, key);
                    if (!string.IsNullOrWhiteSpace(value))
                        logins.Add(value);
                }
            }
        }
        if (node.TryGetProperty("assignees", out var assignees) && assignees.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in assignees.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    var s = e.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        logins.Add(s);
                }
            }
        }
        return logins;
    }

    private static string StateNameOf(JsonElement node)
    {
        if (node.TryGetProperty("state_detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            var name = Str(detail, "name");
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return FirstNonEmpty(Str(node, "state_name"), Str(node, "state"));
    }

    private static string StateGroupOf(JsonElement node)
    {
        if (node.TryGetProperty("state_detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            var group = Str(detail, "group");
            if (!string.IsNullOrWhiteSpace(group))
                return group;
        }
        return Str(node, "state_group");
    }

    private static string? ActorOf(JsonElement node)
    {
        foreach (var key in new[] { "updated_by_detail", "created_by_detail" })
        {
            if (node.TryGetProperty(key, out var actor) && actor.ValueKind == JsonValueKind.Object)
            {
                var email = Str(actor, "email");
                if (!string.IsNullOrWhiteSpace(email))
                    return email;
                var display = Str(actor, "display_name");
                if (!string.IsNullOrWhiteSpace(display))
                    return display;
            }
        }
        foreach (var key in new[] { "updated_by", "created_by" })
        {
            if (node.TryGetProperty(key, out var actor))
            {
                if (actor.ValueKind == JsonValueKind.String)
                {
                    var s = actor.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
                else if (actor.ValueKind == JsonValueKind.Object)
                {
                    var email = Str(actor, "email");
                    if (!string.IsNullOrWhiteSpace(email))
                        return email;
                }
            }
        }
        return null;
    }

    private static string Str(JsonElement el, string name) =>
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

    private static string PlainText(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf('<') < 0)
            return html;
        var sb = new System.Text.StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<')
            {
                inTag = true;
                continue;
            }
            if (ch == '>')
            {
                inTag = false;
                continue;
            }
            if (!inTag)
                sb.Append(ch);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString()).Trim();
    }
}

/// <summary>Plane workflow state (id + name + group) within one project.</summary>
public sealed record PlaneWorkflowState(string Id, string Name, string Group);

/// <summary>Plane webhook registration (workspace-level).</summary>
public sealed record PlaneWebhookRegistration(string Id, string Url, bool Enabled);
