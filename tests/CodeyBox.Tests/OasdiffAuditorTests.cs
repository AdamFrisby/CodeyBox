using System.Diagnostics;
using CodeyBox.OasdiffAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the oasdiff auditor plugin:
/// - Missing binary or wrong-version binary is an infrastructure failure naming oasdiff (never a pass or finding).
/// - Both specs must be existing local repository-relative documents; URLs, absolute
///   paths, ".." escapes, and identical base/spec pointers are deterministic infrastructure failures.
/// - Both documents must pass `oasdiff validate`; an invalid baseline or candidate is coverage
///   unavailable (never a pass, never a finding) and the scan never runs.
/// - Exit codes 0 and 1 are findings-producing candidates; the parser tells
///   "breaking changes found" apart from "could not run" by report content, while
///   100/102 and anything else are infrastructure without parsing.
/// - Error/warn entries map to findings with rule ids and tool-supplied file/line locations,
///   and mapped (never raw) severities.
/// - Malformed, truncated, and wrong-shape output all fail closed as infrastructure.
/// - The configured fail-on threshold selects `--fail-on`; contract-altering extra arguments
///   (`--format`, `--fail-on`, `--composed`, ignore/severity files, ...) are rejected before any exec.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_oasdiff", "true")].
/// </summary>
public sealed class OasdiffAuditorTests
{
    private static readonly string? InstalledOasdiffVersion = ProbeInstalledOasdiffVersion();

    // Shaped like the verified oasdiff 1.33.0 contract:
    // `oasdiff breaking base.yaml rev.yaml --format json --fail-on ERR` prints
    // a JSON array on stdout, then exits 1 when ERR-level entries exist.
    private const string BreakingWithFindings = """
        [
          {"id":"api-path-removed-without-deprecation","text":"api path removed without deprecation","level":3,"operation":"GET","operationId":"listWidgets","path":"/widgets","section":"paths","baseSource":{"file":"api/openapi.base.yaml","line":7,"column":5},"fingerprint":"c076ed7b5e8d"},
          {"id":"request-parameter-removed","text":"deleted the 'query' request parameter 'filter'","level":2,"operation":"GET","operationId":"listWidgets","path":"/widgets","section":"paths","revisionSource":{"file":"api/openapi.yaml","line":12,"column":9},"fingerprint":"abc123"}
        ]
        """;

    // Exit 0: no entries at the --fail-on ERR threshold; WARN entries may still be listed.
    private const string BreakingWarnOnly = """
        [
          {"id":"request-parameter-removed","text":"deleted the 'query' request parameter 'filter'","level":2,"operation":"GET","operationId":"listWidgets","path":"/widgets","section":"paths","revisionSource":{"file":"api/openapi.yaml","line":12,"column":9},"fingerprint":"abc123"}
        ]
        """;

    // Exit 0: identical documents.
    private const string BreakingClean = """[]""";

    // Exit 102 with no JSON report: a spec failed to load.
    private const string LoadErrorStderr = """
        Error: failed to load base spec from "api/openapi.base.yaml": open api/openapi.base.yaml: no such file or directory
        """;

    private const string DefaultBaseSpec = "api/openapi.base.yaml";
    private const string DefaultSpec = "api/openapi.yaml";

