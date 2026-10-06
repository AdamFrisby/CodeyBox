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
/// Covers the shared shader-validation family seam and the glslang backend auditor:
/// - Target/stage/environment resolution is explicit and fail-closed (suffix, extension, default;
///   single-stage vs mixed-stage invocation rule; HLSL/ShaderLab/config-file rejection; containment).
/// - The glslang backend passes the environment explicitly and the stage flag only for single-stage sets.
/// - glslangValidator diagnostics map to findings with asset paths and lines; warnings are advisory.
/// - Program-level link failures and exits without diagnostics are infrastructure, never findings or passes.
/// - Missing or wrong-version binary, missing/empty targets, and flag-like ExtraArguments never produce
///   a verdict.
/// - The plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_glslang", "true")] use fixture-local shaders,
///   so they need the binary but no network.
/// </summary>
public sealed class GlslangAuditorTests
{
    private static readonly string? InstalledGlslangVersion = ProbeInstalledGlslangVersion();

    private const string VertTarget = "shaders/water.vert";
    private const string FragTarget = "shaders/water.frag";

    private const string StderrError =
        "ERROR: shaders/water.vert:12: 'foo' : undeclared identifier\n";

    private const string StderrWarning =
        "WARNING: shaders/water.frag:3: 'bar' : deprecated texture call\n";

    private const string StderrLinkFailure =
        "ERROR: Linking vertex stage: Multiple function definitions for main\nLink failed.\n";

    private const string StderrUsageText =
        "Usage: glslangValidator [option]... [file]...\n";

    [Fact]
    public void ResolveTargets_SuffixExtensionAndDefault_AllResolveExplicitly()
    {
        var set = ShaderValidationSupport.ResolveTargets(
            ["shaders/a.vert", "shaders/b.glsl:vert", "shaders/c.glsl"],
            "vert",
            GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey,
            GlslangAuditor.DefaultStageKey);

        Assert.Equal(3, set.Targets.Count);
        Assert.Equal("vert", set.Targets[0].Stage);
        Assert.True(set.Targets[0].StageFromExtension);
        Assert.Equal("vert", set.Targets[1].Stage);
        Assert.False(set.Targets[1].StageFromExtension);
        Assert.Equal("vert", set.Targets[2].Stage);
        Assert.False(set.Targets[2].StageFromExtension);
        Assert.Equal("vert", set.SingleStage);
    }

    [Fact]
    public void ResolveTargets_MixedStagesWithExtensions_AcceptedWithoutSingleStage()
    {
        var set = ShaderValidationSupport.ResolveTargets(
            ["shaders/a.vert", "shaders/b.frag"],
            null,
            GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey,
            GlslangAuditor.DefaultStageKey);

        Assert.Equal(2, set.Targets.Count);
        Assert.All(set.Targets, static t => Assert.True(t.StageFromExtension));
        Assert.Null(set.SingleStage);
    }

    [Fact]
    public void ResolveTargets_SingleSharedStage_ReportsSingleStage()
    {
        var set = ShaderValidationSupport.ResolveTargets(
            ["shaders/a.vert", "shaders/b.glsl:VERT"],
            null,
            GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey,
            GlslangAuditor.DefaultStageKey);

        Assert.Equal("vert", set.SingleStage);
        Assert.Equal("vert", set.Targets[1].Stage);
    }

    [Fact]
    public void ResolveTargets_DuplicatesFolded_KeepingFirst()
    {
        var set = ShaderValidationSupport.ResolveTargets(
            ["shaders/a.vert", "shaders/a.vert"],
            null,
            GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey,
            GlslangAuditor.DefaultStageKey);

        Assert.Single(set.Targets);
    }

