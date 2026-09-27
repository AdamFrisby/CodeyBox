using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PipAuditAuditorPlugin;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the pip-audit auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming pip-audit (never a pass or finding).
/// - pip-audit exits 1 both when vulnerabilities are found and when the run
///   fails outright (_fatal) — the JSON manifest on stdout is the
///   discriminator between "ran" and "could not run".
/// - Both report shapes parse (the current {"dependencies":[…]} object and
///   the legacy bare array); advisories map to findings with their bare
///   vulnerability id as the rule id and the package context in the message.
/// - pip-audit emits no per-vulnerability severity, so ungraded advisories
///   take the declared Error default; explicit severity tokens go through
///   the declared mapping, never raw.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - No live-binary tests: every pip-audit run queries the vulnerability
///   service over the network, so all coverage is through FakeSandbox with
///   fixture reports (deterministic, no live-network dependence).
/// </summary>
public sealed class PipAuditAuditorTests
{
    private const string ReportWithFindings =
        """
        {
          "dependencies": [
            {
              "name": "django",
              "version": "1.2",
              "vulns": [
                {
                  "id": "PYSEC-2019-13",
                  "fix_versions": ["1.3"],
                  "aliases": ["CVE-2019-12345"],
                  "description": "Django before 1.3 has a cross-site scripting issue."
                },
                {
                  "id": "CVE-2020-99999",
                  "fix_versions": [],
                  "aliases": [],
                  "description": "Another issue with no fix released."
                }
              ]
            },
            {"name": "pip", "version": "24.0", "vulns": []},
            {"name": "unresolvable", "skip_reason": "Could not resolve"}
          ],
          "fixes": []
        }
        """;

    private const string ReportClean =
        """
        {
          "dependencies": [
            {"name": "pip", "version": "24.0", "vulns": []}
          ],
          "fixes": []
        }
        """;