    private const string PinnedOasdiffVersion = "1.33.0";
    private const string PinnedVersionBanner = "oasdiff version 1.33.0";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "oasdiff version 1.11.7", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChanges_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = StandardSandbox(
            onScan: exec =>
            {
                scanExec = exec;
                return new SandboxExecResult(1, BreakingWithFindings, "");
            });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var removed = Assert.Single(result.Findings, f => f.Title.StartsWith("api-path-removed-without-deprecation:", StringComparison.Ordinal));
        Assert.Equal("codeybox:oasdiff", removed.AuditorName);
        Assert.Equal(AuditSeverity.Error, removed.Severity);
        // Removed content carries only the base origin; the tool's file/line are preserved.
        Assert.Equal("api/openapi.base.yaml:7", removed.Location);
        // The tool's raw numeric level (3) is canonicalized and mapped, not passed through.
        Assert.Contains("Severity (tool): error", removed.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Severity (tool): 3", removed.Description, StringComparison.Ordinal);
        Assert.Contains("/widgets", removed.Description, StringComparison.Ordinal);

        var param = Assert.Single(result.Findings, f => f.Title.StartsWith("request-parameter-removed:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, param.Severity);
        // Changed content prefers the revision origin: the candidate document under audit.
        Assert.Equal("api/openapi.yaml:12", param.Location);
        Assert.Contains("Severity (tool): warn", param.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal(
            new[] { "oasdiff", "breaking", DefaultBaseSpec, DefaultSpec, "--format", "json", "--fail-on", "ERR" },
            scanExec!.Argv.ToArray());
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, BreakingClean, ""));

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task AdvisoryOnlyChanges_AreAdvisory_AndPass()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, BreakingWarnOnly, ""));

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.StartsWith("request-parameter-removed:", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("api/openapi.yaml:12", finding.Location);
    }

