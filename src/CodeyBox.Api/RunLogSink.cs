using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace CodeyBox.Api;

/// <summary>
/// Append-only run-log sink for the orchestrator's plain-text console stream
/// (the same lines Serilog writes to stdout). It replaces the historical
/// unbounded <c>&gt;&gt; codeybox-orchestrator.run.log</c> shell redirect and the
/// startup-pinned Serilog file sink with a writer that stays correct for the
/// lifetime of the process:
/// <list type="bullet">
/// <item>Every line carries a full-date UTC timestamp
/// (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>), so a time-based grep never matches lines
/// from an earlier day.</item>
/// <item>One physical line per record. The rendered message and exception
/// text carry agent stdout, provider error bodies, branch names and
/// repository content, so control characters (CR, LF, the ANSI escape
/// introducer and other C0/C1 controls) are backslash-escaped before the
/// line is written. An embedded newline can therefore never split a record
/// or defeat the one-line-per-record framing, and terminal escape sequences
/// cannot reach an operator tailing the file.</item>
/// <item>Size-based rotation with a retained-file count. Both knobs
/// (<see cref="ConsoleLogOptions.MaxFileSizeBytes"/>,
/// <see cref="ConsoleLogOptions.RetainedFileCountLimit"/>) are re-read from
/// the supplied accessor on every write, so operator edits hot-reload without
/// a restart. No single record can exceed
/// <see cref="ConsoleLogOptions.MaxFileSizeBytes"/>: a record that would not
/// fit is truncated to the cap (with a <c>[truncated]</c> marker) before it is
/// written, so every file the sink creates is at most
/// <c>MaxFileSizeBytes</c> long and the total on-disk footprint stays bounded
/// by <c>RetainedFileCountLimit x MaxFileSizeBytes</c> regardless of write
/// rate. Retention evicts the oldest sealed segments first, by file count and
/// by bytes, so lowering the knobs (or inheriting oversized segments written
/// under older settings) converges back under the bound as segments age out;
/// a pre-existing oversized active file is sealed on the next write and then
/// ages out by the same rules.</item>
/// <item>Rotation happens under a lock while the file is held open for append
/// (<c>FileShare.Read</c> so <c>tail</c>/<c>grep</c> work concurrently). Lines
/// are flushed before any rename, so rotation neither loses nor duplicates
/// lines and never requires a restart.</item>
/// </list>
/// File layout for a dated template such as
/// <c>logs/codeybox-console-.log</c> (the trailing <c>-</c> marks where the
/// UTC date goes, matching the previous Serilog convention):
/// <c>codeybox-console-20260908.log</c> is the active file and
/// <c>codeybox-console-20260908_001.log</c>, <c>_002</c>, … are the sealed
/// size segments for that day. A new UTC day opens a new dated active file.
/// Retention is enforced across all dates and segments: the newest sealed
/// files are kept next to the active one while they fit both the file-count
/// budget (<c>RetainedFileCountLimit - 1</c> files) and the byte budget
/// (<c>(RetainedFileCountLimit - 1) x MaxFileSizeBytes</c> bytes). Emit never
/// throws; I/O failures are reported to Serilog's SelfLog and
/// retried on the next event.
/// </summary>
internal sealed class RunLogSink : ILogEventSink, IDisposable
{
    private const string DateFormat = "yyyyMMdd";
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff";

    private readonly string _directory;
    private readonly string _stem;
    private readonly string _extension;
    private readonly bool _dated;
    private readonly Func<ConsoleLogOptions> _readOptions;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private FileStream? _stream;
    private string? _activePath;
    private string _activeDate = "";
    private long _activeSize;
    private int _nextSequence = 1;
    private bool _disposed;

