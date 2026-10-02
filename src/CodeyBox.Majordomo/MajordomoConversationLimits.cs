namespace CodeyBox.Majordomo;

/// <summary>
/// The single storage-bounding helper shared by every
/// <see cref="IMajordomoConversationStore"/> implementation, so in-memory
/// and SQLite rows truncate identically.
/// </summary>
public static class MajordomoConversationLimits
{
    /// <summary>
    /// Caps <paramref name="text"/> at <paramref name="maxChars"/>
    /// characters, keeping the head and appending how much was cut. The cap
    /// is enforced before the text is persisted, so one huge tool result
    /// cannot grow the state database at request rate.
    /// </summary>
    public static string TruncateForStorage(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars < 1)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "maxChars must be >= 1");
        if (text.Length <= maxChars)
            return text;
        var marker = $"[...truncated {text.Length - maxChars} chars]";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
