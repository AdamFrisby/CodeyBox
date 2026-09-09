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
    /// Resolves an artifact fill or press value using the runtime-only secrets map.
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

    /// <summary>
    /// Applies runtime fill-secret resolution to every fill and press step.
    /// </summary>
    public static IReadOnlyList<E2eReplayStep> ResolveStepSecrets(
        IReadOnlyList<E2eReplayStep> steps,
        IReadOnlyDictionary<string, string>? secrets)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (secrets is null || secrets.Count == 0)
            return steps;

        var resolved = new List<E2eReplayStep>(steps.Count);
        foreach (var step in steps)
        {
            if (!RequiresSecretResolution(step.Action ?? string.Empty))
            {
                resolved.Add(step);
                continue;
            }

            resolved.Add(step with { Value = ResolveFillValue(step.Value, secrets) });
        }

        return resolved;
    }

    private static bool RequiresSecretResolution(string action)
        => string.Equals(action, "fill", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "press", StringComparison.OrdinalIgnoreCase);
}
