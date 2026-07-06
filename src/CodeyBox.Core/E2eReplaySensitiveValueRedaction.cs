namespace CodeyBox.Core;

/// <summary>
/// Placeholder values emitted into committed replay artifacts for sensitive
/// fill steps. Real values are supplied at replay time via
/// <see cref="E2eExecutionOptions.FillSecrets"/> — never persisted in the
/// artifact JSON.
/// </summary>
public static class E2eReplaySensitiveValueRedaction
{
    public const string PasswordPlaceholder = "<redacted-password>";

    /// <summary>
    /// Resolves an artifact fill value using the runtime-only secrets map.
    /// Unknown values pass through unchanged.
    /// </summary>
    public static string ResolveFillValue(string? artifactValue, IReadOnlyDictionary<string, string>? secrets)
    {
        if (string.IsNullOrEmpty(artifactValue))
            return artifactValue ?? string.Empty;

        if (secrets is not null
            && secrets.TryGetValue(artifactValue, out var resolved)
            && !string.IsNullOrEmpty(resolved))
            return resolved;

        return artifactValue;
    }
}
