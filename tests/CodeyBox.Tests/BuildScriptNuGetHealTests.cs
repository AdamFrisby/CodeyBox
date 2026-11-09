using CodeyBox.Core;
using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the per-user NuGet-home recovery block in the repository's build.sh
/// against real filesystem fixtures. That block is one call site of the shared
/// recovery that lets the direct <c>dotnet build</c>/<c>dotnet test</c> gates run in
/// environments that COW-inherited an unreadable or unwritable NuGet home. NuGet
/// performs a fatal user-config read during restore, so the home has to be healed on
/// disk first; the primary hook is the MSBuild <c>InitialTargets</c> in
/// Directory.Build.props / Directory.Solution.props (covered by
/// <see cref="MsBuildNuGetHealTests"/>), and build.sh dot-sources the same recovery
/// for callers that route through it. These tests fail if the recovery stops
/// detecting a broken home, stops quarantining it aside, or stops preserving the
/// baked offline package cache.
/// </summary>
[Collection("Real build toolchain")]
public sealed class BuildScriptNuGetHealTests
{
    private static string BuildScriptPath()
    {
        var root = FindAncestorContaining(AppContext.BaseDirectory, "build.sh")
            ?? throw new InvalidOperationException(
                "Cannot locate build.sh from " + AppContext.BaseDirectory +
                " — ensure build.sh exists in an ancestor directory.");
        return Path.Combine(root, "build.sh");
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

    private static async Task ChmodAsync(string mode, string path)
    {
        var result = await new DefaultProcessRunner().RunAsync(
            ["chmod", mode, path], null, CancellationToken.None,
            maxStdoutBytes: 256, maxStderrBytes: 256);
        Assert.True(result.Success, result.Stderr);
    }

    // Runs the real build.sh with an isolated HOME and a stub `dotnet` on PATH, so
    // the heal block executes against the fixture while build.sh's trailing
    // `dotnet` invocation returns without a real build. The stub records the exact
    // argument vector it received, which both proves the heal fell through to the
    // build step rather than aborting and lets callers assert argument forwarding.
    private static async Task<(ProcessRunResult Result, string Home, string Sentinel)>
        RunBuildScriptAsync(
            string root,
            Func<string, Task> seedHomeAsync,
            IReadOnlyList<string>? scriptArgs = null)
    {
        var home = Path.Combine(root, "home");
        var stubDir = Path.Combine(root, "stub-bin");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(stubDir);

        var sentinel = Path.Combine(root, "dotnet-invoked");
        var stub = Path.Combine(stubDir, "dotnet");
        // NUL-separate the recorded argv so an argument containing whitespace is
        // still unambiguous when the test reads it back.
        await File.WriteAllTextAsync(
            stub, "#!/bin/sh\nprintf '%s\\0' \"$@\" > '" + sentinel + "'\nexit 0\n");
        await ChmodAsync("755", stub);

        await seedHomeAsync(home);

        // Replace the environment so the fixture HOME is authoritative and the real
        // user's NuGet home can never be touched; TMPDIR is contained under root too.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["PATH"] = stubDir + ":/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["TMPDIR"] = root,
        };

        var argv = new List<string> { "/bin/sh", BuildScriptPath() };
        if (scriptArgs is not null)
            argv.AddRange(scriptArgs);
        var result = await new DefaultProcessRunner().RunAsync(
            argv,
            null,
            CancellationToken.None,
            maxStdoutBytes: 65536,
            maxStderrBytes: 65536,
            environment: env);
        return (result, home, sentinel);
    }

