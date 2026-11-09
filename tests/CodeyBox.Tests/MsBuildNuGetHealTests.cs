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

    private static async Task SetUmask002PermissionsAsync(
        string nugetHome,
        string packageDir,
        string nugetDir,
        string marker,
        string config)
    {
        var packagesRoot = Path.Combine(nugetHome, "packages");
        await ChmodAsync(UnixUmask002Permissions.DirectoryMode, nugetHome);
        await ChmodAsync(UnixUmask002Permissions.DirectoryMode, packagesRoot);
        await ChmodAsync(UnixUmask002Permissions.DirectoryMode, packageDir);
        await ChmodAsync(UnixUmask002Permissions.DirectoryMode, nugetDir);
        await ChmodAsync(UnixUmask002Permissions.FileMode, marker);
        await ChmodAsync(UnixUmask002Permissions.FileMode, config);
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

    private static async Task CreateProjectAsync(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        // Record the RestorePackagesPath the build actually evaluated so tests
        // can prove the in-process NuGetHomeGuard repointed the auto-pinned
        // cache away from a rejected home — the pin lives in
        // Directory.Build.props and only the guard task may move it.
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "proj.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <Target Name="CodeyBoxDumpRestorePackagesPath" AfterTargets="Build">
                <WriteLinesToFile
                  File="$(MSBuildProjectDirectory)/restore-packages-path.txt"
                  Lines="$(RestorePackagesPath)"
                  Overwrite="true" />
              </Target>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Class1.cs"),
            "namespace Proj; public static class Class1 { public static int Value => 1; }\n");
        var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");
        await File.WriteAllTextAsync(
            offlineConfig,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<configuration><packageSources><clear /></packageSources></configuration>\n");
    }

    // Write the fixture's Directory.Build.*/Directory.Solution.* files so the
    // throwaway tree imports the repository's REAL heal wiring — the shared
    // InitialTargets hook, the _CodeyBoxInSolution suppression, the
    // _CodeyBoxNuGetHealSolutionLevel solution-level marker, the
    // CODEYBOX_NUGET_HEAL_NO_QUARANTINE per-project mode, and the
    // NuGetHomeGuard in-process redirect — rather than a hand-copied subset
    // that could drift from what production builds actually evaluate.
    private static async Task CreateBuildWiringAsync(string root, string repoRoot)
    {
        string Import(string repoFile) =>
            $"<Project><Import Project=\"{repoRoot}/{repoFile}\" /></Project>\n";
        await File.WriteAllTextAsync(
            Path.Combine(root, "Directory.Build.props"), Import("Directory.Build.props"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "Directory.Build.targets"), Import("Directory.Build.targets"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "Directory.Solution.props"), Import("Directory.Solution.props"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "Directory.Solution.targets"), Import("Directory.Solution.targets"));
    }

    private static Task<ProcessRunResult> RunDotnetBuildAsync(
        string dotnetDir,
        string targetPath,
        string offlineConfig,
        Dictionary<string, string> env,
        params string[] extraArguments)
    {
        var argv = new List<string>
        {
            Path.Combine(dotnetDir, "dotnet"),
            "build",
            targetPath,
            "--configuration", "Debug",
            "-nodeReuse:false",
            "-p:UseSharedCompilation=false",
            "-p:RestoreConfigFile=" + offlineConfig,
        };
        argv.AddRange(extraArguments);
        return new DefaultProcessRunner().RunAsync(
            argv,
            null,
            CancellationToken.None,
            maxStdoutBytes: 1 << 20,
            maxStderrBytes: 1 << 20,
            environment: env);
    }

    private static async Task<string> ReadRestorePackagesPathAsync(string projectDir)
    {
        return (await File.ReadAllTextAsync(
            Path.Combine(projectDir, "restore-packages-path.txt"))).Trim();
    }

    private static Dictionary<string, string> CreateBuildEnv(
        string dotnetDir,
        string home,
        string tmpDir,
        string? nugetHomeUnderTest = null)
    {
        // Seed NuGet's applied-migration marker under the test home: without it,
        // dotnet CLI first-use takes the "NuGet-Migrations" named mutex before
        // MSBuild starts, which fails under concurrent first-run execution.
        var markerDir = Path.Combine(home, ".local", "share", "NuGet", "Migrations");
        Directory.CreateDirectory(markerDir);
        var markerPath = Path.Combine(markerDir, "1");
        if (!File.Exists(markerPath))
        {
            File.WriteAllText(markerPath, string.Empty);
        }

        // NuGet derives the per-user home from DOTNET_CLI_HOME, falling back
        // to HOME; pin both to the fixture so nothing escapes to the real user
        // profile. The MSBuild/Roslyn server flags keep node-reuse processes
        // from outliving the fixture build and starving later timing tests.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["DOTNET_CLI_HOME"] = home,
            ["XDG_RUNTIME_DIR"] = home,
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
            // Throwaway solution + project wired to the repository's REAL
            // Directory.Build.*/Directory.Solution.* files, so this exercises
            // the exact solution-level InitialTargets hook, the
            // _CodeyBoxInSolution per-project suppression, and the
            // _CodeyBoxNuGetHealSolutionLevel destructive-repair marker the
            // gates rely on. The project references no packages, so restore is
            // offline once the home is healed.
            await CreateProjectAsync(projectDir);
            await CreateBuildWiringAsync(root, repoRoot!);

            // Cleared package sources keep restore fully offline and deterministic:
            // the bare project needs no packages, so an empty source list suffices
            // and guarantees the test never touches the network.
            var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");

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

            var env = CreateBuildEnv(dotnetDir, home, root);

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

            // The quarantine notice must reach the operator: the solution-level
            // hook is the one place destructive repair runs, and its stderr is
            // deliberately not suppressed.
            Assert.Contains("quarantined unusable", result.Stdout + result.Stderr, StringComparison.Ordinal);

            // The broken tree is quarantined aside (preserved), not deleted, and a
            // fresh writable per-user home replaced it — exactly once, because
            // per-project hooks are suppressed during a solution build.
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

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-groupw-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        var nugetHome = Path.Combine(home, ".nuget");
        var packageDir = Path.Combine(nugetHome, "packages", "pkg");
        var nugetDir = Path.Combine(nugetHome, "NuGet");

        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(nugetDir);
        var marker = Path.Combine(packageDir, "marker.txt");
        await File.WriteAllTextAsync(marker, "cached");
        var config = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration/>");

        // Linux umask 002: group-writable tree owned by current UID
        await SetUmask002PermissionsAsync(nugetHome, packageDir, nugetDir, marker, config);

        await CreateProjectAsync(projectDir);
        await CreateBuildWiringAsync(root, repoRoot!);
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
            Assert.Equal(
                Path.Combine(nugetHome, "packages"),
                await ReadRestorePackagesPathAsync(projectDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MsBuild_PerProjectHook_NeverQuarantines_UnusableTree()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-nondestruct-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(home);
        // A present-but-unreadable inherited user config makes the home
        // unusable — but the .nuget tree itself stays writable, so the
        // in-process NuGetHomeGuard redirect (not a quarantine) is what lets
        // the build proceed.
        var nugetDir = Path.Combine(nugetHome, "NuGet");
        Directory.CreateDirectory(nugetDir);
        var poisonedConfig = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(poisonedConfig, "<configuration/>");
        await ChmodAsync("000", poisonedConfig);

        await CreateProjectAsync(projectDir);
        await CreateBuildWiringAsync(root, repoRoot!);
        var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");

        try
        {
            var env = CreateBuildEnv(dotnetDir, home, root, nugetHome);
            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env);

            // The build succeeds through the NuGetHomeGuard DOTNET_CLI_HOME
            // redirect — and the unusable tree is left EXACTLY as found: a
            // per-project InitialTarget must never run the destructive
            // quarantine (that is reserved for the once-per-solution hook).
            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(File.Exists(poisonedConfig), "unusable .nuget must not be moved aside");
        }
        finally
        {
            await ChmodAsync("644", poisonedConfig);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MsBuild_ConcurrentPerProjectBuilds_NeverQuarantine_AndNeverRemoveHealthyCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-concurrent-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var nugetHome = Path.Combine(home, ".nuget");

        var proj1 = Path.Combine(root, "proj1");
        var proj2 = Path.Combine(root, "proj2");
        await CreateProjectAsync(proj1);
        await CreateProjectAsync(proj2);
        await CreateBuildWiringAsync(root, repoRoot!);
        var offline1 = Path.Combine(proj1, "offline.nuget.config");
        var offline2 = Path.Combine(proj2, "offline.nuget.config");

        try
        {
            // Case 1: Unusable tree (present-but-unreadable inherited user
            // config). Two concurrent PER-PROJECT builds must never quarantine —
            // destructive repair is reserved for the once-per-solution hook —
            // so both heal through the NuGetHomeGuard redirect and the broken
            // tree stays exactly as found.
            var brokenNugetDir = Path.Combine(nugetHome, "NuGet");
            Directory.CreateDirectory(brokenNugetDir);
            var poisonedConfig = Path.Combine(brokenNugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(poisonedConfig, "<configuration/>");
            await ChmodAsync("000", poisonedConfig);
            var envBroken = CreateBuildEnv(dotnetDir, home, root, nugetHome);

            var t1Broken = RunDotnetBuildAsync(dotnetDir, Path.Combine(proj1, "proj.csproj"), offline1, envBroken);
            var t2Broken = RunDotnetBuildAsync(dotnetDir, Path.Combine(proj2, "proj.csproj"), offline2, envBroken);
            var resultsBroken = await Task.WhenAll(t1Broken, t2Broken);

            Assert.True(resultsBroken[0].Success, resultsBroken[0].Stdout + resultsBroken[0].Stderr);
            Assert.True(resultsBroken[1].Success, resultsBroken[1].Stdout + resultsBroken[1].Stderr);
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
            Assert.True(File.Exists(poisonedConfig), "unusable .nuget must not be moved aside");
            await ChmodAsync("644", poisonedConfig);

            // Case 2: Healthy tree with umask 002. Concurrent builds must not quarantine
            // and must leave cached packages intact.
            var homeHealthy = Path.Combine(root, "home-healthy");
            var nugetHealthy = Path.Combine(homeHealthy, ".nuget");
            var packageDir = Path.Combine(nugetHealthy, "packages", "pkg");
            var nugetDir = Path.Combine(nugetHealthy, "NuGet");
            Directory.CreateDirectory(packageDir);
            Directory.CreateDirectory(nugetDir);
            var marker = Path.Combine(packageDir, "marker.txt");
            await File.WriteAllTextAsync(marker, "concurrent-cache");
            var config = Path.Combine(nugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration/>");

            await SetUmask002PermissionsAsync(nugetHealthy, packageDir, nugetDir, marker, config);

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
            var expectedPackages = Path.Combine(nugetHealthy, "packages");
            Assert.Equal(expectedPackages, await ReadRestorePackagesPathAsync(proj1));
            Assert.Equal(expectedPackages, await ReadRestorePackagesPathAsync(proj2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The audit gap this pins: a foreign-owned or co-tenant-writable
    // .nuget/packages tree with a READABLE NuGet.Config used to survive a
    // non-solution build untouched — the old guard only checked config
    // readability while Directory.Build.props kept RestorePackagesPath pinned
    // at the rejected cache, so extracted package build/.props/.targets kept
    // executing inside the build. The in-process NuGetHomeGuard task now
    // applies the heal script's own whole-tree admission verdict and must
    // repoint the auto-pinned RestorePackagesPath at verified storage.
    [Fact]
    public async Task MsBuild_NonSolutionBuild_RepointsRejectedPackageCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-repoint-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(projectDir);
        try
        {
            await CreateProjectAsync(projectDir);
            await CreateBuildWiringAsync(root, repoRoot!);
            var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");

            // A READABLE config inside an other-writable .nuget: the old
            // readability-only guard saw nothing wrong, while the heal's
            // private-storage admission rejects the tree outright.
            var nugetDir = Path.Combine(nugetHome, "NuGet");
            Directory.CreateDirectory(nugetDir);
            var config = Path.Combine(nugetDir, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration/>");
            var packageDir = Path.Combine(nugetHome, "packages", "pkg");
            Directory.CreateDirectory(packageDir);
            var marker = Path.Combine(packageDir, "marker.txt");
            await File.WriteAllTextAsync(marker, "rejected-cache");
            await ChmodAsync("777", nugetHome); // o+w: co-tenant-writable

            var env = CreateBuildEnv(dotnetDir, home, root, nugetHome);
            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env);

            Assert.True(result.Success, result.Stdout + result.Stderr);
            // Per-project builds never quarantine — the rejected tree stays.
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));

            var pinned = await ReadRestorePackagesPathAsync(projectDir);
            var rejected = Path.Combine(nugetHome, "packages");
            Assert.False(
                string.Equals(pinned, rejected, StringComparison.Ordinal),
                $"RestorePackagesPath must be repointed away from the rejected cache, got '{pinned}'");
            Assert.True(Path.IsPathRooted(pinned), $"RestorePackagesPath must be absolute, got '{pinned}'");
            Assert.True(
                pinned.EndsWith("/.nuget/packages", StringComparison.Ordinal),
                $"RestorePackagesPath must be the verified NuGet packages path, got '{pinned}'");
            Assert.False(
                pinned.StartsWith(home + "/", StringComparison.Ordinal),
                $"RestorePackagesPath must not resolve inside the rejected home, got '{pinned}'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // CODEYBOX_NUGET_HOME_UNDER_TEST is a test-only hook for the dot-sourced
    // heal scripts. The in-process guard must strip it from the report child
    // so the verdict describes the home MSBuild actually consumes; at the
    // same time a usable verdict must repoint the auto-pinned
    // RestorePackagesPath at the canonical home, not the retargetable symlink
    // spelling Directory.Build.props evaluated.
    [Fact]
    public async Task MsBuild_NonSolutionBuild_IgnoresTestHomeOverride_AndCanonicalizesUsablePackagesPath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-under-test-{Guid.NewGuid():N}");
        var realHome = Path.Combine(root, "real-home");
        var homeLink = Path.Combine(root, "home-link");
        var brokenHome = Path.Combine(root, "broken-home");
        var outside = Path.Combine(root, "outside");
        var projectDir = Path.Combine(root, "proj");
        var realNuget = Path.Combine(realHome, ".nuget");
        var realPackages = Path.Combine(realNuget, "packages");
        Directory.CreateDirectory(realPackages);
        Directory.CreateDirectory(Path.Combine(realNuget, "NuGet"));
        await File.WriteAllTextAsync(
            Path.Combine(realNuget, "NuGet", "NuGet.Config"), "<configuration/>");
        Directory.CreateDirectory(brokenHome);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(projectDir);
        File.CreateSymbolicLink(homeLink, realHome);
        File.CreateSymbolicLink(Path.Combine(brokenHome, ".nuget"), outside);
        try
        {
            await CreateProjectAsync(projectDir);
            await CreateBuildWiringAsync(root, repoRoot!);
            var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");
            var env = CreateBuildEnv(
                dotnetDir, homeLink, root, Path.Combine(brokenHome, ".nuget"));
            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env);

            Assert.True(result.Success, result.Stdout + result.Stderr);
            Assert.Equal(realPackages, await ReadRestorePackagesPathAsync(projectDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The POSIX guard must fail closed when it cannot obtain the heal
    // script's verdict at all: the old readability-only fallback left the
    // auto-pinned RestorePackagesPath on storage that was never admitted.
    [Fact]
    public async Task MsBuild_NonSolutionBuild_FailsClosed_WhenHealReportUnavailable()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-no-report-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(projectDir);
        try
        {
            await CreateProjectAsync(projectDir);
            await CreateBuildWiringAsync(root, repoRoot!);
            var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");
            var env = CreateBuildEnv(dotnetDir, home, root);

            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env,
                "-p:_CodeyBoxNuGetHealScript=/nonexistent/nuget-home-heal.sh");

            Assert.False(result.Success, result.Stdout + result.Stderr);
            Assert.Contains(
                "heal report unavailable",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A "failed" verdict means the heal could not prove ANY verified package
    // cache — including the repository-local last resort. The guard must
    // refuse the build rather than repoint RestorePackagesPath back at the
    // rejected .nuget/packages tree.
    [Fact]
    public async Task MsBuild_NonSolutionBuild_FailsClosed_WhenHealCannotVerifyPackageCache()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repoRoot = FindAncestorContaining(
            AppContext.BaseDirectory, "Directory.NuGetHomeHeal.targets");
        Assert.NotNull(repoRoot);

        var dotnetDir = ResolveDotnetDirectory();
        if (dotnetDir is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"codeybox-msbuild-heal-failed-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var projectDir = Path.Combine(root, "proj");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Combine(home, ".nuget"), outside);
        try
        {
            await CreateProjectAsync(projectDir);
            await CreateBuildWiringAsync(root, repoRoot!);
            var offlineConfig = Path.Combine(projectDir, "offline.nuget.config");
            var env = CreateBuildEnv(dotnetDir, home, root);
            // TMPDIR must stay writable for MSBuild itself, but be storage the
            // heal's anchor admission refuses (other-writable without sticky):
            // the shared per-user dir and the mktemp fallback both resolve
            // under it and fail verification, while the repository-local last
            // resort below is outside writable storage too.
            var untrustedTmp = Path.Combine(root, "untrusted-tmp");
            Directory.CreateDirectory(untrustedTmp);
            await ChmodAsync("777", untrustedTmp);
            env["TMPDIR"] = untrustedTmp;

            var result = await RunDotnetBuildAsync(
                dotnetDir,
                Path.Combine(projectDir, "proj.csproj"),
                offlineConfig,
                env,
                "-p:_CodeyBoxRepositoryDotNetHome=/proc/codeybox-nuget-home");

            Assert.False(result.Success, result.Stdout + result.Stderr);
            Assert.Contains(
                "could not materialize verified storage",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            Assert.False(
                File.Exists(Path.Combine(projectDir, "restore-packages-path.txt")),
                "a failed heal verdict must stop the build before package consumption");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

