using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests DepsCveScanDeepAuditor in the "clean" path — no vulnerable packages.
/// </summary>
public sealed class DeepAuditConvergenceTests
{
    private static readonly DeepAuditContext TestCtx = new(
        ReleaseId: ReleaseId.New(),
        ProjectId: new ProjectId("test"),
        BranchName: "release/v1.0",
        Iteration: 1);

    [Fact]
    public async Task DepsCveScan_CleanOutput_PassesWithNoFindings()
    {
        var sandbox = new ScriptedSandbox(
            new SandboxExecResult(0,
                "The following sources were used:\n   https://api.nuget.org/v3/index.json\n\nProject 'MyApp' has no vulnerable packages.",
                ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task DepsCveScan_EmptyOutput_Passes()
    {
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, "", ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task DepsCveScan_DotnetNotInstalled_ReturnsPassedWithInfoFinding()
    {
        var sandbox = new ScriptedSandbox(
            new SandboxExecResult(127, "", "dotnet: command not found"));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.True(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, result.Findings[0].Severity);
        Assert.Contains("SDK not installed", result.Findings[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DepsCveScan_LowSeverity_PassesWithInfoFinding()
    {
        var output =
            "Project 'MyApp' has the following vulnerable packages:\n" +
            "   [net10.0]:\n" +
            "   > SomePackage   1.0.0   1.0.0   Low   https://example.com/advisory\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.True(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, result.Findings[0].Severity);
    }

    [Fact]
    public async Task DepsCveScan_ModerateSeverity_PassesWithWarningFinding()
    {
        var output =
            "Project 'MyApp' has the following vulnerable packages:\n" +
            "   [net10.0]:\n" +
            "   > SomePackage   2.0.0   2.0.0   Moderate   https://example.com/advisory\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.True(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, result.Findings[0].Severity);
    }

    [Fact]
    public void DepsCveScan_Name_IsStable()
    {
        Assert.Equal("deps-cve-scan", new DepsCveScanDeepAuditor().Name);
    }
}
