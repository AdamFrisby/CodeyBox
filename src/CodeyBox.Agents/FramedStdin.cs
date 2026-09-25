using System.Text;

namespace CodeyBox.Agents;

/// <summary>
/// The framed-stdin wire contract shared by the in-sandbox launcher
/// transports (<c>AcpClaudeTransport</c>'s bridge launcher, the devin ACP
/// shim dispatch): a base64-encoded payload block wrapped at
/// <see cref="Base64LineWidth"/>, an end-marker line, then the verbatim
/// trailing payload (prompt / session stdin). Producer and consuming bash
/// stanzas (<see cref="BashReaderBlock"/> for a file collector,
/// <see cref="BashReaderBlockToVariable"/> for a variable collector) are ONE
/// framing contract — marker, wrap width, and reader variables must agree —
/// so both halves live here and a framing change propagates to every
/// transport instead of silently diverging.
/// </summary>
internal static class FramedStdin
{
    /// <summary>
    /// Line width of the base64 payload block — the MIME/base64 convention.
    /// The consuming bash reader re-collects the block line-by-line before
    /// decoding, so the wrap width is part of the framing contract.
    /// </summary>
    internal const int Base64LineWidth = 76;

    /// <summary>
    /// Serialises one framed-stdin stream: the base64 payload wrapped at
    /// <see cref="Base64LineWidth"/>, the end marker on its own line, then
    /// the trailing payload verbatim. The marker can never appear inside
    /// the base64 block; a matching line inside the trailing payload is
    /// harmless — only the FIRST occurrence ends the block, and it is
    /// written before the payload by construction.
    /// </summary>
    internal static string Build(string base64Payload, string endMarker, string trailingPayload)
    {
        ArgumentNullException.ThrowIfNull(base64Payload);
        ArgumentNullException.ThrowIfNull(endMarker);
        ArgumentNullException.ThrowIfNull(trailingPayload);

        var sb = new StringBuilder(base64Payload.Length + trailingPayload.Length + endMarker.Length + 32);
        for (var i = 0; i < base64Payload.Length; i += Base64LineWidth)
        {
            var len = Math.Min(Base64LineWidth, base64Payload.Length - i);
            sb.Append(base64Payload, i, len).Append('\n');
        }
        sb.Append(endMarker).Append('\n');
        sb.Append(trailingPayload);
        return sb.ToString();
    }

    /// <summary>
    /// The bash stanza that consumes the <see cref="Build"/> frame: reads
    /// stdin lines into <c>$<paramref name="collectorVariable"/></c> until
    /// the end-marker line, setting <c><paramref name="foundVariable"/>=1</c>
    /// when the marker is seen. The caller emits the marker-not-found check,
    /// decodes the collector file, and then reads the remaining stdin (the
    /// trailing payload) however it needs. Variable names are validated as
    /// shell identifiers — they interpolate into generated bash.
    /// </summary>
    internal static string BashReaderBlock(string endMarker, string collectorVariable, string foundVariable)
    {
        ArgumentNullException.ThrowIfNull(endMarker);
        ValidateBashIdentifier(collectorVariable);
        ValidateBashIdentifier(foundVariable);

        return string.Join('\n',
            $"{foundVariable}=0",
            "while IFS= read -r line; do",
            $"  if [ \"$line\" = {ShellQuote(endMarker)} ]; then {foundVariable}=1; break; fi",
            $"  printf '%s\\n' \"$line\" >> \"${collectorVariable}\"",
            "done");
    }

    /// <summary>
    /// Variant of <see cref="BashReaderBlock"/> that accumulates the payload
    /// block into a shell VARIABLE instead of a file, for consumers that must
    /// never stage the payload at a re-openable path inside the sandbox (a
    /// same-uid watcher could swap a staged file between write and exec).
    /// The caller emits the marker-not-found check and then consumes the
    /// trailing stdin payload however it needs — typically by passing the
    /// still-open descriptor 0 on to the payload's consumer.
    /// </summary>
    internal static string BashReaderBlockToVariable(string endMarker, string collectorVariable, string foundVariable)
    {
        ArgumentNullException.ThrowIfNull(endMarker);
        ValidateBashIdentifier(collectorVariable);
        ValidateBashIdentifier(foundVariable);

        return string.Join('\n',
            $"{foundVariable}=0",
            $"{collectorVariable}=''",
            "while IFS= read -r line; do",
            $"  if [ \"$line\" = {ShellQuote(endMarker)} ]; then {foundVariable}=1; break; fi",
            $"  {collectorVariable}=\"${{{collectorVariable}}}$line\"",
            "done");
    }

    private static void ValidateBashIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)
            || !(IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(static ch => !(IsAsciiLetter(ch) || ch is >= '0' and <= '9' || ch == '_')))
        {
            throw new ArgumentException("Bash variable name must be a shell identifier.", nameof(name));
        }

        static bool IsAsciiLetter(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    }

    /// <summary>
    /// Single-quotes <paramref name="value"/> for safe interpolation into a
    /// <c>bash</c>/<c>sh</c> script, escaping embedded single quotes. The
    /// one POSIX-quoting implementation shared by every generated-script
    /// helper in this assembly (and by sibling agent assemblies via
    /// InternalsVisibleTo).
    /// </summary>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
