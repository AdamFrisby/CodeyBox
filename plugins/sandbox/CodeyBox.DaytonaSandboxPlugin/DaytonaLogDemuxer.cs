namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// Incremental demultiplexer for the Daytona daemon's command-log WebSocket
/// framing. The daemon emits frames whose payload is prefixed with a 3-byte
/// channel marker — <c>01 01 01</c> for stdout, <c>02 02 02</c> for stderr
/// (the framing the official SDKs decode in <c>_utils/stream.py</c>).
/// Prefixes may straddle frame boundaries, so a carry buffer holds back bytes
/// that could be a partial prefix. Emitted payloads are raw bytes — the
/// collector owns UTF-8 decoding so binary output (e.g. a base64 tar archive
/// being synced back to the host) survives byte-exact.
/// </summary>
internal sealed class DaytonaLogDemuxer
{
    internal static readonly byte[] StdoutPrefix = [0x01, 0x01, 0x01];
    internal static readonly byte[] StderrPrefix = [0x02, 0x02, 0x02];
    private const int MaxPrefixLength = 3;

    private readonly List<byte> _buffer = new();
    private readonly Func<byte[], Task> _onStdout;
    private readonly Func<byte[], Task> _onStderr;
    private byte _channel;

    // Consumed head offset into _buffer. Draining advances _head instead of
    // removing from the head (which memmoves the tail, quadratic on large
    // exec streams); the prefix is compacted only once the head grows past
    // CompactionThresholdBytes, keeping steady-state drain amortized O(1).
    private int _head;
    private const int CompactionThresholdBytes = 64 * 1024;

    public DaytonaLogDemuxer(Func<byte[], Task> onStdout, Func<byte[], Task> onStderr)
    {
        _onStdout = onStdout ?? throw new ArgumentNullException(nameof(onStdout));
        _onStderr = onStderr ?? throw new ArgumentNullException(nameof(onStderr));
    }

    /// <summary>Feeds one received chunk (a full WS message or fragment).</summary>
    public async Task FeedAsync(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.Length == 0)
            return;
        foreach (var b in chunk.Span)
            _buffer.Add(b);
        await DrainAsync().ConfigureAwait(false);
        CompactIfNeeded();
    }

    /// <summary>Flushes any retained tail when the stream ends.</summary>
    public async Task CompleteAsync()
    {
        // Anything left is payload (a trailing partial prefix cannot be one
        // once the stream is closed — emit it so nothing is silently dropped).
        if (Available == 0)
            return;
        await EmitAsync(_buffer.GetRange(_head, Available).ToArray()).ConfigureAwait(false);
        _buffer.Clear();
        _head = 0;
    }

    private int Available => _buffer.Count - _head;

    private void CompactIfNeeded()
    {
        if (_head < CompactionThresholdBytes)
            return;
        _buffer.RemoveRange(0, _head);
        _head = 0;
    }

    private async Task DrainAsync()
    {
        while (Available > 0)
        {
            var stdoutIndex = IndexOf(StdoutPrefix, Available);
            var stderrIndex = IndexOf(StderrPrefix, Available);
            var nextIndex = -1;
            byte nextChannel = 0;
            if (stdoutIndex >= 0 && (stderrIndex < 0 || stdoutIndex < stderrIndex))
            {
                nextIndex = stdoutIndex;
                nextChannel = 1;
            }
            else if (stderrIndex >= 0)
            {
                nextIndex = stderrIndex;
                nextChannel = 2;
            }

            if (nextIndex < 0)
            {
                // No marker in the window: emit everything except a trailing
                // run that a later chunk could complete into a marker. When
                // nothing is emittable, wait for more bytes (CompleteAsync
                // flushes the remainder at stream end).
                var holdBack = TrailingMarkerRunLength();
                var emitLength = Available - holdBack;
                if (emitLength <= 0)
                    return;
                await EmitAsync(_buffer.GetRange(_head, emitLength).ToArray()).ConfigureAwait(false);
                _head += emitLength;
                continue;
            }

            if (nextIndex > 0)
                await EmitAsync(_buffer.GetRange(_head, nextIndex).ToArray()).ConfigureAwait(false);
            _head += nextIndex + MaxPrefixLength;
            _channel = nextChannel;
        }
    }

    /// <summary>
    /// Length (0-2) of the trailing run of one marker byte value that a later
    /// chunk could extend into a full 3-byte channel marker. Markers are
    /// homogeneous triples, so only a uniform trailing run of 0x01/0x02 can
    /// become one - and a complete marker would already have been consumed by
    /// the scan above, so the run is always shorter than a full marker.
    /// </summary>
    private int TrailingMarkerRunLength()
    {
        var available = Available;
        if (available == 0)
            return 0;
        var last = _buffer[_head + available - 1];
        if (last is not 0x01 and not 0x02)
            return 0;
        var run = 1;
        while (run < available && run < MaxPrefixLength - 1 && _buffer[_head + available - 1 - run] == last)
            run++;
        return run;
    }

    // Searches the unconsumed window; the returned index is relative to
    // _head so callers can advance the head without re-scanning.
    private int IndexOf(byte[] needle, int within)
    {
        for (var i = 0; i + needle.Length <= within; i++)
        {
            if (_buffer[_head + i] == needle[0] && _buffer[_head + i + 1] == needle[1] && _buffer[_head + i + 2] == needle[2])
                return i;
        }
        return -1;
    }

    private Task EmitAsync(byte[] payload)
    {
        if (payload.Length == 0 || _channel is not (1 or 2))
            return Task.CompletedTask;
        return _channel == 2 ? _onStderr(payload) : _onStdout(payload);
    }
}
