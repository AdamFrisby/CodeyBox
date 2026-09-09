namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Guards cheap-model authoring against frontier coding-agent model ids.
/// </summary>
public static class CheapModelAllowlist
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-haiku-4-5-20251001",
        "claude-3-5-haiku-20241022",
        "claude-3-haiku-20240307",
        "gemini-2.0-flash",
        "gemini-2.5-flash",
        "gemini-3-flash-preview",
    };

    private static readonly string[] FrontierDenylist =
    [
        "opus",
        "sonnet-4",
        "gpt-5",
        "composer",
        "codex",
        "o1",
        "o3",
    ];

    public static void EnsureCheap(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model id is required for cheap-model authoring.", nameof(modelId));

        var normalized = modelId.Trim();
        if (Allowed.Contains(normalized))
            return;

        var lower = normalized.ToLowerInvariant();
        foreach (var fragment in FrontierDenylist)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
                throw new ArgumentException($"Model id '{modelId}' is a frontier coding agent and cannot be used for CUA authoring.", nameof(modelId));
        }

        if (!lower.Contains("haiku", StringComparison.Ordinal)
            && !lower.Contains("flash", StringComparison.Ordinal))
            throw new ArgumentException($"Model id '{modelId}' is not on the cheap-model allowlist.", nameof(modelId));
    }
}
