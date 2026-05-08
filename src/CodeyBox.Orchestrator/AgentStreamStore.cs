using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public interface IAgentStreamStore
{
    AgentStreamsOptions Options { get; }
    Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default);
    Task<IReadOnlyList<AgentStreamFile>> ListAsync(
        WorkItemId workItemId,
        int limit = AgentStreamStore.DefaultListLimit,
        bool includeLineCount = false,
        CancellationToken ct = default);
    Task<AgentStreamFile?> GetAsync(
        WorkItemId workItemId,
        string fileName,
        bool includeLineCount = false,
        CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default);
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default);
}

public sealed record AgentStreamFile(
    string FileName,
    string Phase,
    int Iteration,
    long SizeBytes,
    long? LineCount,
    DateTimeOffset CapturedAt);

public sealed class AgentStreamStore : IAgentStreamStore
{
    public const int DefaultListLimit = 100;
    public const int MaxListLimit = 500;

    private static readonly char[] InvalidPhaseChars = Path.GetInvalidFileNameChars()
        .Where(c => c != ':')
        .ToArray();

    private readonly ILogger<AgentStreamStore> _log;

    public AgentStreamStore(AgentStreamsOptions options, ILogger<AgentStreamStore> log)
    {
        Options = options;
        _log = log;
    }

    public AgentStreamsOptions Options { get; }

