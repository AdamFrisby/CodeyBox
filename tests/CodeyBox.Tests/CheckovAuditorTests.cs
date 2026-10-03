using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.CheckovAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the checkov auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming checkov (never a pass or finding).
/// - Exits 0 and 1 are findings-producing via the SARIF report file; a crash (exit 2) or any other
///   exit is infrastructure. An exit 0/1 without a readable report (e.g. an interrupted scan —
///   checkov's SIGINT handler exits 1 with no report) fails closed as infrastructure, not findings.
/// - SARIF results map to findings with check ids (CKV_*) and file/line locations.
/// - Tool severities are mapped through the declared severity mapping (never passed through).
/// - Reserved ExtraArguments (report sink, console purity, scan scope, repo-config surface, check
///   selection, exit contract) are rejected deterministically.
/// - A repository .checkov.yaml/.checkov.yml fails the run closed unless TrustRepositoryConfig is set.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_checkov", "true")] use the bundled checks
///   only, so they need the binary but no network.
/// </summary>
public sealed class CheckovAuditorTests
{
    private static readonly string? InstalledCheckovVersion = ProbeInstalledCheckovVersion();

    private const string SarifWithHigh = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "checkov", "version": "3.3.22" } },
              "results": [
                {
                  "ruleId": "CKV_AWS_21",
                  "level": "error",
                  "message": { "text": "Ensure all data stored in the S3 bucket have versioning enabled" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "main.tf" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "checkov", "version": "3.3.22" } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifWithSeverities = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "checkov", "version": "3.3.22" } },
              "results": [
                {
                  "ruleId": "CKV_AWS_21",
                  "level": "error",
                  "message": { "text": "high severity check" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.tf" } } }]
                },
                {
                  "ruleId": "CKV_AWS_20",
                  "level": "warning",
                  "message": { "text": "medium severity check" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "b.tf" } } }]
                },
                {
                  "ruleId": "CKV_AWS_19",
                  "level": "note",
                  "message": { "text": "low severity check" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "c.tf" } } }]
                },
                {
                  "ruleId": "CKV_AWS_18",
                  "level": "none",
                  "message": { "text": "no severity check" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "d.tf" } } }]
                },
                {
                  "ruleId": "CKV_AWS_17",
                  "level": "fatal",
                  "message": { "text": "unknown future level" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "e.tf" } } }]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithVendoredPaths = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "checkov", "version": "3.3.22" } },
              "results": [
                {
                  "ruleId": "CKV_AWS_21",
                  "level": "error",
                  "message": { "text": "root violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "main.tf" } } }]
                },
                {
                  "ruleId": "CKV_AWS_21",
                  "level": "error",
                  "message": { "text": "downloaded module violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": ".terraform/modules/vpc/main.tf" } } }]
                },
                {
                  "ruleId": "CKV_AWS_21",
                  "level": "error",
                  "message": { "text": "vendored violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/shared/main.tf" } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCheckov_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "checkov: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingCheckov()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(2, "", "checkov: error: unrecognized arguments"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "2.9.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.9.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CheckovAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithKnownIssue_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifWithHigh, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(
                1, "terraform scan results:\n\nPassed checks: 0, Failed checks: 1\n", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // An error-severity result fails the audit.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:checkov", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("CKV_AWS_21", finding.Title, StringComparison.Ordinal);
        Assert.Equal("main.tf:1", finding.Location);
        Assert.Contains("versioning", finding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("checkov", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("-d", argv);
        Assert.Contains(".", argv);
        var outputIndex = argv.ToList().IndexOf("-o");
        Assert.True(outputIndex >= 0 && argv[outputIndex + 1] == "sarif");
        var outputPathIndex = argv.ToList().IndexOf("--output-file-path");
        Assert.True(outputPathIndex >= 0 && argv[outputPathIndex + 1].Length > 0);
        Assert.Contains("--compact", argv);
        Assert.Contains("--quiet", argv);
        // The SARIF report file must live outside the audited worktree.
        Assert.DoesNotContain("results.sarif", argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "Passed checks: 12, Failed checks: 0\n", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FoundSomethingExit_Code1_WithSarifReport_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifWithHigh, ""));
            return Task.FromResult(new SandboxExecResult(1, "Failed checks: 1\n", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(
            result.Findings,
            f => f.Title.Contains("CKV_AWS_21", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FoundSomethingExit_Code1_WithoutSarifReport_IsInfrastructureFailure()
    {
        // Checkov's SIGINT handler exits 1 with no report — an interrupted
        // scan must fail closed as infrastructure, not report findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(
                    1, "", "cat: results_sarif.sarif: No such file or directory"));
            return Task.FromResult(new SandboxExecResult(1, "", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Contains("report", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrashedExit_Code2_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            return Task.FromResult(new SandboxExecResult(2, "", "Traceback (most recent call last): ..."));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            return Task.FromResult(new SandboxExecResult(42, "", "unexpected exit"));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 42", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            return Task.FromResult(new SandboxExecResult(127, "", "checkov: command not found"));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("checkov", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsToCodeyBoxSeverities_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifWithSeverities, ""));
            return Task.FromResult(new SandboxExecResult(1, "Failed checks: 5\n", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);

        var error = Assert.Single(result.Findings, f => f.Location == "a.tf");
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(result.Findings, f => f.Location == "b.tf");
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var note = Assert.Single(result.Findings, f => f.Location == "c.tf");
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var none = Assert.Single(result.Findings, f => f.Location == "d.tf");
        Assert.Equal(AuditSeverity.Info, none.Severity);

        // An unrecognized level from a foreign build stays visible as a
        // warning rather than passing through raw or dropping to info.
        var unknown = Assert.Single(result.Findings, f => f.Location == "e.tf");
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);

        // Only the error-severity finding fails the audit: warnings, notes,
        // and nones are advisory.
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("warning", warning.Description, StringComparison.Ordinal);
        Assert.Contains("note", note.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--output,json",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--output", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SoftFailExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--soft-fail",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--soft-fail", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task CheckSelectionExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--check,CKV_AWS_21",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--check", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepositoryConfigPresent_FailsClosedUnlessTrusted()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, ".checkov.yaml\n", ""));
            if (IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(".checkov.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CheckovAuditor.TrustRepositoryConfigKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepositoryConfigPresent_TrustedByOperator_AllowsScan()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, ".checkov.yaml\n", ""));
            if (IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task InTreeConfigFile_IsRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(EmulateRealpath(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "repo/.checkov.yaml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(CheckovAuditor.ConfigFileKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolAbsentFromBaselineProvisioning()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        Assert.Empty(loader.DiscoverPlugins());
        var status = Assert.Single(
            loader.GetDiscoveryStatuses(),
            s => s.PluginId == CheckovAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("checkov", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCheckovRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CheckovAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CheckovAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("checkov", tool.Binary);
        // Verify-only by design: no distro package carries a pinned checkov, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("checkov", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "3.4.0\n", ""));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "3.4.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_CheckAndFrameworkSelection_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Frameworks"] = "terraform,kubernetes",
                ["Scoped:IncludedChecks"] = "CKV_AWS_21",
                ["Scoped:SkippedChecks"] = "CKV_AWS_19",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var frameworkIndexes = argv
            .Select((value, index) => (value, index))
            .Where(pair => pair.value == "--framework")
            .Select(pair => argv[pair.index + 1])
            .ToList();
        Assert.Contains("terraform", frameworkIndexes);
        Assert.Contains("kubernetes", frameworkIndexes);
        var checkIndex = argv.ToList().IndexOf("--check");
        Assert.True(checkIndex >= 0 && argv[checkIndex + 1] == "CKV_AWS_21");
        var skipIndex = argv.ToList().IndexOf("--skip-check");
        Assert.True(skipIndex >= 0 && argv[skipIndex + 1] == "CKV_AWS_19");
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_BecomeRepeatableDirectoryArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "infra,terraform",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var directoryValues = argv
            .Select((value, index) => (value, index))
            .Where(pair => pair.value == "-d")
            .Select(pair => argv[pair.index + 1])
            .ToList();
        Assert.Equal(["infra", "terraform"], directoryValues);
    }

    [Fact]
    public async Task ScopedConfiguration_EscapingTarget_IsRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "../outside",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(CheckovAuditor.TargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifWithVendoredPaths, ""));
            return Task.FromResult(new SandboxExecResult(1, "Failed checks: 3\n", ""));
        });

        IAuditor auditor = new CheckovAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("main.tf", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoConfigProbe(exec) || IsReportPrep(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, SarifWithHigh, ""));
            return Task.FromResult(new SandboxExecResult(1, "Failed checks: 1\n", ""));
        });

        var auditor = new CheckovAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "CKV_AWS_999",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    [Trait("requires_checkov", "true")]
    public async Task RealCheckov_IssueFixture_ProducesFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledCheckovVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCheckovFixtureRepoAsync(unencryptedBucket: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new CheckovAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("CKV_AWS_", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.StartsWith("main.tf", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_checkov", "true")]
    public async Task RealCheckov_CleanFixture_Passes()
    {
        var installed = InstalledCheckovVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCheckovFixtureRepoAsync(unencryptedBucket: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new CheckovAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CheckovAuditorPlugin.dll");
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: CheckovAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Checkov IaC Security",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, CheckovAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("checkov", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "checkov" && exec.Argv[1] == "--version";

    private static bool IsRepoConfigProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    private static bool IsReportPrep(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("mkdir -m 700", StringComparison.Ordinal);

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "realpath";

    private static bool IsReportRead(SandboxExec exec)
        => exec.Argv.Count == 3 && exec.Argv[0] == "cat" && exec.Argv[1] == "--";

    private static SandboxExecResult EmulateRealpath(SandboxExec exec)
    {
        // Mirrors `realpath -m -- <configured> .`: a configured absolute path
        // stays absolute; a relative one resolves under the /work probe cwd.
        var configured = exec.Argv[3];
        var canonical = configured.StartsWith("/", StringComparison.Ordinal)
            ? configured
            : "/work/" + configured.TrimStart('.', '/');
        return new SandboxExecResult(0, canonical + "\n/work\n", "");
    }

    private static async Task<string> SeedCheckovFixtureRepoAsync(bool unencryptedBucket)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-checkov-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // An S3 bucket without server-side encryption fails long-stable
        // bundled checks (e.g. CKV_AWS_19); a file with no resources fails none.
        var manifest = unencryptedBucket
            ? """
              resource "aws_s3_bucket" "data" {
                bucket = "codeybox-checkov-fixture"
              }
              """
            : """
              terraform {
                required_version = ">= 1.0"
              }
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "main.tf"), manifest);

        return dir;
    }

    private static string? ProbeInstalledCheckovVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "checkov",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 60_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var match = Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return process.ExitCode == 0 && match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture cleanup */ }
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class TestPluginHost(IConfigurationSection scoped) : IPluginHost
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; } = scoped;
    }

    private sealed class FakeSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec) : ISandbox
    {
        public string Id => "fake";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await onExec(exec, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
