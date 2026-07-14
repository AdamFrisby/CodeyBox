using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <see cref="NuGetHomeGuard"/>: the command-selection rule and the
/// package-cache preservation branches of the shared relocation preamble. The
/// preamble is run through a real <c>/bin/sh</c> so the assertions reflect what
/// the production shell actually does when the sandbox ships an unusable
/// <c>$HOME/.nuget</c>.
/// </summary>
public sealed class NuGetHomeGuardTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-nuget-home-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public NuGetHomeGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PreambleForCommand_OnlyGuardsDotnetInvocations()
    {
        Assert.Same(NuGetHomeGuard.RelocationPreamble,
            NuGetHomeGuard.PreambleForCommand(["dotnet", "build"]));
        Assert.Null(NuGetHomeGuard.PreambleForCommand(["npm", "test"]));
        Assert.Null(NuGetHomeGuard.PreambleForCommand([]));
    }

    [Fact]
    public async Task Preamble_WritableCache_ExportsNugetPackagesAndRelocatesHome()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permission bits only.

        // Unusable settings dir (mode 000) forces relocation; a writable cache is
        // present under the real HOME.
        var home = Path.Combine(_root, "writable-" + Guid.NewGuid().ToString("N")[..8]);
        var settings = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(settings);
        File.SetUnixFileMode(settings, UnixFileMode.None);
        var cache = Path.Combine(home, ".nuget", "packages");
        Directory.CreateDirectory(cache); // owner-writable by default

        var (invokedHome, nugetPackages, fallbackConfig) = await RunPreambleAsync(home);

        Assert.NotEqual(home, invokedHome);          // relocated
        Assert.Equal(cache, nugetPackages);          // writable cache -> global folder
        Assert.Null(fallbackConfig);                 // no fallback config needed
    }

    [Fact]
    public async Task Preamble_ReadOnlyCache_WritesFallbackFolderConfigInsteadOfNugetPackages()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Unusable settings dir + a readable-but-NOT-writable cache (a root-owned
        // shared mount). NuGet cannot use a read-only global packages folder, so
        // the guard must register it as a read-only fallback folder instead.
        var home = Path.Combine(_root, "readonly-" + Guid.NewGuid().ToString("N")[..8]);
        var settings = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(settings);
        var cache = Path.Combine(home, ".nuget", "packages");
        Directory.CreateDirectory(Path.Combine(cache, "somepkg")); // non-empty
        File.SetUnixFileMode(cache,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(settings, UnixFileMode.None); // unusable -> relocate

        var (invokedHome, nugetPackages, fallbackConfig) = await RunPreambleAsync(home);

        Assert.NotEqual(home, invokedHome);          // relocated
        Assert.Null(nugetPackages);                  // cannot write a read-only cache
        Assert.NotNull(fallbackConfig);
        Assert.Contains("<fallbackPackageFolders>", fallbackConfig);
        Assert.Contains(cache, fallbackConfig);      // points at the read-only cache
    }

    [Fact]
    public async Task Preamble_HealthyHome_DoesNotRelocateOrTouchPackages()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(_root, "healthy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(home, ".nuget", "NuGet"));

        var (invokedHome, nugetPackages, fallbackConfig) = await RunPreambleAsync(home);

        Assert.Equal(home, invokedHome);             // untouched
        Assert.Null(nugetPackages);
        Assert.Null(fallbackConfig);
    }

    /// <summary>
    /// Runs the real preamble under <paramref name="home"/> and reports the
    /// effective HOME afterwards, the exported NUGET_PACKAGES (or null), and the
    /// contents of any fallback NuGet.Config the guard wrote into the relocated
    /// HOME (or null if none).
    /// </summary>
    private async Task<(string InvokedHome, string? NugetPackages, string? FallbackConfig)> RunPreambleAsync(string home)
    {
        var script = "set -eu\n" + NuGetHomeGuard.RelocationPreamble + "\n" +
            "printf 'HOME=%s\\n' \"$HOME\"\n" +
            "printf 'NUGET_PACKAGES=%s\\n' \"${NUGET_PACKAGES:-}\"\n" +
            "if [ -f \"$HOME/.nuget/NuGet/NuGet.Config\" ]; then " +
            "printf 'FALLBACK_BEGIN\\n'; cat \"$HOME/.nuget/NuGet/NuGet.Config\"; printf 'FALLBACK_END\\n'; fi\n";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = "/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = home;

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = await stdoutTask;

        Assert.Equal(0, proc.ExitCode);

        var invokedHome = ExtractLine(stdout, "HOME=");
        var nugetPackagesRaw = ExtractLine(stdout, "NUGET_PACKAGES=");
        var nugetPackages = string.IsNullOrEmpty(nugetPackagesRaw) ? null : nugetPackagesRaw;

        string? fallback = null;
        var begin = stdout.IndexOf("FALLBACK_BEGIN\n", StringComparison.Ordinal);
        var end = stdout.IndexOf("FALLBACK_END", StringComparison.Ordinal);
        if (begin >= 0 && end > begin)
        {
            var start = begin + "FALLBACK_BEGIN\n".Length;
            fallback = stdout[start..end];
        }

        return (invokedHome!, nugetPackages, fallback);
    }

    private static string? ExtractLine(string stdout, string prefix)
    {
        foreach (var line in stdout.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }
        return null;
    }

    public void Dispose()
    {
        // Restore write on the directories the tests strip it from (the mode-000
        // settings subdirectory and the read-only cache) so the recursive delete
        // can traverse them. Enumerate only top-level homes and touch known
        // subpaths rather than descending -- SearchOption.AllDirectories would
        // itself throw on the inaccessible mode-000 directory.
        if (!OperatingSystem.IsWindows() && Directory.Exists(_root))
        {
            foreach (var homeDir in Directory.EnumerateDirectories(_root))
            {
                foreach (var dir in new[]
                         {
                             Path.Combine(homeDir, ".nuget"),
                             Path.Combine(homeDir, ".nuget", "NuGet"),
                             Path.Combine(homeDir, ".nuget", "packages"),
                         })
                {
                    if (!Directory.Exists(dir))
                        continue;
                    try
                    {
                        File.SetUnixFileMode(dir,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }
}
