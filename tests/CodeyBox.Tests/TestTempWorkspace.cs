namespace CodeyBox.Tests;

/// <summary>
/// Bounds the test process's peak temp-disk usage by giving every test case its
/// own scratch directory that is deleted the moment the case finishes.
///
/// <para>The main suite creates GUID-named SQLite databases (plus their
/// <c>-wal</c>/<c>-shm</c> siblings) and git/log/agent-stream directories under
/// <see cref="Path.GetTempPath"/> per test, and cleans them up only on a
/// best-effort basis. Undeleted residue accumulates monotonically across the
/// run, and on a space-constrained (tmpfs/RAM-backed) CI temp filesystem the
/// disk fills mid-run — after which SQLite can no longer create or write tables,
/// producing cascades of <c>no such table: work_items</c> and
/// "test-project cannot be removed" failures across unrelated tests.</para>
///
/// <para>This type redirects the process temp environment variables into a
/// single per-run root once at assembly load, then swaps them to a fresh
/// per-case subdirectory around each test case and recursively deletes that
/// subdirectory afterwards. Because the suite runs strictly sequentially
/// (<see cref="XunitAssemblyConfig"/> sets
/// <c>DisableTestParallelization = true</c>), at most one case executes at a
/// time, so mutating the process-global temp environment per case is race-free.
/// Peak temp usage is thereby bounded to a single case's working set rather than
/// the whole run's cumulative footprint. A process-exit sweep of the run root is
/// the backstop for shared class/collection fixtures created between cases and
/// for any case whose own wipe could not complete.</para>
///
/// <para>The delete and environment helpers are pure with respect to their
/// arguments so they can be exercised directly in unit tests without touching
/// the process-global run-root state.</para>
/// </summary>
internal static class TestTempWorkspace
{
    /// <summary>Temp-directory environment variables, spanning Unix (<c>TMPDIR</c>)
    /// and Windows (<c>TMP</c>/<c>TEMP</c>). <see cref="Path.GetTempPath"/> reads
    /// these live on every call, so redirecting them re-homes all temp usage.</summary>
    private static readonly string[] TempEnvironmentVariables = ["TMPDIR", "TMP", "TEMP"];

    private const string RunRootPrefix = "codeybox-test-run-";
    private const int DeleteAttempts = 5;
    private const int DeleteRetryDelayMs = 25;

    private static string _runRoot = string.Empty;
    private static int _initialized;

    /// <summary>The per-run temp root, or empty before <see cref="Initialize"/> runs.</summary>
    internal static string RunRoot => _runRoot;

    /// <summary>
    /// Creates the per-run temp root under <paramref name="baseTempDirectory"/>
    /// (the process temp directory as it was before any redirection) and points
    /// the process temp environment variables at it. Idempotent — the first call
    /// wins and later calls return the established root unchanged. Returns the
    /// run root.
    /// </summary>
    internal static string Initialize(string baseTempDirectory)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return _runRoot;

        var root = Path.Combine(baseTempDirectory, RunRootPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _runRoot = root;
        PointTempEnvironmentAt(root);
        return root;
    }

    /// <summary>
    /// Allocates a fresh scratch directory for one test case and redirects the
    /// process temp environment at it. Must be paired with <see cref="EndTestCase"/>.
    /// Returns the directory, or <c>null</c> when the workspace is not initialized
    /// or the directory could not be created (e.g. the temp disk is already full);
    /// in the <c>null</c> case the environment is left pointing at the run root so
    /// the case still runs.
    /// </summary>
    internal static string? BeginTestCase()
    {
        if (Volatile.Read(ref _initialized) == 0)
            return null;

        try
        {
            var dir = CreateTestCaseDirectory(_runRoot);
            PointTempEnvironmentAt(dir);
            return dir;
        }
        catch (IOException)
        {
            RestoreEnvironmentToRunRoot();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            RestoreEnvironmentToRunRoot();
            return null;
        }
    }

    /// <summary>
    /// Restores the process temp environment to the run root and deletes the
    /// case's scratch directory. Best-effort: returns <c>true</c> when the
    /// directory is gone (or was never allocated), <c>false</c> when residue
    /// survived every delete attempt, so the caller can surface a diagnostic
    /// without failing the test.
    /// </summary>
    internal static bool EndTestCase(string? testCaseDirectory)
    {
        RestoreEnvironmentToRunRoot();
        return testCaseDirectory is null || TryDeleteDirectory(testCaseDirectory);
    }

    /// <summary>
    /// Recursively deletes the entire run root. Safe to call at process exit as a
    /// backstop; swallows failures because the process is ending regardless.
    /// </summary>
    internal static void WipeRunRoot()
    {
        var root = _runRoot;
        if (!string.IsNullOrEmpty(root))
            TryDeleteDirectory(root);
    }

    /// <summary>Creates and returns a fresh GUID-named subdirectory of <paramref name="runRoot"/>.</summary>
    internal static string CreateTestCaseDirectory(string runRoot)
    {
        var dir = Path.Combine(runRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Points every temp-directory environment variable at <paramref name="directory"/>.</summary>
    internal static void PointTempEnvironmentAt(string directory)
    {
        foreach (var name in TempEnvironmentVariables)
            Environment.SetEnvironmentVariable(name, directory);
    }

    private static void RestoreEnvironmentToRunRoot()
    {
        if (!string.IsNullOrEmpty(_runRoot))
            PointTempEnvironmentAt(_runRoot);
    }

    /// <summary>
    /// Recursively deletes <paramref name="path"/>, retrying a bounded number of
    /// times to ride out transient locks and clearing read-only attributes that
    /// block deletion on Windows. Returns <c>true</c> once the path no longer
    /// exists. Never throws.
    /// </summary>
    internal static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                    return true;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
                // Transient lock (e.g. a not-yet-finalized handle); retry below.
            }
            catch (UnauthorizedAccessException)
            {
                TryClearReadOnlyAttributes(path);
            }

            if (attempt < DeleteAttempts - 1)
                Thread.Sleep(DeleteRetryDelayMs);
        }

        return !Directory.Exists(path);
    }

    private static void TryClearReadOnlyAttributes(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
