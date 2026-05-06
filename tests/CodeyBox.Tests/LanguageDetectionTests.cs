using CodeyBox.Audit.Presets;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class LanguageDetectionTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? stdoutChunkCallback = null)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public async Task EnabledLanguageWithoutMarker_ReportsInfoAndDoesNotRunTool()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass");
        var sandbox = new MarkerlessSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("python preset enabled", finding.Title);
        Assert.DoesNotContain(sandbox.Commands, c => c.Contains("pytest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoveryFailure_ReportsErrorAndDoesNotRunTool()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass");
        var sandbox = new DiscoveryFailureSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("discovery failed", finding.Title);
        Assert.DoesNotContain(sandbox.Commands, c => c.Contains("pytest", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("csharp", "csharp:format-check")]
    [InlineData("python", "python:format-check")]
    [InlineData("node", "node:lint")]
    [InlineData("go", "go:vet")]
    [InlineData("rust", "rust:lint")]
    public void SupportedLanguagePresetsResolveExpectedAuditors(string language, string auditorName)
    {
        var catalog = new PresetCatalog();
        var auditors = catalog.ResolveLanguage(language, new PresetContext(new FakeAgent()));
        Assert.Contains(auditors, a => a.Name == auditorName);
        Assert.All(auditors, a => Assert.Equal(AuditCapabilities.None, a.Required));
    }

    [Fact]
    public async Task SideBySideFixtureMarkers_RunAuditorsFromNestedProjectDirectories()
    {
        var catalog = new PresetCatalog();
        var sandbox = new FixtureDispatchSandbox();
        var fixture = MultiLanguageFixturePath();
        var context = FakeAuditContext();

        await catalog.ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "csharp:build-WaE")
            .RunAsync(sandbox, fixture, context);
        await catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass")
            .RunAsync(sandbox, fixture, context);
        await catalog.ResolveLanguage("node", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "node:test-pass")
            .RunAsync(sandbox, fixture, context);

        Assert.Contains(sandbox.Invocations, i =>
            i.Command == "dotnet build --no-incremental /warnaserror" &&
            i.WorkingDirectory.EndsWith($"{Path.DirectorySeparatorChar}csharp", StringComparison.Ordinal));
        Assert.Contains(sandbox.Invocations, i =>
            i.Command == "pytest" &&
            i.WorkingDirectory.EndsWith($"{Path.DirectorySeparatorChar}python", StringComparison.Ordinal));
        Assert.Contains(sandbox.Invocations, i =>
            i.Command == "npm test" &&
            i.WorkingDirectory.EndsWith($"{Path.DirectorySeparatorChar}node", StringComparison.Ordinal));
        Assert.DoesNotContain(sandbox.Invocations, i =>
            i.WorkingDirectory.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static AuditContext FakeAuditContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private static string MultiLanguageFixturePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "tests",
                "CodeyBox.Tests",
                "Fixtures",
                "multi-language-repo");
            if (Directory.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate multi-language fixture.");
    }

    private sealed class MarkerlessSandbox : ISandbox
    {
        public List<string> Commands { get; } = [];
        public string Id => "markerless";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Commands.Add(string.Join(' ', exec.Argv));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DiscoveryFailureSandbox : ISandbox
    {
        public List<string> Commands { get; } = [];
        public string Id => "discovery-failure";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Commands.Add(string.Join(' ', exec.Argv));
            return Task.FromResult(new SandboxExecResult(2, "", "find failed"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureDispatchSandbox : ISandbox
    {
        public List<(string Command, string WorkingDirectory)> Invocations { get; } = [];
        public string Id => "fixture-dispatch";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var command = string.Join(' ', exec.Argv);
            var workingDirectory = exec.WorkingDirectory ?? Environment.CurrentDirectory;
            Invocations.Add((command, workingDirectory));

            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
                return await RunShellAsync(exec.Argv[2], workingDirectory, ct);

            return new SandboxExecResult(0, "", "");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async Task<SandboxExecResult> RunShellAsync(
            string script,
            string workingDirectory,
            CancellationToken ct)
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sh",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(script);

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SandboxExecResult(process.ExitCode, stdout, stderr);
        }
    }
}
