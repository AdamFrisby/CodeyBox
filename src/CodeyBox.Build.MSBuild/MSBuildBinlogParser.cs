using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using CodeyBox.Core;
using StructuredLog = Microsoft.Build.Logging.StructuredLogger;

namespace CodeyBox.Build.MSBuild;

/// <summary>
/// Parses MSBuild binary logs captured from the isolated build execution
/// path into language-neutral <see cref="BuildDiagnosticsEvidence"/> using
/// the pinned MSBuild.StructuredLogger reader (see
/// <see cref="MSBuildDiagnosticsOptions"/> for the version pin).
///
/// Security posture (least data):
/// <list type="bullet">
/// <item>Never executes embedded commands or project content: the reader only
/// deserialises the event stream into an in-memory tree inside this process;
/// no MSBuild evaluation, task execution, or file write occurs.</item>
/// <item>Never retains command lines (<c>Task.CommandLineArguments</c>),
/// environment blocks, or embedded project imports — only error/warning
/// records plus their project/target ancestry.</item>
/// <item>Every retained string is redacted
/// (<see cref="RawOutputRedactor"/>) and truncated before it reaches
/// evidence.</item>
/// </list>
/// Failure attribution is data, never instructions: returned diagnostics
/// describe the failure; callers must not execute anything they contain.
/// </summary>
public static class MSBuildBinlogParser
{
    private const int MaxPathChars = 260;
    private const int MaxProjectChars = 260;
    private const int MaxTargetChars = 128;

