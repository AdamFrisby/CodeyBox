namespace CodeyBox.Core;

/// <summary>
/// Platform-neutral rendering primitives shared by notification provider
/// plugins. Lives in Core (like <see cref="NotificationCorrelation"/>) so
/// every provider applies one truncation rule and one set of presentation
/// bounds — never a per-plugin fork.
/// </summary>
public static class NotificationRendering
{
    /// <summary>Character bound applied to a rendered field name — a fixed
    /// presentation cap shared by every provider.</summary>
    public const int MaxFieldNameChars = 100;

    /// <summary>Character bound applied to a rendered field value — a fixed
    /// presentation cap shared by every provider.</summary>
    public const int MaxFieldValueChars = 500;

    /// <summary>Truncate to a character budget, marking the cut.</summary>
    public static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars < 1)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;
        const string marker = "… (truncated)";
        if (maxChars <= marker.Length)
            return text[..maxChars];
        return text[..(maxChars - marker.Length)] + marker;
    }
}
