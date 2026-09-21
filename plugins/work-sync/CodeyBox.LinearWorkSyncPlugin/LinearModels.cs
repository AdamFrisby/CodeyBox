using System.Text.Json;

namespace CodeyBox.LinearWorkSyncPlugin;

/// <summary>
/// Linear issue shape used by the work-sync plugin. Field names mirror the
/// Linear GraphQL schema (<c>identifier</c> is the human key <c>ENG-123</c>;
/// <c>id</c> is the immutable UUID used for mutations).
/// </summary>
public sealed record LinearIssue
{
    /// <summary>Immutable Linear UUID. Used for mutations; never an ingestion key.</summary>
    public required string Id { get; init; }

    /// <summary>Human key (e.g. <c>ENG-123</c>). The ingestion key: stable, readable, non-UUID.</summary>
    public required string Identifier { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string TeamKey { get; init; } = string.Empty;

    public string TeamId { get; init; } = string.Empty;

    public IReadOnlyList<string> LabelNames { get; init; } = [];

    public string? AssigneeId { get; init; }

    public string? AssigneeEmail { get; init; }

    public string? AssigneeName { get; init; }

    public string StateName { get; init; } = string.Empty;

    public string StateId { get; init; } = string.Empty;

    /// <summary>
    /// Parses one issue node from a GraphQL <c>nodes[]</c> element. Unknown
    /// fields are ignored so additive Linear schema changes do not break ingestion.
    /// </summary>
    public static LinearIssue FromNode(JsonElement node)
    {
        return new LinearIssue
        {
            Id = Str(node, "id"),
            Identifier = Str(node, "identifier"),
            Title = Opt(node, "title"),
            Description = Opt(node, "description"),
            TeamKey = node.TryGetProperty("team", out var team) && team.ValueKind == JsonValueKind.Object
                ? Opt(team, "key") : string.Empty,
            TeamId = node.TryGetProperty("team", out var teamId) && teamId.ValueKind == JsonValueKind.Object
                ? Opt(teamId, "id") : string.Empty,
            LabelNames = node.TryGetProperty("labels", out var labels)
                && labels.ValueKind == JsonValueKind.Object
                && labels.TryGetProperty("nodes", out var labelNodes)
                && labelNodes.ValueKind == JsonValueKind.Array
                ? labelNodes.EnumerateArray()
                    .Select(e => Opt(e, "name"))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList()
                : [],
            AssigneeId = node.TryGetProperty("assignee", out var assignee) && assignee.ValueKind == JsonValueKind.Object
                ? OptOrNull(assignee, "id") : null,
            AssigneeEmail = node.TryGetProperty("assignee", out var ae) && ae.ValueKind == JsonValueKind.Object
                ? OptOrNull(ae, "email") : null,
            AssigneeName = node.TryGetProperty("assignee", out var an) && an.ValueKind == JsonValueKind.Object
                ? OptOrNull(an, "name") : null,
            StateName = node.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object
                ? Opt(state, "name") : string.Empty,
            StateId = node.TryGetProperty("state", out var sid) && sid.ValueKind == JsonValueKind.Object
                ? Opt(sid, "id") : string.Empty,
        };
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Opt(JsonElement el, string name) => Str(el, name);

    private static string? OptOrNull(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>Linear workflow state (id + name) within one team.</summary>
public sealed record LinearWorkflowState(string Id, string Name);

/// <summary>Linear webhook registration.</summary>
public sealed record LinearWebhookRegistration(string Id, string Url, bool Enabled, IReadOnlyList<string> ResourceTypes);
