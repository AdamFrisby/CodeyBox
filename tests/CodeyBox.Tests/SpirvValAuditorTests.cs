using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.GlslangAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the SPIR-V module auditor in the shared shader-validation family:
/// - Module selection is explicit and fail-closed (binary-only extension
///   gate, containment, dedupe, empty/overlarge sets; never a discovery
///   walk, never an operand-less stdin run).
/// - The target environment resolves through the shared family seam and is
///   always passed explicitly; the tool is the environment oracle.
/// - The two-component SPIRV-Tools release token extracts on both sides of
///   the version gate (the shared three-component extraction cannot
///   represent it).
/// - spirv-val diagnostics map to findings on the validated module with the
///   validator's instruction position kept in the message and no fabricated
///   source line; warnings are advisory.
/// - Tool-operation failures, exits without diagnostics, unknown exits,
///   missing/empty/non-module/oversize inputs, and flag-like
///   ExtraArguments never produce a verdict.
/// - Each module gets its own single-file invocation; any unverifiable
///   module fails the whole run (partial coverage is never a pass).
/// - The plugin is disabled by default, absent from baseline provisioning
///   until enabled, and coexists with the glslang plugin in its assembly.
/// - Real binary execution tests under [Trait("requires_spirv", "true")]
///   use embedded fixture modules, so they need the binary but no network.
/// </summary>
public sealed class SpirvValAuditorTests
{
    private static readonly string? InstalledSpirvValVersion = ProbeInstalledSpirvValVersion();

    private const string ModuleTarget = "shaders/fx.spv";
    private const string SecondModuleTarget = "shaders/post.spv";

    private const string StderrEntryPointError =
        "error: line 4: 2 Entry points cannot share the same name and ExecutionMode.\n"
        + "  OpEntryPoint GLCompute %1 \"main\"\n";

    private const string StderrWarning =
        "warning: line 2: Consider using a newer target environment.\n";

    private const string StderrMagicError =
        "error: line 0: Invalid SPIR-V magic number.\n";

    private const string StderrMissingFile =
        "error: file does not exist 'shaders/gone.spv'\n";

    private const string StderrBadEnvironment =
        "error: Unrecognized target env: vulkan9.9\n";

    private const string StderrMultiFile =
        "error: More than one input file specified\n";

    private const string VersionBanner =
        "SPIRV-Tools v2025.1 unknown hash, 2026-05-13T06:03:38+00:00\n"
        + "Targets:\n"
        + "  SPIR-V 1.0\n";

    [Fact]
    public void ResolveModules_ExplicitEntries_DedupedInOrder()
    {
        var set = SpirvValidationSupport.ResolveModules(
            [ModuleTarget, SecondModuleTarget, ModuleTarget],
            SpirvValAuditor.PluginId,
            SpirvValAuditor.SpirvTargetsKey);

        Assert.Equal([ModuleTarget, SecondModuleTarget], set.Modules);
    }

    [Fact]
    public void ResolveModules_BlankEntriesSkipped()
    {
        var set = SpirvValidationSupport.ResolveModules(
            ["  ", ModuleTarget],
            SpirvValAuditor.PluginId,
            SpirvValAuditor.SpirvTargetsKey);

        Assert.Equal([ModuleTarget], set.Modules);
    }