    public Task<AgentStreamCapture?> BeginCaptureAsync(
        WorkItemId workItemId,
        string phase,
        int iteration,
        CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return Task.FromResult<AgentStreamCapture?>(null);

        try
        {
            var safePhase = ValidatePhase(phase);
            if (iteration < 1)
                throw new ArgumentOutOfRangeException(nameof(iteration), "Iteration must be >= 1");

            var dir = GetWorkItemDirectory(workItemId);
            Directory.CreateDirectory(dir);
            var path = ReserveUniqueCapturePath(dir, safePhase, iteration);
            var maxBytes = Options.MaxFileSizeMb * 1024L * 1024L;
            return Task.FromResult<AgentStreamCapture?>(new AgentStreamCapture(path, maxBytes, _log));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex,
                "Failed to start agent stream capture for work item {WorkItemId} phase {Phase}",
                workItemId, phase);
            return Task.FromResult<AgentStreamCapture?>(null);
        }
    }

    public Task<IReadOnlyList<AgentStreamFile>> ListAsync(
        WorkItemId workItemId,
        int limit = DefaultListLimit,
        bool includeLineCount = false,
        CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return Task.FromResult<IReadOnlyList<AgentStreamFile>>([]);

        try
        {
            limit = Math.Clamp(limit, 1, MaxListLimit);
            var dir = GetWorkItemDirectory(workItemId);
            if (!Directory.Exists(dir))
                return Task.FromResult<IReadOnlyList<AgentStreamFile>>([]);

            var result = new List<AgentStreamFile>(limit);
            var visited = 0;
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (visited >= limit)
                    break;
                visited++;

                if (BuildFile(path, includeLineCount, ct) is not { } file)
                    continue;

                result.Add(file);
            }

            return Task.FromResult<IReadOnlyList<AgentStreamFile>>(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Failed to list agent streams for work item {WorkItemId}", workItemId);
            return Task.FromResult<IReadOnlyList<AgentStreamFile>>([]);
        }
    }

    public Task<AgentStreamFile?> GetAsync(
        WorkItemId workItemId,
        string fileName,
        bool includeLineCount = false,
        CancellationToken ct = default)
    {
        if (!Options.Enabled || !IsSafeFileName(fileName) || !TryParseFileName(fileName, out _, out _))
            return Task.FromResult<AgentStreamFile?>(null);

        try
        {
            var path = Path.Combine(GetWorkItemDirectory(workItemId), fileName);
            if (!File.Exists(path))
                return Task.FromResult<AgentStreamFile?>(null);

            return Task.FromResult(BuildFile(path, includeLineCount, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Failed to get agent stream file {FileName} for work item {WorkItemId}", fileName, workItemId);
            return Task.FromResult<AgentStreamFile?>(null);
        }
    }

    public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default)
    {
        if (!Options.Enabled || !IsSafeFileName(fileName) || !TryParseFileName(fileName, out _, out _))
            return Task.FromResult<Stream?>(null);

        var path = Path.Combine(GetWorkItemDirectory(workItemId), fileName);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
        Stream stream = new CappedReadStream(file, Options.MaxFileSizeMb * 1024L * 1024L);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Options.Enabled || Options.RetainedDays == 0)
            return Task.FromResult(0);

        var deleted = 0;
        try
        {
            var root = Options.Path;
            if (!Directory.Exists(root))
                return Task.FromResult(0);

            var cutoff = now.UtcDateTime.AddDays(-Options.RetainedDays);
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Failed to delete expired agent stream file {Path}", file);
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Failed to delete empty agent stream directory {Path}", dir);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Failed during agent stream retention sweep");
        }

        return Task.FromResult(deleted);
    }

    private string GetWorkItemDirectory(WorkItemId workItemId) =>
        Path.Combine(Options.Path, workItemId.ToString());

    private static string ReserveUniqueCapturePath(string dir, string safePhase, int iteration)
    {
        const int MaxAttempts = 32;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var fileName = $"{safePhase}-{iteration}-{ShortId()}.jsonl";
            var path = Path.Combine(dir, fileName);
            try
            {
                using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;
            }
        }

        throw new IOException(
            $"Could not reserve a unique agent stream capture file for phase '{safePhase}' iteration {iteration}");
    }

    private static string ValidatePhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
            throw new ArgumentException("Phase must be non-empty", nameof(phase));
        if (phase.Any(c => InvalidPhaseChars.Contains(c)) || phase.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid agent stream phase '{phase}'", nameof(phase));
        return phase;
    }

    private static string ShortId()
    {
        Span<byte> bytes = stackalloc byte[3];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsSafeFileName(string fileName) =>
        fileName == Path.GetFileName(fileName)
        && !fileName.Contains("..", StringComparison.Ordinal)
        && fileName.EndsWith(".jsonl", StringComparison.Ordinal);

    private static bool TryParseFileName(string fileName, out string phase, out int iteration)
    {
        phase = string.Empty;
        iteration = 0;
        if (!IsSafeFileName(fileName))
            return false;

        var stem = fileName[..^".jsonl".Length];
        var lastDash = stem.LastIndexOf('-');
        if (lastDash <= 0 || lastDash == stem.Length - 1)
            return false;
        var prevDash = stem.LastIndexOf('-', lastDash - 1);
        if (prevDash <= 0)
            return false;
        var id = stem[(lastDash + 1)..];
        if (id.Length != 6 || id.Any(c => !Uri.IsHexDigit(c)))
            return false;
        if (!int.TryParse(stem.AsSpan(prevDash + 1, lastDash - prevDash - 1), out iteration) || iteration < 1)
            return false;
        phase = stem[..prevDash];
        return !string.IsNullOrWhiteSpace(phase);
    }

    private static AgentStreamFile? BuildFile(string path, bool includeLineCount, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!TryParseFileName(info.Name, out var phase, out var iteration))
            return null;

        return new AgentStreamFile(
            info.Name,
            phase,
            iteration,
            info.Length,
            includeLineCount ? CountLines(info.FullName, ct) : null,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero));
    }

    private static long CountLines(string path, CancellationToken ct)
    {
        var count = 0L;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is not null)
        {
            ct.ThrowIfCancellationRequested();
            count++;
        }
        return count;
    }

    private sealed class CappedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _position;

        public CappedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => Math.Min(_inner.Length, _maxBytes);
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _maxBytes)
                return 0;
            var allowed = (int)Math.Min(count, _maxBytes - _position);
            var read = _inner.Read(buffer, offset, allowed);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= _maxBytes)
                return 0;
            var allowed = (int)Math.Min(buffer.Length, _maxBytes - _position);
            var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class AgentStreamCapture : IAsyncDisposable
{
    private const int MaxQueuedChunkChars = 64 * 1024;
    private const int MaxQueuedChunks = 128;
    private const int TruncationMarkerReserveBytes = 128;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly long _directWriteLimitBytes;
    private readonly ILogger _log;
    private readonly Channel<string> _chunks;
    private readonly Task _worker;
    private long _enqueueDroppedBytes;
    private int _enqueueTruncated;
    private int _writerFailed;

    public AgentStreamCapture(string path, long maxBytes, ILogger log)
    {
        _path = path;
        _maxBytes = maxBytes;
        _directWriteLimitBytes = Math.Max(0, maxBytes - TruncationMarkerReserveBytes);
        _log = log;
        _chunks = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxQueuedChunks)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        _worker = Task.Run(ProcessSafelyAsync);
    }

    public string Path => _path;
    public string FileName => System.IO.Path.GetFileName(_path);

    public void WriteChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        try
        {
            if (Volatile.Read(ref _writerFailed) != 0)
            {
                Interlocked.Add(ref _enqueueDroppedBytes, Encoding.UTF8.GetByteCount(chunk));
                return;
            }

            if (Volatile.Read(ref _enqueueTruncated) != 0)
            {
                Interlocked.Add(ref _enqueueDroppedBytes, Encoding.UTF8.GetByteCount(chunk));
                return;
            }

            for (var offset = 0; offset < chunk.Length; offset += MaxQueuedChunkChars)
            {
                var length = Math.Min(MaxQueuedChunkChars, chunk.Length - offset);
                if (!WriteQueuedChunk(chunk.Substring(offset, length)))
                {
                    Interlocked.Add(ref _enqueueDroppedBytes, Encoding.UTF8.GetByteCount(chunk.AsSpan(offset)));
                    break;
                }

                if (Volatile.Read(ref _enqueueTruncated) != 0)
                {
                    var remainingOffset = offset + length;
                    if (remainingOffset < chunk.Length)
                        Interlocked.Add(ref _enqueueDroppedBytes, Encoding.UTF8.GetByteCount(chunk.AsSpan(remainingOffset)));
                    break;
                }
            }
        }
        catch (ChannelClosedException)
        {
            Interlocked.Add(ref _enqueueDroppedBytes, Encoding.UTF8.GetByteCount(chunk));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to enqueue agent stream chunk for {Path}", _path);
        }
    }

    private bool WriteQueuedChunk(string chunk)
    {
        while (true)
        {
            if (_chunks.Writer.TryWrite(chunk))
                return true;

            if (Volatile.Read(ref _writerFailed) != 0 || Volatile.Read(ref _enqueueTruncated) != 0)
                return false;

            try
            {
                if (!_chunks.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
                    return false;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _log.LogWarning(ex, "Failed to enqueue agent stream chunk for {Path}", _path);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to enqueue agent stream chunk for {Path}", _path);
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _chunks.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Agent stream writer failed for {Path}", _path);
        }
    }

    private async Task ProcessSafelyAsync()
    {
        try
        {
            await ProcessAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _writerFailed, 1);
            _chunks.Writer.TryComplete(ex);
            _log.LogWarning(ex, "Agent stream writer failed for {Path}", _path);
        }
    }

    private async Task ProcessAsync()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        await using var file = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(file, new UTF8Encoding(false));

        var buffer = new StringBuilder();
        var bufferBytes = 0L;
        var bytesWritten = file.Length;
        var droppedBytes = 0L;
        var truncated = false;
        var redactor = new AgentStreamLineRedactor();
        var pendingTail = new StringBuilder();
        var pendingTailBytes = 0L;
        var pendingTailRawBytes = 0L;

        await foreach (var chunk in _chunks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            DrainChunk(
                chunk,
                writer,
                buffer,
                redactor,
                pendingTail,
                ref bufferBytes,
                ref pendingTailBytes,
                ref pendingTailRawBytes,
                ref bytesWritten,
                ref droppedBytes,
                ref truncated);
        }

        if (buffer.Length > 0)
        {
            WriteLine(
                buffer.ToString(),
                bufferBytes,
                writer,
                redactor,
                pendingTail,
                ref pendingTailBytes,
                ref pendingTailRawBytes,
                ref bytesWritten,
                ref droppedBytes,
                ref truncated);
            buffer.Clear();
            bufferBytes = 0;
        }

        var enqueueDroppedBytes = Interlocked.Read(ref _enqueueDroppedBytes);
        if (enqueueDroppedBytes > 0)
        {
            truncated = true;
            droppedBytes += pendingTailRawBytes + enqueueDroppedBytes;
        }

        if (truncated)
        {
            pendingTail.Clear();
            pendingTailBytes = 0;
            pendingTailRawBytes = 0;
            WriteTruncationMarker(writer, droppedBytes, ref bytesWritten);
        }
        else if (pendingTail.Length > 0)
        {
            writer.Write(pendingTail.ToString());
            bytesWritten += pendingTailBytes;
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    private void DrainChunk(
        string chunk,
        StreamWriter writer,
        StringBuilder buffer,
        AgentStreamLineRedactor redactor,
        StringBuilder pendingTail,
        ref long bufferBytes,
        ref long pendingTailBytes,
        ref long pendingTailRawBytes,
        ref long bytesWritten,
        ref long droppedBytes,
        ref bool truncated)
    {
        if (truncated)
        {
            droppedBytes += Encoding.UTF8.GetByteCount(chunk);
            return;
        }

        for (var i = 0; i < chunk.Length; i++)
        {
            var ch = chunk[i];
            if (ch == '\n')
            {
                WriteLine(
                    buffer.ToString(),
                    bufferBytes + 1,
                    writer,
                    redactor,
                    pendingTail,
                    ref pendingTailBytes,
                    ref pendingTailRawBytes,
                    ref bytesWritten,
                    ref droppedBytes,
                    ref truncated);
                buffer.Clear();
                bufferBytes = 0;
                if (truncated && i + 1 < chunk.Length)
                    droppedBytes += Encoding.UTF8.GetByteCount(chunk.AsSpan(i + 1));
                if (truncated)
                    return;
                continue;
            }

            if (ch == '\r')
                continue;

            var chBytes = Encoding.UTF8.GetByteCount(chunk.AsSpan(i, 1));
            if (bytesWritten + pendingTailBytes + bufferBytes + chBytes + 1 > _maxBytes)
            {
                truncated = true;
                Interlocked.Exchange(ref _enqueueTruncated, 1);
                droppedBytes += pendingTailRawBytes + bufferBytes + Encoding.UTF8.GetByteCount(chunk.AsSpan(i));
                pendingTail.Clear();
                pendingTailBytes = 0;
                pendingTailRawBytes = 0;
                buffer.Clear();
                bufferBytes = 0;
                return;
            }

            buffer.Append(ch);
            bufferBytes += chBytes;
        }
    }

    private void WriteLine(
        string line,
        long rawLineBytes,
        StreamWriter writer,
        AgentStreamLineRedactor redactor,
        StringBuilder pendingTail,
        ref long pendingTailBytes,
        ref long pendingTailRawBytes,
        ref long bytesWritten,
        ref long droppedBytes,
        ref bool truncated)
    {
        var redacted = redactor.RedactLine(line);
        var lineBytes = Encoding.UTF8.GetByteCount(redacted) + 1;
        if (truncated || bytesWritten + pendingTailBytes + lineBytes > _maxBytes)
        {
            truncated = true;
            Interlocked.Exchange(ref _enqueueTruncated, 1);
            droppedBytes += pendingTailRawBytes + rawLineBytes;
            pendingTail.Clear();
            pendingTailBytes = 0;
            pendingTailRawBytes = 0;
            return;
        }

        if (bytesWritten + pendingTailBytes + lineBytes <= _directWriteLimitBytes)
        {
            if (pendingTail.Length > 0)
            {
                writer.Write(pendingTail.ToString());
                bytesWritten += pendingTailBytes;
                pendingTail.Clear();
                pendingTailBytes = 0;
                pendingTailRawBytes = 0;
            }

            writer.WriteLine(redacted);
            writer.Flush();
            bytesWritten += lineBytes;
            return;
        }

        pendingTail.Append(redacted);
        pendingTail.Append('\n');
        pendingTailBytes += lineBytes;
        pendingTailRawBytes += rawLineBytes;
    }

    private void WriteTruncationMarker(StreamWriter writer, long droppedBytes, ref long bytesWritten)
    {
        var marker = $"[...truncated by {droppedBytes} bytes]";
        var markerBytes = Encoding.UTF8.GetByteCount(marker) + 1;
        if (bytesWritten + markerBytes > _maxBytes)
        {
            _log.LogWarning(
                "Agent stream truncation marker for {Path} did not fit within the configured size cap",
                _path);
            return;
        }

        writer.WriteLine(marker);
        bytesWritten += markerBytes;
    }

    private sealed class AgentStreamLineRedactor
    {
        private const string PrivateKeyBeginPattern = "-----BEGIN ";
        private const string PrivateKeyEndPattern = "-----END ";
        private static readonly HashSet<string> JsonEnvelopeStringProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "type",
            "role",
            "id",
            "event",
            "name",
            "phase",
            "iteration",
            "timestamp",
        };

        private bool _insidePrivateKey;

        public string RedactLine(string line)
        {
            var startsInsidePrivateKey = _insidePrivateKey;
            var containsPrivateKeyBegin = ContainsPrivateKeyBegin(line);
            var containsPrivateKeyEnd = ContainsPrivateKeyEnd(line);

            if ((startsInsidePrivateKey || containsPrivateKeyBegin || containsPrivateKeyEnd)
                && TryRedactJsonPrivateKeyLine(
                    line,
                    startsInsidePrivateKey,
                    out var jsonLine,
                    out var jsonPrivateKeyBegin,
                    out var jsonPrivateKeyEnd))
            {
                UpdatePrivateKeyState(startsInsidePrivateKey, jsonPrivateKeyBegin, jsonPrivateKeyEnd);
                return SensitiveDataRedactionEnricher.RedactText(jsonLine);
            }

            if (_insidePrivateKey)
            {
                if (containsPrivateKeyEnd)
                    _insidePrivateKey = false;
                return "***";
            }

            if (containsPrivateKeyBegin && !containsPrivateKeyEnd)
            {
                _insidePrivateKey = true;
                return "***";
            }

            return SensitiveDataRedactionEnricher.RedactText(line);
        }

        private void UpdatePrivateKeyState(bool startsInsidePrivateKey, bool containsBegin, bool containsEnd)
        {
            if (startsInsidePrivateKey)
            {
                _insidePrivateKey = !containsEnd;
                return;
            }

            _insidePrivateKey = containsBegin && !containsEnd;
        }

        private static bool TryRedactJsonPrivateKeyLine(
            string line,
            bool startsInsidePrivateKey,
            out string redacted,
            out bool containsBegin,
            out bool containsEnd)
        {
            redacted = line;
            containsBegin = false;
            containsEnd = false;

            try
            {
                using var document = JsonDocument.Parse(line);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    var privateKeyBodyActive = startsInsidePrivateKey;
                    WriteRedactedJsonElement(
                        document.RootElement,
                        writer,
                        propertyName: null,
                        ref privateKeyBodyActive,
                        ref containsBegin,
                        ref containsEnd);
                }

                redacted = Encoding.UTF8.GetString(stream.ToArray());
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static void WriteRedactedJsonElement(
            JsonElement element,
            Utf8JsonWriter writer,
            string? propertyName,
            ref bool privateKeyBodyActive,
            ref bool containsBegin,
            ref bool containsEnd)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteRedactedJsonElement(
                            property.Value,
                            writer,
                            property.Name,
                            ref privateKeyBodyActive,
                            ref containsBegin,
                            ref containsEnd);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteRedactedJsonElement(
                            item,
                            writer,
                            propertyName: null,
                            ref privateKeyBodyActive,
                            ref containsBegin,
                            ref containsEnd);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    WriteRedactedJsonString(
                        element,
                        writer,
                        propertyName,
                        ref privateKeyBodyActive,
                        ref containsBegin,
                        ref containsEnd);
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static void WriteRedactedJsonString(
            JsonElement element,
            Utf8JsonWriter writer,
            string? propertyName,
            ref bool privateKeyBodyActive,
            ref bool containsBegin,
            ref bool containsEnd)
        {
            var value = element.GetString() ?? string.Empty;
            var beginsHere = ContainsPrivateKeyBegin(value);
            var endsHere = ContainsPrivateKeyEnd(value);
            containsBegin |= beginsHere;
            containsEnd |= endsHere;

            var preserveEnvelope = propertyName is not null && JsonEnvelopeStringProperties.Contains(propertyName);
            var redact = beginsHere || (!preserveEnvelope && privateKeyBodyActive);

            writer.WriteStringValue(redact ? "***" : value);

            if (privateKeyBodyActive && endsHere)
                privateKeyBodyActive = false;
            else if (!privateKeyBodyActive && beginsHere && !endsHere)
                privateKeyBodyActive = true;
        }

        private static bool ContainsPrivateKeyBegin(string line) =>
            line.Contains(PrivateKeyBeginPattern, StringComparison.Ordinal)
            && line.Contains("PRIVATE KEY-----", StringComparison.Ordinal);

        private static bool ContainsPrivateKeyEnd(string line) =>
            line.Contains(PrivateKeyEndPattern, StringComparison.Ordinal)
            && line.Contains("PRIVATE KEY-----", StringComparison.Ordinal);
    }
}
