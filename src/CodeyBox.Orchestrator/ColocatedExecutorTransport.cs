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
/// instance is not supported. Each dispatch stages to its own leaf (the
/// per-repo leaf plus a dispatch-key hash, resolved identically by the
/// executor-side runner), so concurrent dispatches against the same repo
/// cannot interleave delete/copy/run/tar; same-key duplicates serialize
/// their copy window on the shared <see cref="StagingCopyGate"/>.
/// </remarks>
public sealed class ColocatedExecutorTransport : IStreamingExecutorPhaseTransport
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly string _stagingRoot;
    private readonly string? _repositoriesRoot;
    private readonly ExecutorHostPhaseRunner _runner;
    private readonly StagingCopyGate _copyGate;

    private string? _sourceRepoPath;
    private string? _sourceRootName;
    private string? _stagedLeaf;

    public ColocatedExecutorTransport(
        string hostId,
        string stagingRoot,
        ExecutorHostPhaseRunner runner,
        string? repositoriesRootDirectory = null,
        StagingCopyGate? copyGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(runner);
        HostId = hostId.Trim();
        _stagingRoot = Path.GetFullPath(stagingRoot.Trim());
        _repositoriesRoot = string.IsNullOrWhiteSpace(repositoriesRootDirectory)
            ? null
            : Path.GetFullPath(repositoriesRootDirectory.Trim());
        _runner = runner;
        _copyGate = copyGate ?? new StagingCopyGate();
    }

    /// <inheritdoc />
    public string HostId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The path is canonicalized and — when the transport was composed with
    /// the repositories root — contained under it here at the sink, so the
    /// guard travels with this public entry point instead of living only at
    /// the proxy's distant call site. Links are never followed: a staged
    /// source that is itself a link, or contains one, fails loudly.
    /// </remarks>
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
        if (_repositoriesRoot is not null)
        {
            var prefix = _repositoriesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(prefix, StringComparison.Ordinal))
                throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path escapes the repositories root.");
        }
        if (!Directory.Exists(canonical))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", $"Host repository path does not exist: '{canonical}'.");
        if (IsLink(canonical))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path is a link; refusing to stage through it.");
        var rootName = Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
            throw new ExecutorPhaseTransportException(HostId, "stage-in", "Host repository path has no directory name.");
        _sourceRepoPath = canonical;
        _sourceRootName = rootName;
        _stagedLeaf = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ExecutorPhaseResult> RunPhaseAsync(ExecutorPhaseRequest request, CancellationToken ct) =>
        RunPhaseAsync(request, onChunk: null, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The colocated streaming path: the in-process runner forwards
    /// <paramref name="onChunk"/> to a streaming handler as agent output is
    /// produced, so a local phase streams incrementally through the same
    /// orchestrator-side relay a remote phase uses — never buffered to
    /// completion. A null callback behaves exactly like the non-streaming
    /// overload; a non-streaming handler ignores the callback.
    /// </remarks>
    public async Task<ExecutorPhaseResult> RunPhaseAsync(
        ExecutorPhaseRequest request,
        Func<ExecutorStreamChunk, CancellationToken, Task>? onChunk,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = _sourceRepoPath
            ?? throw new ExecutorPhaseTransportException(HostId, "run-phase", "No repository was staged; StageInAsync must run first.");
        // Per-dispatch leaf: concurrent dispatches against the same repo run
        // on isolated copies. A failed run intentionally leaves its leaf
        // behind — a same-key duplicate may still reference it, and a
        // failover retry re-stages under the copy gate — while stage-out
        // deletes the leaf once the tar is written.
        var staged = ExecutorPhaseExecution.ResolveStagedRepoPathForDispatch(_stagingRoot, request);
        using (await _copyGate.AcquireAsync(staged, ct).ConfigureAwait(false))
        {
            RefreshStagedCopy(source, staged, ct);
        }
        _stagedLeaf = staged;
        try
        {
            return await _runner.ExecutePhaseAsync(request, onChunk, ct).ConfigureAwait(false);
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
        finally
        {
            // The tar now carries the result; drop the per-dispatch leaf so
            // staging does not accumulate one directory per phase run.
            _stagedLeaf = null;
            DeleteQuietly(staged);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                DeleteDirectory(path);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup: never mask the dispatch outcome.
        }
    }

    private void RefreshStagedCopy(string source, string staged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // A link at the staging root or the staged leaf would redirect
            // the delete/create/copy below onto attacker-chosen targets, so
            // refuse before touching anything. Directory.CreateDirectory on a
            // link to a directory would silently succeed, hence check first.
            if (Directory.Exists(_stagingRoot) && IsLink(_stagingRoot))
                throw new ExecutorPhaseTransportException(HostId, "stage", "Staging root is a link; refusing to stage through it.");
            Directory.CreateDirectory(_stagingRoot);
            if ((Directory.Exists(staged) || File.Exists(staged)) && IsLink(staged))
                throw new ExecutorPhaseTransportException(HostId, "stage", "Staged repository path is a link; refusing to stage through it.");
            if (Directory.Exists(staged))
                DeleteDirectory(staged);
            else if (File.Exists(staged))
                throw new ExecutorPhaseTransportException(HostId, "stage", "Staged repository path is blocked by a file.");
            Directory.CreateDirectory(staged);
            CopyDirectory(source, staged, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExecutorPhaseTransportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutorPhaseTransportException(HostId, "stage", ex.Message, ex);
        }
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string RelativeName(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.Length > 256)
            relative = relative[..256] + "…";
        return relative;
    }

    /// <summary>
    /// Copies one level at a time, refusing every link before descending or
    /// opening: enumerating with <see cref="SearchOption.AllDirectories"/>
    /// would follow a planted directory link and copy outside content, and
    /// the tar writer would then materialize the link target as regular
    /// files. The source is orchestrator-owned, so a link there is anomalous
    /// and fails the dispatch loudly instead of being followed or silently
    /// dropped. The check-then-open window is best-effort (the platform
    /// offers no open-without-following here); worst case under a concurrent
    /// swap is a partially refreshed copy, which the dispatch treats as a
    /// transport failure on the next inconsistent read.
    /// </summary>
    private void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            bool isLink;
            try
            {
                isLink = IsLink(entry);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            if (isLink)
                throw new ExecutorPhaseTransportException(
                    HostId, "stage",
                    $"Refusing to stage link '{RelativeName(source, entry)}'.");
            if (Directory.Exists(entry))
            {
                var target = Path.Combine(destination, Path.GetFileName(entry));
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target, ct);
            }
            else if (File.Exists(entry))
            {
                var target = Path.Combine(destination, Path.GetFileName(entry));
                var attributes = File.GetAttributes(entry);
                using var input = File.OpenRead(entry);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                File.SetAttributes(target, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden));
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
        {
            bool isLink;
            try
            {
                isLink = IsLink(entry);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            try
            {
                // Delete the link itself, never its target: recursive delete
                // must not follow directory links into outside trees.
                if (isLink)
                {
                    if (Directory.Exists(entry) && !File.Exists(entry))
                        Directory.Delete(entry, recursive: false);
                    else
                        File.Delete(entry);
                }
                else if (Directory.Exists(entry))
                {
                    DeleteDirectory(entry);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    Directory.Delete(entry, recursive: false);
                }
                else if (File.Exists(entry))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        Directory.Delete(path, recursive: false);
    }

    /// <summary>
    /// Tars the staged copy one level at a time, refusing every link before
    /// reading: a link that survived to stage-out (planted mid-phase) must
    /// fail the transfer loudly rather than have its target materialized as
    /// regular-file entries that bypass the stage-out link rejection.
    /// Deterministic entry order (ordinal per level, directories before
    /// files) so repeated runs tar identically.
    /// </summary>
    private static async Task WriteTarOfDirectoryAsync(
        string sourceDir,
        string rootName,
        Stream destination,
        CancellationToken ct)
    {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        await WriteTarLevelAsync(writer, sourceDir, sourceDir, rootName, ct).ConfigureAwait(false);
    }

    private static async Task WriteTarLevelAsync(
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
            bool isLink;
            try
            {
                isLink = IsLink(entry);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            if (isLink)
                throw new ExecutorPhaseException(
                    $"Refusing to archive link '{RelativeName(rootDir, entry)}' from the staged copy.");
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
                await WriteTarLevelAsync(writer, rootDir, entry, rootName, ct).ConfigureAwait(false);
        }
    }

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
