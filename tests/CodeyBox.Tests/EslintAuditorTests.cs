using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.EslintAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the ESLint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming eslint (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
/// - Exit 1 without JSON output fails closed as an infrastructure failure.
/// - ESLint JSON output maps to findings with rule ids, locations, and mapped severity;
///   absolute filePath values are relativized against the scan root.
/// - Raw tool severities (numeric "1"/"2", "fatal") go through the declared mapping.
/// - Default exclusions (vendored + generated trees), --no-inline-config suppression posture,
///   and scoped options (ExpectedVersion, ConfigPath, TrustRepositorySuppression).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_eslint", "true")].
/// </summary>
public sealed class EslintAuditorTests
{
    private static readonly string? InstalledEslintVersion = ProbeInstalledEslintVersion();

    private const string JsonWithJsAndTsIssues = """
        {
          "results": [
          {
            "filePath": "/work/src/app.js",
            "messages": [
              {
                "ruleId": "no-unused-vars",
                "severity": 2,
                "message": "'unused' is assigned a value but never used.",
                "line": 3,
                "column": 7,
                "nodeType": "Identifier"
              },
              {
                "ruleId": "eqeqeq",
                "severity": 1,
                "message": "Expected '===' and instead saw '=='.",
                "line": 10,
                "column": 12,
                "nodeType": "BinaryExpression"
              }
            ],
            "errorCount": 1,
            "fatalErrorCount": 0,
            "warningCount": 1,
            "fixableErrorCount": 0,
            "fixableWarningCount": 0
          },
          {
            "filePath": "/work/src/widget.ts",
            "messages": [
              {
                "ruleId": "@typescript-eslint/no-explicit-any",
                "severity": 2,
                "message": "Unexpected any. Specify a different type.",
                "line": 5,
                "column": 14,
                "nodeType": "TSAnyKeyword"
              }
            ],
            "errorCount": 1,
            "fatalErrorCount": 0,
            "warningCount": 0,
            "fixableErrorCount": 0,
            "fixableWarningCount": 0
          }
          ],
          "metadata": { "cwd": "/work", "rulesMeta": {} }
        }
        """;

    private const string JsonClean = """
        { "results": [], "metadata": { "cwd": "/work", "rulesMeta": {} } }
        """;

    private const string JsonWithFatalAndFilteredPaths = """
        {
          "results": [
          {
            "filePath": "/work/src/broken.ts",
            "messages": [
              {
                "ruleId": null,
                "fatal": true,
                "severity": 2,
                "message": "Parsing error: Unexpected token }",
                "line": 8,
                "column": 1
              }
            ],
            "errorCount": 1,
            "fatalErrorCount": 1,
            "warningCount": 0,
            "fixableErrorCount": 0,
            "fixableWarningCount": 0
          },
          {
            "filePath": "/work/vendor/lib.js",
            "messages": [
              { "ruleId": "no-unused-vars", "severity": 2, "message": "'x' is defined but never used.", "line": 1, "column": 5 }
            ]
          },
          {
            "filePath": "/work/node_modules/pkg/index.js",
            "messages": [
              { "ruleId": "no-unused-vars", "severity": 2, "message": "'y' is defined but never used.", "line": 2, "column": 5 }
            ]
          },
          {
            "filePath": "/work/dist/bundle.js",
            "messages": [
              { "ruleId": "no-undef", "severity": 2, "message": "'z' is not defined.", "line": 4, "column": 1 }
            ]
          }
          ],
          "metadata": { "cwd": "/work", "rulesMeta": {} }
        }
        """;

    private const string JsonWithSeverities = """
        {
          "results": [
          {
            "filePath": "/work/src/a.js",
            "messages": [
              { "ruleId": "rule-error", "severity": 2, "message": "Error-level violation.", "line": 1, "column": 1 },
              { "ruleId": "rule-warn", "severity": 1, "message": "Warn-level violation.", "line": 2, "column": 1 },
              { "ruleId": null, "fatal": true, "severity": 2, "message": "Parsing error: Unexpected token.", "line": 3, "column": 1 },
              { "ruleId": "rule-unknown", "severity": 7, "message": "Unrecognised level.", "line": 4, "column": 1 },
              { "ruleId": "rule-noseverity", "message": "No severity field.", "line": 5, "column": 1 }
            ]
          }
          ],
          "metadata": { "cwd": "/work", "rulesMeta": {} }
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingEslint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "eslint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("eslint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingEslint()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("eslint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "v9.99.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("eslint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.99.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(EslintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new EslintAuditor();
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
    public async Task Fixture_WithJsAndTsIssues_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsAndTsIssues, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var jsFinding = Assert.Single(result.Findings, f => f.Title.Contains("no-unused-vars", StringComparison.Ordinal));
        Assert.Equal("codeybox:eslint", jsFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, jsFinding.Severity);
        // ESLint reports absolute filePath; the auditor relativizes to the repo root.
        Assert.Equal("src/app.js:3", jsFinding.Location);

        var warnFinding = Assert.Single(result.Findings, f => f.Title.Contains("eqeqeq", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warnFinding.Severity);
        Assert.Equal("src/app.js:10", warnFinding.Location);

        var tsFinding = Assert.Single(result.Findings, f => f.Title.Contains("@typescript-eslint/no-explicit-any", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, tsFinding.Severity);
        Assert.Equal("src/widget.ts:5", tsFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("eslint", scanExec!.Argv[0]);
        Assert.Contains("--format", scanExec.Argv);
        Assert.Contains("json-with-metadata", scanExec.Argv);
        Assert.Contains("--no-warn-ignored", scanExec.Argv);
        // Repo-authored inline suppression is inert by default.
        Assert.Contains("--no-inline-config", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsAndTsIssues, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // ESLint exits 2 on configuration/internal errors, e.g. no eslint.config.* in the repo.
            return Task.FromResult(new SandboxExecResult(2, "", "ESLint couldn't find an eslint.config.(js|mjs|cjs) file."));
        });

        IAuditor auditor = new EslintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("eslint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 with usage text rather than a JSON report fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(1, "", "Invalid option '--bogus'."));
        });

        IAuditor auditor = new EslintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("eslint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "eslint: command not found"));
        });

        IAuditor auditor = new EslintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("eslint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeverities, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(5, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);

        var fatal = Assert.Single(findings, f => f.Title.Contains("Parsing error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, fatal.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-noseverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> default
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
            s => s.PluginId == EslintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("eslint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresEslintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [EslintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == EslintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("eslint", tool.Binary);
        // Verify-only by design: eslint ships via npm, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("eslint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "v9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new EslintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "9.9.9",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new EslintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/eslint.config.operator.js",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/eslint.config.operator.js", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFatalAndFilteredPaths, ""));
        });

        IAuditor auditor = new EslintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by the default ExcludePaths;
        // the src/ fatal parse error survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/broken.ts:8", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsAndTsIssues, ""));
        });

        var auditor = new EslintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "eqeqeq",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("eqeqeq", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_RemovesNoInlineConfig()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new EslintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--no-inline-config", scanExec!.Argv);
    }

    [Fact]
    [Trait("requires_eslint", "true")]
    public async Task RealEslint_JsAndTsFixture_YieldsFindings()
    {
        var installed = InstalledEslintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedEslintFixtureRepoAsync(clean: false);

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

            var auditor = new EslintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);

            var jsFinding = Assert.Single(result.Findings, f => f.Title.Contains("no-unused-vars", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, jsFinding.Severity);
            Assert.Equal("bad.js:1", jsFinding.Location);

            // No TypeScript parser is configured: the .ts file surfaces as a
            // fatal parse error (rule id none), still an Error finding.
            var tsFinding = Assert.Single(result.Findings, f => f.Location == "bad.ts:1");
            Assert.Equal(AuditSeverity.Error, tsFinding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_eslint", "true")]
    public async Task RealEslint_CleanFixture_Passes()
    {
        var installed = InstalledEslintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedEslintFixtureRepoAsync(clean: true);

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

            var auditor = new EslintAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.EslintAuditorPlugin.dll");
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
            PluginId: EslintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ESLint JavaScript/TypeScript Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "v" + EslintAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("eslint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "eslint" && exec.Argv[1] == "--version";

    private static async Task<string> SeedEslintFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-eslint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // Flat config covering .js and .ts with no TypeScript parser: the .ts
        // file is a real fatal parse error, the .js file a rule violation.
        var config = """
            export default [
              {
                files: ["**/*.js", "**/*.ts"],
                rules: { "no-unused-vars": "error" }
              }
            ];
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "eslint.config.js"), config);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "clean.js"), "export const answer = 42;\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.js"), "const unused = 1;\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.ts"), "const x: number = 1;\n");
        }

        return dir;
    }

    private static string? ProbeInstalledEslintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "eslint",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
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
