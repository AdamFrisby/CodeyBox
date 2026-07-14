using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the per-user NuGet-home recovery block in the repository's build.sh
/// against real filesystem fixtures. That block is the load-bearing remedy that
/// lets the direct <c>dotnet build</c>/<c>dotnet test</c> gates run in environments
/// that COW-inherited an unreadable or unwritable NuGet home: NuGet performs a fatal
/// user-config read during restore-graph generation, before any in-tree MSBuild hook
/// can execute, so the home has to be healed on disk before <c>dotnet</c> starts.
/// These tests fail if the recovery stops detecting a broken home, stops quarantining
/// it aside, or stops preserving the baked offline package cache.
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
    // `dotnet build` returns without a real build. The stub drops a sentinel that
    // proves the heal fell through to the build step rather than aborting.
    private static async Task<(ProcessRunResult Result, string Home, string Sentinel)>
        RunBuildScriptAsync(string root, Func<string, Task> seedHomeAsync)
    {
        var home = Path.Combine(root, "home");
        var stubDir = Path.Combine(root, "stub-bin");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(stubDir);

        var sentinel = Path.Combine(root, "dotnet-invoked");
        var stub = Path.Combine(stubDir, "dotnet");
        await File.WriteAllTextAsync(stub, "#!/bin/sh\nprintf ran > '" + sentinel + "'\nexit 0\n");
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

        var result = await new DefaultProcessRunner().RunAsync(
            ["/bin/sh", BuildScriptPath()],
            null,
            CancellationToken.None,
            maxStdoutBytes: 65536,
            maxStderrBytes: 65536,
            environment: env);
        return (result, home, sentinel);
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
}
