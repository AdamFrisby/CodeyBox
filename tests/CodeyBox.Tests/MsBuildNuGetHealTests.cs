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
}
