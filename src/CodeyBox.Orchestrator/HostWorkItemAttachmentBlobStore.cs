using System.Buffers;
using System.Security.Cryptography;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Stores attachment blobs on the host filesystem under a content-addressed
/// layout: <c>&lt;root&gt;/&lt;sha256[..2]&gt;/&lt;sha256&gt;</c>. Uploads are
/// streamed to a temp file with hashing, size-checked on the fly, then
/// atomically promoted into their final path. Repeated uploads of the same
/// bytes (same hash) are deduplicated — only one on-disk copy exists per
/// distinct blob. On dedup the existing blob's last-write time is touched so
/// the orphan-sweep grace window protects a freshly-referenced blob whose
/// metadata row has not landed yet.
/// </summary>
public sealed class HostWorkItemAttachmentBlobStore : IWorkItemAttachmentBlobStoreAdmin
{
    private const int StreamBufferSize = 81920; // .NET default FileStream buffer; rent/write/read all share it.
    private const string TempDirName = ".tmp";

    private readonly Func<string> _rootResolver;
    private readonly ILogger<HostWorkItemAttachmentBlobStore>? _log;

    public HostWorkItemAttachmentBlobStore(
        Func<string> rootResolver,
        ILogger<HostWorkItemAttachmentBlobStore>? log = null)
    {
        _rootResolver = rootResolver ?? throw new ArgumentNullException(nameof(rootResolver));
        _log = log;
    }

    public async Task<AttachmentBlobStageResult> StageAsync(
        Stream source,
        long maxBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be positive.");

        var root = Resolve();
        Directory.CreateDirectory(root);
        var tempDir = Path.Combine(root, TempDirName);
        Directory.CreateDirectory(tempDir);

        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        long total = 0;
        byte[]? finalHash = null;
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: StreamBufferSize,
                useAsync: true))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new AttachmentBlobTooLargeException(maxBytes);
                    }
                    sha.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);
                finalHash = sha.GetHashAndReset();
            }

            var hashHex = Convert.ToHexString(finalHash).ToLowerInvariant();
            var finalPath = PathFor(root, hashHex);
            var finalDir = Path.GetDirectoryName(finalPath)!;
            Directory.CreateDirectory(finalDir);

            if (File.Exists(finalPath))
            {
                TryDeleteTemp(tempPath);
                // Touch the existing blob's last-write time so a concurrent
                // upload's metadata write (which lands after this dedup) is
                // protected by the orphan-sweep grace window: the blob reads
                // as freshly staged, not as the original creation time.
                TouchLastWrite(finalPath);
                return new AttachmentBlobStageResult(hashHex, total, WasDeduplicated: true);
            }

            try
            {
                File.Move(tempPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Raced with another upload of the same bytes — the existing
                // file is already canonical, drop the temp.
                TryDeleteTemp(tempPath);
                TouchLastWrite(finalPath);
                return new AttachmentBlobStageResult(hashHex, total, WasDeduplicated: true);
            }

            return new AttachmentBlobStageResult(hashHex, total, WasDeduplicated: false);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Stream? OpenRead(string sha256)
    {
        if (!IsValidHash(sha256)) return null;
        var path = PathFor(Resolve(), sha256);
        if (!File.Exists(path)) return null;
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: StreamBufferSize,
            useAsync: true);
    }

    public bool Exists(string sha256)
    {
        if (!IsValidHash(sha256)) return false;
        return File.Exists(PathFor(Resolve(), sha256));
    }

    public bool TryDelete(string sha256)
    {
        if (!IsValidHash(sha256)) return false;
        var path = PathFor(Resolve(), sha256);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to delete attachment blob {Sha256} at {Path}", sha256, path);
            return false;
        }
    }

    public DateTimeOffset? GetBlobLastWriteTimeUtc(string sha256)
    {
        if (!IsValidHash(sha256)) return null;
        var path = PathFor(Resolve(), sha256);
        if (!File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            // LastWriteTimeUtc is reliably maintained on every common Linux
            // filesystem (unlike CreationTimeUtc/birthtime which is missing on
            // stock ext4 and silently returns a sentinel). Every stage / dedup
            // touch refreshes it, so the orphan-sweep grace window is portable.
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Failed to stat attachment blob {Sha256} at {Path}", sha256, path);
            return null;
        }
    }

    public IReadOnlyCollection<string> EnumerateHashes()
    {
        var root = Resolve();
        if (!Directory.Exists(root)) return Array.Empty<string>();

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shard in Directory.EnumerateDirectories(root))
        {
            var shardName = Path.GetFileName(shard);
            if (shardName is null
                || shardName.Length != 2
                || !IsHexShard(shardName))
                continue;
            foreach (var file in Directory.EnumerateFiles(shard))
            {
                var name = Path.GetFileName(file);
                if (IsValidHash(name) && name.StartsWith(shardName, StringComparison.Ordinal))
                    result.Add(name);
            }
        }
        return result;
    }

    public int SweepTempFiles(TimeSpan grace)
    {
        var root = Resolve();
        var tempDir = Path.Combine(root, TempDirName);
        if (!Directory.Exists(tempDir)) return 0;

        var cutoff = DateTimeOffset.UtcNow - grace;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(tempDir))
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                if (new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) > cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "Failed to sweep temp attachment file {Path}", file);
            }
        }
        return deleted;
    }

    private string Resolve()
    {
        var root = _rootResolver();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Attachment blob root is not configured.");
        return root;
    }

    private static string PathFor(string root, string sha256) =>
        Path.Combine(root, sha256[..2], sha256);

    private static bool IsValidHash(string? s)
    {
        if (s is null || s.Length != 64) return false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex) return false;
        }
        return true;
    }

    private static bool IsHexShard(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex) return false;
        }
        return true;
    }

    private static void TouchLastWrite(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception) { /* best-effort; grace window still bounds the sweep */ }
    }

    private void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log?.LogDebug(ex, "Failed to delete temp attachment file {Path}", path); }
    }
}
