using System.Formats.Tar;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatch transport for the colocated executor: runs the phase through the
/// same <see cref="ExecutorHostPhaseRunner"/> a remote host runs, but over
/// the local filesystem instead of a network hop. Stage-in records the
/// phase's single bare repo, the phase run copies it into the colocated
/// staging root and executes against that staged copy, and stage-out tars
/// the copy back through real tar bytes so the proxy validates a real
/// archive exactly as it would for a remote host. Only the per-item repo
/// path is ever transferred — never the whole repos root.
/// </summary>
/// <remarks>
/// Instances carry one dispatch: the transport factory hands out a fresh
/// instance per resolve, and the proxy uses one instance per host attempt, so
/// concurrent dispatches never share staging state. Sequential reuse is safe
/// (a new stage-in overwrites the recorded source); concurrent use of one
/// instance is not supported.
/// </remarks>
public sealed class ColocatedExecutorTransport : IExecutorPhaseTransport
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly string _stagingRoot;
    private readonly ExecutorHostPhaseRunner _runner;

    private string? _sourceRepoPath;
    private string? _sourceRootName;
    private string? _stagedLeaf;

    public ColocatedExecutorTransport(
        string hostId,
        string stagingRoot,
        ExecutorHostPhaseRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(runner);
        HostId = hostId.Trim();
        _stagingRoot = Path.GetFullPath(stagingRoot.Trim());
        _runner = runner;
    }

    /// <inheritdoc />
    public string HostId { get; }

    /// <inheritdoc />
    public Task StageInAsync(string hostRepoPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(hostRepoPath))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path is empty.");
        string canonical;
        try
        {
            canonical = Path.GetFullPath(hostRepoPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path is not a valid path.", ex);
        }
        if (!Directory.Exists(canonical))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", $"Host repository path does not exist: '{canonical}'.");
        var rootName = Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path has no directory name.");
        _sourceRepoPath = canonical;
        _sourceRootName = rootName;
        _stagedLeaf = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = _sourceRepoPath
            ?? throw new ExecutorPhaseTransportException(HostId, "run-phase", "No repository was staged; StageInAsync must run first.");
        var staged = ExecutorPhaseExecution.ResolveStagedRepoPath(_stagingRoot, request.RepositoryId);
        RefreshStagedCopy(source, staged, ct);
        _stagedLeaf = staged;
        try
        {
            return await _runner.ExecutePhaseAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExecutorPhaseTransportException)
        {
            throw;
        }
        catch (ExecutorPhaseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExecutorPhaseTransportException(HostId, "run-phase", ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task StageOutToArchiveAsync(string hostArchivePath, long maxArchiveBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostArchivePath);
        if (maxArchiveBytes <= 0)
            throw new ExecutorPhaseTransportException(HostId, "stage-out", $"Archive cap must be positive, got {maxArchiveBytes}.");
        var staged = _stagedLeaf
            ?? throw new ExecutorPhaseTransportException(HostId, "stage-out", "No phase has run; RunPhaseAsync must run before stage-out.");
        if (!Directory.Exists(staged))
            throw new ExecutorPhaseTransportException(HostId, "stage-out", "Staged repository is not present; the phase was never staged.");
        // The validator expects the archive root to be the orchestrator-side
        // repo basename, which can differ from the staging leaf name (unsafe
        // repository ids map to a content-hashed leaf). Rewrite the root on
        // the way out so the archive validates exactly like a remote host's.
        var rootName = _sourceRootName
            ?? throw new ExecutorPhaseTransportException(HostId, "stage-out", "No repository was staged; StageInAsync must run first.");

        string canonicalArchive;
        try
        {
            canonicalArchive = Path.GetFullPath(hostArchivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExecutorPhaseTransportException(HostId, "stage-out", "Archive path is not a valid path.", ex);
        }

        var parent = Path.GetDirectoryName(canonicalArchive);
        if (parent is not null)
            Directory.CreateDirectory(parent);
        try
        {
            await using var file = new FileStream(
                canonicalArchive, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize, useAsync: true);
            await using var bounded = new BoundedArchiveStream(file, maxArchiveBytes, HostId);
            try
            {
                await WriteTarOfDirectoryAsync(staged, rootName, bounded, ct).ConfigureAwait(false);
                await bounded.FlushAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(canonicalArchive); } catch { }
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
            throw new ExecutorPhaseTransportException(HostId, "stage-out", ex.Message, ex);
        }
    }

    private void RefreshStagedCopy(string source, string staged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (Directory.Exists(staged))
                DeleteDirectory(staged);
            Directory.CreateDirectory(staged);
            CopyDirectory(source, staged, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException(HostId, "stage", ex.Message, ex);
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            using var input = File.OpenRead(file);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            File.SetAttributes(target, File.GetAttributes(file) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden));
        }
        foreach (var dir in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories))
            File.SetAttributes(dir, File.GetAttributes(dir) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden));
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var entry in Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        Directory.Delete(path, recursive: true);
    }

    private static async Task WriteTarOfDirectoryAsync(
        string sourceDir,
        string rootName,
        Stream destination,
        CancellationToken ct)
    {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var name = rootName + "/" + Path.GetRelativePath(sourceDir, dir).Replace('\\', '/');
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name), ct).ConfigureAwait(false);
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var name = rootName + "/" + Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
            await using var data = File.OpenRead(file);
            entry.DataStream = data;
            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write-only wrapper that aborts the stage-out as soon as the configured
    /// archive cap is exceeded, so a hostile staged copy cannot fill the
    /// orchestrator disk before validation rejects the payload. Exceeding the
    /// cap is an <see cref="ExecutorPhaseException"/> (phase failure: the
    /// host was reachable), never a transport failure.
    /// </summary>
    private sealed class BoundedArchiveStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly string _hostId;
        private long _written;

        public BoundedArchiveStream(Stream inner, long maxBytes, string hostId)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _hostId = hostId;
        }

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
            if (_written + count > _maxBytes)
                throw new ExecutorPhaseException(
                    $"Staged-back archive from host '{_hostId}' exceeded configured StageOutMaxArchiveBytes={_maxBytes}.");
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
