using System.Diagnostics;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the committed <c>scripts/reclaim-nuget-home.sh</c> operator recovery
/// against a temporary HOME. The script makes an unwritable user-level NuGet home
/// (a root-owned <c>~/.nuget</c>, common in agent sandboxes) writable so a
/// directly-launched <c>dotnet build</c>/<c>dotnet test</c> can materialise
/// NuGet's user-config directory — see docs/audit.md "NuGet-home precondition".
/// The real script file is executed (not a reimplementation), so these tests fail
/// if its healthy-no-op, create, reclaim, or unset-HOME branches regress. POSIX
/// shell, matching the rest of the sandbox test surface.
/// </summary>
public sealed class ReclaimNuGetHomeScriptTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-reclaim-nuget-").FullName;

    public void Dispose()
    {
        // A reclaimed test leaves a read-only backup subtree behind; restore write
        // bits first so the recursive delete can remove it.
        RestoreWritable(_workspace);
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void HealthyWritableHome_IsLeftUntouched()
    {
        var home = CreateHome();
        var configDir = Path.Combine(home, ".nuget", "NuGet");
        Directory.CreateDirectory(configDir);
        var sentinel = Path.Combine(configDir, "NuGet.Config");
        File.WriteAllText(sentinel, "<configuration/>");

        var result = RunScript(home);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains("no action needed", result.Stdout, StringComparison.Ordinal);
        // A legitimate, writable ~/.nuget (real cache/credentials) must survive.
        Assert.True(File.Exists(sentinel), "healthy NuGet.Config was removed");
        Assert.Empty(BackupDirectories(home));
    }

    [Fact]
    public void MissingHome_CreatesWritableNuGetConfigDirectory()
    {
        var home = CreateHome();

        var result = RunScript(home);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}; stderr: {result.Stderr}");
        var configDir = Path.Combine(home, ".nuget", "NuGet");
        Assert.True(Directory.Exists(configDir));
        AssertWritableDirectory(configDir);
    }

    [Fact]
    public void UnwritableHome_IsReclaimedAndPreviousContentsPreserved()
    {
        // POSIX-only: the reclaim trigger relies on Unix directory permissions.
        if (OperatingSystem.IsWindows())
            return;
        // A privileged process can write through any mode bits, so the unwritable
        // branch is unobservable; skip rather than flake when running as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var home = CreateHome();
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(nugetHome);
        // A marker inside the unwritable home must be preserved in the backup,
        // never destroyed (the real contents may be root-owned and unremovable).
        File.WriteAllText(Path.Combine(nugetHome, "marker"), "root-owned");
        // Deny writes on ~/.nuget so "mkdir -p ~/.nuget/NuGet" fails exactly as a
        // root-owned ~/.nuget denies the unprivileged agent user.
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = RunScript(home);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}; stderr: {result.Stderr}");
        Assert.Contains("reclaimed", result.Stdout, StringComparison.Ordinal);
        // A fresh, writable config dir now exists...
        AssertWritableDirectory(Path.Combine(home, ".nuget", "NuGet"));
        // ...and the previous contents were preserved, not destroyed.
        var backup = Assert.Single(BackupDirectories(home));
        Assert.True(
            File.Exists(Path.Combine(backup, "marker")),
            "backup lost the preserved marker");
    }

    [Fact]
    public void UnsetHome_FailsCleanlyWithoutReclaiming()
    {
        // Empty HOME exercises the same "${HOME:-}" guard as a genuinely unset
        // HOME; exit 2 is the script's contract and must not silently pass.
        var result = RunScript(home: "");

        Assert.Equal(2, result.ExitCode);
    }

    private string CreateHome()
    {
        var home = Path.Combine(_workspace, "home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static string[] BackupDirectories(string home) =>
        Directory.GetDirectories(home, ".nuget.unwritable-backup.*");

    private static void AssertWritableDirectory(string dir)
    {
        var probe = Path.Combine(dir, "probe");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
    }

    private static void RestoreWritable(string root)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetUnixFileMode(
                    dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort restore before delete */ }
        }
    }

    private readonly record struct ScriptResult(int ExitCode, string Stdout, string Stderr);

    private static ScriptResult RunScript(string? home)
    {
        var scriptRoot = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Could not locate the repository root from the test output directory" +
                " — ensure CodeyBox.slnx exists in an ancestor directory.");
        var script = Path.Combine(scriptRoot, "scripts", "reclaim-nuget-home.sh");

        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(script);
        // The child must see exactly the HOME under test — never the runner's own.
        psi.Environment.Remove("HOME");
        if (home is not null)
            psi.Environment["HOME"] = home;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /bin/sh for reclaim-nuget-home.sh.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "reclaim-nuget-home.sh did not exit within 30s.");
        return new ScriptResult(process.ExitCode, stdout, stderr);
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
