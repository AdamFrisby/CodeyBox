using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <c>scripts/repair-nuget-home.sh</c> — the self-heal that lets
/// <c>dotnet restore</c> proceed when a build host provisions the build user's
/// <c>$HOME</c> with a root-owned, non-writable <c>~/.nuget</c> (see the script
/// header and <c>Directory.Build.props</c> for the full rationale). The script
/// is POSIX-shell and mutates a real directory tree, so it is verified through
/// real execution against a fabricated <c>HOME</c> rather than mocks.
///
/// POSIX-only: the defect (unwritable Unix mode bits) and the userspace rename
/// fix do not apply on Windows, matching the <c>Condition</c> on the MSBuild
/// target that invokes the script.
/// </summary>
public sealed class NugetHomeRepairScriptTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-nuget-repair-").FullName;

    public void Dispose()
    {
        // Restore write bits so the temp tree (which the script may leave with a
        // read-only ".unwritable" copy) can be deleted.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var dir in Directory.EnumerateDirectories(_workspace, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(dir, RwxAll); } catch { /* best-effort */ }
            }
        }
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private const UnixFileMode RwxAll =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // r-x------ : readable + traversable by the owner, but NOT writable, which
    // is what makes NuGet's user-settings create/read fail on the real hosts.
    private const UnixFileMode ReadOnlyDir =
        UnixFileMode.UserRead | UnixFileMode.UserExecute;

    [SkippableFact]
    public void UnwritableNugetHome_IsMovedAsideAndRecreatedWithCachePreserved()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only self-heal.");

        var home = Path.Combine(_workspace, "home-broken");
        var nuget = Path.Combine(home, ".nuget");
        var config = Path.Combine(nuget, "NuGet", "NuGet.Config");
        var cachedPackage = Path.Combine(nuget, "packages", "somepkg", "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPackage)!);
        File.WriteAllText(config, "<configuration><!--baked-mirror--></configuration>");
        File.WriteAllText(cachedPackage, "cached-nupkg");

        // Simulate the provisioning defect: ~/.nuget exists but is not writable.
        MakeReadOnly(nuget);
        Assert.False(IsWritable(nuget), "precondition: ~/.nuget must be non-writable");

        var exit = RunScript(home);
        Assert.Equal(0, exit);

        // A fresh, writable ~/.nuget replaced the broken one.
        Assert.True(Directory.Exists(nuget));
        Assert.True(IsWritable(nuget), "repaired ~/.nuget must be writable");

        // Non-destructive: the original tree was renamed aside, not deleted.
        var aside = Directory.EnumerateDirectories(home, ".nuget.unwritable.*").ToList();
        Assert.Single(aside);

        // The baked user config was preserved into the fresh tree.
        Assert.Equal(
            "<configuration><!--baked-mirror--></configuration>",
            File.ReadAllText(Path.Combine(nuget, "NuGet", "NuGet.Config")));

        // The baked package cache is reachable through the fresh tree, so a
        // cache-only / offline restore keeps working.
        Assert.Equal("cached-nupkg", File.ReadAllText(Path.Combine(nuget, "packages", "somepkg", "marker.txt")));
    }

    [SkippableFact]
    public void WritableNugetHome_IsLeftUntouched()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only self-heal.");

        var home = Path.Combine(_workspace, "home-healthy");
        var nuget = Path.Combine(home, ".nuget");
        var config = Path.Combine(nuget, "NuGet", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "healthy");

        var exit = RunScript(home);
        Assert.Equal(0, exit);

        // No aside copy: a healthy home must never be disturbed.
        Assert.Empty(Directory.EnumerateDirectories(home, ".nuget.unwritable.*"));
        Assert.Equal("healthy", File.ReadAllText(config));
    }

    [SkippableFact]
    public void AbsentNugetHome_IsNoOp()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only self-heal.");

        var home = Path.Combine(_workspace, "home-absent");
        Directory.CreateDirectory(home);

        var exit = RunScript(home);
        Assert.Equal(0, exit);

        // The script must not fabricate a ~/.nuget where none existed; NuGet
        // creates its own when HOME is genuinely fresh and writable.
        Assert.False(Directory.Exists(Path.Combine(home, ".nuget")));
        Assert.Empty(Directory.EnumerateDirectories(home, ".nuget.unwritable.*"));
    }

    private static int RunScript(string fakeHome)
    {
        var scriptDir = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Could not locate the repository root — CodeyBox.slnx not found in any ancestor.");
        var script = Path.Combine(scriptDir, "scripts", "repair-nuget-home.sh");
        Assert.True(File.Exists(script), $"repair script missing at {script}");

        var psi = new ProcessStartInfo("sh", $"\"{script}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // Isolate the script from the real user environment.
        psi.Environment["HOME"] = fakeHome;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sh.");
        proc.WaitForExit(30_000);
        Assert.True(proc.HasExited, "repair script did not exit within 30s");
        return proc.ExitCode;
    }

    private static void MakeReadOnly(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(dir, ReadOnlyDir);
    }

    private static bool IsWritable(string dir)
    {
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(dir) & UnixFileMode.UserWrite) != 0;
    }

    private static string? FindAncestorContaining(string start, string fileName)
    {
        var dir = start;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, fileName)))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
