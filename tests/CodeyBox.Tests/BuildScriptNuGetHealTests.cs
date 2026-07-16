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

    private static async Task<ProcessRunResult> DotSourceHealAsync(string root, string home)
    {
        var repoRoot = HealScriptRepoRoot();
        Assert.True(
            File.Exists(Path.Combine(repoRoot, "scripts", "nuget-home-heal.sh")),
            "the shared heal script must exist for the gate call sites to source it");

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["TMPDIR"] = root,
        };
        // Mirror the gate call sites verbatim: cd into the checkout, then dot-source
        // the repository-relative recovery.
        return await new DefaultProcessRunner().RunAsync(
            ["/bin/sh", "-c", "cd \"$1\" && . ./scripts/nuget-home-heal.sh", "sh", repoRoot],
            null,
            CancellationToken.None,
            maxStdoutBytes: 65536,
            maxStderrBytes: 65536,
            environment: env);
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
            var repoRoot = HealScriptRepoRoot();
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                ["TMPDIR"] = root,
            };
            // Dot-source the heal, then record the exported DOTNET_CLI_HOME so the
            // test can inspect the scratch home the fallback redirected dotnet to.
            var result = await new DefaultProcessRunner().RunAsync(
                [
                    "/bin/sh", "-c",
                    "cd \"$1\" && . ./scripts/nuget-home-heal.sh && printf '%s' \"$DOTNET_CLI_HOME\" > \"$2\"",
                    "sh", repoRoot, sentinel,
                ],
                null,
                CancellationToken.None,
                maxStdoutBytes: 65536,
                maxStderrBytes: 65536,
                environment: env);
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
}