    private static async Task<IReadOnlyList<string>> ReadRecordedArgvAsync(string sentinel)
    {
        var raw = await File.ReadAllTextAsync(sentinel);
        return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task BuildScript_HealsUnreadableInheritedNuGetConfig_PreservingCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-buildsh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (result, home, sentinel) = await RunBuildScriptAsync(root, async seededHome =>
            {
                var nugetDir = Path.Combine(seededHome, ".nuget", "NuGet");
                Directory.CreateDirectory(nugetDir);
                var config = Path.Combine(nugetDir, "NuGet.Config");
                await File.WriteAllTextAsync(config, "<configuration/>");
                // A present-but-unreadable inherited config reproduces the exact
                // "Failed to read NuGet.Config due to unauthorized access" abort.
                await ChmodAsync("000", config);

                var marker = Path.Combine(seededHome, ".nuget", "packages", "pkg", "marker.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, "cached");
            });

            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.True(File.Exists(sentinel), "build.sh must reach the build after healing");

            // The broken tree is quarantined aside (preserved), not deleted.
            var quarantines = Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*");
            Assert.Single(quarantines);

            // A fresh, writable per-user NuGet home replaced it.
            var newNuGetDir = Path.Combine(home, ".nuget", "NuGet");
            Assert.True(Directory.Exists(newNuGetDir));
            var probe = Path.Combine(newNuGetDir, "writable-probe");
            await File.WriteAllTextAsync(probe, "ok"); // throws if the new home is not writable

            // A readable user config is seeded into the fresh home so the next
            // `dotnet`'s user-config *read* (the exact operation the broken home
            // fails) succeeds without depending on first-run creation.
            var healedConfig = Path.Combine(newNuGetDir, "NuGet.Config");
            Assert.True(File.Exists(healedConfig), "heal must seed a user NuGet.Config");
            var seeded = await File.ReadAllTextAsync(healedConfig); // throws if unreadable
            Assert.Contains("<configuration", seeded, StringComparison.Ordinal);

            // The populated offline package cache survives through the preserved symlink,
            // so restore stays offline-safe.
            var healedMarker = Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt");
            Assert.Equal("cached", await File.ReadAllTextAsync(healedMarker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildScript_LeavesWritableNuGetHomeIntact()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-buildsh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (result, home, sentinel) = await RunBuildScriptAsync(root, async seededHome =>
            {
                var marker = Path.Combine(seededHome, ".nuget", "packages", "pkg", "marker.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, "cached");
            });

            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.True(File.Exists(sentinel));

            // A usable home is left untouched: no quarantine, no leftover probe, and
            // the real package-cache directory (not a symlink) with its contents intact.
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.False(File.Exists(
                Path.Combine(home, ".nuget", "NuGet", ".codeybox-writable-probe")));
            var marker = Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt");
            Assert.Equal("cached", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildScript_ForwardsGateArgumentsThroughHeal()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-buildsh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // The audit gates invoke `dotnet test --no-build` (and other commands)
            // directly; routing them through build.sh must apply the same NuGet-home
            // heal and then run the requested command verbatim, not the default build.
            string[] gateArgs = ["test", "--no-build", "CodeyBox.slnx"];
            var (result, _, sentinel) = await RunBuildScriptAsync(
                root,
                seededHome => Task.CompletedTask,
                gateArgs);

            Assert.True(result.Success, result.Stdout + result.Stderr);
            var forwarded = await ReadRecordedArgvAsync(sentinel);
            Assert.Equal(gateArgs, forwarded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildScript_WithoutArguments_BuildsTheSolution()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-buildsh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (result, _, sentinel) = await RunBuildScriptAsync(
                root,
                seededHome => Task.CompletedTask);

            Assert.True(result.Success, result.Stdout + result.Stderr);
            var forwarded = await ReadRecordedArgvAsync(sentinel);
            Assert.Equal(["build", "CodeyBox.slnx"], forwarded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The audit build/test gates that cannot route through build.sh — the
    // required-build BuildScript and the tool-audit sandbox setup — dot-source the
    // shared recovery directly as `. ./scripts/nuget-home-heal.sh` from the
    // repository root. That invocation form (cwd-relative, no `$0`) differs from
    // build.sh's `dirname $0` resolution, so exercise it explicitly against a
    // broken home and assert the same on-disk recovery.
    private static string HealScriptRepoRoot()
        => FindAncestorContaining(AppContext.BaseDirectory, "build.sh")
            ?? throw new InvalidOperationException(
                "Cannot locate the repository root from " + AppContext.BaseDirectory);

    private static string HealScriptPath()
        => Path.Combine(HealScriptRepoRoot(), "scripts", "nuget-home-heal.sh");

    // The minimal replaced environment every heal fixture runs under: the
    // fixture HOME is authoritative (the real user's NuGet home can never be
    // touched) and TMPDIR is contained under the test root. Tests add
    // case-specific keys (DOTNET_CLI_HOME, XDG_*) on top.
    private static Dictionary<string, string> NewHealEnv(string root, string home)
        => new(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["TMPDIR"] = root,
        };

    // Runs `sh -c <command> sh <args...>` with the given environment — the
    // shared runner for every dot-source-the-heal scenario below so each test
    // only states its fixture env and its command.
    private static async Task<ProcessRunResult> RunHealShellAsync(
        IReadOnlyDictionary<string, string> environment,
        string command,
        params string[] args)
    {
        var argv = new List<string> { "/bin/sh", "-c", command, "sh" };
        argv.AddRange(args);
        return await new DefaultProcessRunner().RunAsync(
            argv,
            null,
            CancellationToken.None,
            maxStdoutBytes: 65536,
            maxStderrBytes: 65536,
            environment: environment);
    }

    private static async Task<ProcessRunResult> DotSourceHealAsync(string root, string home)
    {
        var repoRoot = HealScriptRepoRoot();
        Assert.True(
            File.Exists(HealScriptPath()),
            "the shared heal script must exist for the gate call sites to source it");

        // Mirror the gate call sites verbatim: cd into the checkout, then dot-source
        // the repository-relative recovery.
        return await RunHealShellAsync(
            NewHealEnv(root, home),
            "cd \"$1\" && . ./scripts/nuget-home-heal.sh",
            repoRoot);
    }

    [Fact]
    public async Task HealScript_DirectDotSource_HealsBrokenNuGetHome_PreservingCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(nugetDir);
            var config = Path.Combine(nugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration/>");
            await ChmodAsync("000", config); // present-but-unreadable reproduces the fatal read
            var marker = Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "cached");

            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            // Broken tree quarantined aside (preserved), fresh writable home created.
            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            var probe = Path.Combine(home, ".nuget", "NuGet", "writable-probe");
            await File.WriteAllTextAsync(probe, "ok"); // throws if not writable
            var healedConfig = Path.Combine(home, ".nuget", "NuGet", "NuGet.Config");
            Assert.Contains("<configuration", await File.ReadAllTextAsync(healedConfig), StringComparison.Ordinal);
            // Populated offline cache survives through the preserved symlink.
            Assert.Equal("cached", await File.ReadAllTextAsync(
                Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HealScript_RedirectsCliHomeWhenHomeUnwritable_PreservingCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            // An inherited home whose .nuget cannot be relocated aside (the home
            // itself is a read-only mount) forces the DOTNET_CLI_HOME-redirect
            // fallback. The inherited package cache is still readable, so the
            // scratch home must link it in to stay offline-safe.
            var cacheMarker = Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(cacheMarker)!);
            await File.WriteAllTextAsync(cacheMarker, "cached");
            await ChmodAsync("555", Path.Combine(home, ".nuget")); // unusable: cannot create NuGet/ or write config
            await ChmodAsync("555", home);                          // unwritable: cannot quarantine .nuget aside

            var sentinel = Path.Combine(root, "cli-home");
            // Dot-source the heal, then record the exported DOTNET_CLI_HOME so the
            // test can inspect the scratch home the fallback redirected dotnet to.
            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . ./scripts/nuget-home-heal.sh && printf '%s' \"$DOTNET_CLI_HOME\" > \"$2\"",
                HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var scratchHome = await File.ReadAllTextAsync(sentinel);
            Assert.False(string.IsNullOrEmpty(scratchHome), "fallback must export a scratch DOTNET_CLI_HOME");
            Assert.NotEqual(home, scratchHome);

            // The scratch home carries a readable user config so the fatal
            // user-config read succeeds against it.
            var scratchConfig = Path.Combine(scratchHome, ".nuget", "NuGet", "NuGet.Config");
            Assert.Contains("<configuration", await File.ReadAllTextAsync(scratchConfig), StringComparison.Ordinal);

            // The inherited cache is linked in, so restore under the scratch home
            // stays offline-safe without a re-download.
            var scratchMarker = Path.Combine(scratchHome, ".nuget", "packages", "pkg", "marker.txt");
            Assert.Equal("cached", await File.ReadAllTextAsync(scratchMarker));
        }
        finally
        {
            // Restore write bits so the fixture can be removed.
            await ChmodAsync("755", home);
            await ChmodAsync("755", Path.Combine(home, ".nuget"));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HealScript_PinsWritableUserDirs_WhenHomeUnusable()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process can write anywhere; the unusable-home branch is
        // unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            // An unusable $HOME does not only break NuGet's settings dir:
            // runtime 10.0.13+ creates user-scoped named mutexes (NuGet's
            // first-use "NuGet-Migrations") under $XDG_RUNTIME_DIR or $HOME,
            // so a raw `dotnet` aborts before MSBuild. The dot-sourced
            // recovery must pin both to a writable dir for the caller's
            // process tree.
            await ChmodAsync("555", home);

            var sentinel = Path.Combine(root, "vars");
            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . ./scripts/nuget-home-heal.sh && printf '%s\\n%s\\n%s' \"$HOME\" \"${XDG_RUNTIME_DIR:-}\" \"${DOTNET_CLI_HOME:-}\" > \"$2\"",
                HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = await File.ReadAllLinesAsync(sentinel);
            Assert.Equal(3, lines.Length);
            var exportedHome = lines[0];
            var xdgRuntimeDir = lines[1];
            var dotnetCliHome = lines[2];

            // HOME and XDG_RUNTIME_DIR were both unusable/absent and must now
            // point at a writable dir — the just-redirected CLI home — never
            // the unusable $HOME.
            Assert.NotEqual(home, exportedHome);
            Assert.Equal(dotnetCliHome, exportedHome);
            Assert.Equal(dotnetCliHome, xdgRuntimeDir);
            Assert.True(Directory.Exists(xdgRuntimeDir));
            var probe = Path.Combine(exportedHome, "writable-probe");
            await File.WriteAllTextAsync(probe, "ok"); // throws if not writable
        }
        finally
        {
            await ChmodAsync("755", home);
            Directory.Delete(root, recursive: true);
        }
    }

    // The C# directory names are parsed OUT of the script's leaf assignments —
    // DotnetCliHomeConventions.DirectoryName via NuGetHomeSelfHeal.RepoHomeLeaf,
    // NuGetHomeSelfHeal.WritableHomeLeaf, and NuGetHomeSelfHeal
    // .NuGetMigrationsMarkerDir (consumed by the Incus provisioning seed) — so
    // the script is the single source and the assertions can only fail on a
    // missing/malformed assignment or a deliberate rename. The leaf strings
    // themselves are baked into sandbox path conventions (the stamped
    // repo-local home, the shared per-user fallback under TMPDIR, NuGet's
    // applied-migration marker layout), so a rename must trip this pin
    // deliberately rather than drift silently.
    [Fact]
    public async Task HealScript_MirrorsCliHomeLeafConstants()
    {
        var script = await File.ReadAllTextAsync(HealScriptPath());

        Assert.Contains(
            "_codeybox_repo_home_leaf=\".dotnet-cli-home\"",
            script, StringComparison.Ordinal);
        Assert.Contains(
            "_codeybox_home_leaf=\"codeybox-nuget-home\"",
            script, StringComparison.Ordinal);
        Assert.Contains(
            "_codeybox_nuget_migrations_marker=\".local/share/NuGet/Migrations\"",
            script, StringComparison.Ordinal);
        Assert.Equal(".dotnet-cli-home", DotnetCliHomeConventions.DirectoryName);
        Assert.Equal("codeybox-nuget-home", NuGetHomeSelfHeal.WritableHomeLeaf);
        Assert.Equal(".local/share/NuGet/Migrations", NuGetHomeSelfHeal.NuGetMigrationsMarkerDir);
    }

    // The C# preamble does not carry a second copy of the dir-admission
    // policy: it is composed from the marked region of
    // scripts/nuget-home-heal.sh (embedded into CodeyBox.Core at build time).
    // Pin both halves of that contract — the sentinels in the script and the
    // helpers actually reaching the composed Preamble — so a guard edit can
    // never land in only one copy again.
    [Fact]
    public async Task HealScript_SharedHelpers_ReachTheCSharpPreamble()
    {
        var script = await File.ReadAllTextAsync(HealScriptPath());

        Assert.Contains(NuGetHomeSelfHeal.SharedHelpersBeginMarker, script, StringComparison.Ordinal);
        Assert.Contains(NuGetHomeSelfHeal.SharedHelpersEndMarker, script, StringComparison.Ordinal);
        Assert.Contains("_codeybox_secure_dir()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
        Assert.Contains("_codeybox_adopt_dir()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
        Assert.Contains("_codeybox_pick_user_dir()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
        Assert.Contains("_codeybox_secure_package_cache()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
        Assert.Contains("_codeybox_reuse_nuget_packages()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
        Assert.Contains("_codeybox_unset_home_env()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
        Assert.Contains("_codeybox_strip_ctrl()", NuGetHomeSelfHeal.Preamble, StringComparison.Ordinal);
    }

    // The seeded user-config staging file must come from mktemp's unpredictable
    // O_EXCL create — never a PID-predictable `>` target a co-tenant-planted
    // symlink could redirect into an arbitrary-path write as the gate user.
    // Pins the guard so a future edit cannot quietly regress to `tmp.$$`.
    [Fact]
    public async Task HealScript_SeedsUserConfig_ThroughUnpredictableMktempStaging()
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(HealScriptRepoRoot(), "scripts", "nuget-home-heal.sh"));

        Assert.DoesNotContain("tmp.$$", script, StringComparison.Ordinal);
        Assert.Contains(".NuGet.Config.tmp.XXXXXX", script, StringComparison.Ordinal);
    }

    // The runtime-dir pin must not trust the env-provided DOTNET_CLI_HOME
    // verbatim: it is canonicalized first, so a legitimately symlinked cli home
    // resolves and the pin exports the real directory — reused rather than
    // refused.
    [Fact]
    public async Task HealScript_PinsWritableUserDirs_ResolvesSymlinkedCliHome()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var cliReal = Path.Combine(root, "cli-real");
        var cliLink = Path.Combine(root, "cli-link");
        Directory.CreateDirectory(cliReal);
        File.CreateSymbolicLink(cliLink, cliReal);
        var sentinel = Path.Combine(root, "vars");
        try
        {
            var env = NewHealEnv(root, Path.Combine(root, "no-such-home"));
            // No usable HOME at all forces the runtime-dir pin.
            env["DOTNET_CLI_HOME"] = cliLink;
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf '%s\\n%s' \"$HOME\" \"${XDG_RUNTIME_DIR:-}\" > \"$3\"",
                root, HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = await File.ReadAllLinesAsync(sentinel);
            Assert.Equal(2, lines.Length);
            // Both variables land on the canonical directory, not the link path.
            Assert.Equal(cliReal, lines[0]);
            Assert.Equal(cliReal, lines[1]);
            Assert.True(
                File.Exists(Path.Combine(cliReal, ".nuget", "NuGet", "NuGet.Config")),
                "the pinned home must carry the seeded readable user config");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Regression for the runtime-dir pin admitting a foreign-owned
    // DOTNET_CLI_HOME: the env value is untrusted, so after canonicalization
    // the resolved dir must be owned by this uid — a foreign-owned but
    // writable directory (/tmp is exactly that for an unprivileged user) must
    // be refused in favour of the owned per-user fallback, or the seeding and
    // dotnet's user-scoped mutex storage would land in foreign-controlled
    // storage.
    [Fact]
    public async Task HealScript_PinsWritableUserDirs_RefusesForeignOwnedCliHome()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // /tmp is euid-owned for a privileged process, so the refusal is
        // unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var sentinel = Path.Combine(root, "vars");
        Directory.CreateDirectory(root);
        try
        {
            var env = NewHealEnv(root, Path.Combine(root, "no-such-home"));
            env["DOTNET_CLI_HOME"] = "/tmp";
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf '%s\\n%s' \"$HOME\" \"${XDG_RUNTIME_DIR:-}\" > \"$3\"",
                root, HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = await File.ReadAllLinesAsync(sentinel);
            Assert.Equal(2, lines.Length);
            Assert.Equal(lines[0], lines[1]);
            Assert.NotEqual("/tmp", lines[0]);
            var uid = await TestSupport.CurrentUidAsync();
            Assert.Equal(
                Path.Combine(root, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid),
                lines[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Same admission class as the foreign-owned case, one permission axis
    // over: a HOME that is group/world-writable — even one owned by this uid —
    // lets a co-tenant plant the NuGet.Config, swap the seeded entries, or
    // hold the user-scoped mutex storage, so "usable" must refuse it for the
    // owned per-user fallback rather than keeping the writable dir.
    [Fact]
    public async Task HealScript_PinsWritableUserDirs_RefusesCoTenantWritableHome()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var sentinel = Path.Combine(root, "vars");
        Directory.CreateDirectory(home);
        try
        {
            await ChmodAsync("777", home); // writable by every co-tenant

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf '%s\\n%s\\n%s' \"$HOME\" \"${XDG_RUNTIME_DIR:-}\" \"${DOTNET_CLI_HOME:-}\" > \"$3\"",
                root, HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = await File.ReadAllLinesAsync(sentinel);
            Assert.Equal(3, lines.Length);
            Assert.NotEqual(home, lines[0]);
            var uid = await TestSupport.CurrentUidAsync();
            var fallback = Path.Combine(root, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
            Assert.Equal(fallback, lines[0]);
            Assert.Equal(fallback, lines[1]);
            Assert.Equal(fallback, lines[2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The same merely-writable-is-not-ownership rule on the env-provided
    // DOTNET_CLI_HOME: a group/world-writable cli home would keep the
    // NuGet.Config every restore reads inside co-tenant-controlled storage,
    // so the heal redirects to the owned fallback instead.
    [Fact]
    public async Task HealScript_RefusesCoTenantWritableCliHome()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var cliHome = Path.Combine(root, "cli");
        var sentinel = Path.Combine(root, "vars");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cliHome);
        try
        {
            await ChmodAsync("777", cliHome); // writable by every co-tenant

            var env = NewHealEnv(root, home);
            env["DOTNET_CLI_HOME"] = cliHome;
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf '%s\\n%s\\n%s' \"$HOME\" \"${XDG_RUNTIME_DIR:-}\" \"${DOTNET_CLI_HOME:-}\" > \"$3\"",
                root, HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = await File.ReadAllLinesAsync(sentinel);
            Assert.Equal(3, lines.Length);
            var uid = await TestSupport.CurrentUidAsync();
            var fallback = Path.Combine(root, NuGetHomeSelfHeal.WritableHomeLeaf + "-" + uid);
            Assert.Equal(home, lines[0]);       // healthy HOME is kept
            Assert.Equal(fallback, lines[2]);   // the 777 cli home is refused
            Assert.Equal(fallback, lines[1]);   // runtime dir lands on the fallback
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Regression for the mkdir -p descent: adopting the env-provided cli home
    // must never create directories through a symlinked intermediate — a path
    // like <planted-link>/deep/deeper used to materialize the whole chain
    // under the link's target (an attacker-chosen dir-create as this user).
    // A missing intermediate simply refuses the candidate now.
    [Fact]
    public async Task HealScript_NeverCreatesCliHomeThroughSymlinkedIntermediate()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(link, outside);
        try
        {
            var env = NewHealEnv(root, home);
            env["DOTNET_CLI_HOME"] = Path.Combine(link, "deep", "deeper");
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\"",
                root, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A .nuget planted as a symlink must be quarantined aside — never probed
    // or created through — and the heal must leave a real, seeded settings
    // directory behind. `mkdir -p` descending the link would otherwise plant
    // the NuGet settings tree inside the link's foreign target.
    [Fact]
    public async Task HealScript_QuarantinesSymlinkedNuGetDir()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Combine(home, ".nuget"), outside);
        try
        {
            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            // The planted link was moved aside (preserved), not followed.
            var quarantines = Directory.GetFileSystemEntries(home, ".nuget.codeybox-unwritable.*");
            Assert.Single(quarantines);
            Assert.True(
                File.GetAttributes(quarantines[0]).HasFlag(FileAttributes.ReparsePoint),
                "the quarantined entry must be the symlink itself");
            Assert.Empty(Directory.GetFileSystemEntries(outside));

            // A real settings dir with a readable seeded config replaced it.
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Assert.True(Directory.Exists(nugetDir));
            Assert.Contains(
                "<configuration",
                await File.ReadAllTextAsync(Path.Combine(nugetDir, "NuGet.Config")),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The redirected-home path must not link a co-tenant-writable package
    // cache into the healed home: NuGet consumes extracted entries with a
    // valid .nupkg.metadata without re-extracting, so a cache a co-tenant can
    // write would satisfy package installs with planted content.
    [Fact]
    public async Task HealScript_Redirect_DoesNotLinkCoTenantWritableCache()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process can write anywhere; the unwritable-home
        // redirect this test needs is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var sentinel = Path.Combine(root, "cli-home");
        try
        {
            var packages = Path.Combine(home, ".nuget", "packages", "pkg");
            Directory.CreateDirectory(packages);
            await File.WriteAllTextAsync(Path.Combine(packages, "marker.txt"), "planted");
            await ChmodAsync("777", Path.Combine(home, ".nuget", "packages"));
            await ChmodAsync("555", Path.Combine(home, ".nuget"));
            await ChmodAsync("555", home);

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf '%s' \"$DOTNET_CLI_HOME\" > \"$3\"",
                root, HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var scratchHome = await File.ReadAllTextAsync(sentinel);
            Assert.False(string.IsNullOrEmpty(scratchHome), "fallback must export a scratch DOTNET_CLI_HOME");
            // The seeded home carries the readable config but NOT the foreign
            // cache: no packages entry (link or dir) may appear under .nuget.
            Assert.True(File.Exists(Path.Combine(scratchHome, ".nuget", "NuGet", "NuGet.Config")));
            Assert.False(
                File.Exists(Path.Combine(scratchHome, ".nuget", "packages"))
                || Directory.Exists(Path.Combine(scratchHome, ".nuget", "packages")),
                "a co-tenant-writable cache must not be linked into the healed home");
        }
        finally
        {
            await ChmodAsync("755", home);
            await ChmodAsync("755", Path.Combine(home, ".nuget"));
            await ChmodAsync("755", Path.Combine(home, ".nuget", "packages"));
            Directory.Delete(root, recursive: true);
        }
    }

    // A clean cache ROOT is not enough: a co-tenant-writable DESCENDANT must
    // also refuse the link. NuGet consumes an extracted entry as-is once
    // .nupkg.metadata is present, and a package's build/.props/.targets
    // execute inside the consuming build, so one tampered descendant is
    // attacker code in the gate — the admission scan covers the whole tree.
    [Fact]
    public async Task HealScript_Redirect_DoesNotLinkCacheWithCoTenantWritableDescendant()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process can write anywhere; the unwritable-home
        // redirect this test needs is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var sentinel = Path.Combine(root, "cli-home");
        try
        {
            var nested = Path.Combine(home, ".nuget", "packages", "pkg", "contents");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "marker.txt"), "planted");
            // The packages ROOT stays clean — only a deeper dir is writable
            // by every co-tenant, so a root-only check would wrongly admit it.
            await ChmodAsync("777", nested);
            await ChmodAsync("555", Path.Combine(home, ".nuget"));
            await ChmodAsync("555", home);

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf '%s' \"$DOTNET_CLI_HOME\" > \"$3\"",
                root, HealScriptRepoRoot(), sentinel);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var scratchHome = await File.ReadAllTextAsync(sentinel);
            Assert.False(string.IsNullOrEmpty(scratchHome), "fallback must export a scratch DOTNET_CLI_HOME");
            Assert.True(File.Exists(Path.Combine(scratchHome, ".nuget", "NuGet", "NuGet.Config")));
            Assert.False(
                File.Exists(Path.Combine(scratchHome, ".nuget", "packages"))
                || Directory.Exists(Path.Combine(scratchHome, ".nuget", "packages")),
                "a cache with a co-tenant-writable descendant must not be linked into the healed home");
        }
        finally
        {
            await ChmodAsync("755", home);
            await ChmodAsync("755", Path.Combine(home, ".nuget"));
            await ChmodAsync("755", Path.Combine(home, ".nuget", "packages", "pkg", "contents"));
            Directory.Delete(root, recursive: true);
        }
    }

    // Same tree-wide rule on the in-place quarantine path: the quarantined
    // cache must not be linked into the rebuilt home when a descendant is
    // co-tenant-writable, even though the packages root itself is clean.
    [Fact]
    public async Task HealScript_Quarantine_DoesNotLinkCacheWithCoTenantWritableDescendant()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process ignores the unreadable config that forces the
        // quarantine path, so the home verifies usable and no quarantine is
        // created when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(nugetDir);
            var config = Path.Combine(nugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration/>");
            await ChmodAsync("000", config); // present-but-unreadable forces the quarantine path

            var nested = Path.Combine(home, ".nuget", "packages", "pkg", "contents");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "marker.txt"), "planted");
            await ChmodAsync("777", nested);

            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(
                File.Exists(Path.Combine(home, ".nuget", "NuGet", "NuGet.Config")),
                "the rebuilt home must carry the seeded readable user config");
            Assert.False(
                File.Exists(Path.Combine(home, ".nuget", "packages"))
                || Directory.Exists(Path.Combine(home, ".nuget", "packages")),
                "a cache with a co-tenant-writable descendant must not be linked into the rebuilt home");
        }
        finally
        {
            // The broken .nuget was quarantined aside, so the dirty
            // descendant lives under the moved directory now.
            var quarantined = Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*");
            if (quarantined.Length > 0)
                await ChmodAsync("755", Path.Combine(quarantined[0], "packages", "pkg", "contents"));
            Directory.Delete(root, recursive: true);
        }
    }

    // A planted symlink inside an otherwise-clean cache must not smuggle in a
    // co-tenant-writable target outside the tree: the scan evaluates each
    // link as its target, so this cache is declined like a dirty descendant.
    [Fact]
    public async Task HealScript_Quarantine_DoesNotLinkCache_WhoseSymlinkEscapesToWritableTarget()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process ignores the unreadable config that forces the
        // quarantine path, so the home verifies usable and no quarantine is
        // created when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outside);
        try
        {
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(nugetDir);
            var config = Path.Combine(nugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration/>");
            await ChmodAsync("000", config);

            var packages = Path.Combine(home, ".nuget", "packages");
            Directory.CreateDirectory(packages);
            var target = Path.Combine(outside, "planted.props");
            await File.WriteAllTextAsync(target, "<Project/>");
            await ChmodAsync("666", target); // writable by every co-tenant
            File.CreateSymbolicLink(Path.Combine(packages, "pkg"), target);

            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.False(
                File.Exists(Path.Combine(home, ".nuget", "packages"))
                || Directory.Exists(Path.Combine(home, ".nuget", "packages")),
                "a cache whose symlink escapes to a writable target must not be linked");
        }
        finally
        {
            await ChmodAsync("644", Path.Combine(outside, "planted.props"));
            Directory.Delete(root, recursive: true);
        }
    }

    // The dir-admission helper is the file's single entry point for trusting
    // storage, so it must fail CLOSED on an unrecognised rule name rather
    // than silently degrade to the trusted policy — and the package-cache
    // scan must decline a tree it cannot verify within its entry bound. Run
    // both helpers directly in the sourced shell against real fixtures.
    [Fact]
    public async Task HealScript_AdmissionGuards_FailClosed_OnUnknownRuleAndUnboundedTree()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(home);
        try
        {
            // A small clean cache the limit override can cover, and a larger
            // one that exceeds it — both euid-owned with no co-tenant writes.
            Directory.CreateDirectory(Path.Combine(cache, "pkg", "contents"));
            await File.WriteAllTextAsync(
                Path.Combine(cache, "pkg", "contents", "marker.txt"), "cached");

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && "
                + "{ _codeybox_secure_dir \"$3\" bogus-rule >/dev/null 2>&1 "
                + "  && echo RULE=accepted || echo RULE=refused; } && "
                + "{ _codeybox_secure_dir \"$3\" trusted >/dev/null 2>&1 "
                + "  && echo TRUSTED=accepted || echo TRUSTED=refused; } && "
                + "{ _codeybox_secure_package_cache \"$3\" >/dev/null 2>&1 "
                + "  && echo CACHE=accepted || echo CACHE=declined; } && "
                + "{ _codeybox_cache_scan_limit=3; "
                + "  _codeybox_secure_package_cache \"$3\" >/dev/null 2>&1 "
                + "  && echo BOUNDED=accepted || echo BOUNDED=declined; }",
                root, HealScriptRepoRoot(), cache);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("RULE=refused", lines);
            Assert.Contains("TRUSTED=accepted", lines);
            Assert.Contains("CACHE=accepted", lines);
            Assert.Contains("BOUNDED=declined", lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The group-privacy proof behind the umask-002 carve-out must fail
    // CLOSED: a gid this account does not itself hold (`id -G`) can never be
    // "our" private group, no matter what the group/passwd dbs say — that
    // is what keeps a co-tenant-held gid from being admitted on the strength
    // of an incomplete member enumeration. Pin both directions directly.
    [Fact]
    public async Task HealScript_GroupIsPrivate_RequiresHeldGid()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                // The PROBE gid isolates proof 0 (the `id -G` membership check):
                // the fixture _codeybox_group_db supplies a group-db entry with
                // an EMPTY member list, so proofs 2 and 3 would PASS for it —
                // the only thing that can refuse it is proof 0. The gid is
                // picked in-shell from a candidate list, skipping any that the
                // account holds, that already has a group entry in the real
                // dbs, or that some account holds as a primary gid.
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && "
                + "{ _codeybox_group_is_private \"$(id -g)\" "
                + "  && echo EGID=private || echo EGID=shared; } && "
                + "{ _codeybox_group_is_private 999999 "
                + "  && echo STRANGER=private || echo STRANGER=refused; } && "
                + "probe= && i=1 && while [ \"$i\" -le 64 ]; do "
                + "  g=$(( $(id -g) + 100000 + i )); "
                + "  id -G | tr ' ' '\\n' | grep -qx \"$g\" && { i=$((i+1)); continue; }; "
                + "  getent group \"$g\" 2>/dev/null | grep -q . && { i=$((i+1)); continue; }; "
                + "  getent passwd 2>/dev/null | awk -F: -v g=\"$g\" '$4 == g { f = 1 } END { exit(f ? 0 : 1) }' && { i=$((i+1)); continue; }; "
                + "  probe=$g; break; "
                + "done; "
                + "if [ -z \"$probe\" ]; then echo PROBE=skipped; else "
                + "  printf 'probex:x:%s:\\n' \"$probe\" > \"$3/group\" && "
                + "  : > \"$3/passwd\" && "
                + "  _codeybox_group_db=\"$3/group\" && "
                + "  _codeybox_passwd_db=\"$3/passwd\" && "
                + "  _codeybox_private_group_gid= && _codeybox_private_group_verdict= && "
                + "  { _codeybox_group_is_private \"$probe\" "
                + "    && echo PROBE=private || echo PROBE=refused; }; fi",
                root, HealScriptRepoRoot(), root);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // The process's own egid is the gid every umask-002 fixture
            // relies on; if the environment cannot prove it private the
            // group-writable fixtures above would fail too.
            Assert.Contains("EGID=private", lines);
            Assert.Contains("STRANGER=refused", lines);
            // A gid absent from `id -G` whose ONLY group-db entry (the
            // fixture file) has an empty member list and which no account
            // holds as a primary gid must still be refused — deleting proof
            // 0 would flip this to private.
            Assert.False(
                lines.Contains("PROBE=skipped", StringComparer.Ordinal),
                "the held-gid probe must not be skipped — proof 0 is what this test isolates");
            Assert.Contains("PROBE=refused", lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The member-list and primary-gid proofs consume KEYLESS `getent group` /
    // `getent passwd` dumps, which only iterate NSS modules that support
    // setgrent/getgrent enumeration. sssd/AD/LDAP commonly run with
    // enumeration disabled while keyed lookups still resolve, so a non-empty
    // dump is not proof of coverage: _codeybox_nss_enumeration_complete must
    // fail closed unless EVERY configured source for BOTH databases is
    // provably enumerable. These fixtures drive that gate through the
    // script's db-path overrides; the verdict still comes from the real
    // getent dumps (this account's own egid), so "private" additionally
    // requires the host's normal private-group proofs to hold — the same
    // assumption HealScript_GroupIsPrivate_RequiresHeldGid already makes.
    [Fact]
    public async Task HealScript_GroupPrivacy_RequiresEnumerableNssSources()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var fx = Path.Combine(root, "nss");
        var confDir = Path.Combine(fx, "sssd", "conf.d");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(confDir);
        try
        {
            async Task<string> ProbeAsync(
                string nsswitch,
                string? sssdConf,
                string? dropin,
                string? vendorConf = null)
            {
                await File.WriteAllTextAsync(Path.Combine(fx, "nsswitch.conf"), nsswitch);
                var confPath = Path.Combine(fx, "sssd", "sssd.conf");
                if (sssdConf is null)
                {
                    if (File.Exists(confPath))
                        File.Delete(confPath);
                }
                else
                {
                    await File.WriteAllTextAsync(confPath, sssdConf);
                }
                var vendorPath = Path.Combine(fx, "sssd", "vendor-sssd.conf");
                if (vendorConf is null)
                {
                    if (File.Exists(vendorPath))
                        File.Delete(vendorPath);
                }
                else
                {
                    await File.WriteAllTextAsync(vendorPath, vendorConf);
                }
                foreach (var stale in Directory.GetFiles(confDir))
                {
                    File.Delete(stale);
                }
                if (dropin is not null)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(confDir, "10-enum.conf"), dropin);
                }

                var result = await RunHealShellAsync(
                    NewHealEnv(root, home),
                    "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && "
                    + "_codeybox_nsswitch_conf=\"$3/nsswitch.conf\" && "
                    + "_codeybox_sssd_conf=\"$3/sssd/sssd.conf\" && "
                    + "_codeybox_sssd_vendor_conf=\"$3/sssd/vendor-sssd.conf\" && "
                    + "_codeybox_sssd_conf_d=\"$3/sssd/conf.d\" && "
                    + "_codeybox_private_group_gid= && _codeybox_private_group_verdict= && "
                    + "_codeybox_nss_enum_checked=0 && _codeybox_nss_enum_ok=0 && "
                    + "{ _codeybox_group_is_private \"$(id -g)\" "
                    + "  && echo G=private || echo G=refused; }",
                    root, HealScriptRepoRoot(), fx);
                Assert.True(result.Success, result.Stdout + result.Stderr);
                var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Contains(lines, l => l is "G=private" or "G=refused");
                return lines.First(l => l is "G=private" or "G=refused");
            }

            // files-only: fully covered by the direct /etc/* union.
            Assert.Equal(
                "G=private",
                await ProbeAsync("group: files\npasswd: files\n", null, null));

            // sss with enumeration disabled (the sssd default): the keyless
            // dump cannot be shown complete, so the gid must be refused even
            // though every other proof would pass.
            const string SssdNoEnum =
                "[sssd]\ndomains = example.com\n[domain/example.com]\n";
            Assert.Equal(
                "G=refused",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n", SssdNoEnum, null));

            // A later drop-in proving enumerate=true for every served domain
            // restores coverage.
            Assert.Equal(
                "G=private",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n",
                    SssdNoEnum,
                    "[domain/example.com]\nenumerate = true\n"));

            // A non-enumerable module (nss_ldap) can never be proven covered.
            Assert.Equal(
                "G=refused",
                await ProbeAsync("group: files ldap\npasswd: files\n", null, null));

            // Two sssd domains where only one enumerates still fails closed.
            Assert.Equal(
                "G=refused",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n",
                    "[sssd]\ndomains = a.com, b.com\n"
                        + "[domain/a.com]\nenumerate = true\n"
                        + "[domain/b.com]\n",
                    null));

            // With no system sssd.conf the vendor config applies, and the real
            // conf.d snippets still merge over it.
            Assert.Equal(
                "G=refused",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n",
                    null, null, SssdNoEnum));
            Assert.Equal(
                "G=private",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n",
                    null,
                    "[domain/example.com]\nenumerate = true\n",
                    SssdNoEnum));

            // A system conf masks the vendor file entirely.
            Assert.Equal(
                "G=private",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n",
                    "[sssd]\ndomains = example.com\n"
                        + "[domain/example.com]\nenumerate = true\n",
                    null,
                    SssdNoEnum));

            // The retired sssd.conf.d spelling is not SSSD's snippet dir: an
            // override planted there must not prove enumeration for a system
            // conf that still has enumerate=false.
            var wrongDropinDir = Path.Combine(fx, "sssd", "sssd.conf.d");
            Directory.CreateDirectory(wrongDropinDir);
            await File.WriteAllTextAsync(
                Path.Combine(wrongDropinDir, "10-enum.conf"),
                "[domain/example.com]\nenumerate = true\n");
            Assert.Equal(
                "G=refused",
                await ProbeAsync(
                    "group: files sss\npasswd: files sss\n", SssdNoEnum, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // CODEYBOX_NUGET_HEAL_REPORT=1 makes the dot-sourced recovery emit the
    // machine-readable verdict the in-process NuGetHomeGuard.targets task
    // applies inside MSBuild. Pin the contract: a usable home reports
    // "usable" with its canonical paths; an unusable home under
    // CODEYBOX_NUGET_HEAL_NO_QUARANTINE reports "redirected" with the
    // fallback DOTNET_CLI_HOME and leaves the tree untouched; the same home
    // WITHOUT the suppression is quarantined once and reports "usable" for
    // the recreated home.
    [Fact]
    public async Task HealScript_Report_EmitsVerdictForGuardConsumer()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = HealScriptRepoRoot();

        // Case 1: healthy home -> usable, paths reported.
        var rootOk = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var homeOk = Path.Combine(rootOk, "home");
        Directory.CreateDirectory(homeOk);
        try
        {
            var env = NewHealEnv(rootOk, homeOk);
            env["CODEYBOX_NUGET_HEAL_REPORT"] = "1";
            env["CODEYBOX_NUGET_HEAL_NO_QUARANTINE"] = "1";
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . ./scripts/nuget-home-heal.sh",
                repoRoot);
            Assert.True(result.Success, result.Stdout + result.Stderr);
            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("CODEYBOX_HEAL_VERDICT=usable", lines);
            var reportedHomeLine = lines.FirstOrDefault(
                l => l.StartsWith("CODEYBOX_HEAL_HOME=", StringComparison.Ordinal));
            Assert.NotNull(reportedHomeLine);
            var reportedHome = reportedHomeLine!["CODEYBOX_HEAL_HOME=".Length..];
            Assert.False(string.IsNullOrEmpty(reportedHome));
            var reportedPackagesLine = lines.FirstOrDefault(
                l => l.StartsWith("CODEYBOX_HEAL_NUGET_PACKAGES=", StringComparison.Ordinal));
            Assert.NotNull(reportedPackagesLine);
            Assert.Equal(
                reportedHome.TrimEnd('/') + "/.nuget/packages",
                reportedPackagesLine!["CODEYBOX_HEAL_NUGET_PACKAGES=".Length..]);
        }
        finally
        {
            Directory.Delete(rootOk, recursive: true);
        }

        // Case 2: unusable home + NO_QUARANTINE -> redirected, tree untouched.
        var rootNoQ = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var homeNoQ = Path.Combine(rootNoQ, "home");
        var outsideNoQ = Path.Combine(rootNoQ, "outside");
        Directory.CreateDirectory(homeNoQ);
        Directory.CreateDirectory(outsideNoQ);
        File.CreateSymbolicLink(Path.Combine(homeNoQ, ".nuget"), outsideNoQ);
        try
        {
            var env = NewHealEnv(rootNoQ, homeNoQ);
            env["CODEYBOX_NUGET_HEAL_REPORT"] = "1";
            env["CODEYBOX_NUGET_HEAL_NO_QUARANTINE"] = "1";
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . ./scripts/nuget-home-heal.sh",
                repoRoot);
            Assert.True(result.Success, result.Stdout + result.Stderr);
            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("CODEYBOX_HEAL_VERDICT=redirected", lines);
            var cliHome = lines.FirstOrDefault(
                l => l.StartsWith("CODEYBOX_HEAL_DOTNET_CLI_HOME=", StringComparison.Ordinal));
            Assert.NotNull(cliHome);
            var cliHomePath = cliHome!["CODEYBOX_HEAL_DOTNET_CLI_HOME=".Length..];
            Assert.False(string.IsNullOrEmpty(cliHomePath));
            Assert.True(Directory.Exists(cliHomePath), "reported cli home must exist");
            var packages = lines.FirstOrDefault(
                l => l.StartsWith("CODEYBOX_HEAL_NUGET_PACKAGES=", StringComparison.Ordinal));
            Assert.NotNull(packages);
            var packagesPath = packages!["CODEYBOX_HEAL_NUGET_PACKAGES=".Length..];
            Assert.Equal(
                cliHomePath.TrimEnd('/') + "/.nuget/packages",
                packagesPath);
            Assert.False(
                packagesPath.StartsWith(homeNoQ + "/", StringComparison.Ordinal),
                "the rejected home's packages path must not be reported");
            Assert.Empty(Directory.GetDirectories(homeNoQ, ".nuget.codeybox-unwritable.*"));
            Assert.True(
                File.GetAttributes(Path.Combine(homeNoQ, ".nuget")).HasFlag(FileAttributes.ReparsePoint),
                "the rejected tree must be left exactly as found");
        }
        finally
        {
            Directory.Delete(rootNoQ, recursive: true);
        }

        // Case 3: same unusable home WITHOUT suppression -> quarantined once,
        // fresh home usable -> verdict usable.
        var rootQ = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var homeQ = Path.Combine(rootQ, "home");
        var outsideQ = Path.Combine(rootQ, "outside");
        Directory.CreateDirectory(homeQ);
        Directory.CreateDirectory(outsideQ);
        File.CreateSymbolicLink(Path.Combine(homeQ, ".nuget"), outsideQ);
        try
        {
            var env = NewHealEnv(rootQ, homeQ);
            env["CODEYBOX_NUGET_HEAL_REPORT"] = "1";
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . ./scripts/nuget-home-heal.sh",
                repoRoot);
            Assert.True(result.Success, result.Stdout + result.Stderr);
            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("CODEYBOX_HEAL_VERDICT=usable", lines);
            Assert.Single(Directory.GetDirectories(homeQ, ".nuget.codeybox-unwritable.*"));
        }
        finally
        {
            Directory.Delete(rootQ, recursive: true);
        }
    }

    // CODEYBOX_NUGET_HEAL_NO_QUARANTINE suppresses the destructive quarantine
    // only on the literal value "1": the per-project MSBuild hooks set exactly
    // that, and the solution-level hook unsets the variable before sourcing.
    // A mistyped or inherited "0"/"false" must NOT silently disable the
    // mandated once-per-solution repair.
    [Fact]
    public async Task HealScript_NoQuarantine_SuppressesOnlyLiteralOne()
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (var value in new[] { "0", "false" })
        {
            var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
            var home = Path.Combine(root, "home");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(outside);
            File.CreateSymbolicLink(Path.Combine(home, ".nuget"), outside);
            try
            {
                var env = NewHealEnv(root, home);
                env["CODEYBOX_NUGET_HEAL_NO_QUARANTINE"] = value;
                var result = await DotSourceHealAsyncWithEnv(env);
                Assert.True(result.Success, result.Stdout + result.Stderr);
                Assert.Single(
                    Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // Literal "1" still suppresses: no quarantine, tree left in place.
        var rootOne = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var homeOne = Path.Combine(rootOne, "home");
        var outsideOne = Path.Combine(rootOne, "outside");
        Directory.CreateDirectory(homeOne);
        Directory.CreateDirectory(outsideOne);
        File.CreateSymbolicLink(Path.Combine(homeOne, ".nuget"), outsideOne);
        try
        {
            var env = NewHealEnv(rootOne, homeOne);
            env["CODEYBOX_NUGET_HEAL_NO_QUARANTINE"] = "1";
            var result = await DotSourceHealAsyncWithEnv(env);
            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.Empty(Directory.GetDirectories(homeOne, ".nuget.codeybox-unwritable.*"));
        }
        finally
        {
            Directory.Delete(rootOne, recursive: true);
        }
    }

    private static Task<ProcessRunResult> DotSourceHealAsyncWithEnv(
        IReadOnlyDictionary<string, string> environment)
    {
        var repoRoot = HealScriptRepoRoot();
        return RunHealShellAsync(
            environment,
            "cd \"$1\" && . ./scripts/nuget-home-heal.sh",
            repoRoot);
    }

    // Regression: a directory that grants execute without read is unlistable
    // to find, so its children escaped the old discarded-stderr scan while
    // remaining consumable by NuGet through a constructed path — a tampered
    // extracted entry hidden there would still run its .props/.targets in
    // the gate. The admission scan must fail CLOSED on the traversal error
    // rather than admit a partially verified tree.
    [Fact]
    public async Task HealScript_AdmissionGuards_DeclineUnlistableCacheSubtree()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process lists every directory, so the traversal error
        // is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(home);
        try
        {
            // A clean-looking cache whose sealed subdirectory hides a planted
            // entry: everything VISIBLE is euid-owned with no write bits, so a
            // scan that swallows traversal errors would wrongly admit it.
            var hidden = Path.Combine(cache, "pkg", "sealed");
            Directory.CreateDirectory(hidden);
            await File.WriteAllTextAsync(Path.Combine(hidden, "marker.txt"), "planted");
            await ChmodAsync("111", hidden); // execute without read: unlistable

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && "
                + "{ _codeybox_secure_package_cache \"$3\" >/dev/null 2>&1 "
                + "  && echo CACHE=accepted || echo CACHE=declined; }",
                root, HealScriptRepoRoot(), cache);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("CACHE=declined", lines);
        }
        finally
        {
            var sealedDir = Path.Combine(cache, "pkg", "sealed");
            if (Directory.Exists(sealedDir))
                await ChmodAsync("755", sealedDir);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HealScript_DirectDotSource_LeavesWritableHomeIntact()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var marker = Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "cached");

            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            // A usable home is untouched: no quarantine, no leftover probe, real cache intact.
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.False(File.Exists(
                Path.Combine(home, ".nuget", "NuGet", ".codeybox-writable-probe")));
            Assert.Equal("cached", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A usable SETTINGS dir does not make the consumed sibling cache safe:
    // <cli-home>/.nuget/packages is NuGet's default global-packages folder on
    // every live path (RestorePackagesPath also pins it), and an extracted
    // package's build/.props/.targets run inside the consuming build — so a
    // present cache must pass the same whole-tree admission the reuse/seed
    // paths apply. A co-tenant-writable tree is quarantined aside like any
    // other broken .nuget so NuGet re-extracts into clean storage.
    [Fact]
    public async Task HealScript_UsableHome_QuarantinesCoTenantWritableCache()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process can write anywhere; the writable-cache fixture
        // bit is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            // Healthy settings home (readable config, private dirs) — the
            // only fault is the consumed cache carrying a co-tenant write bit.
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(nugetDir);
            await File.WriteAllTextAsync(Path.Combine(nugetDir, "NuGet.Config"), "<configuration/>");
            var packages = Path.Combine(home, ".nuget", "packages");
            Directory.CreateDirectory(Path.Combine(packages, "pkg"));
            await File.WriteAllTextAsync(Path.Combine(packages, "pkg", "marker.txt"), "planted");
            await ChmodAsync("777", packages);

            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(
                File.Exists(Path.Combine(home, ".nuget", "NuGet", "NuGet.Config")),
                "the rebuilt home must carry the seeded readable user config");
            Assert.False(
                File.Exists(Path.Combine(home, ".nuget", "packages"))
                || Directory.Exists(Path.Combine(home, ".nuget", "packages")),
                "an unconsumable cache must be quarantined aside, not consumed");
        }
        finally
        {
            // The dirty tree moved aside with the quarantined .nuget; restore
            // its write bits so the fixture can be removed.
            var quarantined = Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*");
            if (quarantined.Length > 0)
                await ChmodAsync("755", Path.Combine(quarantined[0], "packages"));
            Directory.Delete(root, recursive: true);
        }
    }

    // Same consumed-cache invariant when `.nuget/packages` is a SYMLINK: the
    // scan evaluates the link's target, so a link resolving into a
    // co-tenant-writable directory outside the home is quarantined too — an
    // existence-only check would have skipped the entry entirely (and -e
    // alone cannot even see a dangling link).
    [Fact]
    public async Task HealScript_UsableHome_QuarantinesPackagesSymlink_ToWritableTarget()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process can write anywhere; the writable-target
        // fixture bit is unobservable when the suite runs as root.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outside);
        try
        {
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(nugetDir);
            await File.WriteAllTextAsync(Path.Combine(nugetDir, "NuGet.Config"), "<configuration/>");
            await ChmodAsync("777", outside); // writable by every co-tenant
            File.CreateSymbolicLink(Path.Combine(home, ".nuget", "packages"), outside);

            var result = await DotSourceHealAsync(root, home);
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(
                File.Exists(Path.Combine(home, ".nuget", "NuGet", "NuGet.Config")),
                "the rebuilt home must carry the seeded readable user config");
            Assert.False(
                File.Exists(Path.Combine(home, ".nuget", "packages"))
                || Directory.Exists(Path.Combine(home, ".nuget", "packages")),
                "a packages symlink to an unconsumable target must not be consumed");
            // The quarantine moves the entry aside — it never deletes through
            // the link, so the outside target survives untouched.
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string MigrationMarkerPath(string home)
        => Path.Combine(home, ".local", "share", "NuGet", "Migrations", "1");

    // Since runtime 10.0.13, NuGet's first-use/restore "NuGet-Migrations"
    // user-scoped mutex resolves under $XDG_RUNTIME_DIR (else
    // $HOME/.dotnet/corefx/Runtime/SharedMemory); a gate exec whose launch env
    // resolves neither aborts before MSBuild. NuGet only takes that mutex while
    // its applied-migration marker is absent, so the heal seeds the empty
    // `Migrations/1` marker under every home a later exec could resolve.
    [Fact]
    public async Task HealScript_DirectDotSource_SeedsMigrationMarkers_UnderResolvedHomes()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var cliHome = Path.Combine(root, "cli");
        var workdir = Path.Combine(root, "work");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cliHome);
        Directory.CreateDirectory(workdir);
        // The script only seeds the stamped repo-local home when its cwd is the
        // repository root — recognized by the script's own presence there.
        Directory.CreateDirectory(Path.Combine(workdir, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "scripts", "nuget-home-heal.sh"), "#\n");

        // An already-applied marker is real migration state — never clobber it.
        var existingMarker = MigrationMarkerPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(existingMarker)!);
        await File.WriteAllTextAsync(existingMarker, "preexisting");
        try
        {
            var env = NewHealEnv(root, home);
            env["DOTNET_CLI_HOME"] = cliHome;
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\"",
                workdir, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var uid = await TestSupport.CurrentUidAsync();
            var seededHomes = new[]
            {
                home,
                cliHome,
                // The stamped repo-local home auditor execs get for dotnet.
                Path.Combine(workdir, ".dotnet-cli-home"),
                // The shared per-user fallback the preamble generations redirect to.
                Path.Combine(root, "codeybox-nuget-home-" + uid),
            };
            foreach (var candidate in seededHomes)
            {
                var marker = MigrationMarkerPath(candidate);
                Assert.True(File.Exists(marker), $"expected migration marker under {candidate}");
            }

            Assert.Equal("preexisting", await File.ReadAllTextAsync(existingMarker));
            // Seeded markers are the empty file NuGet itself writes after a
            // no-op migration run.
            Assert.Equal(string.Empty, await File.ReadAllTextAsync(MigrationMarkerPath(cliHome)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Regression for the confused-deputy write the seeder could perform: the
    // marker leaf is created with `: >`, which writes THROUGH a symlink, so a
    // dangling symlink planted at $XDG_DATA_HOME/NuGet/Migrations/1 — where
    // `-e` is false and a `-e`-only guard would open it for writing — must be
    // left alone, never turned into an empty file at the link's target.
    [Fact]
    public async Task HealScript_DirectDotSource_NeverWritesMarkerThroughSymlinkLeaf()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var xdgData = Path.Combine(root, "xdg-data");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(xdgData);
        Directory.CreateDirectory(outside);
        try
        {
            var markerDir = Path.Combine(xdgData, "NuGet", "Migrations");
            Directory.CreateDirectory(markerDir);
            var markerLink = Path.Combine(markerDir, "1");
            var linkTarget = Path.Combine(outside, "planted-target");
            File.CreateSymbolicLink(markerLink, linkTarget);

            var env = NewHealEnv(root, home);
            env["XDG_DATA_HOME"] = xdgData;
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\"",
                root, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            // The link's target must not exist — nothing was written through
            // the planted symlink — and the placeholder itself is still a link.
            Assert.False(File.Exists(linkTarget), "marker write must not follow the planted symlink");
            Assert.True(
                File.GetAttributes(markerLink).HasFlag(FileAttributes.ReparsePoint),
                "the existing symlink leaf is migration state and must be left untouched");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Same confused-deputy class one level up: `mkdir -p` descends through a
    // symlinked intermediate component, so a planted $HOME/.local link would
    // have planted the whole marker tree under the link's target directory.
    // The seeder must refuse to descend and leave the target untouched.
    [Fact]
    public async Task HealScript_DirectDotSource_NeverDescendsThroughSymlinkedComponent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outside);
        try
        {
            File.CreateSymbolicLink(Path.Combine(home, ".local"), outside);

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\"",
                root, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.False(
                File.Exists(Path.Combine(outside, "share", "NuGet", "Migrations", "1")),
                "no marker may be created under the symlink's target");
            Assert.False(
                Directory.Exists(Path.Combine(outside, "share")),
                "no part of the marker tree may be created under the symlink's target");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Total fallback failure: HOME naming a regular file is rejected by the
    // adopt chain, and TMPDIR naming a regular file means neither the shared
    // per-user dir nor a private mktemp can be materialized. The rejected env
    // values must be UNSET for the caller's dotnet — left exported they would
    // hand it foreign-controlled storage.
    [Fact]
    public async Task HealScript_UnsetsRejectedVars_WhenNoFallbackExists()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home-as-file");
        var tmpFile = Path.Combine(root, "tmp-as-file");
        var workdir = Path.Combine(root, "work");
        Directory.CreateDirectory(workdir);
        try
        {
            await File.WriteAllTextAsync(home, "x");
            await File.WriteAllTextAsync(tmpFile, "x");

            var env = NewHealEnv(root, home);
            env["TMPDIR"] = tmpFile;
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf 'HOME=%s\\nXDG=%s\\nCLI=%s\\n' \"${HOME-UNSET}\" \"${XDG_RUNTIME_DIR-UNSET}\" \"${DOTNET_CLI_HOME-UNSET}\"",
                workdir, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("HOME=UNSET", lines);
            Assert.Contains("XDG=UNSET", lines);
            Assert.Contains("CLI=UNSET", lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Same no-fallback path one variable over: a rejected env-provided
    // DOTNET_CLI_HOME is unset so dotnet falls back to a healthy HOME rather
    // than consuming the foreign value.
    [Fact]
    public async Task HealScript_UnsetsRejectedCliHome_WhenNoFallbackExists()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var cliFile = Path.Combine(root, "cli-as-file");
        var tmpFile = Path.Combine(root, "tmp-as-file");
        var workdir = Path.Combine(root, "work");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workdir);
        try
        {
            await File.WriteAllTextAsync(cliFile, "x");
            await File.WriteAllTextAsync(tmpFile, "x");

            var env = NewHealEnv(root, home);
            env["TMPDIR"] = tmpFile;
            env["DOTNET_CLI_HOME"] = cliFile;
            var result = await RunHealShellAsync(
                env,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\" && printf 'HOME=%s\\nCLI=%s\\n' \"${HOME-UNSET}\" \"${DOTNET_CLI_HOME-UNSET}\"",
                workdir, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("CLI=UNSET", lines);
            Assert.Contains("HOME=" + home, lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The marker descent verifies each EXISTING component like the base — not
    // merely for symlink-ness: a co-tenant-writable intermediate inside a
    // private base can be swapped for a symlink between the checks and the
    // write, so the seeder must refuse to traverse it.
    [Fact]
    public async Task HealScript_DirectDotSource_SkipsCoTenantWritableMarkerComponent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var localDir = Path.Combine(home, ".local");
        var workdir = Path.Combine(root, "work");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(workdir);
        try
        {
            await ChmodAsync("777", localDir); // writable by every co-tenant

            var result = await RunHealShellAsync(
                NewHealEnv(root, home),
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\"",
                workdir, HealScriptRepoRoot());
            Assert.True(result.Success, result.Stdout + result.Stderr);

            Assert.False(
                File.Exists(MigrationMarkerPath(home)),
                "no marker may be seeded under a co-tenant-writable component");
            Assert.False(
                Directory.Exists(Path.Combine(localDir, "share")),
                "no part of the marker tree may be created under it");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // End-to-end: a gate exec whose env gives no usable runtime dir — here a
    // non-writable $XDG_RUNTIME_DIR, which takes precedence over a healthy
    // $HOME — crashed in dotnet first-use creating "NuGet-Migrations" before
    // MSBuild ran. After the heal seeds the migration marker under the stamped
    // repo-local home, the same wrapped `dotnet build` skips the mutex and
    // completes. Uses a stub-free real `dotnet` so the NuGet migration gate is
    // exercised, not simulated.
    [Fact]
    public async Task HealScript_SeededMigrationMarker_LetsDotnetBuildRun_WhenRuntimeDirUnusable()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A privileged process ignores the permission bits that make the
        // runtime dir unusable, so the pre-heal failure is unobservable.
        if (Environment.IsPrivilegedProcess)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-healsh-{Guid.NewGuid():N}");
        var workdir = Path.Combine(root, "work");
        var gateHome = Path.Combine(workdir, ".dotnet-cli-home");
        var cliHome = Path.Combine(root, "cli");
        var brokenRuntimeDir = Path.Combine(root, "xdg-broken");
        Directory.CreateDirectory(workdir);
        Directory.CreateDirectory(cliHome);
        Directory.CreateDirectory(brokenRuntimeDir);
        // Marks the fixture as the repository root so the heal seeds the
        // stamped repo-local home under it.
        Directory.CreateDirectory(Path.Combine(workdir, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(workdir, "scripts", "nuget-home-heal.sh"), "#\n");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workdir, "lib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(workdir, "C.cs"), "public class C { }\n");
            await ChmodAsync("000", brokenRuntimeDir);

            var gateEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Mirrors a raw dotnet auditor exec: the stamped repo-local
                // home, a fresh CLI home, and a runtime dir the user cannot use.
                ["HOME"] = gateHome,
                ["DOTNET_CLI_HOME"] = cliHome,
                ["XDG_RUNTIME_DIR"] = brokenRuntimeDir,
                ["PATH"] = Environment.GetEnvironmentVariable("PATH")
                    ?? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                ["LANG"] = "C.UTF-8",
            };
            var buildArgv = new List<string> { "dotnet", "build", Path.Combine(workdir, "lib.csproj") };

            var preHeal = await new DefaultProcessRunner().RunAsync(
                buildArgv,
                null,
                CancellationToken.None,
                maxStdoutBytes: 65536,
                maxStderrBytes: 65536,
                environment: gateEnv,
                workingDirectory: workdir);
            Assert.False(preHeal.Success, "the fixture must reproduce the NuGet-Migrations abort");
            Assert.Contains("NuGet-Migrations", preHeal.Stdout + preHeal.Stderr, StringComparison.Ordinal);

            var healEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Path.Combine(root, "home"),
                ["DOTNET_CLI_HOME"] = Path.Combine(root, "cli-setup"),
                ["PATH"] = gateEnv["PATH"],
                ["TMPDIR"] = root,
            };
            Directory.CreateDirectory(healEnv["HOME"]);
            var heal = await RunHealShellAsync(
                healEnv,
                "cd \"$1\" && . \"$2/scripts/nuget-home-heal.sh\"",
                workdir, HealScriptRepoRoot());
            Assert.True(heal.Success, heal.Stdout + heal.Stderr);
            Assert.True(
                File.Exists(MigrationMarkerPath(gateHome)),
                "the heal must seed the migration marker under the stamped gate home");

            var postHeal = await new DefaultProcessRunner().RunAsync(
                buildArgv,
                null,
                CancellationToken.None,
                maxStdoutBytes: 65536,
                maxStderrBytes: 65536,
                environment: gateEnv,
                workingDirectory: workdir);
            Assert.True(postHeal.Success, postHeal.Stdout + postHeal.Stderr);
            Assert.True(
                File.Exists(Path.Combine(workdir, "obj", "project.assets.json")),
                "restore must have run to completion");
        }
        finally
        {
            await ChmodAsync("755", brokenRuntimeDir);
            Directory.Delete(root, recursive: true);
        }
    }
}
