using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>
/// One live agent-output chunk produced on an executor host and relayed to
/// the orchestrator while the phase runs. The orchestrator appends
/// <see cref="Data"/> to the same <c>AgentStreamCapture</c> artefact (same
/// directory, same phase/iteration key) a local phase would write, and
/// re-broadcasts it through the existing stdout hub, so live subscribers see
/// remote output with no contract change.
///
/// <para>Sequencing: the executor numbers chunks from zero with no gaps.
/// When the relay observes a discontinuity (a lost or reordered chunk) it
/// records an explicit gap marker in the captured stream rather than
/// presenting a contiguous stream that silently omits output.</para>
/// </summary>
public sealed record ExecutorStreamChunk
{
    /// <summary>
    /// Zero-based position of this chunk in the executor's emission order.
    /// Must be zero or positive; the first chunk of a dispatch is zero.
    /// </summary>
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    /// <summary>
    /// Raw agent-output text for this chunk. May be an arbitrary slice of
    /// the stream (line fragments are fine — the capture reassembles lines).
    /// Null is treated as empty by the relay.
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}
