using System.Text;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Shared flattening for untrusted strings — tool output, tool-supplied
/// paths, scanner messages — before they cross into findings, failure
/// messages, or logs. Every consumer of external-tool text funnels through
/// here so the escape/control policy lives in exactly one place: a crafted
/// byte stream cannot inject terminal sequences or spoof line structure
/// into operator-facing output.
/// </summary>
public static class ToolOutputText
{
    /// <summary>
    /// Flattens <paramref name="value"/> to a single line: every control
    /// character — newlines, tabs, ESC and other terminal-escape bytes —
    /// becomes a space, then the result is trimmed. Null/empty/whitespace
    /// input yields an empty string.
    /// </summary>
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    /// <summary>
    /// <see cref="SingleLine(string?)"/> capped at
    /// <paramref name="maxChars"/>; truncation is marked with a trailing
    /// <c>...</c>.
    /// </summary>
    public static string SingleLine(string? value, int maxChars)
    {
        var flattened = SingleLine(value);
        return flattened.Length <= maxChars ? flattened : flattened[..maxChars] + "...";
    }

    /// <summary>
    /// Flattens control characters in <paramref name="value"/> except line
    /// feeds — for multi-line bodies such as finding descriptions, where the
    /// text keeps its line structure but cannot inject terminal escapes or
    /// carriage-return overwrites.
    /// </summary>
    public static string FlattenControls(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsControl(c) && c != '\n' ? ' ' : c);
        return builder.ToString();
    }
}
