using System.Diagnostics;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the required-build shell guard that makes NuGet's user-settings
/// directory (<c>$HOME/.nuget/NuGet</c>) usable when the sandbox ships it owned
/// by another uid or at mode 000, which otherwise aborts <c>dotnet restore</c>
/// with an "unauthorized access" error before any project builds -- failing the
/// gate for reasons unrelated to the diff under review. When <c>$HOME</c> is
/// writable the guard REPAIRS <c>$HOME/.nuget</c> in place (so the separate
/// build-warnings-as-errors / test gates that reuse the same sandbox also see a
/// healed home); when even <c>$HOME</c> is unwritable it falls back to RELOCATING
/// <c>$HOME</c>. A healthy home is left untouched.
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
    public async Task BuildScript_InaccessibleNuGetSettingsDirectory_RepairsInPlace()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permission bits only.

        var home = CreateHomeWithSettingsDirectory(mode000: true);
        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        // $HOME is writable, so the broken ~/.nuget is repaired in place rather
        // than relocated: dotnet runs under the ORIGINAL HOME (so sibling gates
        // reusing this sandbox see the healed home too), and its settings
        // directory is now usable.
        Assert.Equal(home, invokedHome);
        AssertSettingsDirectoryUsable(home);
    }

    [Fact]
    public async Task BuildScript_UnreadableNuGetConfigFile_RepairsInPlace()
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
        Assert.Equal(home, invokedHome);
        AssertSettingsDirectoryUsable(home);
    }

    [Fact]
    public async Task BuildScript_NuGetSettingsDirectoryAbsentUnderUnwritableDotNuget_RepairsInPlace()
    {
        if (OperatingSystem.IsWindows())
            return;

        // The observed audit-sandbox shape: ~/.nuget exists but is owned by
        // another uid at mode 755 (traversable, NOT writable) and its NuGet
        // settings subdirectory has not been created. NuGet must create that
        // subdirectory on first restore, which fails with "unauthorized access"
        // -- yet $HOME itself is writable, so the foreign-owned ~/.nuget can be
        // renamed aside and recreated uid-owned without root.
        var home = Path.Combine(_root, "home-unwritable-dotnuget-" + Guid.NewGuid().ToString("N")[..8]);
        var dotNuget = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(dotNuget);
        // No "NuGet" subdirectory. Make ~/.nuget traversable+readable but not
        // writable, mirroring a root-owned mode-755 directory.
        File.SetUnixFileMode(dotNuget,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        Assert.Equal(home, invokedHome);
        AssertSettingsDirectoryUsable(home);
    }

    [Fact]
    public async Task BuildScript_SettingsPathOccupiedByNonDirectory_RepairsInPlace()
    {
        if (OperatingSystem.IsWindows())
            return;

        // A false-negative that permission-bit inference misses but the write
        // probe catches: ~/.nuget is writable, yet its "NuGet" settings path is
        // occupied by a regular file rather than a directory. A bit check reads
        // the writable parent and concludes NuGet can proceed; in reality NuGet
        // cannot create/populate a directory there and restore fails. The probe
        // (mkdir -p over the file) fails and the in-place repair renames the
        // whole ~/.nuget aside so a fresh, usable settings directory replaces it.
        var home = Path.Combine(_root, "home-settings-is-file-" + Guid.NewGuid().ToString("N")[..8]);
        var dotNuget = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(dotNuget);
        await File.WriteAllTextAsync(Path.Combine(dotNuget, "NuGet"), "not a directory\n");

        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        Assert.Equal(home, invokedHome);
        AssertSettingsDirectoryUsable(home);
    }

    [Fact]
    public async Task BuildScript_UnwritableHome_FallsBackToRelocation()
    {
        if (OperatingSystem.IsWindows())
            return;

        // When $HOME itself is not writable the in-place repair cannot rename
        // ~/.nuget aside, so the guard must fall back to relocating HOME. This is
        // the shape where the sandbox owns even $HOME with another uid.
        var home = Path.Combine(_root, "home-readonly-" + Guid.NewGuid().ToString("N")[..8]);
        var nugetDir = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(nugetDir);
        File.SetUnixFileMode(nugetDir, UnixFileMode.None); // unusable settings dir
        // Make $HOME read-only so ~/.nuget cannot be renamed aside in place.
        File.SetUnixFileMode(home,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var (exitCode, invokedHome, buildTargets) = await RunBuildScriptAsync(home);

        Assert.Equal(0, exitCode);
        Assert.Contains("./repo.slnx", buildTargets);
        Assert.NotNull(invokedHome);
        Assert.NotEqual(home, invokedHome); // relocated, not repaired in place
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
    /// Asserts that after an in-place repair the NuGet settings directory under
    /// <paramref name="home"/> is a real directory that the build uid can write
    /// to -- i.e. NuGet's first-restore create-and-write would now succeed.
    /// </summary>
    private static void AssertSettingsDirectoryUsable(string home)
    {
        var settings = Path.Combine(home, ".nuget", "NuGet");
        Assert.True(Directory.Exists(settings), $"settings directory missing: {settings}");
        var probe = Path.Combine(settings, ".usable-probe");
        File.WriteAllText(probe, string.Empty); // throws if not writable -> test fails
        File.Delete(probe);
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
                // Restore write on the home itself (one test strips write from
                // $HOME to force relocation) plus every ~/.nuget* directory and
                // its NuGet subdirectory. In-place repair renames the original
                // ~/.nuget to ~/.nuget.codeybox-foreign-owned.<pid>, so the
                // stripped (mode-000 / mode-555) directories can live under an
                // aside path too -- enumerate every ".nuget*" child rather than
                // assuming a fixed layout.
                RestoreWritable(homeDir);
                foreach (var nugetDir in SafeEnumerateDirectories(homeDir, ".nuget*"))
                {
                    RestoreWritable(nugetDir);
                    RestoreWritable(Path.Combine(nugetDir, "NuGet"));
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

    private static void RestoreWritable(string dir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(dir))
            return;
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
            // Best-effort restore; the recursive delete surfaces anything left.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string parent, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(parent, pattern);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
