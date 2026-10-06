using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.GltfValidatorAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the glTF validator auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming gltf_validator (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing) when a JSON report is present; exit 1 without a
///   report, and any other exit, is infrastructure.
/// - One bounded gltf_validator -o invocation per selected asset (the CLI's --stdout mode is single-asset);
///   enumeration is bounded by MaxAssets and every asset is size-gated by MaxAssetBytes.
/// - Reports map to findings preserving issue code, severity, and JSON pointer/offset; locations stay
///   repo-relative; raw tool severities are mapped, never passed through.
/// - Unverifiable assets (missing files, timeouts, partial coverage) fail closed, never pass.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_gltf_validator", "true")].
/// </summary>
public sealed class GltfValidatorAuditorTests
{
    private static readonly string? InstalledGltfValidatorVersion = ProbeInstalledGltfValidatorVersion();

    private const string VersionBanner =
        "glTF 2.0 Validator, version " + GltfValidatorAuditor.DefaultExpectedVersion + "\n"
        + "Supported extensions:\n\textension\n\n"
        + "Usage: gltf_validator [<options>] <input>\n";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "gltf_validator: command not found"));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbe_MissingToken_IsInfrastructureFailure_NamingTool()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "unexpected banner without a version"));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("version could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "glTF 2.0 Validator, version 1.0.0\n"));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GltfValidatorAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ErrorsFixture_YieldsFindings_WithCodePointerAndLocation_AndFails()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, ReportWithErrors("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);

        var error = Assert.Single(result.Findings, f => f.Title.Contains("UNDEFINED_PROPERTY", StringComparison.Ordinal));
        Assert.Equal("codeybox:gltf-validator", error.AuditorName);
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("models/box.gltf", error.Location);
        Assert.Contains("/asset", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(result.Findings, f => f.Title.Contains("NON_RELATIVE_URI", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("models/box.gltf", warning.Location);

        var info = Assert.Single(result.Findings, f => f.Title.Contains("NODE_EMPTY", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);

        var hint = Assert.Single(result.Findings, f => f.Title.Contains("BUFFER_VIEW_TARGET_MISSING", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, hint.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("gltf_validator", scanExec!.Argv[0]);
        Assert.Contains("-o", scanExec.Argv);
        Assert.Contains("--validate-resources", scanExec.Argv);
        Assert.Contains("models/box.gltf", scanExec.Argv);
        Assert.Contains("errors=1 warnings=1 infos=1 hints=1 truncated=False", result.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WarningsOnly_AdvisoryFindings_PassTheAudit()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportWithWarnings("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Warning, f.Severity));
    }

    [Fact]
    public async Task ExitCode1_WithoutReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "models/box.gltf is neither a file nor a directory.\n"));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("models/box.gltf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroUnexpectedExit_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "boom"));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedJsonReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(1, "this is not json", ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportWithoutMessagesArray_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, """{"uri":"models/box.gltf","issues":{}}""", ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OffsetIssue_PreservesAtOffsetPrefix()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportWithOffset("models/box.glb"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.glb",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("GLB_CHUNK_TOO_BIG", finding.Title, StringComparison.Ordinal);
        Assert.Contains("@20:", finding.Description, StringComparison.Ordinal);
        Assert.Equal("models/box.glb", finding.Location);
    }

    [Fact]
    public async Task MissingCode_UsesFallbackRuleId_AndRulesFilterIt()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportMissingCode("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains(GltfValidatorJsonOutputParser.FallbackRuleId, finding.Title, StringComparison.Ordinal);

        var filtered = new GltfValidatorAuditor();
        await filtered.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ExcludedRules"] = GltfValidatorJsonOutputParser.FallbackRuleId,
        }), CancellationToken.None);

        var filteredResult = await ((IAuditor)filtered).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.Empty(filteredResult.Findings);
        Assert.True(filteredResult.Passed);
    }

    [Fact]
    public async Task UnknownSeverity_MapsToWarningDefault()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportUnknownSeverity("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task TruncatedReport_SurfacesSummaryInRawOutput()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportTruncated("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("truncated=True", result.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxFindings_TruncatesGlobally_WithNote()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            var asset = ScanAsset(exec);
            var report = asset.EndsWith("a.gltf", StringComparison.Ordinal)
                ? ReportWithWarnings("models/a.gltf")
                : ReportWithWarnings("models/b.gltf");
            return Task.FromResult(new SandboxExecResult(0, report, ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/a.gltf, models/b.gltf",
            ["Scoped:MaxFindings"] = "2",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.Contains("[findings truncated: 2 finding(s) beyond MaxFindings 2 were dropped]", result.RawOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/absolute/model.gltf")]
    [InlineData("../escape/model.gltf")]
    [InlineData("https://example.com/model.gltf")]
    [InlineData("data:application/octet-stream;base64,AAAA")]
    [InlineData("models/texture.png")]
    [InlineData("--validate-resources")]
    public async Task InvalidTarget_IsDeterministicInfrastructure_WithoutProbes(string target)
    {
        var probeExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            probeExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = target,
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, probeExecs);
    }

    [Fact]
    public async Task MaxAssets_Exceeded_IsDeterministicInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean(ScanAsset(exec)), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/a.gltf, models/b.glb",
            ["Scoped:MaxAssets"] = "1",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("MaxAssets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizeAsset_IsDeterministicInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFindProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "models/huge.glb\0" + long.MaxValue + "\0", ""));
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/huge.glb"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/huge.glb",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("MaxAssetBytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingBinary_DiscoveryMode_IsInfrastructureFailure()
    {
        var probeExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            probeExecs++;
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new GltfValidatorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, probeExecs);
    }

    [Fact]
    public async Task NoAssetsDiscovered_PassesVacuous()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFindProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        IAuditor auditor = new GltfValidatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Contains("nothing to validate", result.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_SelectsAssets_WithoutTargets()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFindProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "models/found.gltf\08512\0", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/found.gltf"), ""));
        });

        IAuditor auditor = new GltfValidatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("models/found.gltf", scanExec!.Argv);
    }

    [Fact]
    public async Task DefaultExcludePaths_FiltersVendoredFindings_AfterScanning()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, ReportWithErrors("vendor/model.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "vendor/model.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(1, scanExecs);
        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task MinimumSeverity_DropsBelowThreshold()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportWithWarnings("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:MinimumSeverity"] = "error",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithErrors("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:IncludedRules"] = "NON_RELATIVE_URI",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("NON_RELATIVE_URI", finding.Title, StringComparison.Ordinal);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ExtraArguments_NoStdout_RejectedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ExtraArguments"] = "--no-stdout",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--no-stdout", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExtraArguments_AbsolutePath_RejectedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ExtraArguments"] = "--absolute-path",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ValidateResources_False_EmitsNoValidateResources()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ValidateResources"] = "false",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("--no-validate-resources", scanExec!.Argv);
        Assert.DoesNotContain("--validate-resources", scanExec.Argv.Where(static a => a != "--no-validate-resources"));
        Assert.Contains("resources-validated=False", result.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtraArguments_ResourceFlagOverride_RecordedInRawOutput()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ExtraArguments"] = "--no-validate-resources",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--validate-resources", scanExec!.Argv.Where(static a => a != "--no-validate-resources"));
        Assert.Contains("resources-validated=False", result.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "glTF 2.0 Validator, version 2.0.0-dev.3.12\n"));
            if (IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:ExpectedVersion"] = "2.0.0-dev.3.12",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task MultiAsset_AggregatesDeterministically_InSortedOrder()
    {
        var scanned = new List<string>();
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            var asset = ScanAsset(exec);
            scanned.Add(asset);
            var report = asset.EndsWith("a.gltf", StringComparison.Ordinal)
                ? ReportWithErrors("models/a.gltf")
                : ReportWithWarnings("models/b.glb");
            return Task.FromResult(new SandboxExecResult(asset.EndsWith("a.gltf", StringComparison.Ordinal) ? 1 : 0, report, ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/b.glb, models/a.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(new[] { "models/a.gltf", "models/b.glb" }, scanned);
        Assert.Equal(4 + 2, result.Findings.Count);
        Assert.Equal("models/a.gltf", result.Findings[0].Location);
        Assert.Contains("=== models/a.gltf ===", result.RawOutput, StringComparison.Ordinal);
        Assert.Contains("=== models/b.glb ===", result.RawOutput, StringComparison.Ordinal);
        Assert.NotNull(result.RawOutput);
        Assert.True(
            result.RawOutput!.IndexOf("=== models/a.gltf ===", StringComparison.Ordinal)
            < result.RawOutput!.IndexOf("=== models/b.glb ===", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateTargets_Deduped_IntoSingleScan()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean("models/box.gltf"), ""));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf, models/box.gltf",
        }), CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, scanExecs);
    }

    [Fact]
    public async Task PartialCoverage_SecondAssetFailure_FailsClosed_NamingAsset()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            var asset = ScanAsset(exec);
            return asset.EndsWith("a.gltf", StringComparison.Ordinal)
                ? Task.FromResult(new SandboxExecResult(0, ReportClean("models/a.gltf"), ""))
                : Task.FromResult(new SandboxExecResult(1, "", "models/b.gltf is neither a file nor a directory.\n"));
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/a.gltf, models/b.gltf",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("models/b.gltf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure_NamingToolAndAsset()
    {
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsFindProbe(exec))
                return ProbeOk(exec);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new SandboxExecResult(0, ReportClean("models/box.gltf"), "");
        });

        var auditor = new GltfValidatorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "models/box.gltf",
            ["Scoped:TimeoutSeconds"] = "2",
        }), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gltf_validator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Contains("models/box.gltf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledToken_PropagatesCancellation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(new SandboxExecResult(0, "", "")));

        IAuditor auditor = new GltfValidatorAuditor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public void Parser_AbsoluteUri_RelativizedAgainstWorkingDirectory()
    {
        var report = ReportWithWarnings("/work/models/box.gltf");
        var findings = GltfValidatorJsonOutputParser.Instance.Parse(
            new CodeyBox.PluginSdk.Tools.ExternalToolParseInput(
                "gltf_validator", report, "", 0, WorkingDirectory: "/work"));

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("models/box.gltf", f.Path));
    }

    [Fact]
    public void Parser_OutsideRootUri_MarkedFileScheme()
    {
        var report = ReportWithWarnings("/etc/foreign.gltf");
        var findings = GltfValidatorJsonOutputParser.Instance.Parse(
            new CodeyBox.PluginSdk.Tools.ExternalToolParseInput(
                "gltf_validator", report, "", 0, WorkingDirectory: "/work"));

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.StartsWith("file://", f.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_WordSeverity_AcceptedDefensively()
    {
        const string report = """{"uri":"a.gltf","issues":{"numErrors":1,"numWarnings":0,"numInfos":0,"numHints":0,"messages":[{"code":"SOME_CODE","message":"Word-form severity.","severity":"Error","pointer":"/asset"}],"truncated":false}}""";
        var findings = GltfValidatorJsonOutputParser.Instance.Parse(
            new CodeyBox.PluginSdk.Tools.ExternalToolParseInput(
                "gltf_validator", report, "", 1));

        var finding = Assert.Single(findings);
        Assert.Equal("error", finding.SeverityLevel);
        Assert.Equal("/asset: Word-form severity.", finding.Message);
    }

    [Fact]
    public void Parser_Summarize_ReturnsCountsAndTruncation()
    {
        Assert.Contains("errors=1 warnings=1", GltfValidatorJsonOutputParser.TrySummarize(ReportWithErrors("a.gltf")), StringComparison.Ordinal);
        Assert.Contains("truncated=True", GltfValidatorJsonOutputParser.TrySummarize(ReportTruncated("a.gltf")), StringComparison.Ordinal);
        Assert.Null(GltfValidatorJsonOutputParser.TrySummarize("not json"));
        Assert.Null(GltfValidatorJsonOutputParser.TrySummarize(""));
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
            s => s.PluginId == GltfValidatorAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("gltf_validator", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGltfValidatorRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GltfValidatorAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GltfValidatorAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("gltf_validator", tool.Binary);
        // Verify-only by design: no distro package carries gltf_validator, so no apt package is specified
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("gltf_validator", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_gltf_validator", "true")]
    public async Task RealGltfValidator_InvalidAsset_ProducesFinding()
    {
        var installed = InstalledGltfValidatorVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedGltfFixtureRepoAsync(valid: false);

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

            var auditor = new GltfValidatorAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Targets"] = "model.gltf",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);
            Assert.Contains(result.Findings, f => f.Location == "model.gltf");
            Assert.Equal(AuditSeverity.Error, result.Findings.First(f => f.Location == "model.gltf").Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_gltf_validator", "true")]
    public async Task RealGltfValidator_ValidAsset_Passes()
    {
        var installed = InstalledGltfValidatorVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedGltfFixtureRepoAsync(valid: true);

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

            var auditor = new GltfValidatorAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Targets"] = "model.gltf",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GltfValidatorAuditorPlugin.dll");
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
            PluginId: GltfValidatorAuditor.PluginId,
            PluginDisplayName: "CodeyBox: glTF Asset Validator",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult VersionOk()
        => new(1, "", VersionBanner);

    private static SandboxExecResult ProbeOk(SandboxExec exec)
    {
        if (IsPresenceProbe(exec))
            return new SandboxExecResult(0, "", "");
        if (IsVersionProbe(exec))
            return VersionOk();
        if (IsFindProbe(exec))
        {
            // Target check (explicit paths) or discovery: echo every
            // requested path back as a 512-byte regular file so the real
            // production matching path runs. Discovery ("." root) reports a
            // single default asset instead.
            if (exec.Argv.Count > 1 && exec.Argv[1] != ".")
            {
                var paths = exec.Argv.Skip(1).TakeWhile(static a => a != "-maxdepth").ToList();
                return new SandboxExecResult(0, string.Concat(paths.Select(static p => p + "\0512\0")), "");
            }
            return new SandboxExecResult(0, "models/box.gltf\0512\0", "");
        }
        throw new InvalidOperationException("ProbeOk called for a non-probe exec.");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("gltf_validator", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 1 && exec.Argv[0] == "gltf_validator";

    private static bool IsFindProbe(SandboxExec exec)
        => exec.Argv.Count > 0 && exec.Argv[0] == "find";

    private static bool IsScanExec(SandboxExec exec)
        => exec.Argv.Count > 1 && exec.Argv[0] == "gltf_validator";

    private static string ScanAsset(SandboxExec exec)
        => exec.Argv.FirstOrDefault(static a =>
            a.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
            || a.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) ?? "";

    private static string Message(string code, string message, int severity, string? pointer, int? offset)
    {
        var payload = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["severity"] = severity,
        };
        if (pointer is not null)
            payload["pointer"] = pointer;
        if (offset.HasValue)
            payload["offset"] = offset.Value;
        return JsonSerializer.Serialize(payload);
    }

    private static string Report(string uri, string messagesJson, int errors, int warnings, int infos, int hints, bool truncated)
        => "{\"uri\":" + JsonSerializer.Serialize(uri)
            + ",\"mimeType\":\"model/gltf+json\""
            + ",\"validatorVersion\":" + JsonSerializer.Serialize(GltfValidatorAuditor.DefaultExpectedVersion)
            + ",\"issues\":{\"numErrors\":" + errors
            + ",\"numWarnings\":" + warnings
            + ",\"numInfos\":" + infos
            + ",\"numHints\":" + hints
            + ",\"messages\":[" + messagesJson + "]"
            + ",\"truncated\":" + (truncated ? "true" : "false") + "}}";

    private static string ReportWithErrors(string uri)
        => Report(uri, string.Join(",",
        [
            Message("UNDEFINED_PROPERTY", "Property 'version' must be defined.", 0, "/asset", null),
            Message("NON_RELATIVE_URI", "Non-relative URI found: 'https://example.com/a.bin'.", 1, "/buffers/0/uri", null),
            Message("NODE_EMPTY", "Empty node encountered.", 2, "/nodes/0", null),
            Message("BUFFER_VIEW_TARGET_MISSING", "bufferView.target should be set for vertex or index data.", 3, "/bufferViews/0", null),
        ]), 1, 1, 1, 1, false);

    private static string ReportWithWarnings(string uri)
        => Report(uri, string.Join(",",
        [
            Message("NON_RELATIVE_URI", "Non-relative URI found: 'a.bin'.", 1, "/buffers/0/uri", null),
            Message("IMAGE_NPOT_DIMENSIONS", "Image has non-power-of-two dimensions: 100x200.", 1, "/images/0", null),
        ]), 0, 2, 0, 0, false);

    private static string ReportClean(string uri)
        => Report(uri, "", 0, 0, 0, 0, false);

    private static string ReportTruncated(string uri)
        => Report(uri, string.Join(",",
        [
            Message("UNDEFINED_PROPERTY", "Property 'version' must be defined.", 0, "/asset", null),
            Message("NON_RELATIVE_URI", "Non-relative URI found: 'a.bin'.", 1, "/buffers/0/uri", null),
        ]), 1, 1, 0, 0, true);

    private static string ReportWithOffset(string uri)
        => Report(uri, Message("BUFFER_GLB_CHUNK_TOO_BIG", "GLB-stored BIN chunk contains 4 extra padding byte(s).", 1, null, 20),
            0, 1, 0, 0, false);

    private static string ReportMissingCode(string uri)
        => "{\"uri\":" + JsonSerializer.Serialize(uri)
            + ",\"issues\":{\"numErrors\":0,\"numWarnings\":1,\"numInfos\":0,\"numHints\":0,"
            + "\"messages\":[{\"message\":\"Ancient validator build.\",\"severity\":1}],\"truncated\":false}}";

    private static string ReportUnknownSeverity(string uri)
        => Report(uri, Message("SOME_FUTURE_CODE", "A future check fired.", 7, "/asset", null),
            0, 0, 0, 0, false);

    private static async Task<string> SeedGltfFixtureRepoAsync(bool valid)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-gltf-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var content = valid
            ? """{"asset":{"version":"2.0"}}"""
            : """{"asset":{}}""";
        await File.WriteAllTextAsync(Path.Combine(dir, "model.gltf"), content);
        return dir;
    }

    private static string? ProbeInstalledGltfValidatorVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gltf_validator",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var match = Regex.Match(stdout + "\n" + stderr, @"\d+\.\d+\.\d+[\w.\-]*");
            return match.Success ? match.Value : null;
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
