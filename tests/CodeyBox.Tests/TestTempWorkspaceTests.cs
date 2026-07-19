using System.Net.Sockets;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the temp-hygiene mechanism that bounds the suite's peak temp-disk
/// usage: recursive deletion of a case's scratch tree (including SQLite
/// <c>-wal</c>/<c>-shm</c> siblings) and the environment redirection that homes
/// all <see cref="Path.GetTempPath"/> usage inside that tree.
/// </summary>
public sealed class TestTempWorkspaceTests
{
    [Fact]
    public void TryDeleteDirectory_RemovesDbWalShmAndNestedTree()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "cb-tw-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        var db = Path.Combine(baseDir, "work_items.db");
        File.WriteAllText(db, "database");
        File.WriteAllText(db + "-wal", "write-ahead-log");
        File.WriteAllText(db + "-shm", "shared-memory");

        var nested = Path.Combine(baseDir, "git-work", "logs", "agent-stream");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "stream.jsonl"), "{\"type\":\"result\"}");

        Assert.True(TestTempWorkspace.TryDeleteDirectory(baseDir));
        Assert.False(Directory.Exists(baseDir));
    }

    [Fact]
    public void TryDeleteDirectory_MissingDirectory_ReportsSuccess()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cb-tw-missing-" + Guid.NewGuid().ToString("N"));

        Assert.False(Directory.Exists(missing));
        Assert.True(TestTempWorkspace.TryDeleteDirectory(missing));
    }

    [Fact]
    public void RedirectThenWriteThenWipe_HomesTempInsideCaseDirAndReclaimsIt()
    {
        // Build an isolated run root; nest it under the current (framework-provided)
        // temp dir so the framework's own per-case wipe reclaims any leftovers too.
        var runRoot = Path.Combine(Path.GetTempPath(), "cb-tw-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);

        var originalTemp = Path.GetTempPath();
        try
        {
            var caseDir = TestTempWorkspace.CreateTestCaseDirectory(runRoot);
            Assert.True(Directory.Exists(caseDir));
            Assert.StartsWith(runRoot, caseDir, StringComparison.Ordinal);

            TestTempWorkspace.PointTempEnvironmentAt(caseDir);

            // Path.GetTempPath now resolves live to the case dir, so an unmodified
            // component writing to temp lands inside it — the property the whole
            // mechanism relies on to attribute (and reclaim) per-test artifacts.
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(caseDir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));

            var db = Path.Combine(Path.GetTempPath(), $"scratch-{Guid.NewGuid():N}.db");
            File.WriteAllText(db, "x");
            File.WriteAllText(db + "-wal", "x");
            Assert.True(File.Exists(db));
            Assert.Equal(caseDir, Path.GetDirectoryName(db));

            Assert.True(TestTempWorkspace.TryDeleteDirectory(caseDir));
            Assert.False(Directory.Exists(caseDir));
        }
        finally
        {
            TestTempWorkspace.PointTempEnvironmentAt(originalTemp);
            TestTempWorkspace.TryDeleteDirectory(runRoot);
        }
    }

    [Fact]
    public void NestedTempArtifactsFitWithinUnixDomainSocketPathLimit()
    {
        // Regression: the per-run + per-case temp nesting must stay short enough
        // that a Unix domain socket bound under a further CreateTempSubdirectory
        // (as IncusBaselineProvisioningTests does when probing special-file
        // rejection) still fits the OS 108-character path cap. Long GUID names at
        // both levels previously overflowed it and failed unrelated tests.
        if (!OperatingSystem.IsLinux())
            return;

        const int UnixDomainSocketPathLimit = 108;

        var caseDir = TestTempWorkspace.CreateTestCaseDirectory(TestTempWorkspace.RunRoot);
        var originalTemp = Path.GetTempPath();
        try
        {
            // Model the provisioning workspace: a CreateTempSubdirectory rooted at
            // the redirected per-case temp dir, holding a cache source directory.
            TestTempWorkspace.PointTempEnvironmentAt(caseDir);
            var provisionDir = Directory.CreateTempSubdirectory("codeybox-incus-provision-").FullName;
            var cacheSource = Directory.CreateDirectory(Path.Combine(provisionDir, "cache-source")).FullName;
            var socketPath = Path.Combine(cacheSource, "cache.sock");

            Assert.True(
                socketPath.Length <= UnixDomainSocketPathLimit,
                $"socket path is {socketPath.Length} chars (limit {UnixDomainSocketPathLimit}): {socketPath}");

            // Prove it end-to-end: the bind that previously threw now succeeds.
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));

            TestTempWorkspace.TryDeleteDirectory(provisionDir);
        }
        finally
        {
            TestTempWorkspace.PointTempEnvironmentAt(originalTemp);
        }
    }

    [Fact]
    public void EndTestCase_NullDirectory_RestoresRunRootEnvironmentAndSucceeds()
    {
        var runRoot = TestTempWorkspace.RunRoot;
        Assert.False(string.IsNullOrEmpty(runRoot));

        // Simulate a case body having pointed temp somewhere else.
        var strayDir = Path.Combine(runRoot, "stray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(strayDir);
        TestTempWorkspace.PointTempEnvironmentAt(strayDir);
        try
        {
            Assert.True(TestTempWorkspace.EndTestCase(null));

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(runRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
        }
        finally
        {
            TestTempWorkspace.PointTempEnvironmentAt(runRoot);
            TestTempWorkspace.TryDeleteDirectory(strayDir);
        }
    }
}
