using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage of the load-bearing NuGet-home heal: the MSBuild
/// <c>InitialTargets</c> hook in Directory.NuGetHomeHeal.targets (wired into
/// Directory.Build.props and Directory.Solution.props). It is what lets the
/// build/test gates run their commands directly — <c>dotnet build &lt;solution&gt;</c>,
/// <c>dotnet build --no-incremental /warnaserror</c>, <c>dotnet test --no-build</c> —
/// in an environment that inherited an unreadable per-user NuGet home, with no
/// wrapper script, environment override, or orchestrator change.
///
/// The test drives a real <c>dotnet build</c> of a throwaway solution that imports
/// the repository's own heal targets, against a fixture HOME whose inherited
/// NuGet.Config is present-but-unreadable (the exact "Failed to read NuGet.Config
/// due to unauthorized access" abort). It fails if the hook stops running before
/// NuGet's user-config read, i.e. if a plain <c>dotnet build</c> no longer heals
/// itself — the regression that forced the gates offline.
/// </summary>
[Collection("Real build toolchain")]
public sealed class MsBuildNuGetHealTests
{
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

    // Locate a `dotnet` host to drive the fixture build with. Prefer the host
    // running this test (the same SDK the gates use), then DOTNET_ROOT, then PATH.
    // Returns null only when none resolves, so the test skips rather than
    // exercising nothing.
    private static string? ResolveDotnetDirectory()
    {
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (processDir is not null && File.Exists(Path.Combine(processDir, "dotnet")))
            return processDir;

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, "dotnet")))
            return dotnetRoot;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(entry, "dotnet")))
                return entry;
        }
        return null;
    }

    private static async Task CreateProjectAsync(string projectDir, string healTargets)
    {
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "proj.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Class1.cs"),
            "namespace Proj; public static class Class1 { public static int Value => 1; }\n");
        var propsBody =
            $"""
            <Project InitialTargets="CodeyBoxHealNuGetHome">
              <Import Project="{healTargets}" />
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Directory.Build.props"), propsBody);
        var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");
        await File.WriteAllTextAsync(
            offlineConfig,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<configuration><packageSources><clear /></packageSources></configuration>\n");
    }

    private static Task<ProcessRunResult> RunDotnetBuildAsync(
        string dotnetDir,
        string targetPath,
        string offlineConfig,
        Dictionary<string, string> env)
    {
        return new DefaultProcessRunner().RunAsync(
            [
                Path.Combine(dotnetDir, "dotnet"),
                "build",
                targetPath,
                "--configuration", "Debug",
                "-nodeReuse:false",
                "-p:UseSharedCompilation=false",
                "-p:RestoreConfigFile=" + offlineConfig,
            ],
            null,
            CancellationToken.None,
            maxStdoutBytes: 1 << 20,
            maxStderrBytes: 1 << 20,
            environment: env);
    }

    private static Dictionary<string, string> CreateBuildEnv(
        string dotnetDir,
        string home,
        string tmpDir,
        string? nugetHomeUnderTest = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["DOTNET_CLI_HOME"] = home,
            ["PATH"] = dotnetDir + ":/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["TMPDIR"] = tmpDir,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        };
        if (nugetHomeUnderTest is not null)
        {
            env["CODEYBOX_NUGET_HOME_UNDER_TEST"] = nugetHomeUnderTest;
        }
        return env;
    }

    [Fact]
    public async Task InitialTargetsHook_HealsUnreadableNuGetHome_ForPlainSolutionBuild()
    {
        // The heal is a POSIX-shell repair invoked from MSBuild; it only applies on
        // the Unix build/audit hosts, matching the target's own OS condition.
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot); // the heal targets file must exist for the gates to import it
        var healTargets = Path.Combine(repoRoot!, "Directory.NuGetHomeHeal.targets");

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return; // cannot locate the running SDK's dotnet host; nothing to drive

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuildheal-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(projectDir);
        try
        {
            // Throwaway solution + project. Both the solution- and project-level
            // props hook the shared heal target via InitialTargets and import the
            // repository's real targets file, so this exercises the exact wiring
            // the gates rely on. The project references no packages, so restore is
            // offline once the home is healed.
            await File.WriteAllTextAsync(
                Path.Combine(projectDir, "proj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(projectDir, "Class1.cs"),
                "namespace Proj; public static class Class1 { public static int Value => 1; }\n");

            var propsBody =
                $"""
                <Project InitialTargets="CodeyBoxHealNuGetHome">
                  <Import Project="{healTargets}" />
                </Project>
                """;
            await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.props"), propsBody);
            await File.WriteAllTextAsync(Path.Combine(root, "Directory.Solution.props"), propsBody);

            // Cleared package sources keep restore fully offline and deterministic:
            // the bare project needs no packages, so an empty source list suffices
            // and guarantees the test never touches the network.
            var offlineConfig = Path.Combine(root, "offline.nuget.config");
            await File.WriteAllTextAsync(
                offlineConfig,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                    + "<configuration><packageSources><clear /></packageSources></configuration>\n");

            await File.WriteAllTextAsync(
                Path.Combine(root, "app.slnx"),
                """
                <Solution>
                  <Project Path="proj/proj.csproj" />
                </Solution>
                """);

            // Present-but-unreadable inherited user config: the exact fatal read the
            // hook must pre-empt. The .nuget directory itself is writable, so the
            // heal takes its in-place quarantine path.
            var nugetDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(nugetDir);
            await File.WriteAllTextAsync(Path.Combine(nugetDir, "NuGet.Config"), "<configuration/>");
            await ChmodAsync("000", Path.Combine(nugetDir, "NuGet.Config"));
            var marker = Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "cached");

            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                // NuGet derives the per-user home from DOTNET_CLI_HOME, falling back
                // to HOME; pin both to the broken fixture so nothing escapes to the
                // real user profile.
                ["DOTNET_CLI_HOME"] = home,
                ["PATH"] = dotnetDir + ":/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                ["TMPDIR"] = root,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                // A default `dotnet build` leaves persistent, CPU- and RAM-hungry
                // MSBuild worker nodes plus a Roslyn VBCSCompiler server alive for
                // minutes (node reuse). Because this collection runs in xUnit's
                // sequential non-parallel phase alongside other timing-sensitive
                // audit tests, those lingering servers would saturate the host and
                // flake them. Disable every persistent server so the build spawns
                // nothing that outlives it.
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            };

            var result = await new DefaultProcessRunner().RunAsync(
                [
                    Path.Combine(dotnetDir, "dotnet"),
                    "build",
                    Path.Combine(root, "app.slnx"),
                    "--configuration", "Debug",
                    "-nodeReuse:false",
                    "-p:UseSharedCompilation=false",
                    "-p:RestoreConfigFile=" + offlineConfig,
                ],
                null,
                CancellationToken.None,
                maxStdoutBytes: 1 << 20,
                maxStderrBytes: 1 << 20,
                environment: env);

            // The build succeeds only because the InitialTargets hook healed the home
            // before NuGet's user-config read; without it restore aborts unauthorized.
            Assert.True(result.Success, result.Stdout + result.Stderr);

            // The broken tree is quarantined aside (preserved), not deleted, and a
            // fresh writable per-user home replaced it.
            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            var healedConfig = Path.Combine(home, ".nuget", "NuGet", "NuGet.Config");
            var seeded = await File.ReadAllTextAsync(healedConfig); // throws if still unreadable
            Assert.Contains("<configuration", seeded, StringComparison.Ordinal);

            // The populated offline cache survives through the preserved symlink.
            Assert.Equal("cached", await File.ReadAllTextAsync(
                Path.Combine(home, ".nuget", "packages", "pkg", "marker.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MsBuild_UidOwnedGroupWritableTree_PreservedIntact()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);
        var healTargets = Path.Combine(repoRoot!, "Directory.NuGetHomeHeal.targets");

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-groupw-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        var nugetHome = Path.Combine(home, ".nuget");
        var packages = Path.Combine(nugetHome, "packages", "pkg");
        var nugetDir = Path.Combine(nugetHome, "NuGet");

        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(nugetDir);
        var marker = Path.Combine(packages, "marker.txt");
        await File.WriteAllTextAsync(marker, "cached");
        var config = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration/>");

        // Linux umask 002: group-writable tree owned by current UID
        await ChmodAsync("0775", nugetHome);
        await ChmodAsync("0775", Path.Combine(nugetHome, "packages"));
        await ChmodAsync("0775", packages);
        await ChmodAsync("0775", nugetDir);
        await ChmodAsync("0664", marker);
        await ChmodAsync("0664", config);

        await CreateProjectAsync(projectDir, healTargets);
        var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");

        try
        {
            var env = CreateBuildEnv(dotnetDir, home, root, nugetHome);
            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env);

            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(File.Exists(marker));
            Assert.Equal("cached", await File.ReadAllTextAsync(marker));
            Assert.False(
                File.GetAttributes(Path.Combine(nugetHome, "packages")).HasFlag(FileAttributes.ReparsePoint),
                "packages must not have been converted to a symlink");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MsBuild_ForeignOwnedTree_IsQuarantined()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);
        var healTargets = Path.Combine(repoRoot!, "Directory.NuGetHomeHeal.targets");

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-foreign-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(home);
        // Point .nuget at a foreign root-owned system directory (/etc)
        File.CreateSymbolicLink(nugetHome, "/etc");

        await CreateProjectAsync(projectDir, healTargets);
        var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");

        try
        {
            var env = CreateBuildEnv(dotnetDir, home, root, nugetHome);
            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env);

            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(Directory.Exists(Path.Combine(nugetHome, "NuGet")));
            Assert.False(
                File.GetAttributes(nugetHome).HasFlag(FileAttributes.ReparsePoint),
                "recreated .nuget must be a real directory, not a symlink");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MsBuild_ConcurrentInvocations_ProduceAtMostOneQuarantine_AndNeverRemoveHealthyCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);
        var healTargets = Path.Combine(repoRoot!, "Directory.NuGetHomeHeal.targets");

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-concurrent-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var nugetHome = Path.Combine(home, ".nuget");

        var proj1 = Path.Combine(root, "proj1");
        var proj2 = Path.Combine(root, "proj2");
        await CreateProjectAsync(proj1, healTargets);
        await CreateProjectAsync(proj2, healTargets);
        var offline1 = Path.Combine(proj1, "offline.nuget.config");
        var offline2 = Path.Combine(proj2, "offline.nuget.config");

        try
        {
            // Case 1: Unusable tree requiring quarantine. Two concurrent builds must
            // coordinate so at most one quarantine directory is created.
            File.CreateSymbolicLink(nugetHome, "/etc");
            var envBroken = CreateBuildEnv(dotnetDir, home, root, nugetHome);

            var t1Broken = RunDotnetBuildAsync(dotnetDir, Path.Combine(proj1, "proj.csproj"), offline1, envBroken);
            var t2Broken = RunDotnetBuildAsync(dotnetDir, Path.Combine(proj2, "proj.csproj"), offline2, envBroken);
            var resultsBroken = await Task.WhenAll(t1Broken, t2Broken);

            Assert.True(resultsBroken[0].Success, resultsBroken[0].Stdout + resultsBroken[0].Stderr);
            Assert.True(resultsBroken[1].Success, resultsBroken[1].Stdout + resultsBroken[1].Stderr);
            Assert.Single(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(Directory.Exists(Path.Combine(nugetHome, "NuGet")));

            // Case 2: Healthy tree with umask 002. Concurrent builds must not quarantine
            // and must leave cached packages intact.
            var homeHealthy = Path.Combine(root, "home-healthy");
            var nugetHealthy = Path.Combine(homeHealthy, ".nuget");
            var packages = Path.Combine(nugetHealthy, "packages", "pkg");
            var nugetDir = Path.Combine(nugetHealthy, "NuGet");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(nugetDir);
            var marker = Path.Combine(packages, "marker.txt");
            await File.WriteAllTextAsync(marker, "concurrent-cache");
            var config = Path.Combine(nugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration/>");

            await ChmodAsync("0775", nugetHealthy);
            await ChmodAsync("0775", Path.Combine(nugetHealthy, "packages"));
            await ChmodAsync("0775", packages);
            await ChmodAsync("0775", nugetDir);
            await ChmodAsync("0664", marker);
            await ChmodAsync("0664", config);

            var envHealthy = CreateBuildEnv(dotnetDir, homeHealthy, root, nugetHealthy);
            var t1Healthy = RunDotnetBuildAsync(dotnetDir, Path.Combine(proj1, "proj.csproj"), offline1, envHealthy);
            var t2Healthy = RunDotnetBuildAsync(dotnetDir, Path.Combine(proj2, "proj.csproj"), offline2, envHealthy);
            var resultsHealthy = await Task.WhenAll(t1Healthy, t2Healthy);

            Assert.True(resultsHealthy[0].Success, resultsHealthy[0].Stdout + resultsHealthy[0].Stderr);
            Assert.True(resultsHealthy[1].Success, resultsHealthy[1].Stdout + resultsHealthy[1].Stderr);
            Assert.Empty(Directory.GetDirectories(homeHealthy, ".nuget.codeybox-unwritable.*"));
            Assert.True(File.Exists(marker));
            Assert.Equal("concurrent-cache", await File.ReadAllTextAsync(marker));
            Assert.False(
                File.GetAttributes(Path.Combine(nugetHealthy, "packages")).HasFlag(FileAttributes.ReparsePoint),
                "packages must not have been converted to a symlink");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

