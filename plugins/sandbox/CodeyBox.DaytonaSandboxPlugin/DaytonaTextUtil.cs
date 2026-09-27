namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>Shared text truncation for error surfaces (one definition, not a copy per file).</summary>
internal static class DaytonaTextUtil
{
    private const int ErrorTailMaxChars = 500;

    /// <summary>
    /// Returns the last <c>ErrorTailMaxChars</c> characters of <paramref name="text"/>,
    /// sanitized for single-line log/exception surfaces (see
    /// <see cref="SanitizeForLog"/>): guest stderr tails flow into structured
    /// logs, where embedded CR/LF would forge log lines.
    /// </summary>
    internal static string Tail(string text) =>
        SanitizeForLog(text.Length <= ErrorTailMaxChars ? text : text[^ErrorTailMaxChars..]);

    /// <summary>
    /// Folds ASCII control characters (CR, LF, and the rest of C0 plus DEL)
    /// to spaces so untrusted service/guest text cannot forge log lines or
    /// inject terminal escapes via exception messages and structured logs.
    /// One-to-one mapping, so prior length bounds still hold.
    /// </summary>
    internal static string SanitizeForLog(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 0x20 || chars[i] == 0x7F)
                chars[i] = ' ';
        }
        return new string(chars);
    }
}
