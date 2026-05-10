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
            .Select(known => new { Id = known, Distance = EditDistanceHelper.Compute(id, known) })
            .Where(candidate => candidate.Distance <= 3)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Id)
            .FirstOrDefault();
}
