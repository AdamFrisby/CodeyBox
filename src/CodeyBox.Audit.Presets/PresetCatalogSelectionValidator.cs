namespace CodeyBox.Audit.Presets;

public static class PresetCatalogSelectionValidator
{
    public static void ValidateLanguageIds(
        string owner,
        IEnumerable<string> languageIds,
        IReadOnlyCollection<string> knownLanguageIds)
    {
        foreach (var id in languageIds)
        {
            if (knownLanguageIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;

            throw new PresetConfigurationException(
                FormatUnknownPreset(owner, "language", id, knownLanguageIds));
        }
    }

    public static void ValidateAuditTypeIds(
        string owner,
        IEnumerable<string> auditTypeIds,
        IReadOnlyCollection<string> knownAuditTypeIds)
    {
        foreach (var id in auditTypeIds)
        {
            if (knownAuditTypeIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;

            throw new PresetConfigurationException(
                FormatUnknownPreset(owner, "audit type", id, knownAuditTypeIds));
        }
    }

    private static string FormatUnknownPreset(
        string owner,
        string kind,
        string id,
        IReadOnlyCollection<string> knownIds)
    {
        var suggestion = FindSuggestion(id, knownIds);
        var known = knownIds.Count == 0
            ? "none"
            : string.Join(", ", knownIds.Order(StringComparer.OrdinalIgnoreCase));
        var didYouMean = suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?";
        return $"{owner}: unknown {kind} id '{id}'.{didYouMean} Known {kind} ids: {known}.";
    }

    private static string? FindSuggestion(string id, IEnumerable<string> knownIds)
        => knownIds
            .Select(known => new { Id = known, Distance = EditDistance(id, known) })
            .Where(candidate => candidate.Distance <= 3)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Id)
            .FirstOrDefault();

    private static int EditDistance(string left, string right)
    {
        var dp = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++)
            dp[i, 0] = i;
        for (var j = 0; j <= right.Length; j++)
            dp[0, j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[left.Length, right.Length];
    }
}
