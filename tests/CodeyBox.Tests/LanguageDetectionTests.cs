using CodeyBox.Audit.Presets;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class LanguageDetectionTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? stdoutChunkCallback = null, bool ________ = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public async Task BuildTestGateLanguageWithoutMarker_BlocksAndDoesNotRunTool()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass");
        var sandbox = new MarkerlessSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("python preset enabled", finding.Title);
        Assert.DoesNotContain(sandbox.Commands, c => c.Contains("pytest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildTestGateLanguageWithMissingTool_Blocks()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass");
        var sandbox = new MarkerWithMissingToolSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("tool not installed", finding.Title);
        Assert.Contains(sandbox.Commands, c =>
            c.Contains("command -v", StringComparison.Ordinal) &&
            c.Contains("pytest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonGateLanguageWithoutMarker_ReportsInfoAndPasses()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new MarkerlessSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("csharp preset enabled", finding.Title);
        Assert.DoesNotContain(sandbox.Commands, c => c.Contains("dotnet format", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PythonTypecheckMissingMypyAndPyright_ReportsInfoAndPasses()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:typecheck");
        var sandbox = new PythonTypecheckMissingToolsSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("tool not installed", finding.Title);
        Assert.Contains("mypy or pyright", finding.Title);
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

    [Fact]
    public async Task HiddenMarkerDirectory_PreservesLeadingDotInWorkingDirectory()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("node", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "node:lint");
        var sandbox = new HiddenMarkerDirectorySandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.True(result.Passed);
        Assert.Equal("/repo/.devcontainer", sandbox.ToolWorkingDirectory);
    }

    [Fact]
    public async Task NodeTestPass_Exit127FromRepositoryScript_RemainsBlocking()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("node", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "node:test-pass");
        var sandbox = new NodeScriptExit127Sandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("command exited 127", finding.Title);
    }

    [Fact]
    public async Task NodeTestPass_MissingNpmTool_Blocks()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("node", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "node:test-pass");
        var sandbox = new MissingNpmSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("tool not installed", finding.Title);
    }

    [Fact]
    public async Task CSharpDiscoveryIncludesStandaloneProjectWhenSolutionExistsElsewhere()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cb-csharp-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "solution"));
            Directory.CreateDirectory(Path.Combine(root, "standalone"));
            await File.WriteAllTextAsync(Path.Combine(root, "solution", "App.sln"), "");
            await File.WriteAllTextAsync(Path.Combine(root, "standalone", "Tool.csproj"), "<Project />");

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sh",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(LanguageProjectDiscovery.CSharpDiscoveryScript);

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", stderr);
            var directories = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("./solution", directories);
            Assert.Contains("./standalone", directories);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ManyMarkerDirectories_AreCapped()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass");
        var sandbox = new ManyMarkersSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings, f =>
            f.Title.Contains("project directory limit reached", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal(LanguageProjectDiscovery.MaxProjectDirectoriesToRun, sandbox.Commands.Count(c => c == "pytest"));
    }

    [Fact]
    public async Task CSharpManyMarkerDirectories_AreCappedWhenNoRootMarker()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "csharp:build-WaE");
        var sandbox = new ManyMarkersSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings, f =>
            f.Title.Contains("project directory limit reached", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal(
            LanguageProjectDiscovery.MaxProjectDirectoriesToRun,
            sandbox.Commands.Count(c => c == "dotnet build --no-incremental /warnaserror"));
    }

    [Fact]
    public async Task CSharpRootMarker_DoesNotSuppressNestedProjectDirectories()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "csharp:build-WaE");
        var sandbox = new ManyMarkersSandbox(includeRootMarker: true);

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings, f =>
            f.Title.Contains("project directory limit reached", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal(
            LanguageProjectDiscovery.MaxProjectDirectoriesToRun,
            sandbox.Commands.Count(c => c == "dotnet build --no-incremental /warnaserror"));
        Assert.Contains("/repo", sandbox.WorkingDirectories);
        Assert.Contains("/repo/project-0", sandbox.WorkingDirectories);
        Assert.DoesNotContain("/repo/project-31", sandbox.WorkingDirectories);
    }

    [Fact]
    public async Task CSharpTestPass_MixedProjectEvidenceReportsUnverified()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "csharp:test-pass");
        var sandbox = new MixedCSharpGateEvidenceSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.True(result.Passed);
        Assert.False(result.BuildTestGateEvidenceVerified);
        Assert.Contains("/repo/project-ok", sandbox.WorkingDirectories);
        Assert.Contains("/repo/project-unverified", sandbox.WorkingDirectories);
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

    private sealed class MarkerWithMissingToolSandbox : ISandbox
    {
        public List<string> Commands { get; } = [];
        public string Id => "marker-with-missing-tool";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var command = string.Join(' ', exec.Argv);
            Commands.Add(command);
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                if (exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(1, "", ""));

                return Task.FromResult(new SandboxExecResult(0, "./python\n", ""));
            }

            return Task.FromResult(new SandboxExecResult(127, "", "pytest: not found"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NodeScriptExit127Sandbox : ISandbox
    {
        public string Id => "node-script-exit-127";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                if (exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, "/usr/bin/npm\n", ""));

                return Task.FromResult(new SandboxExecResult(0, "./node\n", ""));
            }

            return Task.FromResult(new SandboxExecResult(
                127,
                "",
                "sh: 1: definitely-not-a-real-test-command: not found"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HiddenMarkerDirectorySandbox : ISandbox
    {
        public string Id => "hidden-marker-directory";
        public string? ToolWorkingDirectory { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                if (exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, "/usr/bin/eslint\n", ""));

                return Task.FromResult(new SandboxExecResult(0, "./.devcontainer\n", ""));
            }

            ToolWorkingDirectory = exec.WorkingDirectory;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PythonTypecheckMissingToolsSandbox : ISandbox
    {
        public string Id => "python-typecheck-missing-tools";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                var script = exec.Argv[2];
                if (script.Contains("find .", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, "./python\n", ""));

                if (script.Contains("command -v mypy", StringComparison.Ordinal) &&
                    script.Contains("command -v pyright", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(
                        127,
                        "",
                        "mypy or pyright: not found in sandbox"));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MissingNpmSandbox : ISandbox
    {
        public string Id => "missing-npm";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                if (exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(1, "", ""));

                return Task.FromResult(new SandboxExecResult(0, "./node\n", ""));
            }

            return Task.FromResult(new SandboxExecResult(127, "", "npm: not found"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManyMarkersSandbox : ISandbox
    {
        private readonly bool _includeRootMarker;

        public ManyMarkersSandbox(bool includeRootMarker = false)
        {
            _includeRootMarker = includeRootMarker;
        }

        public List<string> Commands { get; } = [];
        public List<string> WorkingDirectories { get; } = [];
        public string Id => "many-markers";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var command = string.Join(' ', exec.Argv);
            Commands.Add(command);
            if (exec.WorkingDirectory is not null)
                WorkingDirectories.Add(exec.WorkingDirectory);

            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                var directories = Enumerable.Range(0, 40).Select(i => $"./project-{i}");
                if (_includeRootMarker)
                    directories = directories.Prepend(".");

                var output = string.Join('\n', directories) + "\n";
                return Task.FromResult(new SandboxExecResult(0, output, ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MixedCSharpGateEvidenceSandbox : ISandbox
    {
        public List<string> WorkingDirectories { get; } = [];
        public string Id => "mixed-csharp-gate-evidence";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.WorkingDirectory is not null)
                WorkingDirectories.Add(exec.WorkingDirectory);

            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                if (exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, "/usr/bin/dotnet\n", ""));

                return Task.FromResult(new SandboxExecResult(0, "./project-ok\n./project-unverified\n", ""));
            }

            if (exec.Argv.Count >= 2 &&
                exec.Argv[0] == "dotnet" &&
                exec.Argv[1] == "test" &&
                exec.WorkingDirectory?.EndsWith("/project-unverified", StringComparison.Ordinal) == true)
            {
                const string output = """
                      Failed App.Tests.E2E.LoginTests.CanOpenLoginPage [< 1 ms]
                      Error Message:
                       Microsoft.Playwright.PlaywrightException : Browser executable was not found
                      Stack Trace:

                    Failed!  - Failed: 1, Passed: 100, Skipped: 0, Total: 101, Duration: 4 s
                    """;
                return Task.FromResult(new SandboxExecResult(1, output, ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
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
            {
                if (exec.Argv[2].Contains("command -v", StringComparison.Ordinal))
                    return new SandboxExecResult(0, "/usr/bin/" + exec.Argv[^1] + "\n", "");

                return await RunShellAsync(exec.Argv[2], workingDirectory, ct);
            }

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
