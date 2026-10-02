namespace CodeyBox.Majordomo;

/// <summary>
/// The deterministic rollup covering every conversation entry at or below
/// <see cref="UpToSequence"/>. Older history is summarised rather than
/// dropped: the assembler replays this text instead of the covered rows, so
/// reasoning like "this failure looks like the one from Tuesday" stays
/// possible without unbounded growth. <see cref="None"/> marks an empty
/// summary; a stored summary covering entries always carries non-empty text.
/// </summary>
public sealed record MajordomoConversationSummary
{
    public static readonly MajordomoConversationSummary None = new(0, string.Empty, DateTimeOffset.MinValue);

    public MajordomoConversationSummary(long upToSequence, string text, DateTimeOffset updatedAt)
    {
        if (upToSequence < 0)
            throw new ArgumentOutOfRangeException(
                nameof(upToSequence), upToSequence, "upToSequence must be >= 0");
        ArgumentNullException.ThrowIfNull(text);
        if (upToSequence > 0 && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException(
                "a summary covering entries must carry non-empty text", nameof(text));

        UpToSequence = upToSequence;
        Text = text;
        UpdatedAt = updatedAt;
    }

    /// <summary>Every entry at or below this sequence is covered by <see cref="Text"/>.</summary>
    public long UpToSequence { get; }

    /// <summary>The rollup text replayed in place of the covered entries.</summary>
    public string Text { get; }

    /// <summary>When the rollup was last extended.</summary>
    public DateTimeOffset UpdatedAt { get; }
}
