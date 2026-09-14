using System.Formats.Tar;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Proxy-side validation for the tar archive an executor stages back.
/// Mirrors the safety properties of the SSH transport's own stage-out
/// validation (bounded archive bytes, bounded entry count, bounded declared
/// payload vs archive size, path containment under the expected repo root,
/// directory/regular-file entries only): the remote payload is untrusted and
/// nothing is extracted over the orchestrator's bare repo until every check
/// passes. The archive-byte cap is enforced first by the transport while
/// receiving (so an unbounded payload cannot fill the orchestrator disk);
/// the size check here is defense in depth for transports that landed the
/// file by other means. Violations throw <see cref="ExecutorPhaseException"/> — the host
/// was reachable, so this is a phase failure, not a transport failure — and
/// leave the bare repo untouched.
/// </summary>
public static class ExecutorStageOutValidator
{
    /// <summary>
    /// Validates the archive at <paramref name="archivePath"/>, extracts it
    /// into a fresh directory under <paramref name="scratchRoot"/>, and
    /// atomically swaps the validated repo tree into
    /// <paramref name="targetRepoPath"/>. The expected single root entry is
    /// the target's basename (for example <c>item-id.git</c>).
    /// </summary>
    public static async Task ValidateAndInstallAsync(
        string archivePath,
        string targetRepoPath,
        string scratchRoot,
        ExecutorPhaseDispatchOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentNullException.ThrowIfNull(options);

        var expectedRoot = Path.GetFileName(targetRepoPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(expectedRoot))
            throw new ExecutorPhaseException($"Target repo path has no basename: '{targetRepoPath}'.");

        var archiveBytes = new FileInfo(archivePath).Length;
        if (archiveBytes > options.StageOutMaxArchiveBytes)
            throw new ExecutorPhaseException(
                $"Staged-back archive for '{expectedRoot}' is {archiveBytes} bytes, exceeding the configured StageOutMaxArchiveBytes={options.StageOutMaxArchiveBytes}.");

        ValidateArchiveEntries(archivePath, archiveBytes, expectedRoot, options);

        var workRoot = Path.Combine(scratchRoot, ".codeybox-stageout-" + Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(workRoot, "extract");
        Directory.CreateDirectory(extractRoot);
        try
        {
            await ExtractArchiveAsync(archivePath, extractRoot, expectedRoot, ct).ConfigureAwait(false);
            var extracted = Path.Combine(extractRoot, expectedRoot);
            if (!Directory.Exists(extracted) && !File.Exists(extracted))
                throw new ExecutorPhaseException(
                    $"Validated staged-back archive did not contain expected root '{expectedRoot}'.");
            ReplacePath(extracted, targetRepoPath);
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true); }
            catch { }
        }
    }

    private static void ValidateArchiveEntries(
        string archivePath,
        long archiveBytes,
        string expectedRoot,
        ExecutorPhaseDispatchOptions options)
    {
        var maxDeclaredBytes = MaxDeclaredPayloadBytes(archiveBytes, options.StageOutMaxExpansionRatio);
        long declaredRegularFileBytes = 0;
        var entryCount = 0;
        var sawRootedEntry = false;
        try
        {
            using var archive = File.OpenRead(archivePath);
            using var reader = new TarReader(archive, leaveOpen: false);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                if (IsMetadataEntry(entry.EntryType))
                    continue;

                entryCount++;
                if (entryCount > options.StageOutMaxEntries)
                    throw new ExecutorPhaseException(
                        $"Staged-back archive exceeded configured StageOutMaxEntries={options.StageOutMaxEntries}.");

                if (!IsSafeEntryType(entry.EntryType))
                    throw new ExecutorPhaseException(
                        $"Unsafe staged-back entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    if (entry.Length > maxDeclaredBytes - declaredRegularFileBytes)
                        throw new ExecutorPhaseException(
                            $"Staged-back archive declared file bytes exceeding StageOutMaxExpansionRatio={options.StageOutMaxExpansionRatio} for archive size {archiveBytes}.");
                    declaredRegularFileBytes += entry.Length;
                }

                var name = NormalizeEntryName(entry.Name);
                EnsureUnderExpectedRoot(name, expectedRoot);
                sawRootedEntry = true;
            }
        }
        catch (ExecutorPhaseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ExecutorPhaseException($"Staged-back archive failed validation: {ex.Message}", ex);
        }

        if (!sawRootedEntry)
            throw new ExecutorPhaseException("Staged-back archive contained no extractable entries.");
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string extractRoot,
        string expectedRoot,
        CancellationToken ct)
    {
        var canonicalRoot = Path.GetFullPath(extractRoot);
        try
        {
            await using var archive = File.OpenRead(archivePath);
            using var reader = new TarReader(archive, leaveOpen: false);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                if (IsMetadataEntry(entry.EntryType))
                    continue;
                if (!IsSafeEntryType(entry.EntryType))
                    throw new ExecutorPhaseException(
                        $"Unsafe staged-back entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");
                var name = NormalizeEntryName(entry.Name);
                EnsureUnderExpectedRoot(name, expectedRoot);
                var destination = Path.GetFullPath(Path.Combine(canonicalRoot, name));
                EnsureContained(canonicalRoot, destination);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(destination);
                }
                else
                {
                    var parent = Path.GetDirectoryName(destination);
                    if (parent is not null)
                        Directory.CreateDirectory(parent);
                    await using var output = new FileStream(
                        destination, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 128 * 1024, useAsync: true);
                    if (entry.DataStream is not null)
                        await BoundedCopyAsync(entry.DataStream, output, entry.Length, name, ct).ConfigureAwait(false);
                }
            }
        }
        catch (ExecutorPhaseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ExecutorPhaseException($"Staged-back archive failed extraction: {ex.Message}", ex);
        }
    }

    private static async Task BoundedCopyAsync(Stream source, Stream destination, long declaredLength, string name, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return;
            copied += read;
            if (copied > declaredLength)
                throw new ExecutorPhaseException($"Staged-back entry '{name}' streamed more bytes than its declared length.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static long MaxDeclaredPayloadBytes(long archiveBytes, double maxExpansionRatio)
    {
        var capped = archiveBytes * maxExpansionRatio;
        if (double.IsInfinity(capped) || capped >= long.MaxValue)
            return long.MaxValue;
        return Math.Max(archiveBytes, (long)Math.Ceiling(capped));
    }

    private static bool IsSafeEntryType(TarEntryType type) =>
        type is TarEntryType.Directory
            or TarEntryType.RegularFile
            or TarEntryType.V7RegularFile;

    private static bool IsMetadataEntry(TarEntryType type) =>
        type is TarEntryType.ExtendedAttributes
            or TarEntryType.GlobalExtendedAttributes;

    internal static string NormalizeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ExecutorPhaseException("Staged-back archive contains an entry with an empty name.");
        if (name.IndexOf('\0') >= 0)
            throw new ExecutorPhaseException("Staged-back archive contains an entry with a NUL byte in its name.");

        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0 || normalized[0] == '/')
            throw new ExecutorPhaseException($"Unsafe staged-back entry path '{name}'.");

        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == "..")
                throw new ExecutorPhaseException($"Unsafe staged-back entry path '{name}'.");
        }

        return normalized;
    }

    private static void EnsureUnderExpectedRoot(string entryName, string expectedRoot)
    {
        if (string.Equals(entryName, expectedRoot, StringComparison.Ordinal))
            return;
        if (entryName.StartsWith(expectedRoot + "/", StringComparison.Ordinal))
            return;
        throw new ExecutorPhaseException(
            $"Unsafe staged-back entry '{entryName}' is outside expected root '{expectedRoot}'.");
    }

    private static void EnsureContained(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, StringComparison.Ordinal))
            throw new ExecutorPhaseException(
                $"Unsafe staged-back entry escapes staging root.");
    }

    private static void ReplacePath(string source, string target)
    {
        var backup = target + ".codeybox-backup-" + Guid.NewGuid().ToString("N");
        var hadTarget = Path.Exists(target);
        if (hadTarget)
            MovePath(target, backup);

        try
        {
            MovePath(source, target);
        }
        catch
        {
            if (hadTarget)
            {
                try
                {
                    if (!Path.Exists(target) && Path.Exists(backup))
                        MovePath(backup, target);
                }
                catch { }
            }
            throw;
        }

        if (hadTarget)
        {
            try
            {
                if (Directory.Exists(backup))
                    Directory.Delete(backup, recursive: true);
                else if (File.Exists(backup))
                    File.Delete(backup);
            }
            catch { }
        }
    }

    private static void MovePath(string source, string target)
    {
        try
        {
            if (Directory.Exists(source))
                Directory.Move(source, target);
            else
                File.Move(source, target);
        }
        catch (IOException ex)
        {
            throw new ExecutorPhaseException($"Failed to install validated staged-back payload: {ex.Message}", ex);
        }
    }
}
