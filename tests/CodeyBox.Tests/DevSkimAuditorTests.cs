using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.DevSkimAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the DevSkim auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), exit 0 with SARIF results
/// is the findings verdict while every non-zero exit is infrastructure,
/// SARIF maps to findings with rule id and file/line, severity goes through
/// the declared mapping (both SARIF levels and DevSkim's native severities)
/// rather than passing through, repo-authored suppression is disabled unless
/// the operator opts in, and the plugin is inert — unloaded and absent from
/// baseline provisioning — until an operator enables it. Every run is
/// dispatched through <see cref="IAuditor"/> so the version pin cannot be
/// bypassed by interface dispatch.
/// </summary>
public sealed class DevSkimAuditorTests
{
    // Mirrors what `devskim analyze -I . -f sarif` writes for a weak-hash
    // match (verified against DevSkim 1.0.90): results carry a SARIF "level",
    // the rule id, message text, and the first physical location's
    // source-root-relative artifact uri plus region.startLine.
    private const string SarifWithWeakHash = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "devskim",
                "rules": [{
                  "id": "DS126858",
                  "name": "WeakbrokenHashAlgorithm",
                  "defaultConfiguration": { "level": "error" },
                  "properties": {
                    "DevSkimSeverity": "Critical",
                    "problem.severity": "error"
                  }
                }]
              }
            },
            "results": [{
              "ruleId": "DS126858",
              "level": "error",
              "message": { "text": "Weak/Broken Hash Algorithm" },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "vuln.py" },
                  "region": { "startLine": 4 }
                }
              }]
            }]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "devskim", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingDevskim_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "devskim: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new DevSkimAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("devskim", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task InsecureApiUsageInFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifWithWeakHash, ""));
        });

        IAuditor auditor = new DevSkimAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:devskim", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("DS126858", finding.Title, StringComparison.Ordinal);
        Assert.Equal("vuln.py:4", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("devskim", argv[0]);
        Assert.Equal("analyze", argv[1]);
        Assert.Contains("-I", argv);
        var sourceFlag = argv.ToList().IndexOf("-I");
        Assert.True(sourceFlag >= 0 && sourceFlag + 1 < argv.Count);
        Assert.Equal(".", argv[sourceFlag + 1]);
        Assert.Contains("-f", argv);
        Assert.Contains("sarif", argv);
        Assert.Contains("--disable-console", argv);
        Assert.Contains("--disable-supression", argv);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new DevSkimAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsRideExitZero_WhileNonZeroExits_AreInfrastructure()
    {
        // DevSkim exits 0 whether or not issues were produced — the SARIF
        // document is the verdict, so exit 0 with results is findings.
        IAuditor auditor = new DevSkimAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(0, SarifWithWeakHash),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // 254 is DevSkim's failure exit for an unreadable source path. Even
        // with parseable SARIF on stdout it means "could not run" — there is
        // no non-zero findings exit.
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(254, SarifWithWeakHash), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 254", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, "some other failure"), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // The fixture's error-level result maps to a blocking Error.
        IAuditor auditor = new DevSkimAuditor();
        var error = await auditor.RunAsync(
            HealthyTool(0, SarifWithWeakHash),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);
        Assert.False(error.Passed);

        // A warning-level result is advisory: findings without a failed audit.
        var warning = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        var warningFinding = Assert.Single(warning.Findings);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.True(warning.Passed);

        // A note-level result is informational.
        var note = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(note.Findings).Severity);
        Assert.True(note.Passed);

        // An unrecognised tool level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);
    }

    [Fact]
    public async Task NativeDevSkimSeverities_MapThroughDeclaredMapping()
    {
        // DevSkim's native vocabulary never reaches findings raw: Critical
        // and Important block, Moderate is advisory, BestPractice and
        // ManualReview are informational.
        IAuditor auditor = new DevSkimAuditor();

        var critical = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "Critical")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(critical.Findings).Severity);
        Assert.False(critical.Passed);

        var important = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "Important")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(important.Findings).Severity);
        Assert.False(important.Passed);

        var moderate = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "Moderate")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(moderate.Findings).Severity);
        Assert.True(moderate.Passed);

        var bestPractice = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "BestPractice")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(bestPractice.Findings).Severity);
        Assert.True(bestPractice.Passed);

        var manualReview = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithWeakHash, "ManualReview")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(manualReview.Findings).Severity);
        Assert.True(manualReview.Passed);
    }

    [Fact]
    public async Task RepositorySuppression_IsDisabled_ByDefault_AndOptIn()
    {
        SandboxExec? defaultExec = null;
        var defaultSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            defaultExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor defaultAuditor = new DevSkimAuditor();
        await defaultAuditor.RunAsync(defaultSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(defaultExec);
        Assert.Contains("--disable-supression", defaultExec!.Argv);

        SandboxExec? trustingExec = null;
        var trustingSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            trustingExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var trustingAuditor = new DevSkimAuditor();
        await trustingAuditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);
        await ((IAuditor)trustingAuditor).RunAsync(trustingSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(trustingExec);
        Assert.DoesNotContain("--disable-supression", trustingExec!.Argv);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "devskim 1.0.30+abc123\n", ""));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new DevSkimAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("1.0.30", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DevSkimAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task RealisticVersionOutput_IsAccepted()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0,
                    "devskim " + DevSkimAuditor.DefaultExpectedVersion + "+fb2d676ce4\n"
                        + "© Microsoft Corporation. All rights reserved.\n",
                    ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new DevSkimAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new DevSkimAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "1.0.70" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "devskim 1.0.70+abc123\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifWithWeakHash, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

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

        IAuditor auditor = new DevSkimAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new DevSkimAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "DS126858",
            }),
            CancellationToken.None);

        var vendored = SarifWithWeakHash.Replace("vuln.py", "vendor/pkg/vuln.py");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(0, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace(
            "DS126858", "DS000000", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(0, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Fixture source assembled at runtime so this test file does not itself
    // carry a scanner-detectable insecure-API literal. Verified against
    // DevSkim 1.0.90 default rules to produce DS126858 (weak/broken hash)
    // findings in vuln.py.
    private static readonly string _fixtureVulnPy =
        "import " + "hashlib\n"
        + "\n"
        + "def fingerprint(data):\n"
        + "    digest = " + "hashlib.md5" + "(data)\n"
        + "    return digest.hexdigest()\n";

    private static readonly string _fixtureCleanPy =
        "import " + "hashlib\n"
        + "\n"
        + "def fingerprint(data):\n"
        + "    digest = " + "hashlib.sha256" + "(data)\n"
        + "    return digest.hexdigest()\n";

    private static readonly string? _installedDevSkimVersion = ProbeInstalledDevSkimVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture repository with a known weak
    /// hash is scanned by the actual DevSkim CLI through a real process
    /// exec — exercising the analyze invocation, the stdout SARIF sink, and
    /// the exit-0-with-findings convention together, so a broken real
    /// invocation cannot stay green. Runs only where a devskim binary is on
    /// PATH; the auditor's version pin is set to the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_devskim", "true")]
    public async Task RealDevSkim_WeakHash_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedDevSkimVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(("vuln.py", _fixtureVulnPy));
        try
        {
            var auditor = new DevSkimAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("codeybox:devskim", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("DS126858", finding.Title, StringComparison.Ordinal);
            Assert.Equal("vuln.py:4", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture repository with no matches
    /// passes with zero findings — and the analysis still exits 0.
    /// </summary>
    [Fact]
    [Trait("requires_devskim", "true")]
    public async Task RealDevSkim_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedDevSkimVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(("clean.py", _fixtureCleanPy));
        try
        {
            var auditor = new DevSkimAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
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
            s => s.PluginId == DevSkimAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("devskim", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresDevskimRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [DevSkimAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == DevSkimAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("devskim", tool.Binary);
        // Verify-only by design: DevSkim ships as a .NET global tool, not a
        // distro package, so no apt line can carry the version pin — the
        // baseline verifies presence and the operator provisions the pinned
        // release. No install commands are emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("devskim", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.DevSkimAuditorPlugin.dll");
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
            PluginId: DevSkimAuditor.PluginId,
            PluginDisplayName: "CodeyBox: DevSkim Insecure API Usage",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static string WithResultLevel(string sarif, string level)
        => sarif.Replace(
            "\"level\": \"error\",",
            "\"level\": \"" + level + "\",",
            StringComparison.Ordinal);

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "devskim " + DevSkimAuditor.DefaultExpectedVersion + "+fb2d676ce4\n", "")
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
        => exec.Argv.Count == 2 && exec.Argv[0] == "devskim" && exec.Argv[1] == "--version";

    private static async Task<string> SeedFixtureRepoAsync((string Name, string Content) file)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-devskim-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(repo, file.Name), file.Content);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    private static string? ProbeInstalledDevSkimVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "devskim",
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
            if (process.ExitCode != 0)
                return null;
            var match = System.Text.RegularExpressions.Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return match.Success ? match.Value : null;
        }
        catch
        {
            // Any failure means no usable devskim on PATH — the gated tests
            // return early rather than fail on a host without the tool.
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
