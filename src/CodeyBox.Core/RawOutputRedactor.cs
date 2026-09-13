using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Applies the same secret-value patterns as <see cref="SensitiveDataRedactionEnricher"/>
/// to arbitrary strings and removes terminal control characters before persistence.
/// Used to scrub auditor raw output before persisting it.
/// Reuses <see cref="SensitiveDataRedactionEnricher.SecretValuePatternSource"/> so
/// the auditor path and SignalR/log paths stay in lockstep on what counts as a secret.
/// </summary>
public static class RawOutputRedactor
{
    private static readonly Regex SecretPattern = new(
        SensitiveDataRedactionEnricher.SecretValuePatternSource,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Replaces any detected secret token in <paramref name="text"/> with <c>***</c>
    /// and strips unsafe control characters. Newlines and tabs are retained so
    /// multi-line diagnostics remain readable.
    /// </summary>
    public static string Redact(string text) =>
        StripUnsafeControlCharacters(
            SensitiveDataRedactionEnricher.RedactJsonSensitiveProperties(
                SecretPattern.Replace(text, "***")));

    private static string StripUnsafeControlCharacters(string text)
    {
        if (!ContainsUnsafeControlCharacter(text))
            return text;

        var buffer = new char[text.Length];
        var written = 0;
        foreach (var ch in text)
        {
            if (IsRetainedCharacter(ch))
                buffer[written++] = ch;
        }

        return new string(buffer, 0, written);
    }

    private static bool IsRetainedCharacter(char ch) =>
        !char.IsControl(ch) || ch is '\n' or '\t';

    private static bool ContainsUnsafeControlCharacter(string text)
    {
        foreach (var ch in text)
        {
            if (!IsRetainedCharacter(ch))
                return true;
        }

        return false;
    }

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

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes
    /// from the end of the string (retaining the tail), prepending <c>[...truncated]\n</c>
    /// when the original exceeded the cap. Char and surrogate boundaries are respected.
    /// The cap is applied AFTER <see cref="Redact"/>; call <see cref="Redact"/> first.
    /// </summary>
    public static string TruncateTailToBytes(string text, int maxBytes)
    {
        const string Marker = "[...truncated]\n";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes) return text;

        var budget = maxBytes - System.Text.Encoding.UTF8.GetByteCount(Marker);
        if (budget <= 0) return Marker.TrimEnd('\n');

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(mid)) <= budget)
                hi = mid;
            else
                lo = mid + 1;
        }

        if (lo > 0 && lo < text.Length && char.IsLowSurrogate(text[lo]))
            lo++;

        return Marker + text[lo..];
    }

    /// <summary>
    /// Collapses consecutive identical lines in <paramref name="text"/> so repeated
    /// diagnostics (e.g. repetitive file watcher or probe logs) do not dilute signal.
    /// When a line is repeated, the first occurrence is kept, followed by
    /// <c>[... repeated N times ...]</c> (or <c>[... repeated 1 time ...]</c>).
    /// </summary>
    public static string CollapseRepeatedLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var endsWithNewline = text.EndsWith('\n');
        using var reader = new System.IO.StringReader(text);
        var sb = new System.Text.StringBuilder(text.Length);

        string? currentLine = null;
        var repeatCount = 0;

        void FlushCurrent()
        {
            if (currentLine is null) return;

            if (sb.Length > 0)
                sb.Append('\n');

            sb.Append(currentLine);
            if (repeatCount > 1)
            {
                var times = repeatCount - 1;
                sb.Append("\n[... repeated ").Append(times).Append(times == 1 ? " time ...]" : " times ...]");
            }
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (currentLine is null)
            {
                currentLine = line;
                repeatCount = 1;
            }
            else if (string.Equals(line, currentLine, StringComparison.Ordinal))
            {
                repeatCount++;
            }
            else
            {
                FlushCurrent();
                currentLine = line;
                repeatCount = 1;
            }
        }

        FlushCurrent();

        if (endsWithNewline && sb.Length > 0)
            sb.Append('\n');

        return sb.ToString();
    }
}
