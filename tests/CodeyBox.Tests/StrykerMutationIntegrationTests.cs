using System.Diagnostics;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end proof that <see cref="StrykerMutationRunner"/> drives the real
/// pinned Stryker.NET engine inside the real supported audit sandbox path
/// (<see cref="ProcessSandboxProvider"/>): a tiny .NET fixture with
/// deliberately weak tests yields a nonzero mutant set with the expected
/// survivor at the expected file/line, and strengthening the tests kills it.
/// A fake executable or a static JSON excerpt alone would not prove engine
/// integration — these tests invoke the actual tool and assert on the scores
/// it produces. When the pinned tool (or the .NET SDK) is unavailable the
/// tests skip with an explicit validation-gap reason instead of passing.
/// </summary>
[Trait("requires_stryker", "true")]
public sealed class StrykerMutationIntegrationTests
{
    private const string PinnedVersion = StrykerMutationRunnerOptions.PinnedVersion;

    private static readonly bool ToolAvailable = ProbeTool("dotnet-stryker", "--help");
    private static readonly bool SdkAvailable = ProbeTool("dotnet", "--version");

    private static bool ProbeTool(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var part in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(part);
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 60_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task RealStryker_WeakTests_YieldExpectedSurvivor()
    {
        Skip.If(!SdkAvailable, "Validation gap: .NET SDK ('dotnet') not on PATH; cannot run the Stryker fixture.");
        Skip.If(!ToolAvailable, $"Validation gap: 'dotnet-stryker' not on PATH; install {PinnedVersion} to prove engine integration.");

        var fixtureDir = await SeedFixtureAsync(strong: false);
        try
        {
            var report = await RunStrykerAsync(fixtureDir);

            Assert.Equal(MutationRunStatus.Completed, report.Status);
            Assert.Equal(MutationRunScope.ChangedFilesOnly, report.Scope);
            // The fixture generates mutants and the weak suite misses one.
            Assert.NotNull(report.ChangedCodeMutationScorePercent);
            Assert.InRange(report.ChangedCodeMutationScorePercent!.Value, 0.01, 99.99);
            var survivor = Assert.Single(report.SurvivingMutantsInChangedCode);
            Assert.Equal("src/SampleCalc/Calc.cs", survivor.FilePath);
            Assert.Equal(5, survivor.Line);
            Assert.Contains("Equality", survivor.Mutator, StringComparison.OrdinalIgnoreCase);
            // A changed-files-only run never establishes an overall score.
            Assert.Null(report.OverallMutationScorePercent);
            // Provenance identifies the exact evidence.
            Assert.Equal(PinnedVersion, report.ToolVersion);
            Assert.False(string.IsNullOrWhiteSpace(report.SourceCommitSha));
            Assert.Contains("SampleCalc.csproj", report.ProjectSelection);
            Assert.NotNull(report.ConfigDigest);
            Assert.Equal(64, report.ConfigDigest!.Length);
            Assert.Contains("overall: unavailable", report.RawOutput);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [SkippableFact]
    public async Task RealStryker_StrengthenedTests_KillAllMutants()
    {
        Skip.If(!SdkAvailable, "Validation gap: .NET SDK ('dotnet') not on PATH; cannot run the Stryker fixture.");
        Skip.If(!ToolAvailable, $"Validation gap: 'dotnet-stryker' not on PATH; install {PinnedVersion} to prove engine integration.");

        var fixtureDir = await SeedFixtureAsync(strong: true);
        try
        {
            var report = await RunStrykerAsync(fixtureDir);

            Assert.Equal(MutationRunStatus.Completed, report.Status);
            Assert.NotNull(report.ChangedCodeMutationScorePercent);
            Assert.Equal(100.0, report.ChangedCodeMutationScorePercent!.Value, precision: 6);
            Assert.Empty(report.SurvivingMutantsInChangedCode);
            Assert.Null(report.OverallMutationScorePercent);
            Assert.Equal(PinnedVersion, report.ToolVersion);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static async Task<MutationRunReport> RunStrykerAsync(string fixtureDir)
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(
            new SandboxSpec
            {
                ImageReference = "ignored",
                WorkingDirectory = "/work",
                // Writable: Stryker builds the fixture in-tree (bin/obj) and
                // the audit working tree is mutable by design.
                Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir, ReadOnly = false }],
            },
            CancellationToken.None);

        var runner = new StrykerMutationRunner(
            () => new StrykerMutationRunnerOptions
            {
                Enabled = true,
                ToolCommand = ["dotnet-stryker"],
                Concurrency = 2,
            },
            NullLogger<StrykerMutationRunner>.Instance,
            TimeProvider.System);
        return await runner.RunAsync(
            sandbox, "/work", ["src/SampleCalc/Calc.cs"], TimeSpan.FromMinutes(10), CancellationToken.None);
    }

    private static async Task<string> SeedFixtureAsync(bool strong)
    {
        var root = Path.Combine(Path.GetTempPath(), "codeybox-stryker-fx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "SampleCalc"));
        Directory.CreateDirectory(Path.Combine(root, "test", "SampleCalc.Tests"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "SampleCalc", "SampleCalc.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "SampleCalc", "Calc.cs"),
            """
            namespace SampleCalc;

            public static class Calc
            {
                public static bool IsPositive(int x) => x > 0;

                public static int Add(int a, int b) => a + b;
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "test", "SampleCalc.Tests", "SampleCalc.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\..\src\SampleCalc\SampleCalc.csproj" />
              </ItemGroup>
            </Project>
            """);
        var weakTests = """
            using SampleCalc;
            using Xunit;

            namespace SampleCalc.Tests;

            public sealed class CalcTests
            {
                [Fact]
                public void IsPositive_Positive_ReturnsTrue() => Assert.True(Calc.IsPositive(5));

                [Fact]
                public void Add_Works() => Assert.Equal(3, Calc.Add(1, 2));
            }
            """;
        var strongTests = """
            using SampleCalc;
            using Xunit;

            namespace SampleCalc.Tests;

            public sealed class CalcTests
            {
                [Fact]
                public void IsPositive_Positive_ReturnsTrue() => Assert.True(Calc.IsPositive(5));

                [Fact]
                public void IsPositive_Zero_ReturnsFalse() => Assert.False(Calc.IsPositive(0));

                [Fact]
                public void IsPositive_Negative_ReturnsFalse() => Assert.False(Calc.IsPositive(-3));

                [Fact]
                public void Add_Works() => Assert.Equal(3, Calc.Add(1, 2));
            }
            """;
        await File.WriteAllTextAsync(
            Path.Combine(root, "test", "SampleCalc.Tests", "CalcTests.cs"),
            strong ? strongTests : weakTests);

        // Best-effort source SHA for report provenance; a missing git binary
        // leaves the SHA "unknown" without failing the fixture.
        TryGitInit(root);
        return root;
    }

    private static void TryGitInit(string root)
    {
        try
        {
            foreach (var (fileName, arguments) in new[]
                     {
                         ("git", "init -q"),
                         ("git", "add -A"),
                         ("git", "-c user.email=stryker-test@codeybox.invalid -c user.name=stryker-test commit -qm init"),
                     })
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var part in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(part);
                using var process = Process.Start(psi);
                if (process is null)
                    return;
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(milliseconds: 60_000) || process.ExitCode != 0)
                    return;
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temp fixture.
        }
    }
}
