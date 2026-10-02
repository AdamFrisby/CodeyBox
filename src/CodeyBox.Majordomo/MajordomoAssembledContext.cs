namespace CodeyBox.Majordomo;

/// <summary>The assembled view one majordomo turn actually sees.</summary>
/// <param name="Text">The bounded context text, never longer than the configured bound.</param>
/// <param name="AssembledAt">The instant the context was assembled.</param>
/// <param name="RecentEntryCount">Entries replayed verbatim.</param>
/// <param name="SummarizedEntryCount">Entries represented only by a summary rollup.</param>
public sealed record MajordomoAssembledContext(
    string Text,
    DateTimeOffset AssembledAt,
    int RecentEntryCount,
    int SummarizedEntryCount);
