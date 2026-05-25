using System.Diagnostics;

namespace CodeyBox.Tests;

public sealed class LeakTrackingFrameworkHookTests
{
    private const string SentinelDirectoryEnvironmentVariable = "CODEYBOX_TESTS_LEAK_TRACKING_FRAMEWORK_SENTINEL_DIR";
    private const string SentinelTestName = nameof(Sentinel_LeaksTrackedWatcher_WhenNestedRunnerEnablesIt);

    [Fact]
    public async Task XunitFramework_ReportsTrackedWatcherLeak_AfterRealTestCaseCompletes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codeybox-leak-framework-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = await RunSentinelTestAsync(tempDir);
            var output = result.Stdout + result.Stderr;

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("warning: 1 FileSystemWatcher-backed resource(s) created by tests were not disposed", output);
            Assert.Contains(
                $"created by {typeof(LeakTrackingFrameworkHookTests).FullName}.{SentinelTestName}",
                output);
            Assert.DoesNotContain("created by unknown test", output);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Sentinel_LeaksTrackedWatcher_WhenNestedRunnerEnablesIt()
    {
        var tempDir = Environment.GetEnvironmentVariable(SentinelDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(tempDir))
            return;

        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "sentinel.txt");
        File.WriteAllText(path, "sentinel");

        _ = TestFileSystemWatcherLeakTracker.CreateWatcher(tempDir, Path.GetFileName(path));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSentinelTestAsync(string tempDir)
    {
        var testAssembly = typeof(LeakTrackingFrameworkHookTests).Assembly.Location;
        var filter = $"FullyQualifiedName={typeof(LeakTrackingFrameworkHookTests).FullName}.{SentinelTestName}";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(testAssembly);
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add(filter);
        psi.ArgumentList.Add("--logger");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[SentinelDirectoryEnvironmentVariable] = tempDir;
        psi.Environment[TestFileSystemWatcherLeakTracker.DisableProcessExitReportEnvironmentVariable] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet test.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("Timed out waiting for the nested leak-tracking sentinel test run.", ex);
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