    /// <summary>
    /// Parses one binlog payload. Never throws for bad input: malformed,
    /// truncated, oversized, or unsupported logs yield
    /// <see cref="BuildDiagnosticsStatus.InsufficientDiagnostics"/> so the
    /// authoritative build outcome always stands. Caller cancellation
    /// (<paramref name="ct"/>) propagates; an expired internal
    /// <see cref="MSBuildDiagnosticsOptions.ParseTimeout"/> yields
    /// insufficient-diagnostics instead.
    /// </summary>
    public static async Task<BuildDiagnosticsEvidence> ParseAsync(
        byte[] binlogBytes,
        BuildDiagnosticsSourceBinding binding,
        MSBuildDiagnosticsOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ParseTimeout <= TimeSpan.Zero)
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding, "parse-timeout: no parse budget was configured");

        if (binlogBytes is null || binlogBytes.Length == 0)
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding, "missing: no binary log bytes were captured");

        if (binlogBytes.Length > options.MaxBinlogBytes)
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding,
                $"oversized: {binlogBytes.Length} bytes exceeds the {options.MaxBinlogBytes}-byte bound");

        if (binlogBytes.Length < 2 || binlogBytes[0] != 0x1F || binlogBytes[1] != 0x8B)
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding, "malformed: payload is not a gzip-framed MSBuild binary log");

        using var timeoutCts = new CancellationTokenSource(options.ParseTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var progress = new StructuredLog.Progress { CancellationToken = linkedCts.Token };
        var settings = new StructuredLog.ReaderSettings { UnknownDataBehavior = StructuredLog.UnknownDataBehavior.ThrowException };

        StructuredLog.Build build;
        try
        {
            // CPU-bound work on a worker thread; the await observes it (no
            // fire-and-forget) and the reader's progress token carries both
            // caller cancellation and the parse budget. The gzip integrity
            // pre-check runs inside the same budget: the pinned reader
            // tolerates truncated streams by returning partial data, so a
            // truncated log must be rejected BEFORE it can become a
            // partial-data "pass".
            build = await Task.Run(
                () =>
                {
                    var integrityFailure = CheckGzipIntegrity(binlogBytes, options);
                    if (integrityFailure is not null)
                        throw new BinlogIntegrityException(integrityFailure);
                    using var stream = new MemoryStream(binlogBytes, writable: false);
                    return StructuredLog.BinaryLog.ReadBuild(stream, progress, null, settings);
                },
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding,
                $"parse-timeout: parsing exceeded {options.ParseTimeout.TotalSeconds:0.##}s");
        }
        catch (BinlogIntegrityException ex)
        {
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding, ex.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Narrow-by-exclusion with justification: enrichment is
            // best-effort and must never mask the authoritative build
            // outcome, so any reader failure (truncated gzip, corrupt
            // records, future schema rejected by ThrowException) becomes
            // explicit insufficient-diagnostics. The message is redacted and
            // single-lined so reader internals cannot leak paths or secrets.
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding,
                $"parser-failure: {SingleLine(RawOutputRedactor.Redact(ex.GetType().Name + ": " + ex.Message))}");
        }

        if (!options.IsSupportedVersion(build.FileFormatVersion))
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding,
                $"unsupported-version: file format v{build.FileFormatVersion} is outside the supported range " +
                $"v{options.MinSupportedFileFormatVersion}-v{options.MaxSupportedFileFormatVersion} " +
                $"(pinned reader {MSBuildDiagnosticsOptions.PayloadFormat})");

        return ExtractDiagnostics(build, binding, options);
    }

    private static BuildDiagnosticsEvidence ExtractDiagnostics(
        StructuredLog.Build build,
        BuildDiagnosticsSourceBinding binding,
        MSBuildDiagnosticsOptions options)
    {
        var errors = new List<ExtractedDiagnostic>();
        var warnings = new List<ExtractedDiagnostic>();
        var truncated = false;
        var visited = 0;
        var sequence = 0;

        var stack = new Stack<(StructuredLog.TreeNode Node, int Depth)>();
        stack.Push((build, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (++visited > options.MaxNodesVisited)
            {
                truncated = true;
                break;
            }
            if (depth > options.MaxDepth)
            {
                truncated = true;
                continue;
            }

            if (node is StructuredLog.AbstractDiagnostic diagnostic)
            {
                var record = ToRecord(diagnostic, $"msbuild:d{sequence++:D4}", options);
                if (record is not null)
                    (record.Record.Severity == BuildDiagnosticSeverity.Error ? errors : warnings).Add(record);
            }

            var children = node.Children;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                if (children[i] is StructuredLog.TreeNode child)
                    stack.Push((child, depth + 1));
            }
        }

        if (errors.Count == 0 && warnings.Count == 0)
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding,
                "no-diagnostics-found: the log parsed but contained no error or warning records"
                + (truncated ? " (search hit producer bounds)" : string.Empty));

        var retained = new List<BuildDiagnostic>(options.MaxDiagnostics);
        foreach (var error in errors)
        {
            if (retained.Count >= options.MaxDiagnostics) break;
            retained.Add(error.Record);
        }
        foreach (var warning in warnings)
        {
            if (retained.Count >= options.MaxDiagnostics) break;
            retained.Add(warning.Record);
        }
        if (errors.Count + warnings.Count > retained.Count)
            truncated = true;

        LinkCauses(retained);

        return new BuildDiagnosticsEvidence
        {
            ProviderId = MSBuildDiagnosticsOptions.ProviderId,
            SourceBinding = binding,
            Status = BuildDiagnosticsStatus.Enriched,
            Diagnostics = retained,
            RootCauseIds = retained.Where(static d => d.IsRootCause).Select(static d => d.Id).ToArray(),
            TotalErrorCount = errors.Count,
            TotalWarningCount = warnings.Count,
            Truncated = truncated,
        };
    }

    private sealed record ExtractedDiagnostic(BuildDiagnostic Record, DateTime Timestamp);

    private static ExtractedDiagnostic? ToRecord(StructuredLog.AbstractDiagnostic diagnostic, string id, MSBuildDiagnosticsOptions options)
    {
        var severity = diagnostic is StructuredLog.Error
            ? BuildDiagnosticSeverity.Error
            : diagnostic is StructuredLog.Warning
                ? BuildDiagnosticSeverity.Warning
                : (BuildDiagnosticSeverity?)null;
        if (severity is null) return null;

        return new ExtractedDiagnostic(
            new BuildDiagnostic
            {
                Id = id,
                ProviderId = MSBuildDiagnosticsOptions.ProviderId,
                Severity = severity.Value,
                Code = Shorten(RawOutputRedactor.Redact(diagnostic.Code ?? string.Empty).Trim(), 32) is { Length: > 0 } code ? code : null,
                Message = Shorten(RawOutputRedactor.Redact(diagnostic.Text ?? string.Empty).Trim(), options.MaxDiagnosticMessageChars),
                Location = string.IsNullOrWhiteSpace(diagnostic.File)
                    ? null
                    : new BuildDiagnosticLocation
                    {
                        Path = Shorten(RawOutputRedactor.Redact(diagnostic.File.Trim()), MaxPathChars),
                        Line = diagnostic.LineNumber > 0 ? diagnostic.LineNumber : null,
                        Column = diagnostic.ColumnNumber > 0 ? diagnostic.ColumnNumber : null,
                        EndLine = diagnostic.EndLineNumber > 0 ? diagnostic.EndLineNumber : null,
                        EndColumn = diagnostic.EndColumnNumber > 0 ? diagnostic.EndColumnNumber : null,
                    },
                Project = Shorten(
                    RawOutputRedactor.Redact(
                        diagnostic.GetNearestParent<StructuredLog.Project>()?.ProjectFile
                        ?? diagnostic.ProjectFile
                        ?? string.Empty).Trim(),
                    MaxProjectChars) is { Length: > 0 } project ? project : null,
                Target = Shorten(
                    RawOutputRedactor.Redact(
                        diagnostic.GetNearestParent<StructuredLog.Target>()?.Name ?? string.Empty).Trim(),
                    MaxTargetChars) is { Length: > 0 } target ? target : null,
            },
            diagnostic.Timestamp);
    }

    /// <summary>
    /// Weak temporal causality: MSBuild aborts the failing target at its
    /// first error, so later errors in the same project execute in an already
    /// broken build. Each project's earliest error is marked the root cause
    /// and later same-project errors link to it, guiding the repair loop to
    /// fix earliest-first. Cross-project errors stay unlinked: independent
    /// project builds fail independently. Warnings never link.
    /// </summary>
    public static void LinkCauses(List<BuildDiagnostic> diagnostics)
    {
        var rootsByProject = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordered = diagnostics
            .Select((diagnostic, index) => (diagnostic, index))
            .OrderBy(static item => item.diagnostic.Severity == BuildDiagnosticSeverity.Error ? 0 : 1)
            .ThenBy(static item => item.index)
            .ToList();

        foreach (var (diagnostic, _) in ordered)
        {
            if (diagnostic.Severity != BuildDiagnosticSeverity.Error) continue;
            var key = diagnostic.Project ?? string.Empty;
            if (!rootsByProject.ContainsKey(key))
                rootsByProject[key] = diagnostic.Id;
        }

        for (var i = 0; i < diagnostics.Count; i++)
        {
            var diagnostic = diagnostics[i];
            if (diagnostic.Severity != BuildDiagnosticSeverity.Error) continue;
            var key = diagnostic.Project ?? string.Empty;
            var rootId = rootsByProject[key];
            if (string.Equals(diagnostic.Id, rootId, StringComparison.Ordinal))
                diagnostics[i] = diagnostic with { IsRootCause = true };
            else
                diagnostics[i] = diagnostic with { CausedByIds = [rootId] };
        }
    }

    private static string SingleLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>
    /// Typed integrity failure: thrown inside the parse worker when the gzip
    /// pre-check rejects the payload, and converted to
    /// insufficient-diagnostics at the single catch site above.
    /// </summary>
    private sealed class BinlogIntegrityException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>
    /// Streams the gzip payload to end without retaining output, verifying
    /// the framing trailer (CRC32 + content length) as it goes. Returns null
    /// when the stream is one complete member; otherwise a reason the payload
    /// must not be trusted. This check exists because the pinned reader
    /// tolerates a cleanly cut record stream by returning partial data, so
    /// without it a truncated log could become a partial-data "pass". Length
    /// alone always catches truncation (a cut log decompresses shorter); the
    /// CRC additionally catches same-length corruption. Only the two
    /// exception types <see cref="GZipStream"/> raises for corrupt input are
    /// converted; anything else propagates.
    /// </summary>
    private static string? CheckGzipIntegrity(byte[] binlogBytes, MSBuildDiagnosticsOptions options)
    {
        // Caller already enforced 0 < Length <= MaxBinlogBytes, and options
        // validation bounds the ratio, so the product fits comfortably.
        var cap = (long)options.MaxBinlogBytes * Math.Max(1, options.MaxDecompressionRatio);

        try
        {
            using var input = new MemoryStream(binlogBytes, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var crc = new Crc32();
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc.Append(buffer.AsSpan(0, read));
                total += read;
                if (total > cap)
                    return $"decompression-ratio-exceeded: gzip output past {cap} bytes for a {binlogBytes.Length}-byte log";
            }

            // Minimum viable gzip member: 10-byte header + 8-byte trailer.
            if (binlogBytes.Length < 18)
                return "truncated: payload is shorter than any complete gzip member";
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(binlogBytes.AsSpan(binlogBytes.Length - 8, 4));
            var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(binlogBytes.AsSpan(binlogBytes.Length - 4, 4));
            if (expectedSize != (uint)(total & 0xFFFFFFFF)
                || expectedCrc != BinaryPrimitives.ReadUInt32LittleEndian(crc.GetCurrentHash()))
                return "truncated: gzip trailer does not match decompressed content; the log is incomplete or substituted";
            return null;
        }
        catch (InvalidDataException)
        {
            return "truncated: gzip framing is incomplete or corrupt; partial records are not trustworthy";
        }
        catch (IOException ex)
        {
            return $"truncated: gzip stream ends unexpectedly ({ex.GetType().Name}); partial records are not trustworthy";
        }
    }

    private static string Shorten(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 3)] + "...";
}