    [Fact]
    public async Task Exit1_WithoutThresholdEntry_IsInfrastructureFailure()
    {
        // Exit 1 claims ERR-level findings the WARN-only report does not show.
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, BreakingWarnOnly, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit102_LoadFailure_IsInfrastructureFailure_NeverFindings()
    {
        // The tool's "could not run" exit carries no report and must never parse as findings.
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(102, "", LoadErrorStderr));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit100_UsageError_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(100, "", "Error: unknown flag: --bogus"));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit0_WithErrorEntry_IsInfrastructureFailure()
    {
        // Exit 0 claims no ERR-level findings the report contradicts.
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, BreakingWithFindings, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, "", ""));

        var auditor = await CreateAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJson_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, "[{not json", ""));

        var auditor = await CreateAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedJson_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, """[{"id":"api-path-removed""", ""));

        var auditor = await CreateAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownLevel_IsInfrastructureFailure_NeverGuessed()
    {
        var report = """[{"id":"api-path-removed-without-deprecation","text":"x","level":9,"path":"/widgets"}]""";
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, report, ""));

        var auditor = await CreateAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task NonObjectEntry_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, """["just-a-string"]""", ""));

        var auditor = await CreateAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Exit127_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(127, "", "oasdiff: command not found"));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPointers_AreDeterministicInfrastructureFailures()
    {
        foreach (var scoped in new[]
            {
                new Dictionary<string, string?>(),
                new Dictionary<string, string?> { ["Scoped:BaseSpec"] = DefaultBaseSpec },
                new Dictionary<string, string?> { ["Scoped:Spec"] = DefaultSpec },
            })
        {
            var execs = 0;
            var sandbox = new FakeSandbox((_, _) =>
            {
                execs++;
                return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
            });

            var auditor = new OasdiffAuditor();
            await auditor.InitializeAsync(BuildPluginContext(scoped), CancellationToken.None);
            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

            Assert.True(ex.IsDeterministic);
            Assert.Equal(0, execs);
        }
    }

    [Theory]
    [InlineData("https://example.com/openapi.yaml")]
    [InlineData("http://example.com/openapi.json")]
    [InlineData("/abs/openapi.yaml")]
    [InlineData("../outside/openapi.yaml")]
    [InlineData("api/spec.txt")]
    [InlineData("main:openapi.yaml")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NonLocalPointers_AreRejected_BeforeAnyProbe(string pointer)
    {
        var execs = 0;
        var sandbox = new FakeSandbox((_, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = pointer,
                ["Scoped:Spec"] = DefaultSpec,
            }),
            CancellationToken.None);

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task IdenticalPointers_AreRejected_AsVacuous()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((_, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultSpec,
                ["Scoped:Spec"] = DefaultSpec,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task UnknownFailOn_IsInfrastructureFailure()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((_, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:FailOn"] = "INFO",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task FailOnWarn_SelectsWarnThreshold_EndToEnd()
    {
        SandboxExec? scanExec = null;
        var sandbox = StandardSandbox(
            onScan: exec =>
            {
                scanExec = exec;
                return new SandboxExecResult(1, BreakingWarnOnly, "");
            });

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:FailOn"] = "warn",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.NotNull(scanExec);
        Assert.Contains("--fail-on", scanExec!.Argv);
        Assert.Contains("WARN", scanExec.Argv);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("--format=json")]
    [InlineData("-f")]
    [InlineData("-fjson")]
    [InlineData("--fail-on")]
    [InlineData("--fail-on=WARN")]
    [InlineData("-o")]
    [InlineData("--composed")]
    [InlineData("-c")]
    [InlineData("--err-ignore")]
    [InlineData("--err-ignore=ignore.txt")]
    [InlineData("--warn-ignore")]
    [InlineData("--severity-levels")]
    [InlineData("--template")]
    [InlineData("--color")]
    [InlineData("--color=always")]
    public async Task ContractAlteringExtraArguments_AreRejected_BeforeExec(string extra)
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            // Presence and version probes run before VerifyToolAsync rejects the flag.
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            execs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:ExtraArguments"] = extra,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task BenignExtraArguments_AreAppended_Verbatim_AsArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = StandardSandbox(
            onScan: exec =>
            {
                scanExec = exec;
                return new SandboxExecResult(0, BreakingClean, "");
            });

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:ExtraArguments"] = "--flatten-allof, --match-path, ^/internal",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        // Operator tuning travels as argv entries, never through a shell.
        Assert.Equal(
            new[] { "oasdiff", "breaking", DefaultBaseSpec, DefaultSpec, "--format", "json", "--fail-on", "ERR", "--flatten-allof", "--match-path", "^/internal" },
            scanExec!.Argv.ToArray());
    }

    [Fact]
    public async Task InvalidBaselineDocument_IsCoverageUnavailable_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = StandardSandbox(
            onValidate: exec => exec.Argv.Contains(DefaultBaseSpec, StringComparer.Ordinal)
                ? new SandboxExecResult(1, "2 findings: 2 error, 0 warning, 0 info", "")
                : new SandboxExecResult(0, "No findings detected", ""),
            onScan: exec =>
            {
                scanExecs++;
                return new SandboxExecResult(1, BreakingWithFindings, "");
            });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(DefaultBaseSpec, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task InvalidCandidateDocument_IsCoverageUnavailable_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = StandardSandbox(
            onValidate: exec => exec.Argv.Contains(DefaultSpec, StringComparer.Ordinal)
                ? new SandboxExecResult(1, "1 findings: 1 error, 0 warning, 0 info", "")
                : new SandboxExecResult(0, "No findings detected", ""),
            onScan: _ =>
            {
                scanExecs++;
                return new SandboxExecResult(1, BreakingWithFindings, "");
            });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(DefaultSpec, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingBaselineFile_FailsClosed_BeforeValidation()
    {
        var validateExecs = 0;
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsValidateProbe(exec))
            {
                validateExecs++;
                return Task.FromResult(Ok(exec));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Equal(0, validateExecs);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ValidateProbe_UsesFailOnErr_AndNamesSpec()
    {
        var validateArgvs = new List<IReadOnlyList<string>>();
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsValidateProbe(exec))
            {
                validateArgvs.Add(exec.Argv.ToArray());
                return Task.FromResult(Ok(exec));
            }
            return Task.FromResult(new SandboxExecResult(0, BreakingClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, validateArgvs.Count);
        Assert.Contains(validateArgvs, argv => argv.SequenceEqual(new[] { "oasdiff", "validate", "--fail-on", "ERR", DefaultBaseSpec }));
        Assert.Contains(validateArgvs, argv => argv.SequenceEqual(new[] { "oasdiff", "validate", "--fail-on", "ERR", DefaultSpec }));
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, BreakingWithFindings, ""));

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_SelectsByRuleId()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, BreakingWithFindings, ""));

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:IncludedRules"] = "request-parameter-removed",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.StartsWith("request-parameter-removed:", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersRevisionFindings()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, BreakingWarnOnly, ""));

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
                ["Scoped:ExcludePaths"] = "api/",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(3, "error")]
    [InlineData(2, "warn")]
    [InlineData(1, "info")]
    public void CanonicalLevel_MapsNumericLevels(int numeric, string expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse($"{{\"level\":{numeric}}}");
        Assert.Equal(expected, OasdiffBreakingJsonParser.CanonicalLevel(document.RootElement));
    }

    [Theory]
    [InlineData("error", "error")]
    [InlineData("ERR", "error")]
    [InlineData("warn", "warn")]
    [InlineData("WARNING", "warn")]
    [InlineData("info", "info")]
    public void CanonicalLevelToken_NormalizesKnownLevels(string token, string expected)
    {
        Assert.Equal(expected, OasdiffBreakingJsonParser.CanonicalLevelToken(token));
    }

    [Theory]
    [InlineData("fatal")]
    [InlineData("")]
    [InlineData("   ")]
    public void CanonicalLevelToken_RejectsUnknown(string token)
    {
        Assert.Null(OasdiffBreakingJsonParser.CanonicalLevelToken(token));
    }

    [Fact]
    public void UnknownNumericLevel_IsUnmappable()
    {
        foreach (var numeric in new[] { 0, 4, -1 })
        {
            using var document = System.Text.Json.JsonDocument.Parse($"{{\"level\":{numeric}}}");
            Assert.Null(OasdiffBreakingJsonParser.CanonicalLevel(document.RootElement));
        }

        using var missing = System.Text.Json.JsonDocument.Parse("""{"id":"x"}""");
        Assert.Null(OasdiffBreakingJsonParser.CanonicalLevel(missing.RootElement));
    }

    [Fact]
    public void ChangeLocation_PrefersRevisionSource_FallsBackToBase()
    {
        using var both = System.Text.Json.JsonDocument.Parse(
            """{"revisionSource":{"file":"api/openapi.yaml","line":12},"baseSource":{"file":"api/openapi.base.yaml","line":7}}""");
        Assert.Equal(("api/openapi.yaml", 12), OasdiffBreakingJsonParser.ChangeLocation(both.RootElement));

        using var baseOnly = System.Text.Json.JsonDocument.Parse(
            """{"baseSource":{"file":"api/openapi.base.yaml","line":7}}""");
        Assert.Equal(("api/openapi.base.yaml", 7), OasdiffBreakingJsonParser.ChangeLocation(baseOnly.RootElement));

        using var neither = System.Text.Json.JsonDocument.Parse("""{"id":"x"}""");
        Assert.Equal((null, null), OasdiffBreakingJsonParser.ChangeLocation(neither.RootElement));
    }

    [Fact]
    public void ValidateSpecPointer_AcceptsLocalDocuments_RejectsRemote()
    {
        Assert.Equal(
            "api/openapi.yaml",
            OasdiffAuditor.ValidateSpecPointer("api/openapi.yaml", OasdiffAuditor.BaseSpecKey, "oasdiff"));
        Assert.Equal(
            "./spec.json",
            OasdiffAuditor.ValidateSpecPointer("./spec.json", OasdiffAuditor.BaseSpecKey, "oasdiff"));

        foreach (var bad in new[]
            {
                null, "", "   ",
                "https://example.com/openapi.yaml",
                "main:openapi.yaml",
                "/abs/openapi.yaml",
                "../outside/openapi.yaml",
                "api/spec.txt",
                new string('a', 2000) + ".yaml",
            })
        {
            Assert.Throws<AuditUnavailableException>(
                () => OasdiffAuditor.ValidateSpecPointer(bad, OasdiffAuditor.BaseSpecKey, "oasdiff"));
        }
    }

    [Fact]
    public void PinnedRelease_IsDeclared_OnRequirement_AndCode()
    {
        Assert.Equal(PinnedOasdiffVersion, OasdiffAuditor.DefaultExpectedVersion);

        var attributes = typeof(OasdiffAuditor)
            .GetCustomAttributes(typeof(CodeyBoxPluginRequiresToolAttribute), inherit: false)
            .Cast<CodeyBoxPluginRequiresToolAttribute>()
            .ToList();
        var oasdiff = Assert.Single(attributes, a => a.Binary == "oasdiff");
        Assert.Null(oasdiff.AptPackage);
        Assert.Contains("1.33.0", oasdiff.InstallHint, StringComparison.Ordinal);
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
            s => s.PluginId == OasdiffAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("oasdiff", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [OasdiffAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == OasdiffAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var oasdiff = Assert.Single(tools, t => t.Binary == "oasdiff");
        Assert.Null(oasdiff.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Empty(contributions.InstallCommands);
        Assert.Contains(
            contributions.VerificationCommands,
            v => v.Argv.Any(a => a.Contains("oasdiff", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("requires_oasdiff", "true")]
    public async Task RealOasdiff_BreakingFixture_YieldsBreakingFinding()
    {
        if (InstalledOasdiffVersion is null)
            return;

        var fixtureDir = await SeedOasdiffFixtureRepoAsync(breaking: true);
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

            var auditor = new OasdiffAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:BaseSpec"] = "base.yaml",
                    ["Scoped:Spec"] = "rev.yaml",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings,
                f => f.Title.StartsWith("api-path-removed-without-deprecation:", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            // Removed content carries only the base origin, with the tool-reported line preserved.
            Assert.Equal("base.yaml:7", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_oasdiff", "true")]
    public async Task RealOasdiff_CleanFixture_Passes()
    {
        if (InstalledOasdiffVersion is null)
            return;

        var fixtureDir = await SeedOasdiffFixtureRepoAsync(breaking: false);
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

            var auditor = new OasdiffAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:BaseSpec"] = "base.yaml",
                    ["Scoped:Spec"] = "rev.yaml",
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

    private static FakeSandbox StandardSandbox(
        Func<SandboxExec, SandboxExecResult> onScan,
        Func<SandboxExec, SandboxExecResult>? onValidate = null)
        => new((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsValidateProbe(exec))
                return Task.FromResult(onValidate is null ? Ok(exec) : onValidate(exec));
            return Task.FromResult(onScan(exec));
        });

    private static async Task<OasdiffAuditor> CreateAuditorAsync()
    {
        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseSpec"] = DefaultBaseSpec,
                ["Scoped:Spec"] = DefaultSpec,
            }),
            CancellationToken.None);
        return auditor;
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.OasdiffAuditorPlugin.dll");
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
            PluginId: OasdiffAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Oasdiff OpenAPI Breaking Changes",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => new(0, "", "");

    private static SandboxExecResult VersionOk()
        => new(0, PinnedVersionBanner, "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("oasdiff", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2
            && exec.Argv[0] == "oasdiff"
            && exec.Argv[1] == "--version";

    private static bool IsFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("test -f", StringComparison.Ordinal);

    private static bool IsValidateProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "oasdiff"
            && exec.Argv[1] == "validate";

    private static async Task<string> SeedOasdiffFixtureRepoAsync(bool breaking)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-oasdiff-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        const string baseSpec = """
            openapi: 3.0.3
            info:
              title: Widgets API
              version: 1.0.0
            paths:
              /widgets:
                get:
                  operationId: listWidgets
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            type: object
            """;

        const string cleanSpec = """
            openapi: 3.0.3
            info:
              title: Widgets API
              version: 1.0.0
            paths:
              /widgets:
                get:
                  operationId: listWidgets
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            type: object
            """;

        const string breakingSpec = """
            openapi: 3.0.3
            info:
              title: Widgets API
              version: 1.0.0
            paths: {}
            """;

        await File.WriteAllTextAsync(Path.Combine(dir, "base.yaml"), baseSpec);
        await File.WriteAllTextAsync(Path.Combine(dir, "rev.yaml"), breaking ? breakingSpec : cleanSpec);

        return dir;
    }

    private static string? ProbeInstalledOasdiffVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "oasdiff",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 60_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            return process.ExitCode == 0 && output.Contains("oasdiff", StringComparison.Ordinal)
                ? "present"
                : null;
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
