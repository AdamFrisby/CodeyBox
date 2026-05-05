namespace CodeyBox.Core;

/// <summary>
/// Thread-safe in-memory ring buffer for agent stdout chunks.
/// Caps at <see cref="CapacityBytes"/>; oldest bytes are evicted when full.
/// Used to provide late-joining dashboard clients with the recent tail of a
/// running agent's output without replaying from the audit log.
/// In-memory only — orchestrator restart loses it.
/// </summary>
public sealed class StdoutRingBuffer
{
    public const int CapacityBytes = 16 * 1024;

    private readonly object _lock = new();
    private readonly char[] _buf = new char[CapacityBytes];
    private int _start;
    private int _length;

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        lock (_lock)
        {
            foreach (var ch in chunk)
            {
                _buf[(_start + _length) % CapacityBytes] = ch;
                if (_length < CapacityBytes)
                    _length++;
                else
                    _start = (_start + 1) % CapacityBytes;
            }
        }
    }

    public string GetContents()
    {
        lock (_lock)
        {
            if (_length == 0) return string.Empty;
            var chars = new char[_length];
            for (var i = 0; i < _length; i++)
                chars[i] = _buf[(_start + i) % CapacityBytes];
            return new string(chars);
        }
    }
}
