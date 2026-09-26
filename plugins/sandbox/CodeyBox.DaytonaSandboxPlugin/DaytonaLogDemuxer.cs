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
    }

    /// <summary>Flushes any retained tail when the stream ends.</summary>
    public async Task CompleteAsync()
    {
        // Anything left is payload (a trailing partial prefix cannot be one
        // once the stream is closed — emit it so nothing is silently dropped).
        if (_buffer.Count == 0)
            return;
        await EmitAsync(_buffer.ToArray()).ConfigureAwait(false);
        _buffer.Clear();
    }

    private async Task DrainAsync()
    {
        while (_buffer.Count > 0)
        {
            var safeLength = SafeEmitLength();
            if (safeLength <= 0)
                return;

            var stdoutIndex = IndexOf(StdoutPrefix, safeLength);
            var stderrIndex = IndexOf(StderrPrefix, safeLength);
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
                await EmitAsync(_buffer.GetRange(0, safeLength).ToArray()).ConfigureAwait(false);
                _buffer.RemoveRange(0, safeLength);
                continue;
            }

            if (nextIndex > 0)
                await EmitAsync(_buffer.GetRange(0, nextIndex).ToArray()).ConfigureAwait(false);
            _buffer.RemoveRange(0, nextIndex + MaxPrefixLength);
            _channel = nextChannel;
        }
    }

    /// <summary>
    /// Bytes in the last MaxPrefixLength-1 slots can extend into a marker on
    /// the next chunk — hold them back. Mirrors the SDK's safe-region rule.
    /// </summary>
    private int SafeEmitLength()
    {
        var length = _buffer.Count;
        if (length < MaxPrefixLength)
            return length - (MaxPrefixLength - 1);

        var last = _buffer[length - 1];
        if (last is not 0x01 and not 0x02)
            return length;
        if (length < MaxPrefixLength + 1)
            return length - (MaxPrefixLength - 1);
        var secondLast = _buffer[length - 2];
        if (secondLast is not 0x01 and not 0x02)
            return length - 1;
        return length - (MaxPrefixLength - 1);
    }

    private int IndexOf(byte[] needle, int within)
    {
        for (var i = 0; i + needle.Length <= within; i++)
        {
            if (_buffer[i] == needle[0] && _buffer[i + 1] == needle[1] && _buffer[i + 2] == needle[2])
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
