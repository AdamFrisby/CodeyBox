using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// The persisted-state durability idiom in one place: write a uniquely-named
/// temp file in the TARGET directory, then <see cref="File.Move(string, string, bool)"/>
/// over the destination so a crash can never leave a torn file. The temp file
/// is deleted on failure; the destination is only ever replaced wholesale.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM).</summary>
    public static void WriteAllText(string path, string contents)
    {
        var (fullPath, tempPath) = Prepare(path);
        try
        {
            File.WriteAllText(tempPath, contents, NoBomUtf8);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM).</summary>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
    {
        var (fullPath, tempPath) = Prepare(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, NoBomUtf8, ct).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static (string FullPath, string TempPath) Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException($"Path '{path}' has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory, $".{Path.GetFileName(fullPath)}.tmp.{Guid.NewGuid():N}");
        return (fullPath, tempPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
