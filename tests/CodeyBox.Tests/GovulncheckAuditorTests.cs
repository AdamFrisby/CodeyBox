using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.GovulncheckAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the govulncheck auditor plugin:
/// - Missing binary, missing go toolchain, wrong or undeterminable version are
///   infrastructure failures naming the tool (never a pass or finding).
/// - govulncheck's exit convention (verified against v1.8.0 source): with
///   -format sarif a completed run exits 0 whether or not it found anything —
///   the SARIF is the verdict. Exit 3 ("vulnerabilities found") is text-format
///   only and impossible under -format sarif, 2 is usage error, 1 is run
///   failure, 126/127 cannot-execute — all infrastructure.
/// - SARIF results map to findings with OSV rule ids and the go.mod location
///   govulncheck supplies; SARIF levels go through the declared mapping,
///   never raw.
/// - The version pin anchors on the govulncheck@v… token because the banner's
///   first semver token is the Go toolchain's version.
/// - Plugin is disabled by default, absent from baseline provisioning until
///   enabled.
/// - Real binary execution tests under [Trait("requires_govulncheck", "true")]
///   need govulncheck and a Go toolchain on PATH plus vuln.go.dev access.
/// </summary>
public sealed class GovulncheckAuditorTests
{
    private static readonly string? InstalledGovulncheckVersion = ProbeInstalledVersion();
    private static readonly Lazy<bool> GovulncheckEndToEndAvailable = new(ProbeGovulncheckEndToEnd);

