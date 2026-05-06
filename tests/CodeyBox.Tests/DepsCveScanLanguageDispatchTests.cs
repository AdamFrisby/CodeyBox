using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class DepsCveScanLanguageDispatchTests
{
    [Fact]
    public async Task RunsScannerForEachDeclaredLanguageWithMarker()
    {
        var sandbox = new DispatchSandbox(markerPresent: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["csharp", "python", "node"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Contains(sandbox.Commands, c => c == "dotnet list package --vulnerable --include-transitive");
        Assert.Contains(sandbox.Commands, c => c.Contains("pip-audit -f json", StringComparison.Ordinal));
        Assert.Contains(sandbox.Commands, c => c == "npm audit --json");
        Assert.Contains(sandbox.WorkingDirectories, d => d == "/repo/csharp");
        Assert.Contains(sandbox.WorkingDirectories, d => d == "/repo/python");
        Assert.Contains(sandbox.WorkingDirectories, d => d == "/repo/node");
    }

    [Fact]
    public async Task SkipsDeclaredLanguageWhenMarkerIsMissing()
    {
        var sandbox = new DispatchSandbox(markerPresent: false);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["go"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.DoesNotContain(sandbox.Commands, c => c.StartsWith("govulncheck", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsetLanguagesDefaultToCSharpForBackwardsCompatibility()
    {
        var sandbox = new DispatchSandbox(markerPresent: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        Assert.Contains(sandbox.Commands, c => c == "dotnet list package --vulnerable --include-transitive");
    }

    [Fact]
    public async Task MissingScannerToolReportsInfo()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, missingTool: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["rust"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("cargo-audit not installed", finding.Title);
    }

    [Fact]
    public async Task MissingCargoAuditSubcommandReportsInfo()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, missingCargoAuditSubcommand: true);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["rust"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("cargo-audit not installed", finding.Title);
    }

    [Fact]
    public async Task GoScannerUsesStructuredJsonOutput()
    {
        var sandbox = new DispatchSandbox(markerPresent: true, scannerStdout: """
            {"finding":{"osv":"GO-2024-0001","trace":[{"package":"example.com/app"}]}}
            """);
        var auditor = new DepsCveScanDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("test-project"),
            "release/v1",
            1,
            Languages: ["go"]);

        var result = await auditor.RunAsync(sandbox, "/repo", ctx);

        Assert.False(result.Passed);
        Assert.Contains(sandbox.Commands, c => c == "govulncheck -json ./...");
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("example.com/app", finding.Title);
    }

    private sealed class DispatchSandbox : ISandbox
    {
        private readonly bool _markerPresent;
        private readonly bool _missingTool;
        private readonly bool _missingCargoAuditSubcommand;
        private readonly string _scannerStdout;

        public DispatchSandbox(
            bool markerPresent,
            bool missingTool = false,
            bool missingCargoAuditSubcommand = false,
            string scannerStdout = "{}")
        {
            _markerPresent = markerPresent;
            _missingTool = missingTool;
            _missingCargoAuditSubcommand = missingCargoAuditSubcommand;
            _scannerStdout = scannerStdout;
        }

        public List<string> Commands { get; } = [];
        public List<string> WorkingDirectories { get; } = [];
        public string Id => "dispatch";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var command = string.Join(' ', exec.Argv);
            Commands.Add(command);
            if (exec.WorkingDirectory is not null)
                WorkingDirectories.Add(exec.WorkingDirectory);

            if (exec.Argv.Count >= 2 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                var script = exec.Argv[2];
                if (script.Contains("find .", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, _markerPresent ? DiscoveryOutput(script) : "", ""));
            }

            if (_missingTool)
                return Task.FromResult(new SandboxExecResult(127, "", "missing"));

            if (_missingCargoAuditSubcommand)
                return Task.FromResult(new SandboxExecResult(101, "", "error: no such command: `audit`"));

            return Task.FromResult(new SandboxExecResult(0, _scannerStdout, ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string DiscoveryOutput(string script)
        {
            if (script.Contains("*.csproj", StringComparison.Ordinal))
                return "./csharp\n";
            if (script.Contains("pyproject.toml", StringComparison.Ordinal))
                return "./python\n";
            if (script.Contains("package.json", StringComparison.Ordinal))
                return "./node\n";
            if (script.Contains("go.mod", StringComparison.Ordinal))
                return "./go\n";
            if (script.Contains("Cargo.toml", StringComparison.Ordinal))
                return "./rust\n";
            return ".\n";
        }
    }
}
