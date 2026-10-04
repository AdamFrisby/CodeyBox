using System.Formats.Tar;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared tar transfer mechanics for the poll-driven remote executor path:
/// the orchestrator tars the per-item bare repo for stage-in, and the
/// executor tars its staged copy for stage-out. Both directions enforce the
/// archive cap <i>while writing/receiving</i> — never as a post-write size
/// check — so a hostile payload cannot fill the receiver's disk before
/// validation rejects it. Exceeding the cap throws
/// <see cref="ExecutorPhaseException"/> (phase failure: the host was
/// reachable), never a transport failure, and leaves no usable archive
/// behind. Links are refused loudly in both directions instead of being
/// followed or materialized.
/// </summary>
internal static class ExecutorTarTransfer
{
    private const int CopyBufferSize = 128 * 1024;

    /// <summary>
    /// Tars <paramref name="sourceDir"/> under <paramref name="rootName"/>
    /// into <paramref name="destinationPath"/>, aborting with
    /// <see cref="ExecutorPhaseException"/> as soon as
    /// <paramref name="maxBytes"/> is exceeded. A partial file never survives
    /// a failure: it is deleted before the exception propagates.
    /// </summary>
    internal static async Task WriteDirectoryToTarAsync(
        string sourceDir,
        string rootName,
        string destinationPath,
        long maxBytes,
        string hostId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (maxBytes <= 0)
            throw new ExecutorPhaseTransportException(hostId, "stage", $"Archive cap must be positive, got {maxBytes}.");
        if (!Directory.Exists(sourceDir))
            throw new ExecutorPhaseTransportException(hostId, "stage", "Source repository is not present.");

        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (parent is not null)
            Directory.CreateDirectory(parent);
        try
        {
            await using var file = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize, useAsync: true);
            await using var bounded = new BoundedWriteStream(file, maxBytes, hostId);
            try
            {
                await WriteTarAsync(sourceDir, rootName, bounded, ct).ConfigureAwait(false);
                await bounded.FlushAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(destinationPath); } catch { }
                throw;
            }
        }
        catch (ExecutorPhaseException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException(hostId, "stage", ex.Message, ex);
        }
    }

    /// <summary>
    /// Copies a landed tar to <paramref name="destinationPath"/>, enforcing
    /// <paramref name="maxBytes"/> while receiving. Exceeding the cap throws
    /// <see cref="ExecutorPhaseException"/> and leaves no usable archive
    /// behind at the destination.
    /// </summary>
    internal static async Task CopyFileBoundedAsync(
        string sourcePath,
        string destinationPath,
        long maxBytes,
        string hostId,
        string operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (maxBytes <= 0)
            throw new ExecutorPhaseTransportException(hostId, operation, $"Archive cap must be positive, got {maxBytes}.");

        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (parent is not null)
            Directory.CreateDirectory(parent);
        try
        {
            await using var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: CopyBufferSize, useAsync: true);
            await using var output = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize, useAsync: true);
            var buffer = new byte[CopyBufferSize];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    output.Dispose();
                    try { File.Delete(destinationPath); } catch { }
                    throw new ExecutorPhaseException(
                        $"Staged-back archive from host '{hostId}' exceeded configured StageOutMaxArchiveBytes={maxBytes}.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (ExecutorPhaseException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException(hostId, operation, ex.Message, ex);
        }
    }

    /// <summary>
    /// Extracts a stage-in tar into an empty <paramref name="destinationDir"/>
    /// with the same containment rules the orchestrator-side stage-out
    /// validator enforces: absolute paths, parent traversal, NUL bytes,
    /// symlinks and hardlinks are rejected before anything is written, and
    /// the entry count plus total payload bytes are capped. Violations throw
    /// <see cref="ExecutorPhaseException"/> and leave the destination empty.
    /// </summary>
    internal static async Task ExtractTarToDirectoryAsync(
        string tarPath,
        string destinationDir,
        long maxTotalBytes,
        int maxEntries,
        string hostId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        if (maxTotalBytes <= 0)
            throw new ExecutorPhaseTransportException(hostId, "stage", $"Archive cap must be positive, got {maxTotalBytes}.");
        if (maxEntries <= 0)
            throw new ExecutorPhaseTransportException(hostId, "stage", $"Entry cap must be positive, got {maxEntries}.");
        if (!File.Exists(tarPath))
            throw new ExecutorPhaseTransportException(hostId, "stage", "Dispatch archive is not present.");

        var root = Path.GetFullPath(destinationDir);
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException(hostId, "stage", ex.Message, ex);
        }

        try
        {
            await using var file = new FileStream(
                tarPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: CopyBufferSize, useAsync: true);
            await using var reader = new TarReader(file, leaveOpen: true);
            var entries = 0;
            long totalBytes = 0;
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                entries++;
                if (entries > maxEntries)
                    throw new ExecutorPhaseException(
                        $"Dispatch archive from host '{hostId}' exceeded the entry cap ({maxEntries}).");
                var name = entry.Name ?? string.Empty;
                var target = MapEntryPath(root, name, hostId);
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(target);
                        break;
                    case TarEntryType.RegularFile:
                        {
                            var parent = Path.GetDirectoryName(target);
                            if (parent is not null)
                                Directory.CreateDirectory(parent);
                            await using var outFile = new FileStream(
                                target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                bufferSize: CopyBufferSize, useAsync: true);
                            if (entry.DataStream is not null)
                            {
                                var buffer = new byte[CopyBufferSize];
                                int read;
                                while ((read = await entry.DataStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                                {
                                    totalBytes += read;
                                    if (totalBytes > maxTotalBytes)
                                        throw new ExecutorPhaseException(
                                            $"Dispatch archive from host '{hostId}' exceeded the payload cap ({maxTotalBytes} bytes).");
                                    await outFile.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                }
                            }
                            break;
                        }
                    default:
                        throw new ExecutorPhaseException(
                            $"Dispatch archive from host '{hostId}' contains an unsupported entry type for '{name}'.");
                }
            }
        }
        catch (ExecutorPhaseException)
        {
            DeleteQuietly(root);
            throw;
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(root);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DeleteQuietly(root);
            throw new ExecutorPhaseTransportException(hostId, "stage", ex.Message, ex);
        }
    }

    private static string MapEntryPath(string root, string name, string hostId)
    {
        if (string.IsNullOrEmpty(name))
            throw new ExecutorPhaseException($"Dispatch archive from host '{hostId}' contains an entry with an empty name.");
        if (name.Contains('\0'))
            throw new ExecutorPhaseException($"Dispatch archive from host '{hostId}' contains an entry with a NUL byte in its name.");
        if (Path.IsPathRooted(name))
            throw new ExecutorPhaseException($"Unsafe dispatch archive entry path '{name}'.");
        var full = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(full, root, StringComparison.Ordinal))
            throw new ExecutorPhaseException($"Unsafe dispatch archive entry path '{name}'.");
        return full;
    }

    private static async Task WriteTarAsync(
        string sourceDir,
        string rootName,
        Stream destination,
        CancellationToken ct)
    {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        await WriteLevelAsync(writer, sourceDir, sourceDir, rootName, ct).ConfigureAwait(false);
    }

    private static async Task WriteLevelAsync(
        TarWriter writer,
        string rootDir,
        string levelDir,
        string rootName,
        CancellationToken ct)
    {
        var entries = Directory.EnumerateFileSystemEntries(levelDir, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (IsLink(entry))
                throw new ExecutorPhaseException(
                    $"Refusing to archive link '{Path.GetRelativePath(rootDir, entry)}' from the staged copy.");
            var name = rootName + "/" + Path.GetRelativePath(rootDir, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name), ct).ConfigureAwait(false);
            }
            else if (File.Exists(entry))
            {
                var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, name);
                await using var data = File.OpenRead(entry);
                tarEntry.DataStream = data;
                await writer.WriteEntryAsync(tarEntry, ct).ConfigureAwait(false);
            }
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(entry) && !IsLinkQuiet(entry))
                await WriteLevelAsync(writer, rootDir, entry, rootName, ct).ConfigureAwait(false);
        }
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsLinkQuiet(string path)
    {
        try
        {
            return IsLink(path);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup: never mask the dispatch outcome.
        }
    }

    /// <summary>
    /// Write-only wrapper that aborts the transfer as soon as the configured
    /// cap is exceeded. Exceeding the cap is an
    /// <see cref="ExecutorPhaseException"/> (phase failure: the host was
    /// reachable), never a transport failure.
    /// </summary>
    private sealed class BoundedWriteStream(Stream inner, long maxBytes, string hostId) : Stream
    {
        private readonly Stream _inner = inner;
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfExceeding(count);
            _inner.Write(buffer, offset, count);
            _written += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ThrowIfExceeding(count);
            await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
            _written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ThrowIfExceeding(buffer.Length);
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            _written += buffer.Length;
        }

        private void ThrowIfExceeding(long count)
        {
            if (_written + count > maxBytes)
                throw new ExecutorPhaseException(
                    $"Transfer from host '{hostId}' exceeded the configured archive cap ({maxBytes} bytes).");
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
