using System.Diagnostics;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the committed <c>scripts/reclaim-nuget-home.sh</c> directly against
/// temporary NuGet homes, covering its no-op / reclaim / operator-only branches
/// without provisioning a sandbox. The script is the one lever a BARE
/// <c>dotnet build</c> has to survive a root-owned, unwritable <c>~/.nuget</c>
/// (an MSBuild InitialTarget cannot repoint HOME but can make the directory NuGet
/// reads writable), so its behaviour is verified through real filesystem effects.
/// POSIX-only: the defect and its repair are Unix-permission specific.
/// </summary>
public sealed class ReclaimNuGetHomeScriptTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-reclaim-nuget-").FullName;

    public void Dispose()
    {
        // Restore write bits any test may have cleared so the tree can be deleted.
        RestoreWritableRecursive(_workspace);
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void AlreadyUsableHome_IsNoOp()
    {
        var nugetHome = Path.Combine(_workspace, ".nuget");
        Directory.CreateDirectory(Path.Combine(nugetHome, "NuGet"));

        var exit = RunReclaim(nugetHome);

        Assert.Equal(0, exit);
        // A usable home is left exactly as-is: no reclaim, no aside copy.
        Assert.True(Directory.Exists(Path.Combine(nugetHome, "NuGet")));
        Assert.Empty(AsideDirs(nugetHome));
    }

    [Fact]
    public void UnwritableHome_IsReclaimedNonDestructively()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        var nugetHome = Path.Combine(_workspace, ".nuget");
        Directory.CreateDirectory(nugetHome);
        var marker = Path.Combine(nugetHome, "packages-marker");
        File.WriteAllText(marker, "base-image-cache");
        // Deny writes on the home itself so "mkdir $home/NuGet" fails exactly as a
        // root-owned ~/.nuget denies the unprivileged build user; the parent
        // workspace stays writable so the reclaim can move it aside.
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var exit = RunReclaim(nugetHome);

        Assert.Equal(0, exit);
        // A fresh, writable home is in place so restore can create its config.
        var nugetDir = Path.Combine(nugetHome, "NuGet");
        Assert.True(Directory.Exists(nugetDir));
        var probe = Path.Combine(nugetDir, "probe");
        File.WriteAllText(probe, "writable");
        Assert.True(File.Exists(probe));
        // Non-destructive: the original home (with its cache) is preserved aside.
        var aside = Assert.Single(AsideDirs(nugetHome));
        Assert.Equal("base-image-cache", File.ReadAllText(Path.Combine(aside, "packages-marker")));
    }

    [Fact]
    public void UnreadableConfigInWritableDir_IsReclaimed()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        var nugetHome = Path.Combine(_workspace, ".nuget");
        var nugetDir = Path.Combine(nugetHome, "NuGet");
        Directory.CreateDirectory(nugetDir);
        var config = Path.Combine(nugetDir, "NuGet.Config");
        File.WriteAllText(config, "<configuration/>");
        // Writable directory but a root-owned, unreadable config inside — NuGet
        // aborts reading it just like the missing-directory case, so the reclaim
        // must relocate it rather than treat the home as usable.
        File.SetUnixFileMode(config, UnixFileMode.None);

        var exit = RunReclaim(nugetHome);

        Assert.Equal(0, exit);
        // The poisoned config was moved aside and a fresh home put in place.
        Assert.Single(AsideDirs(nugetHome));
        var freshConfig = Path.Combine(nugetHome, "NuGet", "NuGet.Config");
        Assert.False(File.Exists(freshConfig));
    }

    [Fact]
    public void ParentNotWritable_IsLeftForOperator()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        var parent = Path.Combine(_workspace, "readonly-home");
        var nugetHome = Path.Combine(parent, ".nuget");
        Directory.CreateDirectory(nugetHome);
        // Both the home AND its parent are unwritable: only the operator can fix
        // this, so the script must leave everything untouched and exit cleanly.
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var exit = RunReclaim(nugetHome);

            Assert.Equal(0, exit);
            // Nothing created, nothing moved aside — the real error is left for
            // NuGet to surface downstream.
            Assert.False(Directory.Exists(Path.Combine(nugetHome, "NuGet")));
            Assert.Empty(AsideDirs(nugetHome));
        }
        finally
        {
            File.SetUnixFileMode(
                parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static IReadOnlyList<string> AsideDirs(string nugetHome)
    {
        var parent = Path.GetDirectoryName(nugetHome)!;
        var name = Path.GetFileName(nugetHome);
        return Directory.Exists(parent)
            ? Directory.GetDirectories(parent, name + ".root-owned.*")
            : [];
    }

    private int RunReclaim(string nugetHome)
    {
        var script = Path.Combine(FindRepoRoot(), "scripts", "reclaim-nuget-home.sh");
        Assert.True(File.Exists(script), $"reclaim script not found at {script}");

        var psi = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(nugetHome);
        // Isolate the reclaim lock to this test's workspace so parallel test
        // classes cannot contend on a shared /tmp lock.
        psi.Environment["TMPDIR"] = _workspace;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /bin/sh for the reclaim script.");
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "reclaim script did not exit within 60s.");
        Assert.True(
            process.ExitCode == 0,
            $"reclaim script exited {process.ExitCode}; stderr: {stderr}");
        return process.ExitCode;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeyBox.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static void RestoreWritableRecursive(string root)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Prepend(root))
        {
            try
            {
                File.SetUnixFileMode(
                    dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort */ }
        }
    }
}
