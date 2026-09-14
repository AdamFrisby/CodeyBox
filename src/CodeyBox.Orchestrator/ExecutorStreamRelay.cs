using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Forwards live agent-output chunks produced on a remote executor host into
/// the orchestrator-side observability path: the same
/// <see cref="AgentStreamCapture"/> artefact (same directory, same
/// phase/iteration key) a local phase would write, plus the existing
/// <see cref="IStdoutBroadcaster"/> so live hub subscribers see remote
/// output with no contract change.
///
/// <para>The relay holds no queue of its own — every piece is forwarded
/// synchronously to the capture and the broadcaster — so a remote producer
/// cannot exceed the local buffering limits: the capture's own queue slicing
/// and per-file truncation (including its truncation marker) apply unchanged.
/// The only bound owned here is <c>MaxStreamChunkChars</c>, which caps the
/// size of a single forwarded piece before it reaches either sink.</para>
///
/// <para>Sequencing: the executor numbers chunks from zero with no gaps. Any
/// discontinuity (lost or reordered chunk) is recorded as an explicit
/// <c>[...stream gap ...]</c> line in both the file and the broadcast rather
/// than presenting a contiguous stream that silently omits output.</para>
///
/// <para>Failure isolation: relaying never fails the phase. This method never
/// throws — a broken capture or broadcaster only degrades observability.</para>
/// </summary>
public sealed class ExecutorStreamRelay
{
    private readonly AgentStreamCapture? _capture;
    private readonly IStdoutBroadcaster? _broadcaster;
    private readonly WorkItemId _workItemId;
    private readonly string _phase;
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private long _expectedSequence;

    public ExecutorStreamRelay(
        AgentStreamCapture? capture,
        IStdoutBroadcaster? broadcaster,
        WorkItemId workItemId,
        string phase,
        Func<ExecutorPhaseDispatchOptions> optionsAccessor,
        ILogger? log = null)
    {
        _capture = capture;
        _broadcaster = broadcaster;
        _workItemId = workItemId;
        _phase = phase;
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Forwards one executor chunk to the capture and the live broadcast.
    /// Records a gap marker when <see cref="ExecutorStreamChunk.Sequence"/>
    /// is not the next expected value. Never throws.
    /// </summary>
    public Task OnChunkAsync(ExecutorStreamChunk chunk, CancellationToken ct)
    {
        try
        {
            if (chunk is null)
                return Task.CompletedTask;
            if (chunk.Sequence < 0)
            {
                _log.LogDebug(
                    "Ignoring executor stream chunk with negative sequence for phase {Phase}",
                    _phase);
                return Task.CompletedTask;
            }

            var data = chunk.Data ?? string.Empty;
            var split = ReadSplitSize();
            lock (_lock)
            {
                if (chunk.Sequence != _expectedSequence)
                {
                    Forward(
                        $"[...stream gap: expected seq {_expectedSequence} but received seq {chunk.Sequence}]\n",
                        split);
                    _expectedSequence = Math.Max(_expectedSequence, chunk.Sequence + 1);
                }
                else
                {
                    _expectedSequence++;
                }

                if (data.Length > 0)
                    Forward(data, split);
            }
        }
        catch (Exception ex)
        {
            // Observability only: sequence numbers are safe to log, chunk
            // payloads are untrusted executor output and never enter logs.
            _log.LogDebug(ex, "Executor stream relay failed for phase {Phase}", _phase);
        }

        return Task.CompletedTask;
    }

    private int ReadSplitSize()
    {
        try
        {
            return Math.Max(1, _optionsAccessor().MaxStreamChunkChars);
        }
        catch (Exception ex)
        {
            // Hot-reload accessor failure must not break the relay; the 64 KiB
            // floor mirrors the capture's own queue slice.
            _log.LogDebug(ex, "Executor stream options unavailable for phase {Phase}", _phase);
            return 64 * 1024;
        }
    }

    private void Forward(string text, int split)
    {
        for (var offset = 0; offset < text.Length; offset += split)
        {
            var piece = text.Substring(offset, Math.Min(split, text.Length - offset));
            try
            {
                _capture?.WriteChunk(piece);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Executor stream capture write failed for phase {Phase}", _phase);
            }

            try
            {
                _broadcaster?.BroadcastChunk(_workItemId, _phase, piece);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Executor stream broadcast failed for phase {Phase}", _phase);
            }
        }
    }
}
