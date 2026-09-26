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
    /// <see cref="MessageValueMaxChars"/>.
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
}
