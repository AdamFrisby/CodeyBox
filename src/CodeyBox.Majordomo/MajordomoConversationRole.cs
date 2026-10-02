namespace CodeyBox.Majordomo;

/// <summary>
/// The kind of one durable majordomo conversation row. Operator and
/// majordomo turns are prose; tool calls and tool results are structured
/// data captured alongside them so a restarted orchestrator (or a fresh
/// sandbox — the sandbox is disposable, the conversation is not) can
/// rebuild exactly what the previous turn saw and did.
/// </summary>
public enum MajordomoConversationRole
{
    /// <summary>A turn supplied by the operator.</summary>
    Operator,

    /// <summary>A turn produced by the majordomo assistant.</summary>
    Majordomo,

    /// <summary>One tool invocation the majordomo issued, with its arguments.</summary>
    ToolCall,

    /// <summary>
    /// The outcome of one tool invocation. Results are untrusted data
    /// (work-item failure text, agent stdout, audit findings) and must be
    /// demarcated — never replayed as instructions — when assembled into
    /// context. See <see cref="MajordomoContextAssembler"/>.
    /// </summary>
    ToolResult,
}
