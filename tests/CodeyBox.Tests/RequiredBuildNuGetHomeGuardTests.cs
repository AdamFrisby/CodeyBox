using System.Diagnostics;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the required-build shell guard that relocates <c>$HOME</c> when
/// NuGet's user-settings directory (<c>$HOME/.nuget/NuGet</c>) is inaccessible.
/// Audit sandboxes have shipped that directory owned by another uid or mode
/// 000, which aborts <c>dotnet restore</c> with an "unauthorized access" error
/// before any project builds -- failing the gate for reasons unrelated to the
/// diff under review. The guard must detect that and move <c>$HOME</c> to a
/// writable scratch directory, while leaving a healthy <c>$HOME</c> untouched.
///
/// The tests run the real <see cref="SandboxRequiredBuildVerifier.BuildScript"/>
/// through <c>/bin/sh</c> with a fake <c>dotnet</c> on <c>PATH</c> that records
/// the <c>$HOME</c> it was invoked with, so the assertion reflects the value the
/// production script actually produced.
/// </summary>
public sealed class RequiredBuildNuGetHomeGuardTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-nuget-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public RequiredBuildNuGetHomeGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BuildScript_InaccessibleNuGetSettingsDirectory_RelocatesHome()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permission bits only.

        var home = CreateHomeWithSettingsDirectory(mode000: true);
        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        Assert.NotNull(invokedHome);
        // The relocation must have moved HOME off the broken directory so the
        // real dotnet restore never touches the inaccessible settings path.
        Assert.NotEqual(home, invokedHome);
    }

    [Fact]
    public async Task BuildScript_UnreadableNuGetConfigFile_RelocatesHome()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Settings directory is traversable but the config file itself is
        // unreadable -- the other shape of the sandbox failure.
        var home = Path.Combine(_root, "home-unreadable-config");
        var nugetDir = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(nugetDir);
        var config = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration/>\n");
        File.SetUnixFileMode(config, UnixFileMode.None);

        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        Assert.NotEqual(home, invokedHome);
    }

    [Fact]
    public async Task BuildScript_HealthyNuGetHome_IsNotRelocated()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = CreateHomeWithSettingsDirectory(mode000: false);
        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        // A usable settings location must be left in place so the pre-populated
        // package cache under the real HOME is reused.
        Assert.Equal(home, invokedHome);
    }

    private string CreateHomeWithSettingsDirectory(bool mode000)
    {
        var home = Path.Combine(_root, "home-" + Guid.NewGuid().ToString("N")[..8]);
        var nugetDir = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(nugetDir);
        File.WriteAllText(Path.Combine(nugetDir, "NuGet.Config"), "<configuration/>\n");
        if (mode000 && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(nugetDir, UnixFileMode.None);
        return home;
    }

    /// <summary>
    /// Runs the real build script under <paramref name="home"/> with a fake
    /// <c>dotnet</c> that records the effective HOME and the build target.
    /// Returns the script exit code, the HOME dotnet observed (or null if it
    /// was never invoked), and every build target passed to dotnet.
    /// </summary>
    private async Task<(int ExitCode, string? InvokedHome, IReadOnlyList<string> BuildTargets)> RunBuildScriptAsync(string home)
    {
        var repo = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "repo.slnx"), "# marker\n");

        var binDir = Path.Combine(_root, "bin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(binDir);
        var homeLog = Path.Combine(_root, "home-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var targetLog = Path.Combine(_root, "target-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var fakeDotnet = Path.Combine(binDir, "dotnet");
        await File.WriteAllTextAsync(fakeDotnet, $$"""
            #!/bin/sh
            printf '%s\n' "$HOME" >> "{{homeLog}}"
            if [ "$1" = "build" ]; then
              printf '%s\n' "$2" >> "{{targetLog}}"
            fi
            echo "Build succeeded."
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeDotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(SandboxRequiredBuildVerifier.BuildScript);
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = binDir + Path.PathSeparator + "/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = home;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);

        string? invokedHome = File.Exists(homeLog)
            ? (await File.ReadAllLinesAsync(homeLog)).FirstOrDefault()
            : null;
        var targets = File.Exists(targetLog)
            ? await File.ReadAllLinesAsync(targetLog)
            : Array.Empty<string>();

        return (proc.ExitCode, invokedHome, targets);
    }

    public void Dispose()
    {
        // Restore permissions on any mode-000 settings directory so the
        // recursive cleanup can traverse and remove it. Enumerating only the
        // top-level home directories (not SearchOption.AllDirectories) avoids
        // descending into the inaccessible directory itself.
        if (!OperatingSystem.IsWindows() && Directory.Exists(_root))
        {
            foreach (var homeDir in Directory.EnumerateDirectories(_root))
            {
                var settingsDir = Path.Combine(homeDir, ".nuget", "NuGet");
                if (!Directory.Exists(settingsDir))
                    continue;
                try
                {
                    File.SetUnixFileMode(settingsDir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException)
                {
                    // Best-effort restore; the delete below surfaces anything left.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
