namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>Shared text truncation for error surfaces (one definition, not a copy per file).</summary>
internal static class DaytonaTextUtil
{
    private const int ErrorTailMaxChars = 500;

    /// <summary>Returns the last <c>ErrorTailMaxChars</c> characters of <paramref name="text"/>.</summary>
    internal static string Tail(string text) =>
        text.Length <= ErrorTailMaxChars ? text : text[^ErrorTailMaxChars..];
}
