using System.Diagnostics;
using System.Globalization;
using CodeyBox.Harness;

namespace CodeyBox.Tests;

/// <summary>
/// Failure-mode coverage for <c>admin-seeded serve</c> supervision: an early
/// child exit must fail fast with the child name, exit code, and captured-log
/// tail (never a misleading readiness timeout), the readiness poll must stop
/// once a child has exited, and a missing Release build must be reported up
/// front with the exact build command. All doubles are in-process (fake
/// children, stub probes, temp directories) — no real processes or servers.
/// </summary>
public sealed class AdminSeededServeTests
{
    private sealed class FakeServeChild : AdminSeededCommand.IServeChild
    {
        public FakeServeChild(string name, bool hasExited, int exitCode, string logPath)
        {
            Name = name;
            HasExited = hasExited;
            ExitCode = exitCode;
            LogPath = logPath;
        }

        public string Name { get; }
        public bool HasExited { get; set; }
        public int ExitCode { get; }
        public string LogPath { get; }
    }

    [Fact]
    public async Task ExitedChildFailsFastWithNameExitCodeAndLogTail()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "codeybox-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "codeybox-api.log");
        await File.WriteAllTextAsync(logPath, "starting up\nUnhandled exception: No such file or directory BOOM-marker-7f3a\n");
        try
        {
            var child = new FakeServeChild("codeybox-api", hasExited: true, exitCode: 1, logPath);
            var budget = TimeSpan.FromMinutes(2);
            var stopwatch = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<AdminSeededCommand.ChildExitException>(() =>
                AdminSeededCommand.WaitForReadyAsync(
                    _ => Task.FromResult((false, false)),
                    [child],
                    new StringWriter(CultureInfo.InvariantCulture),
                    TimeSpan.FromMilliseconds(10),
                    CancellationToken.None));
            stopwatch.Stop();

            Assert.Equal("codeybox-api", ex.ChildName);
            Assert.Equal(1, ex.ExitCode);
            Assert.Contains("codeybox-api", ex.Message, StringComparison.Ordinal);
            Assert.Contains("1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("BOOM-marker-7f3a", ex.Message, StringComparison.Ordinal);
            Assert.True(
                stopwatch.Elapsed < budget / 2,
                $"Supervisor must fail fast, but took {stopwatch.Elapsed} of a {budget} budget.");
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadinessPollStopsOnceChildHasExited()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "codeybox-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "codeybox-admin-web.log");
        await File.WriteAllTextAsync(logPath, "child died mid-poll\n");
        try
        {
            var child = new FakeServeChild("codeybox-admin-web", hasExited: false, exitCode: 3, logPath);
            var probes = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ex = await Assert.ThrowsAsync<AdminSeededCommand.ChildExitException>(() =>
                AdminSeededCommand.WaitForReadyAsync(
                    _ =>
                    {
                        probes++;
                        child.HasExited = true;
                        return Task.FromResult((false, false));
                    },
                    [child],
                    new StringWriter(CultureInfo.InvariantCulture),
                    TimeSpan.FromMilliseconds(1),
                    timeout.Token));

            Assert.Equal("codeybox-admin-web", ex.ChildName);
            Assert.Equal(1, probes);
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public void MissingReleaseOutputsPrecheckNamesBuildCommand()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "codeybox-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(repoRoot);
        try
        {
            var missing = AdminSeededCommand.FindMissingReleaseOutputs(repoRoot);
            Assert.NotEmpty(missing);

            var message = AdminSeededCommand.FormatMissingReleaseMessage(missing);
            Assert.Contains(AdminSeededCommand.RequiredBuildCommand, message, StringComparison.Ordinal);
            Assert.Contains("codeybox-api", message, StringComparison.Ordinal);
            Assert.Contains("codeybox-admin-web", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void PresentReleaseOutputsPassPrecheck()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "codeybox-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            foreach (var (_, rel) in AdminSeededCommand.ServeChildProjects)
            {
                var dir = Path.Combine(repoRoot, rel, "bin", "Release");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "placeholder.dll"), "x");
            }

            Assert.Empty(AdminSeededCommand.FindMissingReleaseOutputs(repoRoot));
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogTailReturnsLastLinesBounded()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "codeybox-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "codeybox-api.log");
        var lines = Enumerable.Range(0, 100).Select(i => $"line-{i.ToString("D3", CultureInfo.InvariantCulture)}");
        await File.WriteAllTextAsync(logPath, string.Join('\n', lines) + "\n");
        try
        {
            var tail = AdminSeededCommand.ReadLogTail(logPath, maxLines: 5, maxChars: 4000);
            Assert.Contains("line-099", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("line-000", tail, StringComparison.Ordinal);
            Assert.True(tail.Length <= 4000, $"Log tail must be bounded, was {tail.Length} chars.");
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }
}
