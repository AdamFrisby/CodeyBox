using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class NuGetHomeSelfHealTests
{
    [Fact]
    public void WrapDotnetInvocation_WrapsDotnetCommand_PreservingArgs()
    {
        IReadOnlyList<string> argv = ["dotnet", "build", "--no-incremental", "/warnaserror"];

        var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(argv);

        // sh -c <script> sh dotnet build --no-incremental /warnaserror
        Assert.Equal("sh", wrapped[0]);
        Assert.Equal("-c", wrapped[1]);
        Assert.Contains("exec \"$@\"", wrapped[2]);
        Assert.Equal("sh", wrapped[3]);
        Assert.Equal(argv, wrapped.Skip(4).ToArray());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("npm")]
    [InlineData("git")]
    public void WrapDotnetInvocation_LeavesNonDotnetCommandUnchanged(string tool)
    {
        IReadOnlyList<string> argv = [tool, "build"];

        var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(argv);

        Assert.Same(argv, wrapped);
    }

    [Fact]
    public void WrapDotnetInvocation_LeavesEmptyArgvUnchanged()
    {
        IReadOnlyList<string> argv = [];

        Assert.Same(argv, NuGetHomeSelfHeal.WrapDotnetInvocation(argv));
    }

    // Executes the real wrapped argv through /bin/sh against a broken (unreadable)
    // ~/.nuget with a fake `dotnet` on PATH that echoes DOTNET_CLI_HOME and its own
    // args. Proves the wrap actually redirects the NuGet home AND execs dotnet with
    // the original arguments intact.
    [Fact]
    public async Task WrapDotnetInvocation_HealsBrokenHomeAndForwardsArgs()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new BrokenHomeFixture();
        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        // Home was redirected to the writable per-user fallback, not the broken one.
        var uid = await CurrentUidAsync();
        Assert.Equal(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid),
            ParseRedirectedHome(stdout));
        // Original arguments reached dotnet unchanged.
        Assert.Contains("ARGS=build --no-incremental /warnaserror", stdout);
    }

    // When the predictable per-user home already exists but is NOT a usable dir the
    // current user owns (here: squatted as a plain file), the preamble must not
    // point dotnet at it; it falls back to a private mktemp dir so the healed home
    // is always usable. Simulates a root-left / cross-principal collision in a
    // world-writable temp without needing another uid.
    [Fact]
    public async Task WrapDotnetInvocation_FallsBackToMktemp_WhenPerUserHomeUnusable()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new BrokenHomeFixture();
        var uid = await CurrentUidAsync();
        // Squat the per-user path as a regular file: `[ -d ]` fails -> mktemp branch.
        var squatted = Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
        File.WriteAllText(squatted, "not a directory");

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        var redirected = ParseRedirectedHome(stdout);
        Assert.NotEqual(squatted, redirected);
        // A private mktemp dir under tmp, named from the same leaf, and a real dir.
        Assert.StartsWith(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "."),
            redirected);
        Assert.True(Directory.Exists(redirected));
    }

    private static string ParseRedirectedHome(string stdout)
    {
        var line = stdout.Split('\n').Single(l => l.StartsWith("HOME=", StringComparison.Ordinal));
        return line["HOME=".Length..].Trim();
    }

    private static async Task<string> CurrentUidAsync()
    {
        var psi = new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var uid = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        return uid;
    }

    // Fake NuGet home whose user-config is unreadable (the exact audit failure mode),
    // plus a fake `dotnet` on PATH echoing DOTNET_CLI_HOME and its args, in an
    // isolated temp tree that is cleaned up (restoring perms) on Dispose.
    private sealed class BrokenHomeFixture : IDisposable
    {
        private readonly string _root;
        public string Home { get; }
        public string Tmp { get; }
        private readonly string _binDir;

        public BrokenHomeFixture()
        {
            _root = Directory.CreateTempSubdirectory("codeybox-selfheal-").FullName;
            Home = Path.Combine(_root, "home");
            var settingsDir = Path.Combine(Home, ".nuget", "NuGet");
            Directory.CreateDirectory(settingsDir);
            var config = Path.Combine(settingsDir, "NuGet.Config");
            File.WriteAllText(config, "<configuration/>");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(config, UnixFileMode.None);

            Tmp = Path.Combine(_root, "tmp");
            Directory.CreateDirectory(Tmp);

            _binDir = Path.Combine(_root, "bin");
            Directory.CreateDirectory(_binDir);
            var fakeDotnet = Path.Combine(_binDir, "dotnet");
            File.WriteAllText(
                fakeDotnet,
                "#!/bin/sh\nprintf 'HOME=%s\\n' \"${DOTNET_CLI_HOME:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    fakeDotnet,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public async Task<(string Stdout, int ExitCode)> RunWrappedDotnetAsync()
        {
            var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(
                ["dotnet", "build", "--no-incremental", "/warnaserror"]);

            var psi = new ProcessStartInfo(wrapped[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in wrapped.Skip(1))
                psi.ArgumentList.Add(arg);
            psi.Environment["HOME"] = Home;
            psi.Environment["TMPDIR"] = Tmp;
            psi.Environment["PATH"] = _binDir + ":" + Environment.GetEnvironmentVariable("PATH");
            psi.Environment.Remove("DOTNET_CLI_HOME");
            psi.Environment.Remove("NUGET_PACKAGES");

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (stdout, process.ExitCode);
        }

        public void Dispose()
        {
            var config = Path.Combine(Home, ".nuget", "NuGet", "NuGet.Config");
            if (File.Exists(config) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
