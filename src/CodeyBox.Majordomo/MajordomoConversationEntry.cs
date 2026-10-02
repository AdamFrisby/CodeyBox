namespace CodeyBox.Majordomo;

/// <summary>
/// One immutable row of the durable majordomo conversation. Entries are
/// append-only and ordered by <see cref="Sequence"/>; a <c>0</c> sequence
/// means "not yet stored" (the store assigns the next sequence on append).
/// Timestamps come from the caller so assembly stays deterministic under an
/// injected clock in tests.
/// </summary>
public sealed record MajordomoConversationEntry
{
    public MajordomoConversationEntry(
        MajordomoConversationRole role,
        string text,
        DateTimeOffset recordedAt,
        long sequence = 0,
        string? toolName = null)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(
                nameof(role), role, $"role must be a defined {nameof(MajordomoConversationRole)}");
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "sequence must be >= 0");

        var wantsTool = role is MajordomoConversationRole.ToolCall or MajordomoConversationRole.ToolResult;
        if (wantsTool)
        {
            if (!MajordomoTools.TryGet(toolName, out _))
                throw new ArgumentException(
                    "toolName must be an exact majordomo tool name for tool calls and results",
                    nameof(toolName));
        }
        else if (toolName is not null)
        {
            throw new ArgumentException(
                "toolName must be null for operator and majordomo turns", nameof(toolName));
        }

        Role = role;
        Text = text;
        RecordedAt = recordedAt;
        Sequence = sequence;
        ToolName = toolName;
    }

    /// <summary>Position in the conversation; 0 when not yet stored.</summary>
    public long Sequence { get; }

    /// <summary>When the turn, call, or result was recorded, in UTC.</summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>Which side produced this row.</summary>
    public MajordomoConversationRole Role { get; }

    /// <summary>The turn prose, call arguments, or result payload.</summary>
    public string Text { get; }

    /// <summary>
    /// The tool this call/result belongs to; null for prose turns.
    /// Always an exact vocabulary name (see <see cref="MajordomoTools.TryGet"/>).
    /// </summary>
    public string? ToolName { get; }
}
