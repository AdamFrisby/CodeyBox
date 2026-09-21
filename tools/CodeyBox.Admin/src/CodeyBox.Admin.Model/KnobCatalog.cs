namespace CodeyBox.Admin.Model;

/// <summary>A per-item knob the orchestrator registers, as the composer offers it.</summary>
public sealed record KnobDescriptor(
    string Key,
    string Label,
    string Description,
    IReadOnlyList<string> AllowedValues,
    string DefaultValue);

/// <summary>
/// The built-in knobs and the parsing of free-form ones. The orchestrator
/// rejects unknown keys and out-of-vocabulary values at create time with a
/// 400 — so the composer shows the known ones as selects with their
/// vocabulary, keeps a free-form line editor for knobs registered after
/// this build, and checks what it can before the POST.
/// </summary>
public static class KnobCatalog
{
    /// <summary>Knobs the orchestrator ships with, in display order.</summary>
    public static IReadOnlyList<KnobDescriptor> BuiltIn { get; } =
    [
        new(
            "changeScope",
            "Change scope",
            "How aggressively the agent may restructure adjacent code: surgical = smallest diff; moderate = default; refactor = restructure permitted.",
            ["surgical", "moderate", "refactor"],
            "moderate"),
        new(
            "plan",
            "Plan first",
            "Whether the agent writes and has reviewed a plan before touching code.",
            ["off", "on"],
            "off"),
    ];

    /// <summary>
    /// Parses "key=value" lines. Blank lines are skipped; a line without
    /// "=" or with an empty key or value is a problem. Later lines win
    /// over earlier duplicates so an operator can correct a value by
    /// appending. Returns null when nothing was set.
    /// </summary>
    public static (IReadOnlyDictionary<string, string>? Knobs, string? Problem) ParseLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        Dictionary<string, string>? knobs = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
            {
                return (null, $"Knob line “{line}” must look like key=value.");
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                return (null, $"Knob line “{line}” must look like key=value.");
            }

            knobs ??= new Dictionary<string, string>(StringComparer.Ordinal);
            knobs[key] = value;
        }

        return (knobs, null);
    }

    /// <summary>
    /// Merges the selected built-in values with free-form extras into the
    /// map the create surface accepts. A built-in left at its default or
    /// blank is omitted (the server default applies); a free-form line
    /// naming a built-in key overrides the select. Values of built-ins are
    /// checked against their vocabulary. Returns null when nothing is set.
    /// </summary>
    public static (IReadOnlyDictionary<string, string>? Knobs, string? Problem) Compose(
        IReadOnlyDictionary<string, string?> builtInSelections,
        IReadOnlyDictionary<string, string>? extras)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var knob in BuiltIn)
        {
            if (builtInSelections.TryGetValue(knob.Key, out var chosen)
                && !string.IsNullOrWhiteSpace(chosen)
                && !string.Equals(chosen.Trim(), knob.DefaultValue, StringComparison.OrdinalIgnoreCase))
            {
                merged[knob.Key] = chosen.Trim();
            }
        }

        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                merged[key] = value;
            }
        }

        foreach (var knob in BuiltIn)
        {
            if (merged.TryGetValue(knob.Key, out var value)
                && !knob.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return (null, $"{knob.Label} must be one of {string.Join(", ", knob.AllowedValues)} — not “{value}”.");
            }
        }

        return (merged.Count == 0 ? null : merged, null);
    }
}

/// <summary>Required-capability tags: a comma-, semicolon- or newline-separated list.</summary>
public static class CapabilityTags
{
    /// <summary>Parses tags, trimmed and de-duplicated case-insensitively; null when empty.</summary>
    public static IReadOnlyList<string>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tags = text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tags.Count == 0 ? null : tags;
    }
}
