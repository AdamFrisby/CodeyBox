using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.CueAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the CUE schema-validation auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cue (never a pass or finding).
/// - A missing SchemaPaths configuration, registry/URL/directory/pattern operands, and flag-like
///   ExtraArguments are deterministic misconfigurations — the scan never runs.
/// - Configured operands are verified present and nonempty before the scan; absence or emptiness is
///   infrastructure, never a pass.
/// - Exits 0 and 1 are findings-producing; exit 1 without a parseable diagnostic (empty output,
///   tool-failure text such as an unresolved import) fails closed as infrastructure — the diagnostic
///   blocks, not the exit code, are the discriminator.
/// - cue vet diagnostics map to blocking findings with the synthesized cue/validation rule id and
///   exact file/line locations; the tool has no severity vocabulary.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_cue", "true")] use fixture-local schema and
///   data files, so they need the binary but no network.
/// </summary>
public sealed class CueAuditorTests
{
    private static readonly string? InstalledCueVersion = ProbeInstalledCueVersion();

    private const string SchemaOperand = "schema/svc.cue";
    private const string InputOperand = "data/bad.yaml";

    private const string StderrViolation = """
        replicas: invalid value 99 (out of bound <=5):
            ./schema/svc.cue:5:24
            ./data/bad.yaml:2:11
        """;

    private const string StderrIncomplete = """
        replicas: incomplete value >=1 & <=5 & int
        """;

    private const string StderrSyntaxError = """
        ./data/broken.yaml:1: did not find expected ',' or ']'
        """;

    private const string StderrImportFailure = """
        import failed: imports are unavailable because there is no cue.mod/module.cue file:
            ./schema/imports.cue:3:8
        """;

