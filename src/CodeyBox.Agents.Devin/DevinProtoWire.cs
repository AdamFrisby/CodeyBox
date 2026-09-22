namespace CodeyBox.Agents.Devin;

/// <summary>
/// Minimal protobuf wire-format reader/writer for the Devin Connect-RPC
/// surface. The SeatManagementService endpoint speaks
/// <c>application/proto</c> only (verified 2026-09-21 against
/// <c>server.codeium.com</c>: <c>application/json</c> is rejected with
/// <c>invalid_argument</c>), and shipping a generated .proto client for one
/// RPC is disproportionate — the request is a fixed metadata envelope and
/// the response needs only a handful of field numbers.
///
/// <para>Parsing is deliberately lenient: unknown fields are skipped, and a
/// field may carry a varint, a length-delimited payload, or fixed-width
/// scalars. Malformed or truncated input throws
/// <see cref="ProtoWireFormatException"/> rather than yielding partial
/// reads.</para>
/// </summary>
internal static class DevinProtoWire
{
    public sealed class ProtoWireFormatException : Exception
    {
        public ProtoWireFormatException(string message) : base(message) { }
    }

    /// <summary>A decoded message: field number → values (ulong for varints, byte[] for length-delimited).</summary>
    public sealed class Message
    {
        private readonly Dictionary<int, List<object>> _fields = new();

        /// <summary>Last varint value seen for <paramref name="field"/>, or null. Preset (-1) int64 sentinels decode as <see cref="ulong.MaxValue"/>.</summary>
        public ulong? TryGetVarint(int field)
        {
            if (!_fields.TryGetValue(field, out var values)) return null;
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i] is ulong v) return v;
            }
            return null;
        }

        /// <summary>Last length-delimited value decoded as a nested message, or null when absent/not a message.</summary>
        public Message? TryGetMessage(int field)
        {
            if (!_fields.TryGetValue(field, out var values)) return null;
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i] is byte[] bytes)
                {
                    try { return Parse(bytes); }
                    catch (ProtoWireFormatException) { /* field carries a string/blob, not a message */ }
                }
            }
            return null;
        }

        /// <summary>Last length-delimited value decoded as UTF-8, or null when absent/not valid UTF-8 text.</summary>
        public string? TryGetString(int field)
        {
            if (!_fields.TryGetValue(field, out var values)) return null;
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i] is byte[] bytes)
                {
                    try
                    {
                        // Strict decoding: a binary payload misread as text
                        // throws rather than producing replacement chars.
                        return StrictUtf8.GetString(bytes);
                    }
                    catch (System.Text.DecoderFallbackException) { /* fallthrough */ }
                }
            }
            return null;
        }

        private static readonly System.Text.UTF8Encoding StrictUtf8 =
            new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        internal void Add(int field, object value)
        {
            if (!_fields.TryGetValue(field, out var list))
                _fields[field] = list = new List<object>(1);
            list.Add(value);
        }
    }

    /// <summary>Parses a protobuf message. Throws <see cref="ProtoWireFormatException"/> on truncation or an unknown wire type.</summary>
    public static Message Parse(ReadOnlySpan<byte> buffer)
    {
        var message = new Message();
        var pos = 0;
        while (pos < buffer.Length)
        {
            var tag = ReadVarint(buffer, ref pos);
            var field = checked((int)(tag >> 3));
            if (field <= 0)
                throw new ProtoWireFormatException($"invalid field number {field}");
            switch (tag & 7)
            {
                case 0:
                    message.Add(field, ReadVarint(buffer, ref pos));
                    break;
                case 1:
                    Require(buffer, pos, 8); pos += 8; break;
                case 2:
                    var len = checked((int)ReadVarint(buffer, ref pos));
                    Require(buffer, pos, len);
                    message.Add(field, buffer.Slice(pos, len).ToArray());
                    pos += len;
                    break;
                case 5:
                    Require(buffer, pos, 4); pos += 4; break;
                default:
                    // Groups (wt 3/4) are not produced by this service.
                    throw new ProtoWireFormatException($"unsupported wire type {tag & 7} on field {field}");
            }
        }
        return message;
    }

    /// <summary>Encodes a varint.</summary>
    public static void WriteVarint(System.IO.Stream stream, ulong value)
    {
        while (value > 0x7f)
        {
            stream.WriteByte((byte)(value & 0x7f | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    /// <summary>Encodes a length-delimited field (string, bytes, or nested message).</summary>
    public static void WriteField(System.IO.Stream stream, int field, ReadOnlySpan<byte> payload)
    {
        WriteVarint(stream, (ulong)field << 3 | 2);
        WriteVarint(stream, (ulong)payload.Length);
        stream.Write(payload);
    }

    public static void WriteField(System.IO.Stream stream, int field, string value)
        => WriteField(stream, field, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Serialises a nested-message field.</summary>
    public static void WriteField(System.IO.Stream stream, int field, MessageWriter child)
        => WriteField(stream, field, child.ToArray());

    /// <summary>Fluent builder for an outgoing message.</summary>
    public sealed class MessageWriter
    {
        private readonly System.IO.MemoryStream _stream = new();

        public MessageWriter Field(int field, string value)
        {
            WriteField(_stream, field, value);
            return this;
        }

        /// <summary>Encodes a varint field.</summary>
        public MessageWriter Field(int field, ulong value)
        {
            WriteVarint(_stream, (ulong)field << 3);
            WriteVarint(_stream, value);
            return this;
        }

        public MessageWriter Field(int field, MessageWriter child)
        {
            WriteField(_stream, field, child);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> buffer, ref int pos)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= buffer.Length)
                throw new ProtoWireFormatException("truncated varint");
            var b = buffer[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift >= 64)
                throw new ProtoWireFormatException("varint overflow");
        }
    }

    private static void Require(ReadOnlySpan<byte> buffer, int pos, int count)
    {
        if (buffer.Length - pos < count)
            throw new ProtoWireFormatException("truncated field");
    }
}
