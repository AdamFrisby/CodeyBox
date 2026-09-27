using System.Text;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Shared text helpers for <see cref="IExternalToolOutputParser"/>
/// implementations: blank normalization, positive line-number parsing, and
/// flattening untrusted tool output to a single line for exception
/// messages. New tool parsers should use these instead of growing
/// per-parser copies; pre-existing parsers migrate as they are touched.
/// </summary>
public static class ToolOutputText
{
    /// <summary>Returns null for null/whitespace input; the value otherwise.</summary>
    public static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Parses a positive integer — the shape of a tool-reported 1-based line
    /// number. Returns null when the value is absent, malformed, or
    /// non-positive.
    /// </summary>
    public static int? ParseLine(string? value)
        => int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && parsed > 0
            ? parsed
            : null;

    /// <summary>
    /// Flattens untrusted tool output to a single line for failure messages:
    /// every control character — newlines, tabs, ESC and other terminal
    /// escape bytes — becomes a space so tool output cannot inject
    /// sequences into logged messages or persisted failure reasons.
    /// </summary>
    public static string SingleLine(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var c in message)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }
}
