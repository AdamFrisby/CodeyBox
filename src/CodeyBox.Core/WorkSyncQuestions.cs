namespace CodeyBox.Core;

/// <summary>
/// The provider-agnostic question-reply protocol for work sync, defined once
/// here so every <see cref="IWorkTracker"/> plugin embeds the same tag and
/// every <see cref="IWorkSource"/> parses the same reply shape — the wire
/// format cannot drift per provider.
/// <para>A surfaced question comment ends with <see cref="TagFor"/> output
/// (e.g. <c>&lt;!-- codeybox-question:q-001 --&gt;</c>). An operator answers
/// by posting a comment that starts with <c>{questionId}:</c>; <see
/// cref="TryExtractReply"/> matches the prefix by exact id against the work
/// item's open questions.</para>
/// </summary>
public static class WorkSyncQuestions
{
    /// <summary>Tag embedded in surfaced question comments so replies can be attributed.</summary>
    public const string TagPrefix = "<!-- codeybox-question:";

    /// <summary>Maximum answer length accepted from a reply comment (chars).</summary>
    public const int MaxAnswerChars = 4000;

    /// <summary>Builds the tag embedded in a surfaced question comment.</summary>
    public static string TagFor(string questionId) => $"{TagPrefix}{questionId} -->";

    /// <summary>
    /// Extracts a question answer from an operator's reply comment: the
    /// comment starts with <c>{questionId}:</c> (e.g. <c>q-001: use
    /// forward-only</c>), matched by exact id against
    /// <paramref name="openQuestionIds"/>. Returns null when the comment
    /// follows no known question or carries an empty answer. Bodies carrying
    /// the CodeyBox marker are ours and never parse as replies.
    /// </summary>
    public static (string QuestionId, string Answer)? TryExtractReply(
        string commentBody, IReadOnlySet<string> openQuestionIds)
    {
        if (string.IsNullOrWhiteSpace(commentBody) || openQuestionIds.Count == 0)
            return null;
        if (WorkSyncLoopGuard.CarriesMarker(commentBody))
            return null;
        var trimmed = commentBody.Trim();
        foreach (var id in openQuestionIds)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (trimmed.StartsWith(id + ":", StringComparison.OrdinalIgnoreCase))
            {
                var answer = trimmed[(id.Length + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(answer))
                    return null;
                return (id, answer.Length > MaxAnswerChars ? answer[..MaxAnswerChars] : answer);
            }
        }
        return null;
    }
}
