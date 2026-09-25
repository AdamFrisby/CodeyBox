using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.LycheeAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the lychee auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming lychee (never a pass or finding).
/// - Exit codes 0 and 2 are verdicts (findings-producing); exit 1/3 and others are infrastructure.
/// - A verdict-class exit without lychee JSON output fails closed as an infrastructure failure.
/// - lychee JSON output maps error_map/timeout_map to findings with rule ids, locations, and
///   mapped severity; "./"-prefixed source paths are normalized to repo-relative.
/// - Raw tool levels ("error"/"timeout") go through the declared mapping.
/// - Default scope ("." input, --hidden, ExcludePaths → --exclude-path), the /dev/null config pin,
///   the .lycheeignore presence gate, and scoped options (ExpectedVersion, ConfigPath, Inputs,
///   RootDirectory, CheckRemoteLinks, TrustRepositorySuppression).
/// - Offline by default (AuditCapabilities.None); CheckRemoteLinks drops --offline and
///   declares Network.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_lychee", "true")].
/// </summary>
public sealed class LycheeAuditorTests
{
    private static readonly string? InstalledLycheeVersion = ProbeInstalledLycheeVersion();

    private const string JsonWithBrokenLinks = """
        {
          "total": 5,
          "unique": 5,
          "successful": 2,
          "unknown": 0,
          "unsupported": 0,
          "timeouts": 1,
          "redirects": 0,
          "remaps": 0,
          "excludes": 0,
          "errors": 2,
          "cached": 0,
          "success_map": {},
          "error_map": {
            "./docs/guide.md": [
              {
                "url": "https://example.com/missing",
                "status": { "text": "404 Not Found", "code": 404 },
                "span": { "line": 12, "column": 5 },
                "duration": { "secs": 0, "nanos": 1 }
              }
            ],
            "./README.md": [
              {
                "url": "./missing-file.md",
                "status": { "text": "Cannot find file", "details": "cannot find file" },
                "span": { "line": 3, "column": 10 }
              }
            ]
          },
          "timeout_map": {
            "./docs/guide.md": [
              {
                "url": "https://slow.example.com/",
                "status": { "text": "Timeout", "details": "Request timed out" },
                "span": { "line": 20, "column": 1 }
              }
            ]
          },
          "suggestion_map": {},
          "redirect_map": {},
          "excluded_map": {},
          "duration": { "secs": 1, "nanos": 0 },
          "detailed_stats": false
        }
        """;

    private const string JsonClean = """
        {
          "total": 3,
          "unique": 3,
          "successful": 3,
          "unknown": 0,
          "unsupported": 0,
          "timeouts": 0,
          "redirects": 0,
          "remaps": 0,
          "excludes": 0,
          "errors": 0,
          "cached": 0,
          "success_map": {},
          "error_map": {},
          "timeout_map": {},
          "suggestion_map": {},
          "redirect_map": {},
          "excluded_map": {},
          "duration": { "secs": 0, "nanos": 1 },
          "detailed_stats": false
        }
        """;

