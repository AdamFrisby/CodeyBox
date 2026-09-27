using System.Text;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Shared text shaping for untrusted tool output that reaches operator-facing
/// surfaces — failure messages, finding descriptions, persisted output. Tool
/// output is a trust-boundary input: these helpers collapse control
/// characters and bound length so a tool cannot inject terminal escapes or
/// flood a message. Auditor implementations reach the same helpers through
/// the protected members on <see cref="ExternalToolAuditorBase"/>; standalone
/// output parsers (which do not derive from the auditor base) call this class
/// directly.
/// </summary>
public static class ToolOutputText
{
    /// <summary>Length cap for a single untrusted value embedded in a failure message.</summary>
    public const int MessageValueMaxChars = 64;

    /// <summary>
    /// Flattens tool output to a single line for failure messages. Every
    /// control character — newlines, tabs, ESC and other terminal escape
    /// bytes — becomes a space so untrusted tool output cannot inject
    /// sequences into logged messages or persisted failure reasons.
    /// </summary>
    public static string SingleLine(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var builder = new StringBuilder(message.Length);
        foreach (var c in message)
            builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Renders an untrusted configuration or output value for embedding in a
    /// failure message: single-line and capped at
    /// <see cref="MessageValueMaxChars"/>. Truncation is marked with a
    /// single-character ellipsis; <see cref="Truncate"/> (finding titles,
    /// wrapped by the auditor base) uses the three-dot form — a cosmetic
    /// difference between the two surfaces, kept stable rather than churned.
    /// </summary>
    public static string TruncateForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = SingleLine(value);
        return single.Length > MessageValueMaxChars
            ? single[..MessageValueMaxChars] + "…"
            : single;
    }

    /// <summary>Truncates <paramref name="value"/> to <paramref name="maxChars"/> with an ellipsis suffix.</summary>
    public static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    /// <summary>
    /// Null when <paramref name="value"/> is null, empty, or whitespace-only;
    /// otherwise the value unchanged. Shared so output parsers do not each
    /// carry a private copy.
    /// </summary>
    public static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Maps every control character except the line feed to a space: ESC and
    /// the C1 controls (CSI, OSC, NEL, …) let untrusted tool output inject
    /// terminal escape sequences into rendered findings; newlines are kept
    /// because finding descriptions are legitimately multi-line.
    /// <see cref="SingleLine"/> is the stricter variant for surfaces that must
    /// stay on one line.
    /// </summary>
    public static string CollapseControlCharacters(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsControl(c) && c != '\n' ? ' ' : c);
        return builder.ToString();
    }
}
