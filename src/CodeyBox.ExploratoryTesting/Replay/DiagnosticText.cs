namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Sanitises untrusted screen-content strings (accessibility role/name/text,
/// OCR text, assertion.Detail) before embedding them in human-readable
/// diagnostic messages. CR / LF / other control characters in those fields
/// can otherwise break structured logging downstream (log forging,
/// OWASP ASVS V16 / OWASP A09) when diagnostics are forwarded to telemetry
/// sinks that key on line boundaries.
/// </summary>
internal static class DiagnosticText
{
    private const int MaxLength = 200;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var span = value.AsSpan();
        var truncated = span.Length > MaxLength ? span[..MaxLength] : span;
        var buffer = new char[truncated.Length];
        for (var i = 0; i < truncated.Length; i++)
        {
            var c = truncated[i];
            // Replace any C0/C1 control char (CR, LF, TAB, …) with U+FFFD so a
            // single embedded newline cannot split a log line.
            buffer[i] = char.IsControl(c) ? '�' : c;
        }
        var result = new string(buffer);
        return span.Length > MaxLength ? result + "…" : result;
    }
}
