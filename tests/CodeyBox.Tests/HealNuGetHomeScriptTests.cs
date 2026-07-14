using System.Diagnostics;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <c>build/heal-nuget-home.sh</c> through a real <c>/bin/sh</c>: the
/// committed pre-restore hook that lets a raw <c>dotnet build</c> survive a
/// sandbox whose <c>$HOME/.nuget</c> is owned by another uid. The script is run
/// with an isolated <c>HOME</c> so the assertions reflect what the production
/// heal actually does to the filesystem.
/// </summary>
public sealed class HealNuGetHomeScriptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-heal-nuget-" + Guid.NewGuid().ToString("N")[..8]);

    public HealNuGetHomeScriptTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task UnwritableNuGetHome_IsRenamedAsideAndRecreatedWritable()
    {
        if (OperatingSystem.IsWindows())
            return; // The foreign-owned-home failure mode is POSIX-specific.

        var home = Path.Combine(_root, "broken");
        var nuget = Path.Combine(home, ".nuget");
        var packages = Path.Combine(nuget, "packages");
        Directory.CreateDirectory(packages);
        var cacheMarker = Path.Combine(packages, "somepkg.marker");
        await File.WriteAllTextAsync(cacheMarker, "prewarmed");

        // Mimic a foreign-owned .nuget: the build uid owns $HOME (so it can rename
        // .nuget aside) but cannot create the .nuget/NuGet settings directory
        // because .nuget itself is not writable.
        File.SetUnixFileMode(nuget,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var exit = await RunHealAsync(home);
        Assert.Equal(0, exit);

        // The recreated home must be writable for NuGet's first restore.
        var settings = Path.Combine(home, ".nuget", "NuGet");
        Assert.True(Directory.Exists(settings));
        var probe = Path.Combine(settings, "probe.txt");
        await File.WriteAllTextAsync(probe, "ok"); // throws if the dir is unusable
        Assert.True(File.Exists(probe));

        // The pre-populated package cache must survive via the preserved symlink so
        // the restore stays offline.
        var preservedMarker = Path.Combine(home, ".nuget", "packages", "somepkg.marker");
        Assert.True(File.Exists(preservedMarker));
        Assert.Equal("prewarmed", await File.ReadAllTextAsync(preservedMarker));
    }

    [Fact]
    public async Task UsableNuGetHome_IsLeftUntouched()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(_root, "healthy");
        var settings = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(settings);
        var sentinel = Path.Combine(settings, "NuGet.Config");
        await File.WriteAllTextAsync(sentinel, "<configuration/>");

        var exit = await RunHealAsync(home);
        Assert.Equal(0, exit);

        // No repair happened: the original settings file is intact and no
        // "foreign-owned" directory was renamed aside.
        Assert.True(File.Exists(sentinel));
        Assert.Equal("<configuration/>", await File.ReadAllTextAsync(sentinel));
        var asideDirs = Directory.EnumerateDirectories(home, ".nuget.codeybox-foreign-owned.*");
        Assert.Empty(asideDirs);
    }

    private static async Task<int> RunHealAsync(string home)
    {
        var script = LocateScript();
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(script);
        psi.Environment["HOME"] = home;

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string LocateScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "build", "heal-nuget-home.sh");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate build/heal-nuget-home.sh from the test output directory.");
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // Restore any mode-restricted directories so cleanup can delete them.
                foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(d,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
