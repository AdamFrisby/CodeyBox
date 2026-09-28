using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.KicsAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the KICS auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming kics (never a pass or finding).
/// - Completed scans exit 0 under the pinned --ignore-on-exit results; the semantic result
///   exits 20/30/40/50/60 are also findings-producing; engine failures (126), interrupts (130),
///   and every other exit are infrastructure.
/// - The JSON report (routed onto stdout via the prepared /dev/stdout symlink) maps to findings
///   with the query UUID as rule id and file/line locations.
/// - Tool severities are mapped through the declared mapping (never passed through); an empty or
///   unparseable report fails closed as infrastructure even on a findings-producing exit.
/// - Secret-bearing report fields (search_key/expected_value/actual_value) never reach finding
///   text; an empty scan-root probe and an in-worktree ConfigFile fail closed.
/// - Reserved ExtraArguments flags are rejected deterministically.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_kics", "true")] exercise the
///   report-symlink plumbing end to end; they need the binary plus its bundled assets/.
/// </summary>
public sealed class KicsAuditorTests
{
    private static readonly string? InstalledKicsVersion = ProbeInstalledKicsVersion();

    private const string JsonWithFindings = """
        {
          "kics_version": "2.2.0",
          "files_scanned": 2,
          "files_failed_to_scan": 0,
          "queries_failed_to_execute": 0,
          "total_counter": 2,
          "severity_counters": { "CRITICAL": 0, "HIGH": 1, "MEDIUM": 0, "LOW": 1, "INFO": 0, "TRACE": 0 },
          "queries": [
            {
              "query_name": "S3 Bucket Without Server Side Encryption",
              "query_id": "bad1170b-9632-4f91-b228-36b62a1edcbf",
              "severity": "HIGH",
              "platform": "Terraform",
              "category": "Encryption",
              "description": "S3 Bucket should have server-side encryption enabled",
              "files": [
                { "file_name": "infra/main.tf", "line": 3, "issue_type": "MissingAttribute",
                  "search_key": "aws_s3_bucket.data", "similarity_id": "sim1" }
              ]
            },
            {
              "query_name": "Some Low Query",
              "query_id": "11111111-2222-3333-4444-555555555555",
              "severity": "LOW",
              "platform": "Terraform",
              "description": "low severity issue",
              "files": [
                { "file_name": "./infra/net.tf", "line": 7, "issue_type": "IncorrectValue",
                  "search_key": "x", "similarity_id": "sim2" }
              ]
            }
          ]
        }
        """;

    private const string JsonClean = """
        {
          "kics_version": "2.2.0",
          "files_scanned": 2,
          "files_failed_to_scan": 0,
          "queries_failed_to_execute": 0,
          "total_counter": 0,
          "severity_counters": { "CRITICAL": 0, "HIGH": 0, "MEDIUM": 0, "LOW": 0, "INFO": 0, "TRACE": 0 },
          "queries": []
        }
        """;

    private const string JsonWithSeverities = """
        {
          "kics_version": "2.2.0",
          "files_scanned": 5,
          "files_failed_to_scan": 0,
          "queries_failed_to_execute": 0,
          "total_counter": 5,
          "queries": [
            { "query_name": "Crit", "query_id": "q-crit", "severity": "CRITICAL", "description": "d",
              "files": [{ "file_name": "a.tf", "line": 1 }] },
            { "query_name": "Hi", "query_id": "q-high", "severity": "HIGH", "description": "d",
              "files": [{ "file_name": "b.tf", "line": 2 }] },
            { "query_name": "Med", "query_id": "q-med", "severity": "MEDIUM", "description": "d",
              "files": [{ "file_name": "c.tf", "line": 3 }] },
            { "query_name": "Trace", "query_id": "q-trace", "severity": "TRACE", "description": "d",
              "files": [{ "file_name": "d.tf", "line": 4 }] },
            { "query_name": "Mystery", "query_id": "q-what", "severity": "SOMETHING_NEW", "description": "d",
              "files": [{ "file_name": "e.tf", "line": 5 }] }
          ]
        }
        """;