    [Fact]
    public void ResolveModules_EmptySet_IsDeterministicMisconfiguration()
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => SpirvValidationSupport.ResolveModules(
            [], SpirvValAuditor.PluginId, SpirvValAuditor.SpirvTargetsKey));
        Assert.True(ex.IsDeterministic);
        Assert.Contains(SpirvValAuditor.SpirvTargetsKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shaders/effect.vert")]
    [InlineData("shaders/effect.frag")]
    [InlineData("shaders/common.glsl")]
    [InlineData("shaders/effect.hlsl")]
    [InlineData("shaders/unity.shader")]
    [InlineData("shaders/limits.conf")]
    [InlineData("shaders/noextension")]
    [InlineData("shaders/fx.spv:bogus")]
    public void ResolveModules_NonBinaryEntries_AreRejected(string entry)
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => SpirvValidationSupport.ResolveModules(
            [entry], SpirvValAuditor.PluginId, SpirvValAuditor.SpirvTargetsKey));
        Assert.True(ex.IsDeterministic);
    }

    [Theory]
    [InlineData("/abs/fx.spv")]
    [InlineData("../escape.spv")]
    [InlineData("a/../../escape.spv")]
    [InlineData("-fx.spv")]
    public void ResolveModules_UncontainedOrFlagShapedEntries_FailClosed(string entry)
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => SpirvValidationSupport.ResolveModules(
            [entry], SpirvValAuditor.PluginId, SpirvValAuditor.SpirvTargetsKey));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void ResolveModules_TooManyEntries_FailsClosed()
    {
        var entries = Enumerable.Range(0, ShaderValidationSupport.MaxTargets + 1)
            .Select(i => $"shaders/m{i}.spv")
            .ToList();
        var ex = Assert.Throws<AuditUnavailableException>(() => SpirvValidationSupport.ResolveModules(
            entries, SpirvValAuditor.PluginId, SpirvValAuditor.SpirvTargetsKey));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void ContainModulePath_BackslashSeparators_NormalizeInsideWorktree()
    {
        Assert.Equal(
            "shaders/fx.spv",
            SpirvValidationSupport.ContainModulePath(
                @"shaders\fx.spv", $"{SpirvValAuditor.PluginId}:{SpirvValAuditor.SpirvTargetsKey}"));
    }

    [Fact]
    public void ExtractSpirvToolsVersion_Banner_YieldsTwoComponentRelease()
    {
        Assert.Equal("2025.1", SpirvValidationSupport.ExtractSpirvToolsVersion(VersionBanner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a version string")]
    [InlineData("built 2026-05-13")]
    public void ExtractSpirvToolsVersion_WithoutReleaseToken_IsNull(string? output)
    {
        Assert.Null(SpirvValidationSupport.ExtractSpirvToolsVersion(output));
    }

    [Fact]
    public void ValidateTargetEnvironment_BlankYieldsFamilyDefault_ExplicitPassesThrough()
    {
        Assert.Equal(
            ShaderValidationSupport.DefaultTargetEnvironment,
            ShaderValidationSupport.ValidateTargetEnvironment(
                null,
                ShaderValidationSupport.DefaultTargetEnvironment,
                $"{SpirvValAuditor.PluginId}:{SpirvValAuditor.TargetEnvironmentKey}"));
        Assert.Equal(
            "vulkan1.3",
            ShaderValidationSupport.ValidateTargetEnvironment(
                " vulkan1.3 ",
                ShaderValidationSupport.DefaultTargetEnvironment,
                $"{SpirvValAuditor.PluginId}:{SpirvValAuditor.TargetEnvironmentKey}"));
    }

    [Fact]
    public void Parser_ErrorDiagnostic_MapsToModuleFindingWithoutFabricatedLine()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var findings = parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", StderrEntryPointError, 1));

        var finding = Assert.Single(findings);
        Assert.Equal(SpirvValDiagnosticParser.ValidationRuleId, finding.RuleId);
        Assert.Equal(SpirvValDiagnosticParser.ErrorLevel, finding.SeverityLevel);
        Assert.Equal(ModuleTarget, finding.Path);
        // The validator's `line 4` addresses an instruction inside the
        // binary, never a source line of the .spv file: no line is mapped.
        Assert.Null(finding.Line);
        Assert.Contains("line 4", finding.Message, StringComparison.Ordinal);
        Assert.Contains("OpEntryPoint", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_WarningDiagnostic_MapsToWarningLevel()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var findings = parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", StderrWarning, 0));

        var finding = Assert.Single(findings);
        Assert.Equal(SpirvValDiagnosticParser.WarningLevel, finding.SeverityLevel);
        Assert.Equal(ModuleTarget, finding.Path);
        Assert.Null(finding.Line);
    }

    [Fact]
    public void Parser_MultipleDiagnostics_AllAttributedToInvokedModule()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var findings = parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", StderrEntryPointError + StderrWarning, 1));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(ModuleTarget, finding.Path));
    }

    [Fact]
    public void Parser_MalformedModuleDiagnostic_IsEvidenceOnTheModule()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var finding = Assert.Single(parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", StderrMagicError, 1)));

        Assert.Equal(SpirvValDiagnosticParser.ErrorLevel, finding.SeverityLevel);
        Assert.Equal(ModuleTarget, finding.Path);
    }

    [Theory]
    [InlineData(StderrMissingFile)]
    [InlineData(StderrBadEnvironment)]
    [InlineData(StderrMultiFile)]
    public void Parser_ToolOperationFailures_FailClosedAsInfrastructure(string stderr)
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("spirv-val", "", stderr, 1)));
    }

    [Fact]
    public void Parser_ExitWithoutDiagnostics_FailsClosedAsInfrastructure()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("spirv-val", "Usage: spirv-val [options] [<filename>]\n", "", 1)));
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("spirv-val", "", "", 1)));
    }

    [Fact]
    public void Parser_TruncatedMidDiagnostic_KeepsAvailableEvidence()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var findings = parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", "error: line 4: 2 Entry points cannot share the", 1));

        var finding = Assert.Single(findings);
        Assert.Equal(ModuleTarget, finding.Path);
        Assert.Contains("Entry points", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_CleanExitWithNoDiagnostics_IsACheckedPass()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        Assert.Empty(parser.Parse(new ExternalToolParseInput("spirv-val", "", "", 0)));
    }

    [Fact]
    public void Parser_OversizedMessage_IsBounded()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var findings = parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", "error: line 1: " + new string('x', 9000) + "\n", 1));

        Assert.InRange(Assert.Single(findings).Message.Length, 1, 4000);
    }

    [Fact]
    public void Parser_ContinuationLines_AreBounded()
    {
        var parser = new SpirvValDiagnosticParser(ModuleTarget);
        var details = string.Concat(
            Enumerable.Range(1, 10).Select(i => $"  detail instruction {i}\n"));
        var findings = parser.Parse(new ExternalToolParseInput(
            "spirv-val", "", "error: line 4: broken\n" + details, 1));

        var message = Assert.Single(findings).Message;
        Assert.Contains("detail instruction 1", message, StringComparison.Ordinal);
        Assert.DoesNotContain("detail instruction 9", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "spirv-val: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spirv-val", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionMismatch_IsInfrastructureFailure_NamingTool()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "SPIRV-Tools v2024.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spirv-val", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsDeterministicInfrastructure()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version",
                ["Scoped:SpirvTargets"] = ModuleTarget,
            }),
            CancellationToken.None);
        // The presence probe runs before the version gate reads the
        // configured expectation; no scan invocation ever runs.
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(ToolVersionPin.ExpectedVersionKey, ex.Message, StringComparison.Ordinal);
        // The presence probe precedes the version gate; no scan runs.
        Assert.Equal(1, execs);
    }

    [Fact]
    public async Task MissingSpirvTargets_IsDeterministicMisconfiguration_NothingRuns()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(SpirvValAuditor.SpirvTargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Theory]
    [InlineData("--skip-block-layout")]
    [InlineData("--before-hlsl-legalization")]
    [InlineData("--target-env")]
    [InlineData("shaders/extra.spv")]
    [InlineData("-")]
    public async Task ExtraArguments_AreRejectedDeterministically(string extra)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
                ["Scoped:ExtraArguments"] = extra,
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task FlagShapedEnvironment_IsDeterministicMisconfiguration()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
                ["Scoped:TargetEnvironment"] = "--target-env",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ToolRejectedEnvironment_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, "", StderrBadEnvironment));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
                ["Scoped:TargetEnvironment"] = "vulkan9.9",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(ModuleTarget, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, scanExecs);
    }

    [Fact]
    public async Task MissingModuleFile_IsInfrastructureFailure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsModuleCheckProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spirv-val", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData(11, "regular file")]
    [InlineData(12, "empty")]
    [InlineData(14, "MaxModuleBytes")]
    [InlineData(15, "magic")]
    public async Task MalformedModuleInputs_AreInfrastructureFailures(int probeExit, string messagePart)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsModuleCheckProbe(exec))
                return Task.FromResult(new SandboxExecResult(probeExit, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ErrorDiagnostic_FailsAudit_WithStableFindingIdentity()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, "", StderrEntryPointError));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("spirv-val/validation", finding.Title, StringComparison.Ordinal);
        // No `:line` suffix: validator positions are instruction
        // references, never source lines of the module.
        Assert.Equal(ModuleTarget, finding.Location);
        Assert.NotNull(scanExec);
        Assert.Equal(
            ["spirv-val", "--target-env", "vulkan1.0", ModuleTarget],
            scanExec.Argv);
    }

    [Fact]
    public async Task ExplicitEnvironment_ReachesArgvVerbatim()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
                ["Scoped:TargetEnvironment"] = "vulkan1.3",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Equal(
            ["spirv-val", "--target-env", "vulkan1.3", ModuleTarget],
            scanExec.Argv);
    }

    [Fact]
    public async Task WarningDiagnostic_PassesAudit_WithAdvisoryFinding()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", StderrWarning));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal(ModuleTarget, finding.Location);
    }

    [Fact]
    public async Task CleanRun_Passes_WithNoFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitWithoutDiagnostics_FailsClosedAsInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "Usage: spirv-val [options] [<filename>]\n", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(ModuleTarget, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_FailsClosedAsInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", StderrEntryPointError));
        });

        IAuditor auditor = await BuildAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task MinimumSeverityError_DropsAdvisoryWarnings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", StderrWarning));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task DuplicateModules_AreDeduplicatedIntoOneInvocation()
    {
        var scanExecs = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = $"{ModuleTarget},{ModuleTarget}",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, scanExecs);
        Assert.NotNull(scanExec);
        Assert.Single(scanExec.Argv, arg => string.Equals(arg, ModuleTarget, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiModuleRun_AttributesFindingsPerModule()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return exec.Argv[^1] == SecondModuleTarget
                ? Task.FromResult(new SandboxExecResult(1, "", StderrEntryPointError))
                : Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = $"{ModuleTarget},{SecondModuleTarget}",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, scanExecs);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal(SecondModuleTarget, finding.Location);
    }

    [Fact]
    public async Task UnverifiableSecondModule_FailsWholeRun_NeverAPartialPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (!IsScanExec(exec))
                return Task.FromResult(Ok(exec));
            return exec.Argv[^1] == SecondModuleTarget
                ? Task.FromResult(new SandboxExecResult(2, "", ""))
                : Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = $"{ModuleTarget},{SecondModuleTarget}",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(SecondModuleTarget, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledRun_PropagatesCancellation_NeverAPass()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sandbox = new FakeSandbox((exec, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = await BuildAuditorAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (!IsScanExec(exec))
                return Ok(exec);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok(exec);
        });

        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
                ["Scoped:TimeoutSeconds"] = "1",
            }),
            CancellationToken.None);
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public void AuditorDeclaresNoCapabilities_AndToolKind()
    {
        Assert.Equal(AuditCapabilities.None, new SpirvValAuditor().Required);
        Assert.Equal("tool", new SpirvValAuditor().Kind);
    }

    [Fact]
    public void DisabledPlugins_AreNotLoaded_AndToolsAbsentFromBaselineProvisioning()
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
        Assert.Contains(
            loader.GetDiscoveryStatuses(),
            s => s.PluginId == SpirvValAuditor.PluginId && s.SkipReason == PluginSkipReason.Disabled);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("spirv-val", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledSpirvPlugin_DeclaresSpirvValRequirement_WithAptPackage()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SpirvValAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SpirvValAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("spirv-val", tool.Binary);
        // Apt-backed by design: spirv-tools carries spirv-val on
        // Debian/Ubuntu, so baseline provisioning installs it — only when
        // this plugin is enabled.
        Assert.Equal("spirv-tools", tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("spirv-val", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("spirv-tools", install, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledGlslangAndSpirvPlugins_CoexistInOneAssembly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CodeyBox.GlslangAuditorPlugin.GlslangAuditor.PluginId, SpirvValAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CodeyBox.GlslangAuditorPlugin.GlslangAuditor.PluginId);
        Assert.Contains(plugins, p => p.PluginId == SpirvValAuditor.PluginId);
        Assert.Equal(2, loader.GetEnabledPluginTools().Count);
    }

    [Fact]
    [Trait("requires_spirv", "true")]
    public async Task RealSpirvVal_ViolationFixture_ProducesFinding_OnModuleWithoutLine()
    {
        var installed = InstalledSpirvValVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSpirvFixtureRepoAsync();

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

            var auditor = new SpirvValAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:SpirvTargets"] = "shaders/good.spv,shaders/bad.spv",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("spirv-val/validation", finding.Title, StringComparison.Ordinal);
            Assert.Equal("shaders/bad.spv", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_spirv", "true")]
    public async Task RealSpirvVal_CleanFixture_Passes()
    {
        var installed = InstalledSpirvValVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSpirvFixtureRepoAsync();

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

            var auditor = new SpirvValAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:SpirvTargets"] = "shaders/good.spv",
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

    private static async Task<SpirvValAuditor> BuildAuditorAsync()
    {
        var auditor = new SpirvValAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpirvTargets"] = ModuleTarget,
            }),
            CancellationToken.None);
        return auditor;
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GlslangAuditorPlugin.dll");
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
            PluginId: SpirvValAuditor.PluginId,
            PluginDisplayName: "CodeyBox: SPIR-V Module Validation",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, $"SPIRV-Tools v{SpirvValAuditor.DefaultExpectedVersion} unknown hash\n", "");
        if (IsRepoPresenceProbe(exec))
            return new SandboxExecResult(0, string.Join("\n", exec.Argv.Skip(4)), "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("spirv-val", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "spirv-val" && exec.Argv[1] == "--version";

    private static bool IsRepoPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("printf '%s", StringComparison.Ordinal);

    private static bool IsModuleCheckProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("od -A n", StringComparison.Ordinal);

    private static bool IsRepoProbe(SandboxExec exec)
        => IsRepoPresenceProbe(exec) || IsModuleCheckProbe(exec);

    private static bool IsScanExec(SandboxExec exec)
        => exec.Argv.Count > 0
            && exec.Argv[0] == "spirv-val"
            && !IsVersionProbe(exec);

    private static async Task<string> SeedSpirvFixtureRepoAsync()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-spirv-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "shaders"));

        // Synthetic fixture modules assembled once with spirv-as from the
        // pinned SPIRV-Tools release (SPIR-V 1.0, GLCompute): good.spv
        // validates clean under --target-env vulkan1.0, bad.spv carries two
        // same-named entry points. No includes, no imports, no network.
        const string goodBase64 =
            "AwIjBwAAAQAAAAcABQAAAAAAAAARAAIAAQAAAA4AAwAAAAAAAQAAAA8ABQAFAAAAAQAAAG1haW4A"
            + "AAAAEAAGAAEAAAARAAAAAQAAAAEAAAABAAAAEwACAAIAAAAhAAMAAwAAAAIAAAA2AAUAAgAAAAEA"
            + "AAAAAAAAAwAAAPgAAgAEAAAA/QABADgAAQA=";
        const string badBase64 =
            "AwIjBwAAAQAAAAcABQAAAAAAAAARAAIAAQAAAA4AAwAAAAAAAQAAAA8ABQAFAAAAAQAAAG1haW4A"
            + "AAAADwAFAAUAAAABAAAAbWFpbgAAAAAQAAYAAQAAABEAAAABAAAAAQAAAAEAAAATAAIAAgAAACEA"
            + "AwADAAAAAgAAADYABQACAAAAAQAAAAAAAAADAAAA+AACAAQAAAD9AAEAOAABAA==";
        await File.WriteAllBytesAsync(
            Path.Combine(dir, "shaders", "good.spv"), Convert.FromBase64String(goodBase64));
        await File.WriteAllBytesAsync(
            Path.Combine(dir, "shaders", "bad.spv"), Convert.FromBase64String(badBase64));

        return dir;
    }

    private static string? ProbeInstalledSpirvValVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "spirv-val",
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
            return process.ExitCode == 0
                ? SpirvValidationSupport.ExtractSpirvToolsVersion(stdout)
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
