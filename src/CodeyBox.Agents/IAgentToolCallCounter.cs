using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Best-effort tool-call counter for an agent CLI's captured stdout. Used by the
/// orchestrator to emit <c>agent.tool_call.&lt;name&gt;</c> telemetry when the
/// agent ran in buffered (non-streaming-capture) mode. Implementations parse the
/// provider-specific stream-json shape from a single buffered string.
/// Implementations must never throw and return null when the output is not
/// recognisable as stream-json for that provider.
/// </summary>
public interface IAgentToolCallCounter
{
    AgentKind Kind { get; }

    AgentToolCallCounts? TryCount(string? bufferedStdout);
}

public sealed record AgentToolCallCounts(
    IReadOnlyDictionary<string, int> ToolCallCounts,
    string? FinalText);
