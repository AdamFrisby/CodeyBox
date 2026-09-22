using CodeyBox.Core;
using CodeyBox.GitleaksAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the gitleaks auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), the findings exit code the
/// plugin assigns is a verdict while gitleaks's error exits are not, SARIF
/// maps to findings with rule id and file/line, severity goes through the
/// declared mapping rather than passing through, and the plugin is inert —
/// unloaded and absent from baseline provisioning — until an operator enables
/// it.
/// </summary>
public sealed class GitleaksAuditorTests
{
    // Mirrors what gitleaks v8.30.1 writes to stdout for `--report-format sarif
    // --report-path -`: no per-result "level" (the shared parser supplies
    // "warning"), ruleId, message text naming rule/file/commit, and the first
    // physical location's artifact uri plus region.startLine. The driver
    // semanticVersion is upstream's hardcoded "v8.0.0" — intentionally not the
    // release version, which is why the plugin probes `gitleaks version`.
    private const string SarifWithSecret = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "gitleaks",
                "semanticVersion": "v8.0.0",
                "informationUri": "https://github.com/gitleaks/gitleaks",
                "rules": [{ "id": "generic-api-key", "shortDescription": { "text": "Generic API Key" } }]
              }
            },
            "results": [{
              "message": { "text": "generic-api-key has detected secret for file src/config.py at commit 0123456789abcdef." },
              "ruleId": "generic-api-key",
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "src/config.py" },
                  "region": { "startLine": 12, "startColumn": 15, "endLine": 12, "endColumn": 50, "snippet": { "text": "***" } }
                }
              }],
              "partialFingerprints": { "commitSha": "0123456789abcdef", "email": "a@b.c", "author": "a", "date": "2026-01-01", "commitMessage": "x" },
              "properties": { "tags": [] }
            }]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "gitleaks", "semanticVersion": "v8.0.0", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGitleaks_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "gitleaks: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new GitleaksAuditor().RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gitleaks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SecretInSourceAndHistory_YieldsFinding_WithRuleIdAndLocation()
    {
        IReadOnlyList<string>? scanArgv = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanArgv = exec.Argv;
            return Task.FromResult(new SandboxExecResult(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        var result = await new GitleaksAuditor().RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:gitleaks", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("generic-api-key", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/config.py:12", finding.Location);

        Assert.NotNull(scanArgv);
        Assert.Equal("gitleaks", scanArgv![0]);
        Assert.Equal("git", scanArgv[1]);
        Assert.Contains("--report-format", scanArgv);
        Assert.Contains("sarif", scanArgv);
        Assert.Contains("--report-path", scanArgv);
        Assert.Contains("-", scanArgv);
        Assert.Contains("--redact=100", scanArgv);
        var exitFlag = scanArgv.ToList().IndexOf("--exit-code");
        Assert.True(exitFlag >= 0 && exitFlag + 1 < scanArgv.Count);
        Assert.Equal(
            GitleaksAuditor.LeaksFoundExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scanArgv[exitFlag + 1]);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        var result = await new GitleaksAuditor().RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsExit_IsVerdict_WhileErrorExits_AreInfrastructure()
    {
        // The plugin-assigned findings exit produces a verdict.
        var found = await new GitleaksAuditor().RunAsync(
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // gitleaks's fatal/error exit is 1 — shared with the *default* findings
        // code, which is exactly why the plugin moves findings to 4. Even with
        // parseable SARIF on stdout, exit 1 means "could not run".
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new GitleaksAuditor().RunAsync(
                HealthyTool(1, SarifWithSecret), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new GitleaksAuditor().RunAsync(
                HealthyTool(2, "usage: gitleaks ..."), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // A fabricated "note" level would map to Info under the shared default
        // map; this auditor declares every gitleaks result a blocking Error.
        var noteSarif = SarifWithSecret.Replace(
            "\"ruleId\": \"generic-api-key\"",
            "\"level\": \"note\", \"ruleId\": \"generic-api-key\"",
            StringComparison.Ordinal);
        var result = await new GitleaksAuditor().RunAsync(
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, noteSarif),
            "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "8.16.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new GitleaksAuditor().RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("8.16.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GitleaksAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new GitleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "8.26.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "8.26.0\n", ""));
            return Task.FromResult(new SandboxExecResult(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task UnparseableVersionOutput_IsInfrastructure_NotAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "dev-build\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new GitleaksAuditor().RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new GitleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = GitleaksAuditor.DefaultExpectedVersion,
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "generic-api-key",
                ["Scoped:MinimumSeverity"] = "warning",
            }),
            CancellationToken.None);

        var vendored = SarifWithSecret.Replace("src/config.py", "vendor/pkg/config.py");
        var kept = await auditor.RunAsync(
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("generic-api-key", "other-rule", StringComparison.Ordinal);
        var dropped = await auditor.RunAsync(
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
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
            s => s.PluginId == GitleaksAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("gitleaks", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGitleaksRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GitleaksAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GitleaksAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("gitleaks", tool.Binary);
        // Verify-only by design: the distro package cannot carry the version
        // pin, so the baseline verifies presence and the operator provisions
        // the pinned release. No apt line is emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("gitleaks", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GitleaksAuditorPlugin.dll");
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
            PluginId: GitleaksAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Gitleaks Secrets",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, GitleaksAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "gitleaks" && exec.Argv[1] == "version";

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
