using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Applies the same secret-value patterns as <see cref="SensitiveDataRedactionEnricher"/>
/// to arbitrary strings. Used to scrub auditor raw output before persisting it.
/// Reuses <see cref="SensitiveDataRedactionEnricher.SecretValuePatternSource"/> so
/// the auditor path and SignalR/log paths stay in lockstep on what counts as a secret.
/// </summary>
public static class RawOutputRedactor
{
    private static readonly Regex SecretPattern = new(
        SensitiveDataRedactionEnricher.SecretValuePatternSource,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Replaces any detected secret token in <paramref name="text"/> with <c>***</c>.
    /// Returns the original string unchanged when no secrets are found.
    /// </summary>
    public static string Redact(string text) =>
        SensitiveDataRedactionEnricher.RedactJsonSensitiveProperties(SecretPattern.Replace(text, "***"));

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes,
    /// appending <c>[...truncated]</c> when the original exceeded the cap.
    /// The cap is applied AFTER <see cref="Redact"/>; call <see cref="Redact"/> first.
    /// </summary>
    public static string TruncateToBytes(string text, int maxBytes)
    {
        const string Marker = "\n[...truncated]";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes) return text;

        // Binary-search the char count that fits within (maxBytes - marker.Length) bytes.
        var budget = maxBytes - System.Text.Encoding.UTF8.GetByteCount(Marker);
        if (budget <= 0) return Marker.TrimStart('\n');

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, mid)) <= budget)
                lo = mid;
            else
                hi = mid - 1;
        }
        return text[..lo] + Marker;
    }
}
