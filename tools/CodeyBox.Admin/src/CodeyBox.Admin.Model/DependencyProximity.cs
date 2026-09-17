namespace CodeyBox.Admin.Model;

/// <summary>
/// A work item that may be picked as a dependency. Kept minimal so the
/// ordering rule stays testable without Blazor or HTTP.
/// </summary>
public sealed record DependencyCandidate(
    string Id,
    string ProjectId,
    string Title,
    string State,
    DateTimeOffset CreatedAt,
    string? ExternalId = null);

/// <summary>
/// Orders dependency candidates by proximity, not memory: members of the
/// chain being filed first, then items from the same project, then
/// everything else — newest first within each band, id as the final
/// tiebreak so the order is deterministic. Search is a case-insensitive
/// substring over title, id, and external id.
/// Pure over its inputs; the caller decides which states are eligible.
/// </summary>
public static class DependencyProximity
{
    /// <summary>
    /// Orders <paramref name="candidates"/> for display. Chain members
    /// keep their relative order; every other band is newest-first.
    /// </summary>
    public static IReadOnlyList<DependencyCandidate> Order(
        IEnumerable<DependencyCandidate> candidates,
        string? currentProjectId,
        IReadOnlySet<string>? sameChainIds = null)
    {
        var list = candidates.ToList();
        var chainPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (sameChainIds is not null)
        {
            var position = 0;
            foreach (var id in sameChainIds)
            {
                chainPositions.TryAdd(id, position++);
            }
        }

        return list
            .Select(c => new
            {
                Candidate = c,
                Band = BandOf(c, currentProjectId, chainPositions),
                ChainPosition = chainPositions.TryGetValue(c.Id, out var p) ? p : int.MaxValue,
            })
            .OrderBy(x => x.Band)
            .ThenBy(x => x.ChainPosition)
            .ThenByDescending(x => x.Candidate.CreatedAt)
            .ThenBy(x => x.Candidate.Id, StringComparer.Ordinal)
            .Select(x => x.Candidate)
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="candidate"/> matches <paramref name="query"/>.
    /// Blank queries match everything; matching is ordinal case-insensitive
    /// over title, full id, short id, and external id.
    /// </summary>
    public static bool Matches(DependencyCandidate candidate, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        return candidate.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || candidate.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (candidate.ExternalId is not null
                && candidate.ExternalId.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Short display title: full text collapsed to one line and capped at
    /// <paramref name="maxLength"/> characters. The full title stays
    /// available for the tooltip.
    /// </summary>
    public static string DisplayTitle(string title, int maxLength = 80)
    {
        var single = string.Join(' ', (title ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= maxLength ? single : single[..maxLength].TrimEnd() + "…";
    }

    private static int BandOf(
        DependencyCandidate candidate,
        string? currentProjectId,
        Dictionary<string, int> chainPositions)
    {
        if (chainPositions.ContainsKey(candidate.Id))
        {
            return 0;
        }

        if (currentProjectId is not null
            && string.Equals(candidate.ProjectId, currentProjectId, StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }
}