    [Fact]
    public void ResolveTargets_EmptySet_IsDeterministicMisconfiguration()
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            [], null, GlslangAuditor.PluginId, GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
        Assert.Contains(GlslangAuditor.ShaderTargetsKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shaders/common.glsl")]
    [InlineData("shaders/notes.txt")]
    [InlineData("shaders/effect.hlsl")]
    [InlineData("shaders/unity.shader")]
    [InlineData("shaders/noextension")]
    public void ResolveTargets_WithoutResolvableStageOrShaderExtension_FailsClosed(string entry)
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            [entry], null, GlslangAuditor.PluginId, GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void ResolveTargets_ConfFile_IsRejectedAsToolConfiguration()
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            ["shaders/limits.conf"], "frag", GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
        Assert.Contains(".conf", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/abs/shader.vert")]
    [InlineData("../escape.vert")]
    [InlineData("a/../../escape.vert")]
    [InlineData("-shader.vert")]
    public void ResolveTargets_UncontainedOrFlagShapedEntries_FailClosed(string entry)
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            [entry], "vert", GlslangAuditor.PluginId, GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void ResolveTargets_MixedStagesWithoutExtensions_FailsClosed()
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            ["shaders/a.glsl:vert", "shaders/b.glsl:frag"],
            null,
            GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey,
            GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
        Assert.Contains("mixed-stage", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveTargets_UnknownStageSuffix_TreatedAsPath_ThenRejected()
    {
        // `bogus` is not a stage, so no suffix splits off; the entry keeps
        // its full spelling and fails the shader-extension check.
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            ["shaders/a.vert:bogus"], null, GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void ResolveTargets_UnknownDefaultStage_FailsClosed()
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ResolveTargets(
            ["shaders/a.glsl"], "geometry", GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey));
        Assert.True(ex.IsDeterministic);
        Assert.Contains(GlslangAuditor.DefaultStageKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTargetEnvironment_BlankYieldsDefault_ExplicitPassesThrough()
    {
        Assert.Equal(
            ShaderValidationSupport.DefaultTargetEnvironment,
            ShaderValidationSupport.ValidateTargetEnvironment(null, ShaderValidationSupport.DefaultTargetEnvironment, "env"));
        Assert.Equal(
            "vulkan1.3",
            ShaderValidationSupport.ValidateTargetEnvironment(" vulkan1.3 ", ShaderValidationSupport.DefaultTargetEnvironment, "env"));
    }

    [Theory]
    [InlineData("--target-env")]
    [InlineData("-V")]
    [InlineData("vulkan 1.3")]
    [InlineData("vulkan1.0;rm -rf /")]
    public void ValidateTargetEnvironment_FlagShapedOrUnboundedValues_FailClosed(string value)
    {
        var ex = Assert.Throws<AuditUnavailableException>(() => ShaderValidationSupport.ValidateTargetEnvironment(
            value, ShaderValidationSupport.DefaultTargetEnvironment, "env"));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public void Backend_SingleStageSet_PassesTargetEnvAndStageExplicitly()
    {
        var backend = new GlslangValidationBackend();
        var set = ShaderValidationSupport.ResolveTargets(
            [VertTarget], null, GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey);

        var args = backend.BuildValidationArguments(set, "vulkan1.3");

        Assert.Equal<string>(["--target-env", "vulkan1.3", "-S", "vert"], args);
        Assert.Equal("glslangValidator", backend.ToolName);
        Assert.Equal(["--version"], backend.VersionProbeArguments);
    }

    [Fact]
    public void Backend_MixedStageSet_OmitsStageFlag_RelyingOnExtensions()
    {
        var backend = new GlslangValidationBackend();
        var set = ShaderValidationSupport.ResolveTargets(
            [VertTarget, FragTarget], null, GlslangAuditor.PluginId,
            GlslangAuditor.ShaderTargetsKey, GlslangAuditor.DefaultStageKey);

        Assert.Null(set.SingleStage);
        Assert.Equal<string>(["--target-env", "vulkan1.0"], backend.BuildValidationArguments(set, "vulkan1.0"));
    }

    [Fact]
    public void Parser_ErrorDiagnostic_MapsToFindingWithPathAndLine()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput("glslangValidator", StderrError, "", 1));

        var finding = Assert.Single(findings);
        Assert.Equal(GlslangDiagnosticParser.ValidationRuleId, finding.RuleId);
        Assert.Equal(GlslangDiagnosticParser.ErrorLevel, finding.SeverityLevel);
        Assert.Equal("shaders/water.vert", finding.Path);
        Assert.Equal(12, finding.Line);
        Assert.Contains("undeclared identifier", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_WarningDiagnostic_MapsToWarningLevel()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput("glslangValidator", StderrWarning, "", 0));

        var finding = Assert.Single(findings);
        Assert.Equal(GlslangDiagnosticParser.WarningLevel, finding.SeverityLevel);
        Assert.Equal("shaders/water.frag", finding.Path);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void Parser_MultipleFiles_MapToTheirOwnAssets()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator", StderrError + StderrWarning, "", 1));

        Assert.Equal(2, findings.Count);
        Assert.Equal("shaders/water.vert", findings[0].Path);
        Assert.Equal("shaders/water.frag", findings[1].Path);
    }

    [Fact]
    public void Parser_ShaderIndexHead_KeepsLineWithoutInventingAPath()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator", "ERROR: 0:12: 'foo' : undeclared identifier\n", "", 1));

        var finding = Assert.Single(findings);
        Assert.Null(finding.Path);
        Assert.Equal(12, finding.Line);
    }

    [Fact]
    public void Parser_SummaryShapedLine_IsNotMisreadAsLocation()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator", "ERROR: 2 other problems found\n", "", 1));

        // No location to extract, but the reported error is never dropped:
        // it stays a path-less finding so the audit fails on evidence.
        var finding = Assert.Single(findings);
        Assert.Null(finding.Path);
        Assert.Null(finding.Line);
    }

    [Fact]
    public void Parser_UncontainedReportedPath_DropsLocationKeepsDiagnostic()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator", "ERROR: ../escape.vert:5: 'foo' : bad\n", "", 1));

        var finding = Assert.Single(findings);
        Assert.Null(finding.Path);
        Assert.Equal(5, finding.Line);
        Assert.Contains("'foo'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_AbsoluteReportedPath_RelativizedAgainstScanRoot()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator",
            "ERROR: /sandbox/work/shaders/water.vert:7: 'foo' : bad\n",
            "",
            1,
            ScanRoot: "/sandbox/work",
            WorkingDirectory: "/sandbox/work"));