    private const string StderrMissingFile = """
        stat schema/nonexistent.cue: no such file or directory
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCue_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "cue: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cue", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionMismatch_IsInfrastructureFailure_NamingCue()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "cue version v0.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cue", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
                ["Scoped:SchemaPaths"] = SchemaOperand,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingSchemaPaths_IsMisconfigured_NeverScans()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        // No InitializeAsync: default options carry no SchemaPaths.
        IAuditor auditor = new CueAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("SchemaPaths", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("cue.dev/x/githubactions@latest")]
    [InlineData("https://example.com/schema.cue")]
    [InlineData("http://example.com/schema.cue")]
    [InlineData("./...")]
    [InlineData("schema/")]
    [InlineData("/abs/schema.cue")]
    [InlineData("../outside/schema.cue")]
    [InlineData("schema/notes.txt")]
    public async Task RegistryUrlPatternOrWrongExtensionSchema_IsRejected_NeverScans(string schema)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = schema,
                ["Scoped:InputPaths"] = InputOperand,
            }),
            CancellationToken.None);

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("data/payload.exe")]
    [InlineData("https://example.com/data.yaml")]
    [InlineData("/abs/data.yaml")]
    public async Task BadInputOperand_IsRejected_NeverScans(string input)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = SchemaOperand,
                ["Scoped:InputPaths"] = input,
            }),
            CancellationToken.None);

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("-i")]
    [InlineData("--ignore")]
    [InlineData("-t")]
    [InlineData("--inject")]
    [InlineData("-d")]
    [InlineData("--schema")]
    [InlineData("-C")]
    [InlineData("--chdir")]
    [InlineData("-E")]
    public async Task FlagLikeExtraArguments_AreRejected_NeverScans(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = SchemaOperand,
                ["Scoped:InputPaths"] = InputOperand,
                ["Scoped:ExtraArguments"] = flag,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingConfiguredFile_IsInfrastructure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsNonEmptyProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsRepoPresenceProbe(exec))
                // The audited tree drifted under the configuration: nothing is present.
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("not found in the audited worktree", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task EmptyOperand_IsInfrastructure_NeverASilentPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsNonEmptyProbe(exec))
                // A configured operand exists but is empty: it would validate vacuously.
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ViolationFixture_YieldsFinding_WithPathAndLine_AndFailsAudit()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, "", StderrViolation));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // A CUE diagnostic is blocking: reported as an error, and the audit fails.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:cue", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("cue/validation", finding.Title, StringComparison.Ordinal);
        Assert.Contains("invalid value 99", finding.Title, StringComparison.Ordinal);
        // The location prefers the validated data over the schema constraint.
        Assert.Equal("data/bad.yaml:2", finding.Location);
        Assert.Contains("invalid value 99", finding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("cue", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("vet", argv);
        Assert.Contains("-c", argv);
        // Operands travel after the `--` separator: schemas first, then inputs.
        var separator = argv.ToList().IndexOf("--");
        Assert.True(separator >= 0);
        var schemaIndex = argv.ToList().IndexOf(SchemaOperand);
        var inputIndex = argv.ToList().IndexOf(InputOperand);
        Assert.True(schemaIndex > separator && inputIndex > schemaIndex);
        Assert.DoesNotContain("-d", argv);
    }

    [Fact]
    public async Task SchemaExpression_BecomesDashDArgument()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = SchemaOperand,
                ["Scoped:InputPaths"] = InputOperand,
                ["Scoped:SchemaExpression"] = "#Service",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var schemaFlag = argv.ToList().IndexOf("-d");
        Assert.True(schemaFlag >= 0 && argv[schemaFlag + 1] == "#Service");
    }

    [Fact]
    public async Task CleanSilentExit_Passes()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitOneWithEmptyOutput_IsInfrastructure_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Theory]
    [InlineData(StderrImportFailure)]
    [InlineData(StderrMissingFile)]
    public async Task ToolFailureDiagnostics_AreInfrastructure_NeverFindings(string stderr)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", stderr));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyntaxErrorDiagnostic_YieldsFinding_WithMessagePrefixLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", StderrSyntaxError));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = SchemaOperand,
                ["Scoped:InputPaths"] = "data/broken.yaml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("data/broken.yaml:1", finding.Location);
    }

    [Fact]
    public async Task LocationlessDiagnostic_YieldsPathlessFinding_AndStillFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", StderrIncomplete));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.True(string.IsNullOrEmpty(finding.Location));
        Assert.Contains("incomplete value", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedDiagnosticOutput_IsBoundedByMaxFindings()
    {
        var block = "replicas: invalid value 99 (out of bound <=5):\n    ./data/bad.yaml:2:11\n\n";
        var stderr = string.Concat(Enumerable.Repeat(block, 1500));
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", stderr));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        // The base MaxFindings default (1000) caps the reported findings.
        Assert.Equal(1000, result.Findings.Count);
    }

    [Fact]
    public async Task ExcludedRules_DropsCueValidation_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", StderrViolation));
        });

        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = SchemaOperand,
                ["Scoped:InputPaths"] = InputOperand,
                ["Scoped:ExcludedRules"] = "cue/validation",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Parser_ToolFailureMarkers_ThrowParseException()
    {
        var parser = new CueVetOutputParser();
        foreach (var marker in CueVetOutputParser.ToolFailureMarkers)
        {
            var input = new ExternalToolParseInput("cue", "", $"something {marker} happened", 1);
            Assert.Throws<ExternalToolParseException>(() => parser.Parse(input));
        }
    }

    [Fact]
    public void Parser_PrefersDataReference_OverSchemaReference()
    {
        var parser = new CueVetOutputParser();
        var findings = parser.Parse(new ExternalToolParseInput("cue", "", StderrViolation, 1));

        var finding = Assert.Single(findings);
        Assert.Equal("cue/validation", finding.RuleId);
        Assert.Equal("error", finding.SeverityLevel);
        Assert.Equal("data/bad.yaml", finding.Path);
        Assert.Equal(2, finding.Line);
    }

    [Fact]
    public void Parser_SilentSuccess_ReturnsNoFindings()
    {
        var parser = new CueVetOutputParser();
        var findings = parser.Parse(new ExternalToolParseInput("cue", "", "", 0));

        Assert.Empty(findings);
    }

    [Fact]
    public void Parser_LongMessage_IsBounded()
    {
        var parser = new CueVetOutputParser();
        var message = new string('x', 6000);
        var findings = parser.Parse(new ExternalToolParseInput("cue", "", message, 1));

        var finding = Assert.Single(findings);
        Assert.True(finding.Message.Length <= 4000);
    }

    [Fact]
    public void Parser_MessageShapedLikeLocation_IsNotMisread()
    {
        var parser = new CueVetOutputParser();
        var findings = parser.Parse(
            new ExternalToolParseInput("cue", "", "port: invalid value 808080: out of range", 1));

        var finding = Assert.Single(findings);
        Assert.Null(finding.Path);
        Assert.Null(finding.Line);
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
            s => s.PluginId == CueAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cue", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCueRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CueAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CueAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("cue", tool.Binary);
        // Verify-only by design: no distro package carries cue, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("cue", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_cue", "true")]
    public async Task RealCue_ViolationFixture_ProducesFinding_WithPathAndLine()
    {
        var installed = InstalledCueVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCueFixtureRepoAsync();

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

            var auditor = new CueAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:SchemaPaths"] = "schema/svc.cue",
                    ["Scoped:InputPaths"] = "data/bad.yaml",
                    ["Scoped:SchemaExpression"] = "#Service",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("cue/validation", finding.Title, StringComparison.Ordinal);
            Assert.Equal("data/bad.yaml:2", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cue", "true")]
    public async Task RealCue_CleanFixture_Passes()
    {
        var installed = InstalledCueVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCueFixtureRepoAsync();

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

            var auditor = new CueAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:SchemaPaths"] = "schema/svc.cue",
                    ["Scoped:InputPaths"] = "data/good.yaml",
                    ["Scoped:SchemaExpression"] = "#Service",
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

    private static async Task<CueAuditor> BuildAuditorAsync()
    {
        var auditor = new CueAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaPaths"] = SchemaOperand,
                ["Scoped:InputPaths"] = InputOperand,
            }),
            CancellationToken.None);
        return auditor;
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CueAuditorPlugin.dll");
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
            PluginId: CueAuditor.PluginId,
            PluginDisplayName: "CodeyBox: CUE Schema Validation",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            // cue version prints the tool release first, then the language
            // and Go versions: the shared first-token extraction must pin
            // the tool release, not the Go toolchain.
            return new SandboxExecResult(0, $"cue version v{CueAuditor.DefaultExpectedVersion}\n", "");
        if (IsRepoPresenceProbe(exec))
            return new SandboxExecResult(0, string.Join("\n", exec.Argv.Skip(4)), "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cue", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cue" && exec.Argv[1] == "version";

    private static bool IsRepoPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("printf '%s", StringComparison.Ordinal);

    private static bool IsNonEmptyProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("test -s", StringComparison.Ordinal);

    private static bool IsRepoProbe(SandboxExec exec)
        => IsRepoPresenceProbe(exec) || IsNonEmptyProbe(exec);

    private static async Task<string> SeedCueFixtureRepoAsync()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-cue-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "schema"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));

        // A hermetic single-file constraint: no imports, no modules, no network.
        var schema = """
            package svc

            #Service: {
            	name:     string
            	replicas: int & >=1 & <=5
            	image:    string
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "schema", "svc.cue"), schema);

        var good = """
            name: web
            replicas: 3
            image: nginx:1.27
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "data", "good.yaml"), good);

        var bad = """
            name: web
            replicas: 99
            image: nginx:1.27
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "data", "bad.yaml"), bad);

        return dir;
    }

    private static string? ProbeInstalledCueVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cue",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
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
