using System.Text.Json;

namespace CodeyBox.OpenProjectWorkSyncPlugin;

/// <summary>
/// Work-package shape used by the work-sync plugin (REST API v3 HAL+JSON).
/// Unknown fields are ignored so additive schema changes do not break
/// ingestion.
/// <para>The ingestion key is the immutable numeric work-package
/// <c>id</c> (e.g. <c>2471</c>), rendered as text. The human-readable
/// <c>displayId</c> (e.g. <c>PROJ-3</c>) is presentation only and never used
/// as an identity. One plugin instance serves one OpenProject host, so
/// numeric ids are stable within the mapping.</para>
/// <para>OpenProject exposes no "last updater" on the work-package resource
/// (only <c>updatedAt</c>), and the assignee/author links carry a numeric
/// user id plus a mutable display name — never an immutable login. Assignee
/// matching therefore uses the numeric user id only; display names are
/// user-editable and never matched against the signal.</para>
/// </summary>
public sealed record OpenProjectWorkPackage
{
    /// <summary>Immutable numeric work-package id, as text. The ingestion key.</summary>
    public required string Id { get; init; }

    public string Subject { get; init; } = string.Empty;

    public string DescriptionRaw { get; init; } = string.Empty;

    /// <summary>Optimistic-locking version required on PATCH. Null when absent.</summary>
    public int? LockVersion { get; init; }

    /// <summary>Current status name (the <c>_links.status.title</c>).</summary>
    public string StatusName { get; init; } = string.Empty;

    /// <summary>Numeric status id parsed from <c>_links.status.href</c>, when present.</summary>
    public string StatusId { get; init; } = string.Empty;

    /// <summary>
    /// Numeric assignee user id parsed from <c>_links.assignee.href</c>
    /// (e.g. <c>42</c>). Empty when unassigned. The only assignee identifier
    /// ever matched against the signal.
    /// </summary>
    public string AssigneeUserId { get; init; } = string.Empty;

    /// <summary>
    /// Assignee display name (<c>_links.assignee.title</c>). Observability
    /// only — user-editable, never matched.
    /// </summary>
    public string AssigneeDisplayName { get; init; } = string.Empty;

    /// <summary>Numeric project id parsed from <c>_links.project.href</c>, when present.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>
    /// Parses one work-package object from a list/detail payload. Unknown
    /// fields are ignored. Returns null when the node carries no numeric id.
    /// </summary>
    public static OpenProjectWorkPackage? FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
            || !idEl.TryGetInt64(out var id) || id <= 0)
            return null;

        var links = Get(node, "_links");
        var (statusId, statusName) = LinkTarget(Get(links, "status"));
        var (assigneeId, assigneeName) = LinkTarget(Get(links, "assignee"));
        var (projectId, _) = LinkTarget(Get(links, "project"));

        return new OpenProjectWorkPackage
        {
            Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Subject = Str(node, "subject"),
            DescriptionRaw = RawOf(Get(node, "description")),
            LockVersion = node.TryGetProperty("lockVersion", out var lv)
                && lv.ValueKind == JsonValueKind.Number
                && lv.TryGetInt32(out var lockVersion) ? lockVersion : null,
            StatusName = statusName,
            StatusId = statusId,
            AssigneeUserId = assigneeId,
            AssigneeDisplayName = assigneeName,
            ProjectId = projectId,
        };
    }

    /// <summary>
    /// Splits a HAL link object into its trailing numeric id segment and
    /// title. A null-href link (unassigned, unset) yields empties.
    /// </summary>
    internal static (string Id, string Title) LinkTarget(JsonElement link)
    {
        if (link.ValueKind != JsonValueKind.Object)
            return (string.Empty, string.Empty);
        var title = Str(link, "title");
        if (!link.TryGetProperty("href", out var hrefEl)
            || hrefEl.ValueKind != JsonValueKind.String)
            return (string.Empty, title);
        var href = hrefEl.GetString() ?? string.Empty;
        var segment = href.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return long.TryParse(segment, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _)
            ? (segment, title)
            : (string.Empty, title);
    }

    internal static string RawOf(JsonElement formattable)
    {
        if (formattable.ValueKind != JsonValueKind.Object)
            return formattable.ValueKind == JsonValueKind.String ? Str(formattable) : string.Empty;
        return Str(formattable, "raw");
    }

    internal static JsonElement Get(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    internal static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    internal static string Str(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Work-package activity (comments and journals) used for idempotency
/// reconcile: outbound writes are recognised by exact comment-body match so
/// a retried post never duplicates. Journal entries without comment text
/// (status changes, edits) carry an empty <c>comment.raw</c> and never match.
/// </summary>
public sealed record OpenProjectActivity
{
    public required string Id { get; init; }

    public string CommentRaw { get; init; } = string.Empty;

    /// <summary>Numeric author user id parsed from <c>_links.user.href</c>.</summary>
    public string UserId { get; init; } = string.Empty;

    public static OpenProjectActivity? FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
            || !idEl.TryGetInt64(out var id) || id <= 0)
            return null;
        var (userId, _) = OpenProjectWorkPackage.LinkTarget(
            OpenProjectWorkPackage.Get(OpenProjectWorkPackage.Get(node, "_links"), "user"));
        return new OpenProjectActivity
        {
            Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CommentRaw = OpenProjectWorkPackage.RawOf(OpenProjectWorkPackage.Get(node, "comment")),
            UserId = userId,
        };
    }
}

/// <summary>
/// One admin-defined status from <c>GET /api/v3/statuses</c>, used to resolve
/// the caller-declared status name to the href the PATCH write model needs.
/// Resolution is by exact name match at the moment of the write — never
/// cached across posts, never guessed.
/// </summary>
public sealed record OpenProjectStatusEntry
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>Self href (e.g. <c>/api/v3/statuses/2</c>) for the PATCH link.</summary>
    public string Href { get; init; } = string.Empty;

    public static OpenProjectStatusEntry? FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
            || !idEl.TryGetInt64(out var id) || id <= 0)
            return null;
        var links = OpenProjectWorkPackage.Get(node, "_links");
        var self = OpenProjectWorkPackage.Get(links, "self");
        var href = OpenProjectWorkPackage.Str(self, "href");
        if (string.IsNullOrWhiteSpace(href))
            href = $"/api/v3/statuses/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return new OpenProjectStatusEntry
        {
            Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name = OpenProjectWorkPackage.Str(node, "name"),
            Href = href,
        };
    }
}