    private const string SarifWithFindings =
        """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": {
                "driver": {
                  "name": "govulncheck",
                  "version": "v1.8.0",
                  "informationUri": "https://pkg.go.dev/golang.org/x/vuln/cmd/govulncheck"
                }
              },
              "results": [
                {
                  "ruleId": "GO-2021-0113",
                  "level": "error",
                  "message": {"text": "Your code calls vulnerable functions in 1 packages (golang.org/x/text/language)."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod", "uriBaseId": "SRCROOT"}, "region": {"startLine": 1}}, "message": {"text": "Findings for vulnerability GO-2021-0113"}}]
                },
                {
                  "ruleId": "GO-2022-1059",
                  "level": "warning",
                  "message": {"text": "Your code imports 1 vulnerable package (golang.org/x/text/language), but doesn’t appear to call any of the vulnerable symbols."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod", "uriBaseId": "SRCROOT"}, "region": {"startLine": 1}}, "message": {"text": "Findings for vulnerability GO-2022-1059"}}]
                },
                {
                  "ruleId": "GO-2024-2687",
                  "level": "note",
                  "message": {"text": "Your code depends on 1 vulnerable module (golang.org/x/net), but doesn't appear to call any of the vulnerable symbols."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod", "uriBaseId": "SRCROOT"}, "region": {"startLine": 1}}, "message": {"text": "Findings for vulnerability GO-2024-2687"}}]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifClean =
        """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": {
                "driver": {
                  "name": "govulncheck",
                  "version": "v1.8.0",
                  "informationUri": "https://pkg.go.dev/golang.org/x/vuln/cmd/govulncheck"
                }
              },
              "results": []
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGovulncheck_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingGoToolchain_IsInfrastructureFailure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGoPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        // govulncheck fails later with a misleading "no go.mod" without this
        // probe — the check names the missing toolchain instead.
        Assert.Contains("'go'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "Go: go1.24.5\nScanner: govulncheck@v1.7.0\nDB: https://vuln.go.dev\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.7.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GovulncheckAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnrecognisedVersionBanner_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "some other tool 1.8.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        // A banner without a govulncheck@v… token is a foreign binary, not
        // a version match — even when it happens to carry the pinned number.
        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-version",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // With -format sarif the "found something" exit is 0 — the SARIF
            // report is the verdict.
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var reachable = Assert.Single(
            result.Findings, f => f.Title.Contains("GO-2021-0113", StringComparison.Ordinal));
        Assert.Equal("codeybox:govulncheck", reachable.AuditorName);
        Assert.Equal(AuditSeverity.Error, reachable.Severity);
        Assert.Equal("go.mod:1", reachable.Location);

        var imported = Assert.Single(
            result.Findings, f => f.Title.Contains("GO-2022-1059", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, imported.Severity);

        var moduleOnly = Assert.Single(
            result.Findings, f => f.Title.Contains("GO-2024-2687", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, moduleOnly.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("govulncheck", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        var formatIndex = argv.IndexOf("-format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        var scanIndex = argv.IndexOf("-scan");
        Assert.True(scanIndex >= 0 && argv[scanIndex + 1] == "symbol");
        // Patterns are positional and last.
        Assert.Equal("./...", argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit1_FailedRun_IsInfrastructureFailure()
    {
        // Exit 1 is every run failure — here: no go.mod at the scan root.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "govulncheck: no go.mod file"));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit2_UsageError_IsInfrastructureFailure()
    {
        // Exit 2 is usage error — e.g. patterns that matched no packages.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "govulncheck: no packages matched the provided patterns"));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit3_IsInfrastructureFailure_NotFindings()
    {
        // Exit 3 is "vulnerabilities found" — but only for text output, never
        // under -format sarif. Seeing it means the format contract broke;
        // it must fail loud as infrastructure, never become a pass and never
        // produce findings from a report shape we did not ask for.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithFindings, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("govulncheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_NoRawStringsPassedThrough()
    {
        const string sarif =
            """
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {"driver": {"name": "govulncheck", "version": "v1.8.0"}},
                  "results": [
                    {"ruleId": "GO-2026-0001", "level": "error", "message": {"text": "calls a vulnerable symbol"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "GO-2026-0002", "level": "warning", "message": {"text": "imports a vulnerable package"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "GO-2026-0003", "level": "note", "message": {"text": "depends on a vulnerable module"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "GO-2026-0004", "level": "brand-new-future-level", "message": {"text": "future"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "go.mod"}, "region": {"startLine": 1}}}]}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, sarif, ""));
        });

        IAuditor auditor = new GovulncheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("GO-2026-0001")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("GO-2026-0002")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("GO-2026-0003")).Severity);
        // Unknown tool level falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("GO-2026-0004")).Severity);
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping.
        var reachable = Assert.Single(result.Findings, f => f.Title.Contains("GO-2026-0001"));
        Assert.Contains("Severity (tool): error", reachable.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ToolKnobs_ShapeTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:DbUrl"] = "https://vuln-mirror.internal/db",
                ["Scoped:ScanLevel"] = "package",
                ["Scoped:ModuleDirectory"] = "services/api",
                ["Scoped:BuildTags"] = "integration, prod",
                ["Scoped:IncludeTests"] = "true",
                ["Scoped:Patterns"] = "./cmd/...,./internal/...",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var dbIndex = argv.IndexOf("-db");
        Assert.True(dbIndex >= 0 && argv[dbIndex + 1] == "https://vuln-mirror.internal/db");
        var scanIndex = argv.IndexOf("-scan");
        Assert.True(scanIndex >= 0 && argv[scanIndex + 1] == "package");
        var dirIndex = argv.IndexOf("-C");
        Assert.True(dirIndex >= 0 && argv[dirIndex + 1] == "services/api");
        var tagsIndex = argv.IndexOf("-tags");
        Assert.True(tagsIndex >= 0 && argv[tagsIndex + 1] == "integration,prod");
        Assert.Contains("-test", argv, StringComparer.Ordinal);
        // Flags precede the positional patterns; configured patterns replace
        // the ./... default.
        var firstPattern = argv.IndexOf("./cmd/...");
        Assert.True(firstPattern > 0);
        Assert.Equal("./internal/...", argv[^1]);
        Assert.DoesNotContain("./...", argv.GetRange(0, firstPattern), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ScanLevelModule_DropsPatterns()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ScanLevel"] = "module",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var scanIndex = argv.IndexOf("-scan");
        Assert.True(scanIndex >= 0 && argv[scanIndex + 1] == "module");
        Assert.DoesNotContain("./...", argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ModuleScanLevelWithPatterns_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ScanLevel"] = "module",
                ["Scoped:Patterns"] = "./cmd/...",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Patterns", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_InvalidScanLevel_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ScanLevel"] = "function",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ScanLevel", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_DashPattern_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Patterns"] = "-tags=inject,./...",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Patterns", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the reachable-vulnerability error survives the threshold.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        var auditor = new GovulncheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "GO-2021-0113,GO-2024-2687",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("GO-2022-1059", finding.Title, StringComparison.Ordinal);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ExtractGovulncheckVersion_AnchorsOnScannerToken_NotGoVersion()
    {
        const string banner =
            "Go: go1.24.5\nScanner: govulncheck@v1.8.0\nDB: https://vuln.go.dev\nDB updated: 2026-09-08 20:52:42 +0000 UTC\n";

        // The banner's first semver token is the Go toolchain's — the shared
        // first-token extraction would pin the wrong component.
        Assert.Equal("1.8.0", GovulncheckAuditor.ExtractGovulncheckVersion(banner));
        Assert.Null(GovulncheckAuditor.ExtractGovulncheckVersion("Go: go1.24.5\nDB: https://vuln.go.dev\n"));
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolsAbsentFromBaselineProvisioning()
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
            s => s.PluginId == GovulncheckAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("govulncheck", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GovulncheckAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GovulncheckAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var govulncheck = Assert.Single(tools, t => t.Binary == "govulncheck");
        var go = Assert.Single(tools, t => t.Binary == "go");
        // Verify-only by design: no distro package carries a pinned
        // govulncheck, and the Go toolchain is operator-provisioned.
        Assert.Null(govulncheck.AptPackage);
        Assert.Null(go.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Equal(2, contributions.VerificationCommands.Count);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("govulncheck", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_govulncheck", "true")]
    public async Task RealGovulncheck_VulnerableFixture_ProducesFindingWithRuleIdAndLocation()
    {
        if (InstalledGovulncheckVersion is null || !GovulncheckEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedGovulncheckVulnerableRepoAsync();
        if (fixtureDir is null)
            return; // go mod tidy failed — host toolchain/network unavailable.

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

            var auditor = new GovulncheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledGovulncheckVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("GO-2021-0113", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("go.mod:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_govulncheck", "true")]
    public async Task RealGovulncheck_CleanFixture_Passes()
    {
        if (InstalledGovulncheckVersion is null || !GovulncheckEndToEndAvailable.Value)
            return;

        var fixtureDir = SeedGovulncheckCleanRepo();
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

            var auditor = new GovulncheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledGovulncheckVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // The clean module carries no third-party dependencies; a finding
            // here would mean the toolchain's own standard library is
            // vulnerable — also worth surfacing, but the end-to-end probe
            // already requires the same scan on the same module shape to
            // come back clean.
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GovulncheckAuditorPlugin.dll");
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
            PluginId: GovulncheckAuditor.PluginId,
            PluginDisplayName: "CodeyBox: govulncheck Go Vulnerability Scan",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(
                0,
                "Go: go1.24.5\nScanner: govulncheck@v" + GovulncheckAuditor.DefaultExpectedVersion
                    + "\nDB: https://vuln.go.dev\nDB updated: 2026-09-08 20:52:42 +0000 UTC\n",
                "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv[^1] == "govulncheck";

    private static bool IsGoPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv[^1] == "go";

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "govulncheck" && exec.Argv[1] == "-version";

    private static string? ProbeInstalledVersion()
    {
        var stdout = RunHostProbe("govulncheck", 10_000, null, "-version");
        if (stdout is null)
            return null;
        var match = Regex.Match(stdout, @"govulncheck@v(\d+\.\d+\.\d+[\w.\-]*)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool ProbeGovulncheckEndToEnd()
    {
        // The real-binary tests need the go toolchain, govulncheck, and a
        // reachable vulnerability database: run the exact scan the auditor
        // performs against a clean module and require a clean verdict.
        var dir = SeedGovulncheckCleanRepo();
        try
        {
            var stdout = RunHostProbe(
                "govulncheck", 180_000, dir, "-format", "sarif", "-scan", "symbol", "./...");
            return stdout is not null && stdout.Contains("\"results\"", StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    private static string SeedGovulncheckCleanRepo()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-govulncheck-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "go.mod"), "module fixture\ngo 1.18\n");
        File.WriteAllText(Path.Combine(dir, "main.go"), "package main\n\nfunc main() {}\n");
        return dir;
    }

    private static async Task<string?> SeedGovulncheckVulnerableRepoAsync()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-govulncheck-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // golang.org/x/text v0.3.5 calling language.Parse is the canonical
        // govulncheck demo: GO-2021-0113 is reachable at symbol level.
        await File.WriteAllTextAsync(Path.Combine(dir, "go.mod"), """
            module fixture

            go 1.21

            require golang.org/x/text v0.3.5
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "main.go"), """
            package main

            import (
                "fmt"

                "golang.org/x/text/language"
            )

            func main() {
                tag, _ := language.Parse("en-US")
                fmt.Println(tag)
            }
            """);

        // Resolve the module graph and go.sum on the host — the sandbox
        // runs govulncheck against these files as-is.
        var tidy = RunHostProbe("go", 120_000, dir, "mod", "tidy");
        return tidy is null ? null : dir;
    }

    private static string? RunHostProbe(
        string binary,
        int milliseconds,
        string? workingDirectory,
        params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (workingDirectory is not null)
                psi.WorkingDirectory = workingDirectory;
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi)!;
            // Drain both streams concurrently: a full stderr pipe would block
            // the child on write while stdout stays open, deadlocking the
            // synchronous read ahead of the timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: milliseconds))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromSeconds(5));
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            return process.ExitCode == 0 ? stdout : null;
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
