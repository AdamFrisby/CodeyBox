using System.IO;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Claude.AcpBridge;

/// <summary>
/// Single-writer JSON line emitter for the bridge's stdout. Mirrors the JS
/// bridge's <c>emit()</c> helper bit-for-bit so the host-side observer keeps
/// parsing every envelope the same way after the language migration.
///
/// <para>The bridge has multiple producers — the stdin pump, the
/// claude-process stdout/stderr readers, the WebSocket frame reader, the
/// turn-deadline timer — so all writes go through a single lock to avoid
/// interleaved bytes on stdout.</para>
/// </summary>
internal static class Emitter
{
    private static readonly object _lock = new();
    private static Stream _stdout = Console.OpenStandardOutput();

    /// <summary>
    /// Test seam — lets unit tests redirect emitter output into an in-memory
    /// stream so envelope shape assertions can run in-process. Production
    /// never calls this; the bridge entrypoint leaves the default
    /// <see cref="Console.OpenStandardOutput"/> in place.
    /// </summary>
    internal static IDisposable OverrideStreamForTests(Stream replacement)
    {
        lock (_lock)
        {
            var previous = _stdout;
            _stdout = replacement;
            return new StreamRestore(previous);
        }
    }

    private sealed class StreamRestore : IDisposable
    {
        private readonly Stream _previous;
        public StreamRestore(Stream previous) => _previous = previous;
        public void Dispose()
        {
            lock (_lock) _stdout = _previous;
        }
    }

    /// <summary>Emit a single envelope with one explicit field.</summary>
    public static void Emit(string type)
    {
        var json = $"{{\"type\":{EncodeString(type)}}}";
        WriteLine(json);
    }

    /// <summary>Emit a single envelope built by the provided writer delegate.</summary>
    public static void Emit(string type, Action<Utf8JsonWriter> writeExtra)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            writeExtra(w);
            w.WriteEndObject();
        }
        WriteLineBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>
    /// Emit an <c>acp_recv</c> envelope wrapping the raw JSON of an inbound
    /// WebSocket frame.
    /// </summary>
    public static void EmitAcpRecv(string rawPayloadJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "acp_recv");
            w.WritePropertyName("payload");
            w.WriteRawValue(rawPayloadJson, skipInputValidation: false);
            w.WriteEndObject();
        }
        WriteLineBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private static void WriteLine(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        lock (_lock)
        {
            try { _stdout.Write(bytes, 0, bytes.Length); _stdout.Flush(); }
            catch { /* stdout closed — host already gone; silent drop. */ }
        }
    }

    private static void WriteLineBytes(ReadOnlySpan<byte> body)
    {
        lock (_lock)
        {
            try
            {
                _stdout.Write(body);
                _stdout.WriteByte((byte)'\n');
                _stdout.Flush();
            }
            catch { /* same as WriteLine: silent drop on closed stdout. */ }
        }
    }

    private static string EncodeString(string value) => JsonSerializeString(value);

    /// <summary>
    /// Minimal AOT-safe JSON string encoder — uses Utf8JsonWriter so escape
    /// rules match the rest of the bridge.
    /// </summary>
    private static string JsonSerializeString(string value)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            w.WriteStringValue(value);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