    /// <param name="pathTemplate">
    /// Destination template, e.g. <c>logs/codeybox-console-.log</c>. A file
    /// name ending in <c>-</c> before the extension takes the UTC date
    /// (<c>yyyyMMdd</c>); any other path is used literally as the active file.
    /// Relative paths resolve from the process working directory.
    /// </param>
    /// <param name="readOptions">
    /// Live accessor for the rotation knobs. Called on every write so both
    /// values hot-reload; values are clamped to a minimum of 1.
    /// </param>
    /// <param name="utcNow">Clock for the line timestamps and day boundary. Defaults to UTC now.</param>
    public RunLogSink(
        string pathTemplate,
        Func<ConsoleLogOptions> readOptions,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathTemplate);
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        var fullPath = Path.GetFullPath(pathTemplate);
        _directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);
        _extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        _dated = nameWithoutExtension.EndsWith('-');
        _stem = _dated ? nameWithoutExtension[..^1] : nameWithoutExtension;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null) return;
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                var options = ReadClampedOptions();
                var today = _utcNow().UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
                if (!EnsureActive(today, options.RetainedCount, options.MaxSizeBytes)) return;

                // Derive the per-line byte budget from the live size knob
                // BEFORE assembling the line, so an over-long message is
                // truncated during rendering and can never materialize as an
                // unbounded string or an oversized file segment.
                var line = FormatLine(logEvent, options.MaxSizeBytes);
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_activeSize > 0 && _activeSize + bytes > options.MaxSizeBytes)
                    RollSize(today, options.RetainedCount, options.MaxSizeBytes);

                // RollSize re-opens the active file; if the disk went away in
                // between, drop this line and retry on the next event.
                if (_writer is null) return;
                _writer.Write(line);
                _writer.Flush();
                _activeSize += bytes;
            }
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: failed to write run-log line: {0}", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseActive();
        }
    }

    private (long MaxSizeBytes, int RetainedCount) ReadClampedOptions()
    {
        var options = _readOptions();
        return (
            Math.Max(1, options.MaxFileSizeBytes),
            Math.Max(1, options.RetainedFileCountLimit));
    }

    private bool EnsureActive(string today, int retainedCount, long maxSizeBytes)
    {
        if (_writer is not null && string.Equals(_activeDate, today, StringComparison.Ordinal))
            return true;

        CloseActive();
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: cannot create run-log directory '{0}': {1}", _directory, ex.Message);
            return false;
        }

        _activeDate = today;
        _activePath = ActivePathFor(today);
        _nextSequence = DiscoverNextSequence(today);
        try
        {
            _stream = new FileStream(_activePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            _activeSize = _stream.Length;
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: cannot open run-log file '{0}': {1}", _activePath, ex.Message);
            CloseActive();
            return false;
        }

        EnforceRetention(retainedCount, maxSizeBytes);
        return true;
    }

    private void RollSize(string today, int retainedCount, long maxSizeBytes)
    {
        CloseActive();
        var segment = SegmentPathFor(today, _nextSequence);
        try
        {
            if (_activePath is not null && File.Exists(_activePath))
            {
                File.Move(_activePath, segment, overwrite: false);
                _nextSequence++;
            }
        }
        catch (Exception ex)
        {
            // The sealed segment may already exist after a crash between
            // rename and counter bump; bump past it and retry once rather
            // than dropping the line.
            SelfLog.WriteLine("RunLogSink: cannot seal run-log segment '{0}': {1}", segment, ex.Message);
            _nextSequence = DiscoverNextSequence(today);
            try
            {
                if (_activePath is not null && File.Exists(_activePath))
                {
                    File.Move(_activePath, SegmentPathFor(today, _nextSequence), overwrite: false);
                    _nextSequence++;
                }
            }
            catch (Exception retryEx)
            {
                SelfLog.WriteLine("RunLogSink: retry sealing run-log segment failed: {0}", retryEx.Message);
            }
        }

        _activeDate = today;
        _activePath = ActivePathFor(today);
        try
        {
            Directory.CreateDirectory(_directory);
            _stream = new FileStream(_activePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            _activeSize = 0;
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: cannot re-open run-log file '{0}': {1}", _activePath, ex.Message);
            CloseActive();
            return;
        }

        EnforceRetention(retainedCount, maxSizeBytes);
    }

    private void EnforceRetention(int retainedCount, long maxSizeBytes)
    {
        string? active = _activePath;
        List<string> sealedFiles;
        try
        {
            var prefix = _dated ? _stem + "-" : _stem + "_";
            sealedFiles = Directory.EnumerateFiles(_directory)
                .Where(f => f.EndsWith(_extension, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal)
                    && !string.Equals(f, active, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: cannot enumerate run-log directory '{0}': {1}", _directory, ex.Message);
            return;
        }

        // Oldest first. Keep only the newest sealed files that fit both the
        // file-count budget (retainedCount - 1 next to the active file) and
        // the byte budget ((retainedCount - 1) x maxSizeBytes). The count rule
        // alone would retain oversized segments written under older settings
        // (or before a knob shrink) without bound; the byte rule evicts those
        // first so the total footprint converges back under the documented
        // ceiling. Segments written under the current settings never exceed
        // maxSizeBytes (records are truncated to the cap before writing), so
        // the byte rule is a no-op for them.
        var maxSealedCount = retainedCount - 1;
        var maxSealedBytes = maxSealedCount <= 0
            ? 0
            : maxSizeBytes > long.MaxValue / maxSealedCount
                ? long.MaxValue
                : (long)maxSealedCount * maxSizeBytes;
        var keep = new bool[sealedFiles.Count];
        var keptBytes = 0L;
        var keptCount = 0;
        for (var i = sealedFiles.Count - 1; i >= 0; i--)
        {
            long size;
            try
            {
                size = new FileInfo(sealedFiles[i]).Length;
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("RunLogSink: cannot stat run-log file '{0}': {1}", sealedFiles[i], ex.Message);
                size = 0;
            }

            if (keptCount < maxSealedCount && size <= maxSealedBytes - keptBytes)
            {
                keep[i] = true;
                keptCount++;
                keptBytes += size;
            }
        }

        for (var i = 0; i < sealedFiles.Count; i++)
        {
            if (keep[i]) continue;
            try
            {
                File.Delete(sealedFiles[i]);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("RunLogSink: cannot delete superseded run-log file '{0}': {1}", sealedFiles[i], ex.Message);
            }
        }
    }

    private string ActivePathFor(string today) =>
        _dated
            ? Path.Combine(_directory, $"{_stem}-{today}{_extension}")
            : Path.Combine(_directory, $"{_stem}{_extension}");

    private string SegmentPathFor(string today, int sequence) =>
        _dated
            ? Path.Combine(_directory, $"{_stem}-{today}_{sequence:000}{_extension}")
            : Path.Combine(_directory, $"{_stem}_{sequence:000}{_extension}");

    private int DiscoverNextSequence(string today)
    {
        try
        {
            var prefix = _dated ? $"{_stem}-{today}_" : $"{_stem}_";
            var max = 0;
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + _extension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var tail = name[prefix.Length..];
                if (int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > max)
                    max = seq;
            }

            return max + 1;
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: cannot scan run-log segments in '{0}': {1}", _directory, ex.Message);
            return 1;
        }
    }

    private void CloseActive()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("RunLogSink: flush before close failed: {0}", ex.Message);
        }

        _writer?.Dispose();
        _writer = null;
        _stream?.Dispose();
        _stream = null;
        _activeSize = 0;
    }

    /// <summary>
    /// Renders one log event as a single physical line whose UTF-8 encoding is
    /// at most <paramref name="maxLineBytes"/> long, including the trailing LF.
    /// CR, LF, the ANSI escape introducer and every other C0/C1 control (plus
    /// DEL) in the message and exception text are backslash-escaped, so one
    /// record always occupies one line and terminal escapes cannot reach a
    /// tailing terminal. Payload beyond the budget is dropped and replaced
    /// with a <c>[truncated]</c> marker when there is room for one; the output
    /// is therefore byte-exact within budget even for multi-byte text.
    /// </summary>
    private string FormatLine(LogEvent logEvent, long maxLineBytes)
    {
        // int.MaxValue keeps the builder/byte accounting well clear of long
        // overflow and bounds transient memory by the configured cap.
        var budget = Math.Min(Math.Max(1, maxLineBytes), int.MaxValue);
        var capacity = budget - 1; // reserve one byte for the trailing LF
        var timestamp = logEvent.Timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        // Timestamp and level tokens are sink-generated from fixed alphabets,
        // so they need truncating against the budget but no sanitizing.
        var prefix = $"[{timestamp}Z {LevelToken(logEvent.Level)}] ";
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        var exceptionText = logEvent.Exception?.ToString();

        var (body, truncated) = BuildBody(prefix, message, exceptionText, capacity);
        if (truncated && capacity > TruncationMarker.Length)
        {
            (body, _) = BuildBody(prefix, message, exceptionText, capacity - TruncationMarker.Length);
            body += TruncationMarker;
        }

        return body + "\n";
    }

    private const string TruncationMarker = "[truncated]";
    private const string ExceptionSeparator = " | ";

    private static (string Body, bool Truncated) BuildBody(
        string prefix, string message, string? exceptionText, long capacity)
    {
        var builder = new StringBuilder((int)Math.Min(Math.Max(capacity, 0), 512));
        var used = AppendRaw(builder, prefix, capacity, 0);
        var truncated = (long)prefix.Length > used;
        (used, var messageTruncated) = AppendSanitized(builder, message, capacity, used);
        truncated |= messageTruncated;

        if (exceptionText is { Length: > 0 })
        {
            if (used + ExceptionSeparator.Length <= capacity)
            {
                builder.Append(ExceptionSeparator);
                used += ExceptionSeparator.Length;
                (used, var exceptionTruncated) = AppendSanitized(builder, exceptionText, capacity, used);
                truncated |= exceptionTruncated;
            }
            else
            {
                truncated = true;
            }
        }

        return (builder.ToString(), truncated);
    }

    private static long AppendRaw(StringBuilder builder, string chunk, long limit, long used)
    {
        var room = limit - used;
        if (room <= 0 || chunk.Length == 0) return used;
        var take = (int)Math.Min(chunk.Length, room);
        builder.Append(chunk, 0, take);
        return used + take;
    }

    private static (long Used, bool Truncated) AppendSanitized(
        StringBuilder builder, string source, long limit, long used)
    {
        foreach (var rune in source.EnumerateRunes())
        {
            string? escaped;
            long bytes;
            if (rune.Value == '\r')
            {
                escaped = "\\r";
                bytes = escaped.Length;
            }
            else if (rune.Value == '\n')
            {
                escaped = "\\n";
                bytes = escaped.Length;
            }
            else if (rune.Value == '\t')
            {
                escaped = "\\t";
                bytes = escaped.Length;
            }
            else if (rune.Value is < 0x20 or 0x7F or (>= 0x80 and <= 0x9F))
            {
                escaped = $"\\u{rune.Value:x4}";
                bytes = escaped.Length;
            }
            else
            {
                escaped = null;
                bytes = rune.Utf8SequenceLength;
            }

            if (used + bytes > limit)
                return (used, true);
            if (escaped is not null)
                builder.Append(escaped);
            else if (rune.IsBmp)
                builder.Append((char)rune.Value);
            else
                builder.Append(rune.ToString());
            used += bytes;
        }

        return (used, false);
    }

    private static string LevelToken(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "INF",
    };
}