    private const string JsonWithFilteredPaths = """
        {
          "total": 4,
          "unique": 4,
          "successful": 1,
          "unknown": 0,
          "unsupported": 0,
          "timeouts": 0,
          "redirects": 0,
          "remaps": 0,
          "excludes": 0,
          "errors": 3,
          "cached": 0,
          "success_map": {},
          "error_map": {
            "./vendor/lib/README.md": [
              { "url": "https://example.com/a", "status": { "text": "404 Not Found", "code": 404 }, "span": { "line": 1, "column": 1 } }
            ],
            "./node_modules/pkg/README.md": [
              { "url": "https://example.com/b", "status": { "text": "404 Not Found", "code": 404 }, "span": { "line": 2, "column": 1 } }
            ],
            "./docs/guide.md": [
              { "url": "./gone.md", "status": { "text": "Cannot find file" }, "span": { "line": 7, "column": 2 } }
            ]
          },
          "timeout_map": {},
          "suggestion_map": {},
          "redirect_map": {},
          "excluded_map": {},
          "duration": { "secs": 0, "nanos": 1 },
          "detailed_stats": false
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingLychee_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "lychee: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingLychee()
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

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "lychee 0.99.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.99.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(LycheeAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new LycheeAuditor();
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
    public async Task Fixture_WithBrokenLinks_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(2, JsonWithBrokenLinks, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var remote404 = Assert.Single(result.Findings, f => f.Title.Contains("example.com/missing", StringComparison.Ordinal));
        Assert.Equal("codeybox:lychee", remote404.AuditorName);
        Assert.Equal(AuditSeverity.Error, remote404.Severity);
        Assert.Contains(LycheeAuditor.BrokenLinkRuleId, remote404.Title, StringComparison.Ordinal);
        // lychee's walker emits "./"-prefixed sources; the parser relativizes.
        Assert.Equal("docs/guide.md:12", remote404.Location);

        var missingFile = Assert.Single(result.Findings, f => f.Title.Contains("missing-file.md", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, missingFile.Severity);
        Assert.Equal("README.md:3", missingFile.Location);

        var timeout = Assert.Single(result.Findings, f => f.Title.Contains(LycheeAuditor.TimeoutRuleId, StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, timeout.Severity);
        Assert.Equal("docs/guide.md:20", timeout.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("lychee", scanExec!.Argv[0]);
        Assert.Contains("--format", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--no-progress", scanExec.Argv);
        Assert.Contains("--hidden", scanExec.Argv);
        Assert.Contains("--include-fragments", scanExec.Argv);
        // Offline by default — no network in the audit sandbox.
        Assert.Contains("--offline", scanExec.Argv);
        // Repo-authored lychee config is pinned out; /dev/null parses empty.
        Assert.Contains("--config", scanExec.Argv);
        Assert.Contains("/dev/null", scanExec.Argv);
        // Vendored/generated trees are excluded at crawl time too.
        Assert.Contains(@"^(?:\./)?vendor/", scanExec.Argv);
        Assert.Contains(@"^(?:\./)?\.git/", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_IsTheFoundSomethingExit_ReportsFindings_NotInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithBrokenLinks, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            // lychee exits 1 on missing inputs and runtime/configuration errors.
            return Task.FromResult(new SandboxExecResult(1, "", "Error: no input files found"));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode3_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            // lychee exits 3 on config-file errors.
            return Task.FromResult(new SandboxExecResult(3, "", "Error while loading config"));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerdictExit_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 2 with no JSON report fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(2, "", "internal error"));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "lychee: command not found"));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lychee", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithBrokenLinks, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // "error" (error_map) -> Error; "timeout" (timeout_map) -> Warning.
        // Raw lychee vocabulary never reaches the finding severity — it stays
        // visible only in the description's "Severity (tool):" line.
        Assert.Equal(2, result.Findings.Count(f => f.Severity == AuditSeverity.Error));
        var timeout = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.Contains("Severity (tool): timeout", timeout.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LycheeignorePresent_IsInfrastructureFailure_FailsClosed()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsIgnoreProbe(exec))
                // exit 0 = .lycheeignore exists at the repo root
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(".lycheeignore", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task TrustRepositorySuppression_SkipsIgnoreGate_AndDropsConfigPin()
    {
        SandboxExec? scanExec = null;
        var ignoreProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsIgnoreProbe(exec))
            {
                ignoreProbes++;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new LycheeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, ignoreProbes);
        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--config", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_OverridesPinnedConfig()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new LycheeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/lychee.toml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/lychee.toml", argv[configIndex + 1]);
        Assert.DoesNotContain("/dev/null", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_Inputs_ReplaceDefaultWholeTreeScope()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new LycheeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Inputs"] = "docs, README.md",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("docs", scanExec!.Argv[^2]);
        Assert.Equal("README.md", scanExec.Argv[^1]);
        Assert.DoesNotContain(".", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_CheckRemoteLinks_DropsOffline_AndRequiresNetwork()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new LycheeAuditor();
        Assert.Equal(AuditCapabilities.None, ((IAuditor)auditor).Required);

        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:CheckRemoteLinks"] = "true",
            }),
            CancellationToken.None);

        Assert.True(((IAuditor)auditor).Required.HasFlag(AuditCapabilities.Network));

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--offline", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsIgnoreProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new LycheeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/ and node_modules/ findings are dropped by the default
        // ExcludePaths; the docs/ broken link survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("docs/guide.md:7", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Theory]
    [InlineData("vendor/", @"^(?:\./)?vendor/")]
    [InlineData("foo.md", @"^(?:\./)?foo\.md$")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExcludePathPattern_TranslatesEntry_ToAnchoredRegex(
        string? entry, string? expected)
    {
        Assert.Equal(expected, LycheeAuditor.ToExcludePathPattern(entry));
    }

    [Fact]
    public void ExcludePathPattern_MatchesWalkerPaths_LikeBasePrefixSemantics()
    {
        var dirPattern = new Regex(LycheeAuditor.ToExcludePathPattern("vendor/")!);
        Assert.Matches(dirPattern, "./vendor/lib/README.md");
        Assert.Matches(dirPattern, "vendor/lib/README.md");
        Assert.DoesNotMatch(dirPattern, "./docs/vendor/lib.md");

        var filePattern = new Regex(LycheeAuditor.ToExcludePathPattern("CONTRIBUTING.md")!);
        Assert.Matches(filePattern, "./CONTRIBUTING.md");
        Assert.DoesNotMatch(filePattern, "./docs/CONTRIBUTING.md");

        // Metacharacters in config entries are data, not pattern text.
        var dotted = new Regex(LycheeAuditor.ToExcludePathPattern("a+b/")!);
        Assert.Matches(dotted, "./a+b/x.md");
        Assert.DoesNotMatch(dotted, "./ab/x.md");
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
            s => s.PluginId == LycheeAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("lychee", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresLycheeRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [LycheeAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == LycheeAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("lychee", tool.Binary);
        // Verify-only by design: no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("lychee", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_lychee", "true")]
    public async Task RealLychee_BrokenLocalLink_YieldsFinding()
    {
        var installed = InstalledLycheeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedLycheeFixtureRepoAsync(clean: false);

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

            var auditor = new LycheeAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings, f => f.Title.Contains("missing-target.md", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("bad.md:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_lychee", "true")]
    public async Task RealLychee_CleanFixture_Passes()
    {
        var installed = InstalledLycheeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedLycheeFixtureRepoAsync(clean: true);

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

            var auditor = new LycheeAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.LycheeAuditorPlugin.dll");
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
            PluginId: LycheeAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Lychee Broken Link Checker",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "lychee " + LycheeAuditor.DefaultExpectedVersion + "\n", "")
            : IsIgnoreProbe(exec)
                // exit 1 = .lycheeignore absent
                ? new SandboxExecResult(1, "", "")
                : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("lychee", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "lychee" && exec.Argv[1] == "--version";

    private static bool IsIgnoreProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv.Contains(".lycheeignore", StringComparer.Ordinal);

    private static async Task<string> SeedLycheeFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-lychee-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "ok.md"), "[existing](./target.md)\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "target.md"), "# Target\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "bad.md"), "[missing](./missing-target.md)\n");
        }

        return dir;
    }

    private static string? ProbeInstalledLycheeVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lychee",
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