    private const string JsonIncompleteScan = """
        {
          "kics_version": "2.2.0",
          "files_scanned": 10,
          "files_failed_to_scan": 2,
          "queries_failed_to_execute": 1,
          "total_counter": 0,
          "severity_counters": { "CRITICAL": 0, "HIGH": 0, "MEDIUM": 0, "LOW": 0, "INFO": 0, "TRACE": 0 },
          "queries": []
        }
        """;

    private const string JsonWithFilteredPaths = """
        {
          "kics_version": "2.2.0",
          "files_scanned": 4,
          "files_failed_to_scan": 0,
          "queries_failed_to_execute": 0,
          "total_counter": 4,
          "queries": [
            { "query_name": "Q", "query_id": "q-vendored", "severity": "HIGH", "description": "d",
              "files": [
                { "file_name": "infra/main.tf", "line": 1 },
                { "file_name": "vendor/charts/x.tf", "line": 2 },
                { "file_name": "third_party/ops/x.tf", "line": 3 },
                { "file_name": "node_modules/pkg/x.tf", "line": 4 }
              ] }
          ]
        }
        """;

    // Mirrors KICS's "Passwords And Secrets" category output: the per-file
    // search_key/expected_value/actual_value fields carry the matched source
    // snippet — here standing in for a committed literal secret.
    private const string JsonWithSecretEcho = """
        {
          "kics_version": "2.2.0",
          "files_scanned": 1,
          "files_failed_to_scan": 0,
          "queries_failed_to_execute": 0,
          "total_counter": 1,
          "severity_counters": { "CRITICAL": 0, "HIGH": 1, "MEDIUM": 0, "LOW": 0, "INFO": 0, "TRACE": 0 },
          "queries": [
            {
              "query_name": "Passwords And Secrets",
              "query_id": "487f4be7-3fd9-4506-9389-f6b2879c9061",
              "severity": "HIGH",
              "platform": "Terraform",
              "category": "Secret Management",
              "description": "do not store plaintext secrets",
              "files": [
                { "file_name": "infra/db.tf", "line": 9, "issue_type": "IncorrectValue",
                  "search_key": "aws_db_instance.db.password=hunter2-plaintext",
                  "expected_value": "hunter2-plaintext",
                  "actual_value": "hunter2-plaintext",
                  "similarity_id": "sim-sec" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingKics_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "kics: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kics", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingKics()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(126, "", "engine exploded"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kics", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(
                    0, "Keeping Infrastructure as Code Secure 2.1.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kics", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.1.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(KicsAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KicsAuditor();
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
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var prepExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsPrepProbe(exec))
            {
                prepExecs++;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var high = Assert.Single(
            result.Findings, f => f.Title.Contains("bad1170b-9632-4f91-b228-36b62a1edcbf", StringComparison.Ordinal));
        Assert.Equal("codeybox:kics", high.AuditorName);
        Assert.Equal(AuditSeverity.Error, high.Severity);
        Assert.Equal("infra/main.tf:3", high.Location);
        Assert.Contains("S3 Bucket", high.Title, StringComparison.Ordinal);
        Assert.Contains("similarity_id", high.Description, StringComparison.Ordinal);
        // search_key is value-bearing (secrets queries embed the matched
        // literal in it) and must not reach finding text.
        Assert.DoesNotContain("search_key", high.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("aws_s3_bucket.data", high.Description, StringComparison.Ordinal);

        var low = Assert.Single(
            result.Findings, f => f.Title.Contains("11111111-2222-3333-4444-555555555555", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, low.Severity);
        // KICS reports "./infra/net.tf"; the parser strips the "./" prefix.
        Assert.Equal("infra/net.tf:7", low.Location);

        Assert.Equal(1, prepExecs);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("kics", argv[0]);
        Assert.Equal("scan", argv[1]);
        var outputIndex = argv.ToList().IndexOf("--output-path");
        Assert.True(outputIndex >= 0 && outputIndex + 1 < argv.Count);
        Assert.Contains("codeybox-kics-", argv[outputIndex + 1], StringComparison.Ordinal);
        var formatsIndex = argv.ToList().IndexOf("--report-formats");
        Assert.True(formatsIndex >= 0 && argv[formatsIndex + 1] == "json");
        var ignoreIndex = argv.ToList().IndexOf("--ignore-on-exit");
        Assert.True(ignoreIndex >= 0 && argv[ignoreIndex + 1] == "results");
        Assert.Contains("--silent", argv);
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Contains("codeybox-empty-kics.config.json", argv[configIndex + 1], StringComparison.Ordinal);
        var pathIndex = argv.ToList().IndexOf("--path");
        Assert.True(pathIndex >= 0 && argv[pathIndex + 1] == ".");
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(60)]
    public async Task SemanticFindingsExitCodes_WithReport_ReportAsFindings(int exitCode)
    {
        // KICS's semantic result exits are reachable if --ignore-on-exit is
        // overridden — they are declared findings-producing either way.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, JsonWithFindings, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(126)]
    [InlineData(130)]
    public async Task CouldNotRunExits_AreInfrastructureFailure(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", "engine error"));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kics", ex.Message, StringComparison.Ordinal);
        // 126 is KICS's EngineErrorCode; the shared base also treats 126/127
        // as cannot-execute, so only the generic exits carry "exit N" text.
        if (exitCode == 126)
            Assert.Contains("could not execute", ex.Message, StringComparison.Ordinal);
        else
            Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsProducingExit_WithoutReport_IsInfrastructureFailure()
    {
        // A completed-scan exit code but no parseable report on stdout means
        // the sink never produced — "could not confirm results" is
        // infrastructure, never a clean pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kics", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportPreparationFailure_IsInfrastructureFailure_NamingKics()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsPrepProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "mkdir: cannot create directory"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kics", ex.Message, StringComparison.Ordinal);
        Assert.Contains("report directory", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SeverityMapping_MapsNativeTokens_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithSeverities, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);
        var byLocation = result.Findings.ToDictionary(f => f.Location!, StringComparer.Ordinal);
        Assert.Equal(AuditSeverity.Error, byLocation["a.tf:1"].Severity);   // CRITICAL
        Assert.Equal(AuditSeverity.Error, byLocation["b.tf:2"].Severity);   // HIGH
        Assert.Equal(AuditSeverity.Warning, byLocation["c.tf:3"].Severity); // MEDIUM
        Assert.Equal(AuditSeverity.Info, byLocation["d.tf:4"].Severity);    // TRACE
        Assert.Equal(AuditSeverity.Warning, byLocation["e.tf:5"].Severity); // unknown -> Warning
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than the
        // severity field itself.
        Assert.Contains("HIGH", byLocation["b.tf:2"].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteScanCounters_EmitSyntheticWarningFinding()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonIncompleteScan, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("kics/incomplete-scan", finding.Title, StringComparison.Ordinal);
        Assert.Contains("failed to parse", finding.Description, StringComparison.Ordinal);
        // A warning does not block the audit by itself.
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task SecretsReportFields_AreNotEchoedIntoFindings()
    {
        // Findings flow to the rework prompt, webhooks, and the persisted
        // audit report — a secret-bearing search_key/expected/actual value
        // must never reach them, the same reason the gitleaks auditor runs
        // with --redact.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithSecretEcho, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("infra/db.tf:9", finding.Location);
        Assert.Contains("similarity_id", finding.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-plaintext", finding.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-plaintext", finding.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("search_key", finding.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("actual_value", finding.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("expected_value", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanRootProbe_EmptyOutput_IsInfrastructureFailure()
    {
        // A pwd probe that exits 0 but prints nothing used to degrade to a
        // null scan root — absolute paths then survived normalization and
        // the vendored-path exclusions silently stopped matching.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPwdProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scan root", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kics.config")]
    [InlineData("./config/kics.json")]
    [InlineData("/work/kics.config")]
    [InlineData("/work/sub/kics.json")]
    [InlineData("/work/../work/nested.json")]
    public async Task InTreeConfigFile_IsRejectedDeterministically(string configFile)
    {
        // A config inside the audited tree lets the diff author bind flags
        // absent from argv (exclude-queries, exclude-severities) and empty
        // the report — rejected before the scan ever runs.
        var scanExecs = 0;
        var prepExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsPrepProbe(exec))
            {
                prepExecs++;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KicsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = configFile,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ConfigFile", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, prepExecs);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DefaultExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new KicsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("infra/main.tf:1", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_Platforms_ConfigFile_ShapeArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KicsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "infra/, deploy/main.tf",
                ["Scoped:Platforms"] = "terraform, k8s",
                ["Scoped:ConfigFile"] = "/opt/kics-policy/config.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var paths = argv
            .Select((arg, i) => (arg, i))
            .Where(t => t.arg == "--path")
            .Select(t => argv[t.i + 1])
            .ToList();
        Assert.Equal(new[] { "infra/", "deploy/main.tf" }, paths);
        var types = argv
            .Select((arg, i) => (arg, i))
            .Where(t => t.arg == "--type")
            .Select(t => argv[t.i + 1])
            .ToList();
        Assert.Equal(new[] { "terraform", "k8s" }, types);
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/opt/kics-policy/config.json");
    }

    [Theory]
    [InlineData("--output-path")]
    [InlineData("-o")]
    [InlineData("--report-formats")]
    [InlineData("--config")]
    [InlineData("--ignore-on-exit")]
    [InlineData("--silent")]
    [InlineData("--ci")]
    [InlineData("--verbose")]
    [InlineData("-p")]
    [InlineData("--path")]
    public async Task ReservedExtraArguments_AreRejectedDeterministically(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPrepProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KicsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = flag,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == KicsAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("kics", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresKicsRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [KicsAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == KicsAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("kics", tool.Binary);
        // Verify-only by design: no distro package carries kics, and the
        // release tarball's assets/ tree must sit next to the binary, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("kics", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_kics", "true")]
    public async Task RealKics_MisconfiguredTerraformFixture_ProducesFinding()
    {
        var installed = InstalledKicsVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKicsFixtureRepoAsync(misconfigured: true);

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

            var auditor = new KicsAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            // The deliverable: a real scan over a fixture with a known IaC
            // issue produces a finding carrying its rule id and location.
            Assert.Contains(result.Findings,
                f => f.Location != null
                    && f.Location.EndsWith(".tf", StringComparison.Ordinal)
                    && f.Description.Contains("Rule: ", StringComparison.Ordinal)
                    && !f.Description.Contains("Rule: (none)", StringComparison.Ordinal));
            Assert.All(result.Findings, f => Assert.Equal("codeybox:kics", f.AuditorName));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_kics", "true")]
    public async Task RealKics_NoIacFixture_Passes()
    {
        var installed = InstalledKicsVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKicsFixtureRepoAsync(misconfigured: false);

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

            var auditor = new KicsAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.KicsAuditorPlugin.dll");
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
            PluginId: KicsAuditor.PluginId,
            PluginDisplayName: "CodeyBox: KICS Infrastructure-as-Code Security",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(
                0, "Keeping Infrastructure as Code Secure " + KicsAuditor.DefaultExpectedVersion + "\n", "")
            : IsPwdProbe(exec)
                ? new SandboxExecResult(0, "/work\n", "")
                : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("kics", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "kics" && exec.Argv[1] == "version";

    private static bool IsPrepProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("ln -sfn", StringComparison.Ordinal);

    private static bool IsPwdProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static async Task<string> SeedKicsFixtureRepoAsync(bool misconfigured)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-kics-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (misconfigured)
        {
            // aws_s3_bucket without encryption/versioning/public-access-block
            // trips several bundled Terraform queries on any KICS release.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "main.tf"),
                """
                resource "aws_s3_bucket" "data" {
                  bucket = "example-data"
                }
                """);
        }
        else
        {
            // No IaC files at all: KICS scans nothing and reports an empty
            // queries array — the honest clean case.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "README.txt"), "no infrastructure here\n");
        }

        return dir;
    }

    private static string? ProbeInstalledKicsVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kics",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            stderr.GetAwaiter().GetResult();
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
