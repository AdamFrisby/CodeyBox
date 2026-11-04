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

        using var fixture = SelfHealFixture.WithUnreadableNuGetConfig();
        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        // Home was redirected to the writable per-user fallback, not the broken one.
        var uid = await TestSupport.CurrentUidAsync();
        Assert.Equal(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid),
            ParseVar(stdout, "HOME"));
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

        using var fixture = SelfHealFixture.WithUnreadableNuGetConfig();
        var uid = await TestSupport.CurrentUidAsync();
        // Squat the per-user path as a regular file: `[ -d ]` fails -> mktemp branch.
        var squatted = Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
        File.WriteAllText(squatted, "not a directory");

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        var redirected = ParseVar(stdout, "HOME");
        Assert.NotEqual(squatted, redirected);
        // A private mktemp dir under tmp, named from the same leaf, and a real dir.
        Assert.StartsWith(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "."),
            redirected);
        Assert.True(Directory.Exists(redirected));
    }

    // An unusable $HOME is a different fault than an unreadable NuGet.Config:
    // since runtime 10.0.13 the dotnet CLI creates the user-scoped
    // "NuGet-Migrations" named mutex under $XDG_RUNTIME_DIR, falling back to
    // $HOME — an unusable HOME aborts first-use before MSBuild runs. The wrap
    // must pin HOME and XDG_RUNTIME_DIR to a writable dir so dotnet first-use
    // can run at all.
    [Fact]
    public async Task WrapDotnetInvocation_PinsWritableUserDirs_WhenHomeUnusable()
    {
        if (OperatingSystem.IsWindows())
            return;
        // A privileged process can write anywhere, so the unusable-home
        // branch is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        using var fixture = SelfHealFixture.WithUnwritableHome();
        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        var uid = await TestSupport.CurrentUidAsync();
        // Both variables land on the shared writable fallback under TMPDIR —
        // never the unusable home.
        var fallback = Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
        Assert.Equal(fallback, ParseVar(stdout, "HOME"));
        Assert.Equal(fallback, ParseVar(stdout, "XDG"));
        Assert.True(Directory.Exists(fallback));
        Assert.Contains("ARGS=build", stdout);
    }

    private static string ParseVar(string stdout, string name)
    {
        var line = stdout.Split('\n').Single(l => l.StartsWith(name + "=", StringComparison.Ordinal));
        return line[(name.Length + 1)..].Trim();
    }

    // Isolated temp tree hosting a fake NuGet home (the fault shape is chosen
    // by the factory) plus a fake `dotnet` on PATH echoing the variables and
    // args a test cares about, so the real wrapped argv runs through /bin/sh
    // end to end. Permission bits the fixture loosened are restored on Dispose
    // before the tree is deleted.
    private sealed class SelfHealFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _binDir;
        private readonly IReadOnlyList<string> _extraEnvRemovals;
        private readonly Action<SelfHealFixture> _restorePermissions;
        public string Home { get; }
        public string Tmp { get; }

        private SelfHealFixture(
            Action<string> prepareHome,
            string fakeDotnetBody,
            IReadOnlyList<string> extraEnvRemovals,
            Action<SelfHealFixture> restorePermissions)
        {
            _root = Directory.CreateTempSubdirectory("codeybox-selfheal-").FullName;
            Home = Path.Combine(_root, "home");
            Directory.CreateDirectory(Home);
            prepareHome(Home);

            Tmp = Path.Combine(_root, "tmp");
            Directory.CreateDirectory(Tmp);

            _binDir = Path.Combine(_root, "bin");
            Directory.CreateDirectory(_binDir);
            var fakeDotnet = Path.Combine(_binDir, "dotnet");
            File.WriteAllText(fakeDotnet, fakeDotnetBody);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    fakeDotnet,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            _extraEnvRemovals = extraEnvRemovals;
            _restorePermissions = restorePermissions;
        }

        // Fake NuGet home whose user-config is unreadable (the exact audit
        // failure mode); fake `dotnet` echoes DOTNET_CLI_HOME and its args.
        public static SelfHealFixture WithUnreadableNuGetConfig() => new(
            prepareHome: static home =>
            {
                var settingsDir = Path.Combine(home, ".nuget", "NuGet");
                Directory.CreateDirectory(settingsDir);
                var config = Path.Combine(settingsDir, "NuGet.Config");
                File.WriteAllText(config, "<configuration/>");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(config, UnixFileMode.None);
            },
            fakeDotnetBody:
                "#!/bin/sh\nprintf 'HOME=%s\\n' \"${DOTNET_CLI_HOME:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n",
            extraEnvRemovals: [],
            restorePermissions: static fixture =>
            {
                var config = Path.Combine(fixture.Home, ".nuget", "NuGet", "NuGet.Config");
                if (File.Exists(config) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            });

        // A $HOME the current user cannot write at all — the runtime-10.0.13+
        // named-mutex failure mode; fake `dotnet` echoes HOME, XDG_RUNTIME_DIR,
        // and its args. XDG_RUNTIME_DIR is additionally cleared so the HOME
        // fallback path is what dotnet would hit.
        public static SelfHealFixture WithUnwritableHome() => new(
            prepareHome: static home =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        home,
                        UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            },
            fakeDotnetBody:
                "#!/bin/sh\nprintf 'HOME=%s\\n' \"${HOME:-}\"\nprintf 'XDG=%s\\n' \"${XDG_RUNTIME_DIR:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n",
            extraEnvRemovals: ["XDG_RUNTIME_DIR"],
            restorePermissions: static fixture =>
            {
                if (!OperatingSystem.IsWindows() && Directory.Exists(fixture.Home))
                {
                    File.SetUnixFileMode(
                        fixture.Home,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            });

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
            foreach (var name in _extraEnvRemovals)
                psi.Environment.Remove(name);

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (stdout, process.ExitCode);
        }

        public void Dispose()
        {
            _restorePermissions(this);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
