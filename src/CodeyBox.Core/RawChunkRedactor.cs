using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Applies the same secret-value patterns as <see cref="SensitiveDataRedactionEnricher"/>
/// to raw strings. Used to redact agent stdout/stderr chunks before broadcasting
/// to SignalR clients, where structured log enrichment is not applicable.
/// Only redacts values matching known prefixes (GitHub PATs, Anthropic keys, etc.) —
/// it cannot redact arbitrary secrets that don't match a known pattern.
/// </summary>
public static class RawChunkRedactor
{
    private static readonly Regex SecretPattern = new(
        SensitiveDataRedactionEnricher.SecretValuePatternSource,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string chunk) => SecretPattern.Replace(chunk, "***");
}
