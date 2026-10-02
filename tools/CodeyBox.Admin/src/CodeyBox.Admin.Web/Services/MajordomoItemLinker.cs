using System.Text.RegularExpressions;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// One rendered piece of majordomo prose: plain text, or a work-item
/// reference. References carry the item id for the link target and the
/// display title — the map's rule that items appear by title, never as a
/// bare id, applied to conversation text.
/// </summary>
public sealed record LinkedSegment(string Text, string? WorkItemId)
{
    public bool IsLink => WorkItemId is not null;
}

/// <summary>
/// Splits majordomo prose around work-item id tokens (GUIDs in
/// <c>N</c> or <c>D</c> form, the shapes <c>WorkItemId</c> serializes as).
/// A token becomes a link only when <paramref name="titleFor"/> resolves
/// it to a title — proposal ids, session ids, and other GUID-shaped text
/// are not work-item references and render unchanged.
/// </summary>
public static class MajordomoItemLinker
{
    private static readonly Regex IdToken = new(
        @"\b(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static IReadOnlyList<LinkedSegment> Linkify(string text, Func<string, string?> titleFor)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(titleFor);

        var segments = new List<LinkedSegment>();
        var cursor = 0;
        foreach (Match match in IdToken.Matches(text))
        {
            var title = titleFor(match.Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;
            if (match.Index > cursor)
                segments.Add(new LinkedSegment(text[cursor..match.Index], null));
            segments.Add(new LinkedSegment(title, match.Value));
            cursor = match.Index + match.Length;
        }
        if (cursor == 0)
            return [new LinkedSegment(text, null)];
        if (cursor < text.Length)
            segments.Add(new LinkedSegment(text[cursor..], null));
        return segments;
    }
}
