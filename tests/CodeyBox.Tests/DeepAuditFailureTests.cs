using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests DepsCveScanDeepAuditor in the "failing" path — Critical or High CVEs
/// must cause passed=false with Error-severity findings.
/// </summary>
public sealed class DeepAuditFailureTests
{
    private static readonly DeepAuditContext TestCtx = new(
        ReleaseId: ReleaseId.New(),
        ProjectId: new ProjectId("test"),
        BranchName: "release/v1.0",
        Iteration: 1);

    [Fact]
    public async Task DepsCveScan_HighSeverity_FailsWithErrorFinding()
    {
        var output =
            "Project 'MyApp' has the following vulnerable packages:\n" +
            "   [net10.0]:\n" +
            "   > VulnPkg   1.2.3   1.2.3   High   https://example.com/advisory\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, result.Findings[0].Severity);
        Assert.Contains("VulnPkg", result.Findings[0].Title);
        Assert.Contains("High", result.Findings[0].Title);
    }

    [Fact]
    public async Task DepsCveScan_CriticalSeverity_FailsWithErrorFinding()
    {
        var output =
            "Project 'MyApp' has the following vulnerable packages:\n" +
            "   [net10.0]:\n" +
            "   > CritPkg   0.9.0   0.9.0   Critical   https://example.com/advisory\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.False(result.Passed);
        Assert.Equal(AuditSeverity.Error, result.Findings[0].Severity);
    }

    [Fact]
    public async Task DepsCveScan_MixedSeverities_FailsIfAnyError()
    {
        var output =
            "Project 'MyApp' has the following vulnerable packages:\n" +
            "   [net10.0]:\n" +
            "   > LowPkg      3.0.0   3.0.0   Low      https://example.com/adv1\n" +
            "   > HighPkg     4.0.0   4.0.0   High     https://example.com/adv2\n" +
            "   > ModeratePkg 5.0.0   5.0.0   Moderate https://example.com/adv3\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Info);
    }

    [Fact]
    public async Task DepsCveScan_FindingIncludesAdvisoryUrl()
    {
        var output =
            "Project 'MyApp' has the following vulnerable packages:\n" +
            "   [net10.0]:\n" +
            "   > BadPkg   2.0.0   2.0.0   High   https://github.com/advisories/GHSA-1234\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("https://github.com/advisories/GHSA-1234", finding.Description);
    }

    [Fact]
    public async Task DepsCveScan_RawOutputCaptured()
    {
        var output = "some raw output\n> Pkg 1.0.0 1.0.0 High https://example.com/\n";
        var sandbox = new ScriptedSandbox(new SandboxExecResult(0, output, ""));
        var auditor = new DepsCveScanDeepAuditor();

        var result = await auditor.RunAsync(sandbox, "/work", TestCtx);

        Assert.NotNull(result.RawOutput);
        Assert.Contains("some raw output", result.RawOutput);
    }
}
