namespace CodeyBox.Tests;

public sealed class TestTempDirectory : IDisposable
{
    private bool _disposed;

    private TestTempDirectory(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public static TestTempDirectory Create(string prefix)
        => new(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));

    public string NewDirectoryPath(string prefix)
        => Path.Combine(Root, prefix + Guid.NewGuid().ToString("N"));

    public string NewDatabasePath(string prefix = "state")
        => Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}.db");

    public string NewLogPath(string prefix)
        => Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}-.json");

    public void Dispose()
    {
        if (_disposed) return;
        TestTempArtifacts.DeleteDirectory(Root);
        _disposed = true;
    }
}

public abstract class CodeyBoxWebApplicationFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    private readonly TestTempDirectory _temp = TestTempDirectory.Create("codeybox-webapp-");

    public string TempRoot => _temp.Root;

    protected TestTempDirectory Temp => _temp;

    protected string TempDatabasePath(string prefix = "state") => _temp.NewDatabasePath(prefix);

    protected void DisposeHostThenDeleteSqliteDatabase(bool disposing, string dbPath, params Action[] cleanupBeforeDatabase)
    {
        ArgumentNullException.ThrowIfNull(cleanupBeforeDatabase);

        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        var cleanupActions = new Action[cleanupBeforeDatabase.Length + 3];
        cleanupActions[0] = () => base.Dispose(disposing);
        cleanupBeforeDatabase.CopyTo(cleanupActions, 1);
        cleanupActions[^2] = () => TestTempArtifacts.DeleteSqliteDatabase(dbPath);
        cleanupActions[^1] = _temp.Dispose;
        TestTempArtifacts.CleanupAll(cleanupActions);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
                _temp.Dispose();
        }
    }
}

public static class TestTempArtifacts
{
    internal const int MaxDeleteAttempts = 5;
    internal static readonly TimeSpan DeleteRetryBackoff = TimeSpan.FromMilliseconds(20);

    public static void DeleteSqliteDatabase(string dbPath)
    {
        CleanupAll(
            () => DeleteFile(dbPath),
            () => DeleteFile(dbPath + "-wal"),
            () => DeleteFile(dbPath + "-shm"));
    }

    public static void DeleteDirectory(string path)
    {
        var fullPath = GetOwnedTempPath(path);
        Retry(() =>
        {
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        });
    }

    public static void DeleteFile(string path)
    {
        var fullPath = GetOwnedTempPath(path);
        Retry(() =>
        {
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        });
    }

    public static void CleanupAll(params Action[] cleanupActions)
    {
        ArgumentNullException.ThrowIfNull(cleanupActions);

        List<Exception>? failures = null;
        foreach (var cleanupAction in cleanupActions)
        {
            try
            {
                cleanupAction();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is null)
            return;

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();

        throw new AggregateException("One or more test artifact cleanup operations failed.", failures);
    }

    internal static void Retry(Action action)
    {
        for (var attempt = 1; attempt <= MaxDeleteAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < MaxDeleteAttempts)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < MaxDeleteAttempts)
            {
            }

            Thread.Sleep(DeleteRetryBackoff * attempt);
        }
    }

    private static string GetOwnedTempPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Test cleanup path must be non-empty.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var tempRoot = EnsureTrailingSeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(tempRoot, comparison) || fullPath.Length == tempRoot.Length)
            throw new ArgumentException("Test cleanup path must be under the process temp directory.", nameof(path));

        var relativePath = fullPath[tempRoot.Length..];
        var topLevelSegment = FirstPathSegment(relativePath);
        if (!IsCodeyBoxTempRoot(topLevelSegment))
        {
            throw new ArgumentException(
                "Test cleanup path must be rooted in a CodeyBox-owned temp directory.",
                nameof(path));
        }

        return fullPath;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return path;

        return path + Path.DirectorySeparatorChar;
    }

    private static string FirstPathSegment(string relativePath)
    {
        var firstSeparator = relativePath.IndexOf(Path.DirectorySeparatorChar);
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            var alternateSeparator = relativePath.IndexOf(Path.AltDirectorySeparatorChar);
            if (alternateSeparator >= 0 && (firstSeparator < 0 || alternateSeparator < firstSeparator))
                firstSeparator = alternateSeparator;
        }

        return firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
    }

    private static bool IsCodeyBoxTempRoot(string topLevelSegment)
        => topLevelSegment.StartsWith("codeybox-", StringComparison.Ordinal)
            || topLevelSegment.StartsWith("cb-", StringComparison.Ordinal);
}

internal sealed class OwnedPipelineArtifacts : IDisposable
{
    private readonly bool _ownsStateDbPath;
    private bool _disposed;

    public OwnedPipelineArtifacts(string gitRoot, string? stateDbPath = null, bool ownsStateDbPath = true)
    {
        GitRoot = gitRoot;
        StateDbPath = stateDbPath;
        _ownsStateDbPath = ownsStateDbPath;
    }

    public string GitRoot { get; }
    public string? StateDbPath { get; }
    public string RequiredStateDbPath =>
        StateDbPath ?? throw new InvalidOperationException("This test artifact bundle does not own a state database path.");

    public void Dispose()
    {
        if (_disposed)
            return;

        TestTempArtifacts.CleanupAll(
            () =>
            {
                if (_ownsStateDbPath && StateDbPath is not null)
                    TestTempArtifacts.DeleteSqliteDatabase(StateDbPath);
            },
            () => TestTempArtifacts.DeleteDirectory(GitRoot));
        _disposed = true;
    }
}
