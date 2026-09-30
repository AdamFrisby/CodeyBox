using System.Diagnostics;
using CodeyBox.BrakemanAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Brakeman auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), exits 0 and 3 with SARIF
/// results are the findings verdict while every other non-zero exit
/// (notably 7 for scan errors and 4 for a non-Rails tree) is
/// infrastructure, SARIF maps to findings with rule id and file/line,
/// severity goes through the declared mapping (both SARIF levels and
/// Brakeman's native High/Medium/Weak confidences) rather than passing
/// through, repo-authored ignore entries stay visible unless the operator
/// opts in, and the plugin is inert — unloaded and absent from baseline
/// provisioning — until an operator enables it. Every run is dispatched
/// through <see cref="IAuditor"/> so the version pin cannot be bypassed by
/// interface dispatch.
/// </summary>
public sealed class BrakemanAuditorTests
{
    // Mirrors what `brakeman --format sarif --output /dev/stdout` writes for
    // a SQL injection warning (shape verified against Brakeman 8.0.6
    // lib/brakeman/report/report_sarif.rb): each result carries its own
    // "level" inferred from the warning confidence (error for High), the
    // rule id in BRAKE%04d shape (illustrative code here), message text, and
    // the first physical location's repo-relative artifact uri plus
    // region.startLine.
    private const string SarifWithSqlInjection = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "Brakeman",
                "informationUri": "https://brakemanscanner.org",
                "semanticVersion": "8.0.6",
                "rules": [{
                  "id": "BRAKE0018",
                  "name": "SQL/SQLInjection",
                  "helpUri": "https://brakemanscanner.org/docs/warning_types/sql_injection/"
                }]
              }
            },
            "results": [{
              "ruleId": "BRAKE0018",
              "ruleIndex": 0,
              "level": "error",
              "message": { "text": "Possible SQL injection." },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "app/controllers/users_controller.rb", "uriBaseId": "%SRCROOT%" },
                  "region": { "startLine": 7 }
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
            "tool": { "driver": { "name": "Brakeman", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingBrakeman_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "brakeman: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BrakemanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("brakeman", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task SqlInjectionInFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(3, SarifWithSqlInjection, ""));
        });

        IAuditor auditor = new BrakemanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:brakeman", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("BRAKE0018", finding.Title, StringComparison.Ordinal);
        Assert.Equal("app/controllers/users_controller.rb:7", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("brakeman", argv[0]);
        Assert.Contains("--quiet", argv);
        Assert.Contains("--no-pager", argv);
        Assert.Contains("--format", argv);
        Assert.Contains("sarif", argv);
        var outputFlag = argv.ToList().IndexOf("--output");
        Assert.True(outputFlag >= 0 && outputFlag + 1 < argv.Count);
        Assert.Equal("/dev/stdout", argv[outputFlag + 1]);
        var pathFlag = argv.ToList().IndexOf("--path");
        Assert.True(pathFlag >= 0 && pathFlag + 1 < argv.Count);
        Assert.Equal(".", argv[pathFlag + 1]);
        Assert.Contains("--show-ignored", argv);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new BrakemanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WarningsExit_IsFindings_WhileErrorExits_AreInfrastructure()
    {
        // Brakeman exits 3 (Warnings_Found_Exit_Code) when warnings were
        // found — the SARIF document is still the verdict, so exit 3 with
        // results is findings.
        IAuditor auditor = new BrakemanAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(3, SarifWithSqlInjection),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // Exit 0 with results is findings too (e.g. ignored-only warnings
        // under --show-ignored, which do not soften the exit code).
        var foundOnZero = await auditor.RunAsync(
            HealthyTool(0, SarifWithSqlInjection),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(foundOnZero.Passed);
        Assert.Single(foundOnZero.Findings);

        // Exit 7 is Brakeman's scan-errors exit. Even with parseable SARIF
        // on stdout it means "could not run" — the scan did not complete.
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(7, SarifWithSqlInjection), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 7", errorEx.Message, StringComparison.Ordinal);

        // Exit 4 means no Rails application was detected: a scoping
        // problem, not a verdict on the diff.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(4, "No Rails application detected"), "/work", FakeContext(), CancellationToken.None));

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, "some other failure"), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // The fixture's error-level result maps to a blocking Error.
        IAuditor auditor = new BrakemanAuditor();
        var error = await auditor.RunAsync(
            HealthyTool(3, SarifWithSqlInjection),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);
        Assert.False(error.Passed);

        // A warning-level result is advisory: findings without a failed audit.
        var warning = await auditor.RunAsync(
            HealthyTool(3, WithResultLevel(SarifWithSqlInjection, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        var warningFinding = Assert.Single(warning.Findings);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.True(warning.Passed);

        // A note-level result is informational.
        var note = await auditor.RunAsync(
            HealthyTool(3, WithResultLevel(SarifWithSqlInjection, "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(note.Findings).Severity);
        Assert.True(note.Passed);

        // An unrecognised tool level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(3, WithResultLevel(SarifWithSqlInjection, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);
    }

    [Fact]
    public async Task NativeBrakemanConfidences_MapThroughDeclaredMapping()
    {
        // Brakeman's native vocabulary never reaches findings raw: High
        // blocks, Medium is advisory, Weak is informational.
        IAuditor auditor = new BrakemanAuditor();

        var high = await auditor.RunAsync(
            HealthyTool(3, WithResultLevel(SarifWithSqlInjection, "High")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(high.Findings).Severity);
        Assert.False(high.Passed);

        var medium = await auditor.RunAsync(
            HealthyTool(3, WithResultLevel(SarifWithSqlInjection, "Medium")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(medium.Findings).Severity);
        Assert.True(medium.Passed);

        var weak = await auditor.RunAsync(
            HealthyTool(3, WithResultLevel(SarifWithSqlInjection, "Weak")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(weak.Findings).Severity);
        Assert.True(weak.Passed);
    }

    [Fact]
    public async Task IgnoredWarnings_AreShown_ByDefault_AndOptIn()
    {
        SandboxExec? defaultExec = null;
        var defaultSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            defaultExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor defaultAuditor = new BrakemanAuditor();
        await defaultAuditor.RunAsync(defaultSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(defaultExec);
        Assert.Contains("--show-ignored", defaultExec!.Argv);

        SandboxExec? trustingExec = null;
        var trustingSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            trustingExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var trustingAuditor = new BrakemanAuditor();
        await trustingAuditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);
        await ((IAuditor)trustingAuditor).RunAsync(trustingSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(trustingExec);
        Assert.DoesNotContain("--show-ignored", trustingExec!.Argv);
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
                return Task.FromResult(new SandboxExecResult(0, "brakeman 6.0.0\n", ""));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BrakemanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("6.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(BrakemanAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
                    "brakeman " + BrakemanAuditor.DefaultExpectedVersion + "\n",
                    ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BrakemanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new BrakemanAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "7.0.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "brakeman 7.0.0\n", ""));
            return Task.FromResult(new SandboxExecResult(3, SarifWithSqlInjection, ""));
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

        IAuditor auditor = new BrakemanAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new BrakemanAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "BRAKE0018",
            }),
            CancellationToken.None);

        var vendored = SarifWithSqlInjection.Replace(
            "app/controllers/users_controller.rb", "vendor/pkg/users_controller.rb", StringComparison.Ordinal);
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(3, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace(
            "BRAKE0018", "BRAKE0000", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(3, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Fixture sources assembled at runtime so this test file does not itself
    // carry a scanner-detectable SQL-injection literal. The vulnerable
    // controller interpolates request input into a SQL string (Brakeman's
    // textbook high-confidence SQL injection); the clean controller uses the
    // parameterized hash form.
    private static readonly string _taint = "#{" + "params[:name]}";

    private static readonly string _fixtureVulnController =
        "class UsersController < ApplicationController\n"
        + "  def index\n"
        + "    @users = User.where(\"name = '" + _taint + "'\")\n"
        + "  end\n"
        + "end\n";

    private static readonly string _fixtureCleanController =
        "class UsersController < ApplicationController\n"
        + "  def index\n"
        + "    @users = User.where(name: " + "params[:name]" + ")\n"
        + "  end\n"
        + "end\n";

    private static readonly string _fixtureApplicationController =
        "class ApplicationController < ActionController::Base\n"
        + "end\n";

    private static readonly string _fixtureApplicationConfig =
        "require \"rails\"\n"
        + "module FixtureApp\n"
        + "  class Application < Rails::Application\n"
        + "  end\n"
        + "end\n";

    private static readonly string _fixtureRoutes =
        "Rails.application.routes.draw do\n"
        + "end\n";

    private static readonly string? _installedBrakemanVersion = ProbeInstalledBrakemanVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture Rails repository with a known
    /// SQL injection is scanned by the actual Brakeman CLI through a real
    /// process exec — exercising the invocation, the stdout SARIF sink, and
    /// the exit-3-with-findings convention together, so a broken real
    /// invocation cannot stay green. Runs only where a brakeman binary is on
    /// PATH; the auditor's version pin is set to the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_brakeman", "true")]
    public async Task RealBrakeman_SqlInjection_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedBrakemanVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            ("config/application.rb", _fixtureApplicationConfig),
            ("config/routes.rb", _fixtureRoutes),
            ("app/controllers/application_controller.rb", _fixtureApplicationController),
            ("app/controllers/users_controller.rb", _fixtureVulnController));
        try
        {
            var auditor = new BrakemanAuditor();
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
            Assert.NotEmpty(result.Findings);
            var finding = Assert.Single(
                result.Findings,
                f => f.Location is not null
                    && f.Location.StartsWith("app/controllers/users_controller.rb", StringComparison.Ordinal));
            Assert.Equal("codeybox:brakeman", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("BRAKE", finding.Title, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture Rails repository with no
    /// findings passes with zero findings — and the analysis exits 0.
    /// </summary>
    [Fact]
    [Trait("requires_brakeman", "true")]
    public async Task RealBrakeman_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedBrakemanVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            ("config/application.rb", _fixtureApplicationConfig),
            ("config/routes.rb", _fixtureRoutes),
            ("app/controllers/application_controller.rb", _fixtureApplicationController),
            ("app/controllers/users_controller.rb", _fixtureCleanController));
        try
        {
            var auditor = new BrakemanAuditor();
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
            s => s.PluginId == BrakemanAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("brakeman", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresBrakemanRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [BrakemanAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == BrakemanAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("brakeman", tool.Binary);
        // Verify-only by design: Brakeman ships as a Ruby gem, not a distro
        // package, so no apt line can carry the version pin — the baseline
        // verifies presence and the operator provisions the pinned release.
        // No install commands are emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("brakeman", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.BrakemanAuditorPlugin.dll");
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
            PluginId: BrakemanAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Brakeman Rails SAST",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static string WithResultLevel(string sarif, string level)
        => sarif.Replace(
            "\"level\": \"error\",",
            "\"level\": \"" + level + "\",",
            StringComparison.Ordinal);

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "brakeman " + BrakemanAuditor.DefaultExpectedVersion + "\n", "")
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
        => exec.Argv.Count == 2 && exec.Argv[0] == "brakeman" && exec.Argv[1] == "--version";

    private static async Task<string> SeedFixtureRepoAsync(params (string Name, string Content)[] files)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-brakeman-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(repo, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    private static string? ProbeInstalledBrakemanVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "brakeman",
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
            // Any failure means no usable brakeman on PATH — the gated tests
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
