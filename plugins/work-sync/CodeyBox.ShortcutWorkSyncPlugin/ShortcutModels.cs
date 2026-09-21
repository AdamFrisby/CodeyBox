using System.Text.Json;

namespace CodeyBox.ShortcutWorkSyncPlugin;

/// <summary>
/// Shortcut story shape used by the work-sync plugin. Field names mirror the
/// Shortcut REST API v3 (<c>id</c> is the immutable numeric id; the ingestion
/// key is the derived <c>sc-{id}</c> because stories carry no human key).
/// </summary>
public sealed record ShortcutStory
{
    /// <summary>Immutable numeric story id. The ingestion key is <c>sc-{Id}</c>.</summary>
    public required long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public long? ProjectId { get; init; }

    public long? EpicId { get; init; }

    public IReadOnlyList<string> LabelNames { get; init; } = [];

    public IReadOnlyList<string> OwnerIds { get; init; } = [];

    public long WorkflowStateId { get; init; }

    public string WorkflowStateName { get; init; } = string.Empty;

    /// <summary>
    /// Parses one story from a search/list response element. Unknown fields are
    /// ignored so additive Shortcut schema changes do not break ingestion.
    /// </summary>
    public static ShortcutStory FromNode(JsonElement node)
    {
        return new ShortcutStory
        {
            Id = Long(node, "id"),
            Name = Str(node, "name"),
            Description = Str(node, "description"),
            ProjectId = OptLong(node, "project_id"),
            EpicId = OptLong(node, "epic_id"),
            LabelNames = Labels(node),
            OwnerIds = Strings(node, "owner_ids"),
            WorkflowStateId = Long(node, "workflow_state_id"),
            WorkflowStateName = Str(node, "workflow_state_name"),
        };
    }

    internal static IReadOnlyList<string> Labels(JsonElement node)
    {
        if (!node.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var e in labels.EnumerateArray())
        {
            string? name = e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Object => e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    private static IReadOnlyList<string> Strings(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    internal static long Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : 0;

    private static long? OptLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : null;

    internal static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Shortcut epic shape. Epics ingest as ONE work item each
/// (<c>sc-epic-{id}</c>) only when <c>IngestEpics</c> is enabled; they never
/// fan out into per-story items.
/// </summary>
public sealed record ShortcutEpic
{
    /// <summary>Immutable numeric epic id. The ingestion key is <c>sc-epic-{Id}</c>.</summary>
    public required long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> LabelNames { get; init; } = [];

    public IReadOnlyList<string> OwnerIds { get; init; } = [];

    /// <summary>
    /// Parses one epic from a list/get response element. Unknown fields are
    /// ignored so additive Shortcut schema changes do not break ingestion.
    /// </summary>
    public static ShortcutEpic FromNode(JsonElement node)
    {
        return new ShortcutEpic
        {
            Id = ShortcutStory.Long(node, "id"),
            Name = ShortcutStory.Str(node, "name"),
            Description = ShortcutStory.Str(node, "description"),
            LabelNames = ShortcutStory.Labels(node),
            OwnerIds = node.TryGetProperty("owner_ids", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList()
                : [],
        };
    }
}

/// <summary>Shortcut member (assignee) identity used for signal matching and loop-guard attribution.</summary>
public sealed record ShortcutMember(string Id, string MentionName, string Email, string Name);

/// <summary>Shortcut workflow state (id + name) resolved from the workflows listing.</summary>
public sealed record ShortcutWorkflowState(long Id, string Name);