        var finding = Assert.Single(findings);
        Assert.Equal("shaders/water.vert", finding.Path);
        Assert.Equal(7, finding.Line);
    }

    [Fact]
    public void Parser_AbsoluteReportedPathWithoutScanRoot_DropsLocation()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator", "ERROR: /etc/shadow.vert:7: 'foo' : bad\n", "", 1));

        Assert.Null(Assert.Single(findings).Path);
    }

    [Fact]
    public void Parser_ExitWithoutDiagnostics_FailsClosedAsInfrastructure()
    {
        var parser = new GlslangDiagnosticParser();
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("glslangValidator", StderrUsageText, "", 1)));
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("glslangValidator", "", "", 1)));
    }

    [Fact]
    public void Parser_LinkFailureWithoutAssetDiagnostics_FailsClosedWithGuidance()
    {
        var parser = new GlslangDiagnosticParser();
        var ex = Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("glslangValidator", StderrLinkFailure, "", 1)));
        Assert.Contains("link", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_LinkFailureAlongsideAssetErrors_ReturnsTheAssetFindings()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator", StderrError + StderrLinkFailure, "", 1));

        var finding = Assert.Single(findings);
        Assert.Equal("shaders/water.vert", finding.Path);
    }

    [Fact]
    public void Parser_CleanExitWithNoDiagnostics_IsACheckedPass()
    {
        var parser = new GlslangDiagnosticParser();
        Assert.Empty(parser.Parse(new ExternalToolParseInput("glslangValidator", "", "", 0)));
    }

    [Fact]
    public void Parser_OversizedMessage_IsBounded()
    {
        var parser = new GlslangDiagnosticParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "glslangValidator",
            "ERROR: shaders/water.vert:1: " + new string('x', 9000) + "\n",
            "",
            1));

        Assert.InRange(Assert.Single(findings).Message.Length, 1, 4000);
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
                return Task.FromResult(new SandboxExecResult(127, "", "glslangValidator: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("glslangValidator", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionMismatch_IsInfrastructureFailure_NamingTool()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Glslang Version: 0.0.0 00000000\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("glslangValidator", ex.Message, StringComparison.Ordinal);
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

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
                ["Scoped:ShaderTargets"] = VertTarget,
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingShaderTargets_IsDeterministicMisconfiguration_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(GlslangAuditor.ShaderTargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("-E")]
    [InlineData("--target-env")]
    [InlineData("shaders/extra.vert")]
    public async Task ExtraArguments_AreRejectedDeterministically(string extra)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ShaderTargets"] = VertTarget,
                ["Scoped:ExtraArguments"] = extra,
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingTargetFile_IsInfrastructureFailure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsNonEmptyProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("glslangValidator", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task EmptyTargetFile_IsInfrastructureFailure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, VertTarget, ""));
            if (IsNonEmptyProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ErrorDiagnostic_FailsAudit_WithStableFindingIdentity()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, StderrError, ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("glslang/validation", finding.Title, StringComparison.Ordinal);
        Assert.Equal("shaders/water.vert:12", finding.Location);
        Assert.NotNull(scanExec);
        Assert.Equal("glslangValidator", scanExec.Argv[0]);
        Assert.Contains("--target-env", scanExec.Argv, StringComparer.Ordinal);
        Assert.Contains(VertTarget, scanExec.Argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task WarningDiagnostic_PassesAudit_WithAdvisoryFinding()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, StderrWarning, ""));
        });

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ShaderTargets"] = FragTarget,
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("shaders/water.frag:3", finding.Location);
    }

    [Fact]
    public async Task CleanRun_Passes_WithNoFindings()
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
    public async Task ExitWithoutDiagnostics_FailsClosedAsInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, StderrUsageText, ""));
        });

        IAuditor auditor = await BuildAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("glslangValidator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_FailsClosedAsInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, StderrError, ""));
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
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, StderrWarning, ""));
        });

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ShaderTargets"] = FragTarget,
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task DuplicateTargets_AreDeduplicatedIntoOneOperand()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ShaderTargets"] = $"{VertTarget},{VertTarget}",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Single(scanExec.Argv, arg => string.Equals(arg, VertTarget, StringComparison.Ordinal));
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
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok(exec);
        });

        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ShaderTargets"] = VertTarget,
                ["Scoped:TimeoutSeconds"] = "1",
            }),
            CancellationToken.None);
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
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
            s => s.PluginId == GlslangAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("glslangValidator", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGlslangRequirement_WithAptPackage()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GlslangAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GlslangAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("glslangValidator", tool.Binary);
        // Apt-backed by design: glslang-tools carries glslangValidator on
        // Debian/Ubuntu, so baseline provisioning installs it — only when
        // this plugin is enabled.
        Assert.Equal("glslang-tools", tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("glslangValidator", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("glslang-tools", install, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_glslang", "true")]
    public async Task RealGlslangValidator_ViolationFixture_ProducesFinding_WithPathAndLine()
    {
        var installed = InstalledGlslangVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedGlslangFixtureRepoAsync(broken: true);

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

            var auditor = new GlslangAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:ShaderTargets"] = "shaders/good.vert,shaders/bad.frag",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("glslang/validation", finding.Title, StringComparison.Ordinal);
            Assert.StartsWith("shaders/bad.frag:", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_glslang", "true")]
    public async Task RealGlslangValidator_CleanFixture_Passes()
    {
        var installed = InstalledGlslangVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedGlslangFixtureRepoAsync(broken: false);

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

            var auditor = new GlslangAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:ShaderTargets"] = "shaders/good.vert,shaders/good.frag",
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

    private static async Task<GlslangAuditor> BuildAuditorAsync()
    {
        var auditor = new GlslangAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ShaderTargets"] = VertTarget,
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
            PluginId: GlslangAuditor.PluginId,
            PluginDisplayName: "CodeyBox: glslang Shader Validation",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, $"Glslang Version: {GlslangAuditor.DefaultExpectedVersion} 00000000\n", "");
        if (IsRepoPresenceProbe(exec))
            return new SandboxExecResult(0, string.Join("\n", exec.Argv.Skip(4)), "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("glslangValidator", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "glslangValidator" && exec.Argv[1] == "--version";

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

    private static async Task<string> SeedGlslangFixtureRepoAsync(bool broken)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-glslang-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "shaders"));

        // A hermetic single-program fixture: no includes, no imports, no network.
        var vert = """
            #version 450
            layout(location = 0) in vec3 inPosition;
            void main() {
                gl_Position = vec4(inPosition, 1.0);
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "shaders", "good.vert"), vert);

        var goodFrag = """
            #version 450
            layout(location = 0) out vec4 outColor;
            void main() {
                outColor = vec4(1.0, 0.0, 0.0, 1.0);
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "shaders", "good.frag"), goodFrag);

        var badFrag = broken
            ? """
                #version 450
                layout(location = 0) out vec4 outColor;
                void main() {
                    outColor = vec4(undefinedIdentifier, 0.0, 0.0, 1.0);
                }
                """
            : goodFrag;
        await File.WriteAllTextAsync(Path.Combine(dir, "shaders", "bad.frag"), badFrag);

        return dir;
    }

    private static string? ProbeInstalledGlslangVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "glslangValidator",
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