    private const string LegacyArrayReport =
        """
        [
          {
            "name": "flask",
            "version": "0.5",
            "vulns": [
              {"id": "PYSEC-2019-179", "fix_versions": ["1.0"], "aliases": ["CVE-2019-1010083"]}
            ]
          }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingPipAudit_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "pip-audit: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "pip-audit 2.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PipAuditAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithVulnerabilities_YieldsFindings_WithRuleId()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // pip-audit exits 1 when vulnerabilities are found.
            return Task.FromResult(new SandboxExecResult(
                1, ReportWithFindings, "Found 2 known vulnerabilities in 1 package"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var pysec = Assert.Single(
            result.Findings, f => f.Title.Contains("PYSEC-2019-13", StringComparison.Ordinal));
        Assert.Equal("codeybox:pip-audit", pysec.AuditorName);
        Assert.Equal(AuditSeverity.Error, pysec.Severity);
        Assert.Contains("django", pysec.Description, StringComparison.Ordinal);
        Assert.Contains("1.2", pysec.Description, StringComparison.Ordinal);
        Assert.Contains("CVE-2019-12345", pysec.Description, StringComparison.Ordinal);
        Assert.Contains("1.3", pysec.Description, StringComparison.Ordinal);

        Assert.Single(result.Findings, f => f.Title.Contains("CVE-2020-99999", StringComparison.Ordinal));

        Assert.NotNull(scanExec);
        Assert.Equal("pip-audit", scanExec!.Argv[0]);
        Assert.NotEqual("sh", scanExec.Argv[0]);
        Assert.Contains("--format", scanExec.Argv, StringComparer.Ordinal);
        Assert.Contains("json", scanExec.Argv, StringComparer.Ordinal);
        var requirementIndex = scanExec.Argv.ToList().IndexOf("-r");
        Assert.True(requirementIndex >= 0
            && scanExec.Argv[requirementIndex + 1] == PipAuditAuditor.DefaultRequirements);
    }

    [Fact]
    public async Task LegacyArrayReport_Parses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, LegacyArrayReport, "Found 1 known vulnerability in 1 package"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("PYSEC-2019-179", finding.Title, StringComparison.Ordinal);
        Assert.Contains("flask", finding.Description, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean, "No known vulnerabilities found"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        // pip-audit's _fatal paths (unresolvable input, unreachable
        // service, strict-mode skip) also exit 1 but print no JSON manifest
        // — no manifest means "could not run".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "ERROR: invalid requirements input: requirements.txt"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageError_Exit2_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                2, "", "pip-audit: error: --locked flag can only be used with a project path"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "pip-audit: command not found"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_NoRawStringsPassedThrough()
    {
        const string report =
            """
            {"dependencies": [
              {"name": "a", "version": "1.0", "vulns": [{"id": "PYSEC-1", "fix_versions": [], "description": "x", "severity": "high"}]},
              {"name": "b", "version": "1.0", "vulns": [{"id": "PYSEC-2", "fix_versions": [], "description": "x", "severity": "medium"}]},
              {"name": "c", "version": "1.0", "vulns": [{"id": "PYSEC-3", "fix_versions": [], "description": "x", "severity": "low"}]},
              {"name": "d", "version": "1.0", "vulns": [{"id": "PYSEC-4", "fix_versions": [], "description": "x", "severity": "future-level"}]},
              {"name": "e", "version": "1.0", "vulns": [{"id": "PYSEC-5", "fix_versions": [], "description": "x"}]}
            ]}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, report, "Found 5 known vulnerabilities"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-1")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-2")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-3")).Severity);
        // Unknown tool level falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-4")).Severity);
        // pip-audit reports no severity today: ungraded CVEs fail the audit.
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-5")).Severity);
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping.
        var high = Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-1"));
        Assert.Contains("Severity (tool): high", high.Description, StringComparison.Ordinal);
        var ungraded = Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-5"));
        Assert.Contains("Severity (tool): (none)", ungraded.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkippedDependency_YieldsNoFindings_AndPasses()
    {
        const string report =
            """
            {"dependencies": [{"name": "unresolvable", "skip_reason": "Could not resolve"}]}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, report, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ScopedConfiguration_Requirements_Service_Local_IgnoreVulns_ShapeTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Requirements"] = "req/base.txt, req/extra.txt",
                ["Scoped:VulnerabilityService"] = "OSV",
                ["Scoped:Local"] = "true",
                ["Scoped:IgnoreVulns"] = "PYSEC-2019-13, CVE-2020-99999",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var serviceIndex = argv.IndexOf("--vulnerability-service");
        Assert.True(serviceIndex >= 0 && argv[serviceIndex + 1] == "osv");
        Assert.Contains("--local", argv, StringComparer.Ordinal);
        Assert.Equal(2, argv.Count(a => a == "--ignore-vuln"));
        Assert.Contains("PYSEC-2019-13", argv, StringComparer.Ordinal);
        var firstRequirement = argv.IndexOf("-r");
        Assert.True(firstRequirement >= 0 && argv[firstRequirement + 1] == "req/base.txt");
        Assert.Contains("req/extra.txt", argv, StringComparer.Ordinal);
        Assert.DoesNotContain(PipAuditAuditor.DefaultRequirements, argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ProjectPath_UsedWhenRequirementsUnset()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = "backend",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("backend", scanExec!.Argv, StringComparer.Ordinal);
        Assert.DoesNotContain("-r", scanExec.Argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_InvalidService_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:VulnerabilityService"] = "bogus",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("VulnerabilityService", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithFindings, "Found 2 known vulnerabilities"));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "PYSEC-2019-13,CVE-2020-99999",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsInfos()
    {
        const string report =
            """
            {"dependencies": [
              {"name": "c", "version": "1.0", "vulns": [{"id": "PYSEC-3", "fix_versions": [], "description": "x", "severity": "low"}]}
            ]}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, report, "Found 1 known vulnerability"));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "warning",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
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
            s => s.PluginId == PipAuditAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("pip-audit", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PipAuditAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PipAuditAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var pipAudit = Assert.Single(tools);
        Assert.Equal("pip-audit", pipAudit.Binary);
        // Verify-only by design: no distro package carries a version-pinned
        // pip-audit — the operator provisions the pinned release into the
        // baseline via pip.
        Assert.Null(pipAudit.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("pip-audit", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PipAuditAuditorPlugin.dll");
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
            PluginId: PipAuditAuditor.PluginId,
            PluginDisplayName: "CodeyBox: pip-audit Python Dependency CVEs",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "pip-audit " + PipAuditAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("pip-audit", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "pip-audit" && exec.Argv[1] == "--version";

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
