using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the POSIX-sh <c>DOTNET_CLI_HOME</c> selection prologue embedded in
/// <see cref="SandboxRequiredBuildVerifier"/> directly, so its unset / writable
/// / non-writable branches are covered without provisioning a sandbox. The
/// prologue is what keeps <c>dotnet restore</c> from aborting on a root-owned,
/// unwritable <c>~/.nuget</c> parent by redirecting <c>DOTNET_CLI_HOME</c> to a
/// writable repo-local home. These tests assume a POSIX shell, matching the
/// rest of the sandbox test surface.
/// </summary>
public sealed class DotnetCliHomeSelectionScriptTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-cli-home-select-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void UnsetCliHome_FallsBackToRepoLocalHomeUnderCwd()
    {
        var resolved = RunSelection(presetCliHome: null, workingDirectory: _workspace);

        var expected = Path.Combine(_workspace, DotnetCliHomeConventions.DirectoryName);
        Assert.Equal(expected, resolved.CliHome);
        // HOME must track the resolved home so NuGet builds that derive the
        // user-config directory from $HOME (rather than DOTNET_CLI_HOME) also
        // avoid a root-owned ~/.nuget.
        Assert.Equal(expected, resolved.Home);
    }

    [Fact]
    public void WritableCliHome_IsPreservedAndNuGetDirIsPrepared()
    {
        var writable = Path.Combine(_workspace, "writable-home");
        Directory.CreateDirectory(writable);

        var resolved = RunSelection(presetCliHome: writable, workingDirectory: _workspace);

        Assert.Equal(writable, resolved.CliHome);
        Assert.Equal(writable, resolved.Home);
        // The probe both verifies and materialises the user-config directory
        // NuGet will populate, so restore never has to create it later.
        Assert.True(Directory.Exists(Path.Combine(writable, ".nuget", "NuGet")));
    }

    [Fact]
    public void NonWritableCliHome_FallsBackToRepoLocalHome()
    {
        // POSIX-only: the read-only probe relies on Unix directory permissions.
        if (OperatingSystem.IsWindows())
            return;
        // A privileged process can write under any directory, so the read-only
        // probe cannot fail and the fallback branch is unobservable. Skip the
        // assertion rather than let it flake when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var readOnlyParent = Path.Combine(_workspace, "readonly");
        Directory.CreateDirectory(readOnlyParent);
        var poisoned = Path.Combine(readOnlyParent, "home");
        // Deny writes on the parent so "mkdir -p $cli_home/.nuget/NuGet" fails
        // exactly as a root-owned ~/.nuget denies the unprivileged agent user.
        File.SetUnixFileMode(readOnlyParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var resolved = RunSelection(presetCliHome: poisoned, workingDirectory: _workspace);

            var expected = Path.Combine(_workspace, DotnetCliHomeConventions.DirectoryName);
            Assert.Equal(expected, resolved.CliHome);
            Assert.Equal(expected, resolved.Home);
        }
        finally
        {
            // Restore write permission so Dispose can delete the tree.
            File.SetUnixFileMode(
                readOnlyParent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private readonly record struct SelectionResult(string CliHome, string Home);

    private static SelectionResult RunSelection(string? presetCliHome, string workingDirectory)
    {
        // Run the exact production prologue, then print the values it exported so
        // the test observes the real selection, not a reimplementation. The two
        // exports are newline-separated so both DOTNET_CLI_HOME and HOME are
        // asserted against the resolved home.
        var script = SandboxRequiredBuildVerifier.DotnetCliHomeSelectionScript
            + "\nprintf '%s\\n%s' \"$DOTNET_CLI_HOME\" \"$HOME\"\n";
        var psi = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        // The child must derive DOTNET_CLI_HOME solely from the preset argument,
        // never from the test runner's own inherited environment.
        psi.Environment.Remove("DOTNET_CLI_HOME");
        if (presetCliHome is not null)
            psi.Environment["DOTNET_CLI_HOME"] = presetCliHome;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /bin/sh for the selection prologue.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "sh selection prologue did not exit within 30s.");
        Assert.True(process.ExitCode == 0, $"sh exited {process.ExitCode}; stderr: {stderr}");
        var parts = stdout.Split('\n');
        Assert.Equal(2, parts.Length);
        return new SelectionResult(parts[0], parts[1]);
    }
}
