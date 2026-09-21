namespace CodeyBox.Agents;

/// <summary>
/// Minimal reader for the Devin CLI's <c>credentials.toml</c>. The file is a
/// flat TOML table of string fields (<c>api_key</c>, <c>windsurf_api_key</c>,
/// <c>api_server_url</c>, …) written by <c>devin auth login</c> — this parser
/// deliberately understands only <c>key = "value"</c> lines plus comments,
/// which is the entire contract the CLI writes. Anything else (sections,
/// arrays, escapes) is ignored rather than half-parsed; the CLI itself is the
/// authority on validity.
///
/// <para>Lives in the shared agents assembly so both the devin agent library
/// (smoke probe) and the orchestrator's credential extractor share one
/// parser.</para>
/// </summary>
public static class DevinCredentialsToml
{
    /// <summary>Reads a top-level string field. Returns null when absent or non-string.</summary>
    public static string? TryGetString(string? toml, string key)
    {
        if (string.IsNullOrWhiteSpace(toml))
            return null;

        foreach (var rawLine in toml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
                continue;

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;

            if (!string.Equals(line[..eq].Trim(), key, StringComparison.Ordinal))
                continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                return value[1..^1];

            // Bare TOML scalars are not part of the credentials contract —
            // treat a non-string field as absent rather than guessing.
            return null;
        }

        return null;
    }
}
