using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class NuGetFallbackCensusTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-nuget-census-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void BuildScript_RejectsBadInputs()
    {
        Assert.Throws<ArgumentException>(() => NuGetFallbackCensus.BuildScript("relative"));
        Assert.Throws<ArgumentException>(() => NuGetFallbackCensus.BuildScript("/work'evil"));
        Assert.Throws<ArgumentException>(() => NuGetFallbackCensus.BuildScript("/work\nx"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NuGetFallbackCensus.BuildScript("/work", maxAssetsFiles: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NuGetFallbackCensus.BuildScript("/work", maxAssetsFiles: NuGetFallbackCensus.MaximumAssetsFiles + 1));
    }

    [Fact]
    public async Task CensusScript_ListsFallbackPackagesAndAssetsFiles()
    {
        var fallback = Path.Combine(_root, "fb");
        Directory.CreateDirectory(Path.Combine(fallback, "Newtonsoft.Json", "13.0.3"));
        Directory.CreateDirectory(Path.Combine(fallback, "Xunit", "2.9.0"));
        File.WriteAllText(Path.Combine(fallback, "Newtonsoft.Json", "13.0.3", "n.nupkg"), "x");
        var work = Path.Combine(_root, "work");
        var project = Path.Combine(work, "proj");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(project, "project.assets.json"),
            """{"libraries":{"Newtonsoft.Json/13.0.3":{"type":"package"},"Zed/9.9.9":{"type":"package"}}}""");
        var script = NuGetFallbackCensus.BuildScript(work);
        var stdout = await RunShellAsync(script, new Dictionary<string, string>
        {
            [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] = $"{fallback};/missing/dir",
        });
        var parsed = NuGetFallbackCensusParser.Parse(stdout);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.FallbackFolders.Count);
        Assert.Equal(fallback, parsed.FallbackFolders[0].Directory);
        Assert.Equal(
            new[] { "newtonsoft.json/13.0.3", "xunit/2.9.0" },
            parsed.FallbackFolders[0].Packages.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Empty(parsed.FallbackFolders[1].Packages);
        Assert.Equal(new[] { Path.Combine(project, "project.assets.json") }, parsed.AssetsPaths);
    }

    [Fact]
    public async Task CensusScript_SurvivesUnsetFallbackAndMissingWorkTree()
    {
        var stdout = await RunShellAsync(
            NuGetFallbackCensus.BuildScript(Path.Combine(_root, "no-such-dir")),
            new Dictionary<string, string>());
        var parsed = NuGetFallbackCensusParser.Parse(stdout);
        Assert.NotNull(parsed);
        Assert.Empty(parsed.FallbackFolders);
        Assert.Empty(parsed.AssetsPaths);
    }

    [Fact]
    public void Parser_RejectsBadFramingAndIgnoresHostileLines()
    {
        Assert.Null(NuGetFallbackCensusParser.Parse(string.Empty));
        Assert.Null(NuGetFallbackCensusParser.Parse("garbage\nEND-CENSUS\n"));
        Assert.Null(NuGetFallbackCensusParser.Parse("CODEYBOX-NUGET-CENSUS-V1\nFALLBACK-VALUE:x\n"));
        var parsed = NuGetFallbackCensusParser.Parse(
            "CODEYBOX-NUGET-CENSUS-V1\n" +
            "FALLBACK-VALUE:/fb\n" +
            "DIR:/fb\n" +
            "good.id/1.2.3\n" +
            "../escape/9.9.9\n" +
            "no-separator-here\n" +
            "/absolute/1.0.0\n" +
            "END-DIR\n" +
            "ASSETS\n" +
            "/work/a/project.assets.json\n" +
            "relative/evil.json\n" +
            "END-ASSETS\n" +
            "END-CENSUS\n");
        Assert.NotNull(parsed);
        Assert.Equal(["good.id/1.2.3"], parsed.FallbackFolders.Single().Packages);
        Assert.Equal(["/work/a/project.assets.json"], parsed.AssetsPaths);
    }

    [Fact]
    public async Task Reporter_ClassifiesCensusAndAssets()
    {
        var sandbox = new CannedSandbox(
            censusStdout:
                "CODEYBOX-NUGET-CENSUS-V1\n" +
                "FALLBACK-VALUE:/fb\n" +
                "DIR:/fb\n" +
                "a/1.0.0\n" +
                "b/2.0.0\n" +
                "END-DIR\n" +
                "ASSETS\n" +
                "/work/p/project.assets.json\n" +
                "END-ASSETS\n" +
                "END-CENSUS\n",
            assets: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/work/p/project.assets.json"] =
                    """{"libraries":{"A/1.0.0":{"type":"package"},"C/3.0.0":{"type":"package"},"P/1.0.0":{"type":"project"}}}""",
            });
        var report = await NuGetFallbackCoverageReporter.TryReportAsync(sandbox, "/work");
        Assert.Equal(
            "NuGet fallback coverage: 1 shared, 1 fetched of 2 referenced (2 available)",
            report);
    }

    [Fact]
    public async Task Reporter_ReturnsNullWhenCoverageCannotBeDetermined()
    {
        Assert.Null(await NuGetFallbackCoverageReporter.TryReportAsync(
            new CannedSandbox("garbage", new Dictionary<string, string>()), "/work"));
        Assert.Null(await NuGetFallbackCoverageReporter.TryReportAsync(
            new CannedSandbox(
                "CODEYBOX-NUGET-CENSUS-V1\nFALLBACK-VALUE:\nASSETS\nEND-ASSETS\nEND-CENSUS\n",
                new Dictionary<string, string>()),
            "/work"));
        Assert.Null(await NuGetFallbackCoverageReporter.TryReportAsync(
            new FailingSandbox(), "/work"));
        var tooMany = string.Join(
            "\n",
            new[] { "CODEYBOX-NUGET-CENSUS-V1", "FALLBACK-VALUE:", "ASSETS" }
                .Concat(Enumerable.Range(0, NuGetFallbackCoverageReporter.MaximumAssetsFilesRead + 1)
                    .Select(i => $"/work/{i}/project.assets.json"))
                .Append("END-ASSETS")
                .Append("END-CENSUS"));
        Assert.Null(await NuGetFallbackCoverageReporter.TryReportAsync(
            new CannedSandbox(tooMany, new Dictionary<string, string>()), "/work"));
    }

    private static async Task<string> RunShellAsync(string script, IDictionary<string, string> environment)
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeybox-census-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, script);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sh",
                    ArgumentList = { path },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var (key, value) in environment)
                process.StartInfo.Environment[key] = value;
            // The host running this test may export NUGET_FALLBACK_PACKAGES
            // (the audit image does). ProcessStartInfo inherits the parent
            // environment, so an "unset" case must explicitly remove it to
            // stay hermetic.
            if (!environment.ContainsKey(NuGetFallbackCache.FallbackPackagesEnvironmentVariable))
                process.StartInfo.Environment.Remove(NuGetFallbackCache.FallbackPackagesEnvironmentVariable);
            Assert.True(process.Start(), "failed to start sh");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"census script failed: {stderr}");
            return stdout;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CannedSandbox(string censusStdout, Dictionary<string, string> assets) : ISandbox
    {
        public string Id => "canned";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 2 && exec.Argv[0] == "sh")
                return Task.FromResult(new SandboxExecResult(0, censusStdout, string.Empty));
            if (exec.Argv.Count == 3 && exec.Argv[0] == "cat")
            {
                return assets.TryGetValue(exec.Argv[2], out var content)
                    ? Task.FromResult(new SandboxExecResult(0, content, string.Empty))
                    : Task.FromResult(new SandboxExecResult(1, string.Empty, "missing"));
            }
            return Task.FromResult(new SandboxExecResult(1, string.Empty, "unexpected"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingSandbox : ISandbox
    {
        public string Id => "failing";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(1, string.Empty, "boom"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
