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
            ParseVar(stdout, "CLI_HOME"));
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
        var redirected = ParseVar(stdout, "CLI_HOME");
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

    // The runtime-dir pin must not trust the env-provided DOTNET_CLI_HOME
    // verbatim: it is canonicalized first, so a legitimately symlinked cli home
    // resolves and is exported as the real directory — reused rather than
    // refused.
    [Fact]
    public async Task WrapDotnetInvocation_PinsWritableUserDirs_ResolvesSymlinkedCliHome()
    {
        if (OperatingSystem.IsWindows())
            return;
        // A privileged process can write anywhere, so the unusable-home
        // branch is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        using var fixture = SelfHealFixture.WithUnwritableHome();
        var real = Path.Combine(fixture.Tmp, "cli-real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(fixture.Tmp, "cli-link");
        File.CreateSymbolicLink(link, real);

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string> { ["DOTNET_CLI_HOME"] = link });

        Assert.Equal(0, exitCode);
        Assert.Equal(real, ParseVar(stdout, "HOME"));
        Assert.Equal(real, ParseVar(stdout, "XDG"));
    }

    // Regression: the runtime-dir pin once admitted a merely writable
    // DOTNET_CLI_HOME, so a foreign-owned but writable directory (/tmp is
    // root-owned and world-writable for an unprivileged user) was pinned as
    // HOME / XDG_RUNTIME_DIR — putting dotnet's user-scoped mutex storage and
    // the NuGet user-config read under foreign-controlled storage. A candidate
    // not owned by this uid must be refused for the owned per-user fallback.
    [Fact]
    public async Task WrapDotnetInvocation_PinsWritableUserDirs_RefusesForeignOwnedCliHome()
    {
        if (OperatingSystem.IsWindows())
            return;
        // /tmp is euid-owned for a privileged process, so the refusal is
        // unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        using var fixture = SelfHealFixture.WithUnwritableHome();
        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string> { ["DOTNET_CLI_HOME"] = "/tmp" });

        Assert.Equal(0, exitCode);
        var uid = await TestSupport.CurrentUidAsync();
        var fallback = Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
        Assert.Equal(fallback, ParseVar(stdout, "HOME"));
        Assert.Equal(fallback, ParseVar(stdout, "XDG"));
    }

    // Merely-writable is not ownership: a group/world-writable $HOME — even
    // one owned by this uid — lets a co-tenant plant the NuGet.Config or hold
    // dotnet's user-scoped mutex storage inside it, so the usable check must
    // refuse it and pin the owned per-user fallback instead.
    [Fact]
    public async Task WrapDotnetInvocation_PinsWritableUserDirs_RefusesCoTenantWritableHome()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithCoTenantWritableHome();
        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        var uid = await TestSupport.CurrentUidAsync();
        var fallback = Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
        Assert.Equal(fallback, ParseVar(stdout, "HOME"));
        Assert.Equal(fallback, ParseVar(stdout, "XDG"));
    }

    // Same rule on the env-provided DOTNET_CLI_HOME: a group/world-writable
    // cli home cannot keep the NuGet.Config restore reads, so the heal
    // redirects to the owned fallback even though the env dir is writable.
    [Fact]
    public async Task WrapDotnetInvocation_RefusesCoTenantWritableCliHome()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithHealthyHome();
        var foreign = Path.Combine(fixture.Tmp, "co-tenant-writable");
        Directory.CreateDirectory(foreign);
        File.SetUnixFileMode(
            foreign,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string> { ["DOTNET_CLI_HOME"] = foreign });

        Assert.Equal(0, exitCode);
        var uid = await TestSupport.CurrentUidAsync();
        var fallback = Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
        Assert.Equal(fallback, ParseVar(stdout, "CLI_HOME"));
        Assert.Equal(fixture.Home, ParseVar(stdout, "HOME"));
        Assert.Equal(fallback, ParseVar(stdout, "XDG"));
    }

    // Regression for the mkdir -p descent on the env-provided cli home: a
    // candidate under a symlinked intermediate must never materialize the
    // chain under the link's target — a missing component simply refuses the
    // candidate now.
    [Fact]
    public async Task WrapDotnetInvocation_NeverCreatesCliHomeThroughSymlinkedIntermediate()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithHealthyHome();
        var outside = Path.Combine(fixture.Tmp, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(fixture.Tmp, "link");
        File.CreateSymbolicLink(link, outside);
        var cli = Path.Combine(link, "deep", "deeper");

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string> { ["DOTNET_CLI_HOME"] = cli });

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        var uid = await TestSupport.CurrentUidAsync();
        Assert.Equal(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid),
            ParseVar(stdout, "CLI_HOME"));
    }

    // Total fallback failure: TMPDIR naming a regular file means neither the
    // shared per-user dir nor a private mktemp can be materialized, so the
    // pick chain fails outright. The env values the preamble rejected must be
    // UNSET for the wrapped dotnet — left exported, dotnet would consume
    // foreign-controlled storage for its user-config and mutex dirs.
    [Fact]
    public async Task WrapDotnetInvocation_UnsetsRejectedVars_WhenNoFallbackExists()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (Environment.IsPrivilegedProcess)
            return;

        using var fixture = SelfHealFixture.WithUnwritableHome();
        var tmpFile = Path.Combine(fixture.Tmp, "tmp-as-file");
        await File.WriteAllTextAsync(tmpFile, "x");

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string> { ["TMPDIR"] = tmpFile });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, ParseVar(stdout, "HOME"));
        Assert.Equal(string.Empty, ParseVar(stdout, "XDG"));
        Assert.Contains("ARGS=build", stdout);
    }

    // Same no-fallback path one variable over: a rejected DOTNET_CLI_HOME is
    // unset so dotnet falls back to a healthy HOME rather than consuming the
    // foreign value.
    [Fact]
    public async Task WrapDotnetInvocation_UnsetsRejectedCliHome_WhenNoFallbackExists()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithHealthyHome();
        var tmpFile = Path.Combine(fixture.Tmp, "tmp-as-file");
        var cliFile = Path.Combine(fixture.Tmp, "cli-as-file");
        await File.WriteAllTextAsync(tmpFile, "x");
        await File.WriteAllTextAsync(cliFile, "x");

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string>
            {
                ["TMPDIR"] = tmpFile,
                ["DOTNET_CLI_HOME"] = cliFile,
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, ParseVar(stdout, "CLI_HOME"));
        Assert.Equal(fixture.Home, ParseVar(stdout, "HOME"));
    }

    // The broken-home redirect preserves an inherited package cache by
    // exporting it as NUGET_PACKAGES so restore stays offline-safe — but only
    // when the WHOLE tree is consumable. A clean root alone is not enough:
    // NuGet skips re-extraction when .nupkg.metadata is present, so a
    // co-tenant-writable descendant anywhere under the root could satisfy
    // installs with planted or tampered content.
    [Fact]
    public async Task WrapDotnetInvocation_ExportsInheritedCache_WhenWholeTreeIsClean()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithBrokenSettingsAndCache(
            packages => Directory.CreateDirectory(Path.Combine(packages, "pkg")));

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(
            Path.Combine(fixture.Home, ".nuget", "packages"),
            ParseVar(stdout, "PKGS"));
        var uid = await TestSupport.CurrentUidAsync();
        Assert.Equal(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid),
            ParseVar(stdout, "CLI_HOME"));
    }

    [Fact]
    public async Task WrapDotnetInvocation_DoesNotExportCache_WithCoTenantWritableDescendant()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithBrokenSettingsAndCache(
            packages =>
            {
                var nested = Path.Combine(packages, "pkg", "contents");
                Directory.CreateDirectory(nested);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        nested,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                }
            });

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        // The redirect still happens — only the dirty cache is refused.
        var uid = await TestSupport.CurrentUidAsync();
        Assert.Equal(
            Path.Combine(fixture.Tmp, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid),
            ParseVar(stdout, "CLI_HOME"));
        Assert.Equal(string.Empty, ParseVar(stdout, "PKGS"));
    }

    // A planted symlink inside an otherwise-clean cache must not smuggle in a
    // co-tenant-writable target outside the tree — the admission scan
    // evaluates each link as its target.
    [Fact]
    public async Task WrapDotnetInvocation_DoesNotExportCache_WhoseSymlinkEscapesToWritableTarget()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithBrokenSettingsAndCache(
            packages =>
            {
                // The link's target lives outside the packages tree but
                // inside the fixture home (deleted with the fixture), and
                // stays co-tenant-writable for the whole run.
                var homeDir = Directory.GetParent(packages)!.Parent!.FullName;
                var target = Path.Combine(homeDir, "planted-target.props");
                File.WriteAllText(target, "<Project/>");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                }

                File.CreateSymbolicLink(Path.Combine(packages, "pkg"), target);
            });

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, ParseVar(stdout, "PKGS"));
    }

    // An operator-provided NUGET_PACKAGES always wins over the inherited
    // cache — the reuse helper must never overwrite an exported value.
    [Fact]
    public async Task WrapDotnetInvocation_KeepsOperatorNuGetPackages_OverInheritedCache()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = SelfHealFixture.WithBrokenSettingsAndCache(
            packages => Directory.CreateDirectory(Path.Combine(packages, "pkg")));
        var operatorCache = Path.Combine(fixture.Tmp, "operator-cache");
        Directory.CreateDirectory(operatorCache);

        var (stdout, exitCode) = await fixture.RunWrappedDotnetAsync(
            new Dictionary<string, string> { ["NUGET_PACKAGES"] = operatorCache });

        Assert.Equal(0, exitCode);
        Assert.Equal(operatorCache, ParseVar(stdout, "PKGS"));
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
                "#!/bin/sh\nprintf 'CLI_HOME=%s\\n' \"${DOTNET_CLI_HOME:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n",
            extraEnvRemovals: [],
            restorePermissions: static fixture =>
            {
                var config = Path.Combine(fixture.Home, ".nuget", "NuGet", "NuGet.Config");
                if (File.Exists(config) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            });

        // Broken NuGet settings home (the unreadable user-config fault shape)
        // plus a `.nuget/packages` cache the caller seeds; fake `dotnet`
        // additionally echoes NUGET_PACKAGES so tests can assert whether the
        // inherited cache was preserved.
        public static SelfHealFixture WithBrokenSettingsAndCache(Action<string> seedCache) => new(
            prepareHome: home =>
            {
                var nugetRoot = Path.Combine(home, ".nuget");
                var settingsDir = Path.Combine(nugetRoot, "NuGet");
                Directory.CreateDirectory(settingsDir);
                var config = Path.Combine(settingsDir, "NuGet.Config");
                File.WriteAllText(config, "<configuration/>");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(config, UnixFileMode.None);
                var packages = Path.Combine(nugetRoot, "packages");
                Directory.CreateDirectory(packages);
                seedCache(packages);
            },
            fakeDotnetBody:
                "#!/bin/sh\nprintf 'CLI_HOME=%s\\n' \"${DOTNET_CLI_HOME:-}\"\nprintf 'HOME=%s\\n' \"${HOME:-}\"\nprintf 'XDG=%s\\n' \"${XDG_RUNTIME_DIR:-}\"\nprintf 'PKGS=%s\\n' \"${NUGET_PACKAGES:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n",
            extraEnvRemovals: ["XDG_RUNTIME_DIR"],
            restorePermissions: static fixture =>
            {
                var config = Path.Combine(fixture.Home, ".nuget", "NuGet", "NuGet.Config");
                if (File.Exists(config) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            });

        // A perfectly healthy NuGet home — the preamble takes no redirect;
        // fake `dotnet` echoes every variable the preamble may pin.
        public static SelfHealFixture WithHealthyHome() => new(
            prepareHome: static home => { },
            fakeDotnetBody:
                "#!/bin/sh\nprintf 'CLI_HOME=%s\\n' \"${DOTNET_CLI_HOME:-}\"\nprintf 'HOME=%s\\n' \"${HOME:-}\"\nprintf 'XDG=%s\\n' \"${XDG_RUNTIME_DIR:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n",
            extraEnvRemovals: ["XDG_RUNTIME_DIR"],
            restorePermissions: static fixture => { });

        // A $HOME that IS writable — but by every co-tenant (mode 777). The
        // writable bit alone must not admit it: anything planted inside by a
        // co-tenant would be consumed by restore.
        public static SelfHealFixture WithCoTenantWritableHome() => new(
            prepareHome: static home =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        home,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
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

        public async Task<(string Stdout, int ExitCode)> RunWrappedDotnetAsync(
            IReadOnlyDictionary<string, string>? extraEnv = null)
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
            if (extraEnv is not null)
                foreach (var kv in extraEnv)
                    psi.Environment[kv.Key] = kv.Value;

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (stdout, process.ExitCode);
        }

        public void Dispose()
        {
            _restorePermissions(this);
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
