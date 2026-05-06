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
        Assert.DoesNotContain(sandbox.Commands, c => c == "govulncheck ./...");
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

    private sealed class DispatchSandbox : ISandbox
    {
        private readonly bool _markerPresent;
        private readonly bool _missingTool;

        public DispatchSandbox(bool markerPresent, bool missingTool = false)
        {
            _markerPresent = markerPresent;
            _missingTool = missingTool;
        }

        public List<string> Commands { get; } = [];
        public string Id => "dispatch";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var command = string.Join(' ', exec.Argv);
            Commands.Add(command);

            if (exec.Argv.Count >= 2 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                var script = exec.Argv[2];
                if (script.Contains("test -f", StringComparison.Ordinal) ||
                    script.Contains("find .", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(_markerPresent ? 0 : 1, "", ""));
            }

            if (_missingTool)
                return Task.FromResult(new SandboxExecResult(127, "", "missing"));

            return Task.FromResult(new SandboxExecResult(0, "{}", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
