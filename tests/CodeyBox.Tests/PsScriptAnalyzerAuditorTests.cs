using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PsScriptAnalyzerAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the PSScriptAnalyzer auditor plugin:
/// - A missing Invoke-ScriptAnalyzer shim — or a missing pwsh host, which fails
///   the shim's --version probe — is an infrastructure failure naming the tool
///   (never a pass, never a finding).
/// - The shim-owned exit convention: 0 = ran clean, 2 = ran with diagnostics; every
///   other exit — including the common-convention 1 — is infrastructure.
/// - The JSON report maps to findings with the rule id and file/line preserved;
///   severities go through the declared mapping (Information → Info, ParseError →
///   Error), never raw pass-through.
/// - -Settings is always emitted: unset SettingsPath → a generated empty settings
///   file (defeating PSScriptAnalyzerSettings.psd1 auto-discovery); a preset name
///   passes verbatim; a path resolving inside the audited worktree (including via
///   canonicalized dot segments or symlinks) or carrying wildcard metacharacters
///   is rejected deterministically.
/// - Reserved ExtraArguments flags are rejected deterministically, including
///   case-insensitive prefix spellings (-Set binds -Settings), parameter aliases
///   (-PSPath, -wi, -cf), and Unicode-dash spellings (U+2013–U+2015).
/// - Plugin is disabled by default, and its tools are absent from baseline
///   provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_psscriptanalyzer", "true")]
///   exercise the shim end to end; they need pwsh, the PSScriptAnalyzer module, and
///   the Invoke-ScriptAnalyzer shim on PATH.
/// </summary>
public sealed class PsScriptAnalyzerAuditorTests
{
    // A parse-error record's RuleName is the parser's ErrorId
    // (MissingEndCurlyBrace) — only its Severity is 'ParseError'.
    private const string JsonWithFindings = """
        [
          {
            "RuleName": "PSAvoidUsingWriteHost",
            "Severity": "Warning",
            "Message": "File 'build.ps1' uses Write-Host. Avoid using Write-Host because it might not work in all hosts.",
            "File": "/work/build.ps1",
            "Line": 2
          },
          {
            "RuleName": "MissingEndCurlyBrace",
            "Severity": "ParseError",
            "Message": "The script 'deploy.ps1' has a missing closing brace.",
            "File": "/work/deploy.ps1",
            "Line": 14
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWithSeverities = """
        [
          { "RuleName": "R1", "Severity": "Error", "Message": "e", "File": "a.ps1", "Line": 1 },
          { "RuleName": "R2", "Severity": "ParseError", "Message": "p", "File": "b.ps1", "Line": 2 },
          { "RuleName": "R3", "Severity": "Warning", "Message": "w", "File": "c.ps1", "Line": 3 },
          { "RuleName": "R4", "Severity": "Information", "Message": "i", "File": "d.ps1", "Line": 4 },
          { "RuleName": "R5", "Severity": "SomethingNew", "Message": "u", "File": "e.ps1", "Line": 5 }
        ]
        """;

    private const string JsonWithFilteredPaths = """
        [
          { "RuleName": "PSAvoidUsingWriteHost", "Severity": "Error", "Message": "x", "File": "/work/scripts/build.ps1", "Line": 1 },
          { "RuleName": "PSAvoidUsingWriteHost", "Severity": "Error", "Message": "x", "File": "/work/vendor/mod/x.ps1", "Line": 2 },
          { "RuleName": "PSAvoidUsingWriteHost", "Severity": "Error", "Message": "x", "File": "/work/third_party/x.ps1", "Line": 3 },
          { "RuleName": "PSAvoidUsingWriteHost", "Severity": "Error", "Message": "x", "File": "/work/node_modules/pkg/x.ps1", "Line": 4 }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingInvokeScriptAnalyzer_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingPwsh_IsInfrastructureFailure_NeverAPass()
    {
        // pwsh is the shim's interpreter: when it is absent the shim itself
        // cannot execute, so the --version probe fails as an exec error
        // (127) and the run dies at the version check naming the declared
        // tool — still infrastructure, still never a pass.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "/usr/bin/env: 'pwsh': No such file or directory"));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
        // The failure must come from the version probe itself — not a later
        // guard — so pin the reason, not just the tool name.
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingTheTool()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "module not found"));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.24.0\n", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.24.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PsScriptAnalyzerAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(2, JsonWithFindings, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var warning = Assert.Single(
            result.Findings, f => f.Title.Contains("PSAvoidUsingWriteHost", StringComparison.Ordinal));
        Assert.Equal("codeybox:psscriptanalyzer", warning.AuditorName);
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("build.ps1:2", warning.Location);

        var parseError = Assert.Single(
            result.Findings, f => f.Title.Contains("MissingEndCurlyBrace", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, parseError.Severity);
        Assert.Equal("deploy.ps1:14", parseError.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("Invoke-ScriptAnalyzer", argv[0]);
        var pathIndex = argv.ToList().IndexOf("-Path");
        Assert.True(pathIndex >= 0 && pathIndex + 1 < argv.Count);
        Assert.Equal(".", argv[pathIndex + 1]);
        Assert.Contains("-Recurse", argv);
        // Structured argv only — no shell wrapper around the tool arguments.
        Assert.DoesNotContain(argv, a => a.Contains("Invoke-ScriptAnalyzer -Path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CouldNotRunExits_AreInfrastructureFailure_EvenWithJsonOnStdout(int exitCode)
    {
        // 1 is the pwsh terminating-error exit (missing module, bad parameters);
        // it is NOT a findings convention even when bytes resembling a report
        // ride stdout — the declared findings exits are exactly {0, 2}.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(exitCode, JsonWithFindings, "The term 'Invoke-ScriptAnalyzer' is not recognized"));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsExit_WithoutReport_IsInfrastructureFailure()
    {
        // Exit 2 declares diagnostics; absent stdout means the report contract
        // broke — "could not confirm results" is infrastructure, never a pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(2, "", ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsExit_WithEmptyReport_IsInfrastructureFailure()
    {
        // Exit 2 but a parseable EMPTY report — shim/version contract drift,
        // not a clean tree.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(2, "[]", ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsNativeTokens_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(2, JsonWithSeverities, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);
        var byLocation = result.Findings.ToDictionary(f => f.Location!, StringComparer.Ordinal);
        Assert.Equal(AuditSeverity.Error, byLocation["a.ps1:1"].Severity);   // Error
        Assert.Equal(AuditSeverity.Error, byLocation["b.ps1:2"].Severity);   // ParseError
        Assert.Equal(AuditSeverity.Warning, byLocation["c.ps1:3"].Severity); // Warning
        Assert.Equal(AuditSeverity.Info, byLocation["d.ps1:4"].Severity);    // Information
        Assert.Equal(AuditSeverity.Warning, byLocation["e.ps1:5"].Severity); // unknown -> Warning
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than the
        // severity field itself.
        Assert.Contains("Information", byLocation["d.ps1:4"].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanRootProbe_EmptyOutput_IsInfrastructureFailure()
    {
        // A pwd probe that exits 0 but prints nothing must not degrade to a
        // null scan root — absolute paths would survive normalization and the
        // vendored-path exclusions would silently stop matching.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPwdProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scan root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanRootProbe_WhitespaceEndingRoot_StillRelativizesFindings()
    {
        // A canonical worktree path may legitimately end in whitespace — a
        // legal POSIX leaf name. The probed root must reach path
        // relativization verbatim: trimming it would leave a finding's
        // absolute path unrelativized (file://-marked) instead of
        // repo-relative.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPwdProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work \n", ""));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(2, """
                    [
                      {
                        "RuleName": "PSAvoidUsingWriteHost",
                        "Severity": "Warning",
                        "Message": "File 'build.ps1' uses Write-Host.",
                        "File": "/work /build.ps1",
                        "Line": 2
                      }
                    ]
                    """, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("build.ps1:2", finding.Location);
    }

    [Theory]
    [InlineData("settings.psd1")]
    [InlineData("./config/pssa.psd1")]
    [InlineData("/work/settings.psd1")]
    [InlineData("/work/sub/settings.psd1")]
    [InlineData("/work/../work/nested.psd1")]
    [InlineData("..pssa.psd1")]
    [InlineData("/work/..pssa.psd1")]
    // A name-shaped value that is NOT a shipped preset resolves as a file
    // path relative to the cmdlet's cwd — the worktree — so it must go
    // through canonicalization like any other path.
    [InlineData("MyCustomSettings")]
    [InlineData("PSScriptAnalyzerSettings.psd1")]
    public async Task InTreeSettingsPath_IsRejectedDeterministically(string settingsPath)
    {
        // A settings file resolving inside the audited tree — however spelled —
        // hands rule selection and custom rule paths to the diff author:
        // rejected on canonicalization, before the scan ever runs.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
                return Task.FromResult(RealpathOk(exec));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = settingsPath,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("SettingsPath", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task InTreeSettingsPath_ViaSymlinkedResolution_IsRejectedDeterministically()
    {
        // The configured path is lexically OUTSIDE the tree, but realpath -m
        // resolves a symlinked component into it — a lexical-only check would
        // wave repo-controlled gate configuration through.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
                return Task.FromResult(RealpathOk(exec, _ => "/work/policy-link.psd1"));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = "/opt/pssa-policy/link.psd1",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("SettingsPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/work/policy-link.psd1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InTreeSettingsPath_UnderWhitespaceEndingRoot_IsRejectedDeterministically()
    {
        // The canonical worktree root itself ends in whitespace (a legal
        // POSIX leaf name) — "realpath -m" emits '/work ' for the cwd. If
        // the probe output were trimmed, the root would mis-derive as
        // '/work' and an in-tree '/work /settings.psd1' would be judged
        // OUTSIDE the tree, slipping repository-controlled gate
        // configuration past containment. The canonical bytes must be
        // compared verbatim.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work /settings.psd1\n/work \n", ""));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = "/opt/pssa-policy/settings.psd1",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("SettingsPath", ex.Message, StringComparison.Ordinal);
        Assert.Contains("inside the audited worktree", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    // Every preset name the pinned module ships must pass verbatim —
    // a missing entry would fail closed as an in-tree path rejection.
    [InlineData("CmdletDesign")]
    [InlineData("CodeFormatting")]
    [InlineData("CodeFormattingAllman")]
    [InlineData("CodeFormattingOTBS")]
    [InlineData("CodeFormattingStroustrup")]
    [InlineData("DSC")]
    [InlineData("PSGallery")]
    [InlineData("ScriptFunctions")]
    [InlineData("ScriptingStyle")]
    [InlineData("ScriptSecurity")]
    public async Task SettingsPath_PresetName_PassesThroughWithoutProbe(string preset)
    {
        var settingsProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
            {
                settingsProbes++;
                return Task.FromResult(RealpathOk(exec));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = preset,
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, settingsProbes);
        Assert.NotNull(scanExec);
        var settingsIndex = scanExec!.Argv.ToList().IndexOf("-Settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < scanExec.Argv.Count);
        Assert.Equal(preset, scanExec.Argv[settingsIndex + 1]);
    }

    [Fact]
    public async Task SettingsPath_Unset_AlwaysEmitsGeneratedSettingsFile()
    {
        // With no -Settings the cmdlet auto-discovers
        // PSScriptAnalyzerSettings.psd1 in the -Path directory — a file the
        // diff author could commit to empty the report. The flag must
        // therefore always be present: unset config names the generated
        // empty settings file under the per-run scratch dir, prepared by
        // VerifyToolAsync.
        var prepExecs = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsPrepProbe(exec))
            {
                prepExecs++;
                return Task.FromResult(Ok(exec));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, prepExecs);
        Assert.NotNull(scanExec);
        var settingsIndex = scanExec!.Argv.ToList().IndexOf("-Settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < scanExec.Argv.Count);
        var settingsValue = scanExec.Argv[settingsIndex + 1];
        Assert.EndsWith("/" + PsScriptAnalyzerAuditor.EmptySettingsFileName, settingsValue, StringComparison.Ordinal);
        // The named file must live OUTSIDE the audited worktree.
        Assert.DoesNotContain("/work", settingsValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsPreparation_Failure_IsInfrastructureFailure()
    {
        // The generated -Settings file cannot be written → the flag would
        // name a missing file; fail closed rather than scanning.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsPrepProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "mkdir: cannot create directory"));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Invoke-ScriptAnalyzer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("settings file", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    // The cmdlet resolves a non-preset -Settings through a globbing
    // provider-path resolver — a wildcard could expand to an in-tree file
    // the canonicalization check never sees, so metacharacters are rejected
    // before any probe runs.
    [InlineData("/opt/*.psd1")]
    [InlineData("conf?g.psd1")]
    [InlineData("/opt/pssa/settings[0-9].psd1")]
    public async Task SettingsPath_Wildcards_AreRejectedDeterministically(string settingsPath)
    {
        var settingsProbes = 0;
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
                settingsProbes++;
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = settingsPath,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("wildcard", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, settingsProbes);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SettingsPath_CanonicalizedToGlobCharacters_IsRejectedDeterministically()
    {
        // The configured value carries no metacharacters, but realpath
        // resolves through a symlinked directory literally named "pol[ic]y" —
        // the canonical string argv hands the cmdlet now carries glob
        // characters the cmdlet's provider-path resolver would expand to a
        // file the containment check never judged.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
                return Task.FromResult(RealpathOk(exec, _ => "/data/pol[ic]y/settings.psd1"));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = "/opt/pssa-policy/settings.psd1",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("wildcard", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SettingsPath_OutsideWorktree_PassesCanonicalized()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSettingsProbe(exec))
                return Task.FromResult(RealpathOk(exec));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // Dot segments canonicalize to the same out-of-tree path —
                // argv carries the canonical spelling realpath returned.
                ["Scoped:SettingsPath"] = "/opt/../opt/pssa/settings.psd1",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var settingsIndex = scanExec!.Argv.ToList().IndexOf("-Settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < scanExec.Argv.Count);
        Assert.Equal("/opt/pssa/settings.psd1", scanExec.Argv[settingsIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_TargetPath_ShapesArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetPath"] = "scripts/",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var pathIndex = scanExec!.Argv.ToList().IndexOf("-Path");
        Assert.True(pathIndex >= 0 && scanExec.Argv[pathIndex + 1] == "scripts/");
    }

    [Fact]
    public async Task TargetPath_LeadingDash_IsRejectedDeterministically()
    {
        // A dash-leading -Path value would be read as another parameter by the
        // cmdlet's binder — fail closed at the argv boundary instead.
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(new SandboxExecResult(1, "", "unexpected exec")));

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetPath"] = "-ExcludeRule",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("TargetPath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-Path")]
    [InlineData("-path:sub/dir")]
    [InlineData("-ScriptDefinition")]
    [InlineData("-Recurse")]
    [InlineData("-Recurse:$false")]
    [InlineData("-Settings")]
    [InlineData("-Profile")]
    [InlineData("-Fix")]
    [InlineData("-SuppressedOnly")]
    [InlineData("-IncludeSuppressed")]
    [InlineData("-ReportSummary")]
    [InlineData("-SaveDscDependency")]
    [InlineData("-EnableExit")]
    [InlineData("-WhatIf")]
    [InlineData("-Confirm")]
    // Parameter aliases bind exactly like the parameter name.
    [InlineData("-PSPath")]
    [InlineData("-wi")]
    [InlineData("-cf")]
    // Custom rule paths load repository-controlled module code resolved
    // against the cmdlet's cwd — the same threat the SettingsPath
    // containment guard exists for.
    [InlineData("-CustomRulePath")]
    [InlineData("-CustomizedRulePath")]
    [InlineData("-RecurseCustomRulePath")]
    [InlineData("-IncludeDefaultRules")]
    // A cmdlet-level ErrorAction would override the shim's
    // $ErrorActionPreference=Stop and turn mid-scan failures into a
    // silently partial report.
    [InlineData("-ErrorAction")]
    [InlineData("-ea")]
    // PowerShell binds unambiguous parameter-name prefixes case-insensitively —
    // "-set" binds -Settings — so the rejection must see through both.
    [InlineData("-set")]
    [InlineData("-SET:/opt/x.psd1")]
    // The parameter-token 'dash' production also accepts U+2013/U+2014/U+2015;
    // '–Settings' binds -Settings.
    [InlineData("–Settings")]
    [InlineData("—WhatIf")]
    [InlineData("―Recurse")]
    public async Task ReservedExtraArguments_AreRejectedDeterministically(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = flag,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task AllowedExtraArguments_PassThroughToCmdlet()
    {
        // Rule/severity selection without a settings file: plain cmdlet flags
        // ride ExtraArguments as structured argv entries.
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PsScriptAnalyzerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-IncludeRule,PSAvoid*,-Severity,Error,Warning",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var includeIndex = argv.IndexOf("-IncludeRule");
        Assert.True(includeIndex >= 0 && argv[includeIndex + 1] == "PSAvoid*");
        var severityIndex = argv.IndexOf("-Severity");
        Assert.True(severityIndex >= 0 && argv[severityIndex + 1] == "Error" && argv[severityIndex + 2] == "Warning");
    }

    [Fact]
    public async Task DefaultExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(2, JsonWithFilteredPaths, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("scripts/build.ps1:1", finding.Location);
    }

    [Fact]
    public async Task ReportedPath_WithDotDotSegments_IsMarkedOutOfTree()
    {
        // "/work/../outside.ps1" collapses to "/outside.ps1" — above the
        // scan root — so it must reach the finding with the out-of-tree
        // file:// marker, not as the pseudo repo-relative "../outside.ps1".
        const string json = """
            [
              { "RuleName": "R", "Severity": "Error", "Message": "x", "File": "/work/../outside.ps1", "Line": 7 },
              { "RuleName": "R", "Severity": "Error", "Message": "x", "File": "../relative-escape.ps1", "Line": 8 },
              { "RuleName": "R", "Severity": "Error", "Message": "x", "File": "a/../b.ps1", "Line": 9 }
            ]
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsPwdProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(2, json, ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new PsScriptAnalyzerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);
        var locations = result.Findings.Select(f => f.Location).Order().ToList();
        Assert.Equal(
            ["b.ps1:9", "file://../relative-escape.ps1:8", "file:///outside.ps1:7"],
            locations);
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolsAbsentFromBaselineProvisioning()
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
            s => s.PluginId == PsScriptAnalyzerAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("Invoke-ScriptAnalyzer", flattened, StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PsScriptAnalyzerAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PsScriptAnalyzerAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Binary == "Invoke-ScriptAnalyzer");
        Assert.Contains(tools, t => t.Binary == "pwsh");
        // Verify-only by design: no distro package carries a pinned
        // PSScriptAnalyzer module, and the shim is operator-provisioned.
        Assert.All(tools, t => Assert.Null(t.AptPackage));

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Equal(2, contributions.VerificationCommands.Count);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_psscriptanalyzer", "true")]
    public async Task RealInvokeScriptAnalyzer_BadScriptFixture_ProducesFinding()
    {
        var installed = await ProbeInstalledModuleVersionAsync();
        if (installed is null)
            return;

        var fixtureDir = await SeedFixtureRepoAsync(misconfigured: true);

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

            var auditor = new PsScriptAnalyzerAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // The deliverable: a real scan over a fixture with a known
            // PowerShell issue produces a finding carrying its rule id and
            // location.
            Assert.Contains(result.Findings,
                f => f.Location != null
                    && f.Location.EndsWith(".ps1:2", StringComparison.Ordinal)
                    && f.Title.Contains("PSAvoidUsingWriteHost", StringComparison.Ordinal));
            Assert.All(result.Findings, f => Assert.Equal("codeybox:psscriptanalyzer", f.AuditorName));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_psscriptanalyzer", "true")]
    public async Task RealInvokeScriptAnalyzer_CleanFixture_Passes()
    {
        var installed = await ProbeInstalledModuleVersionAsync();
        if (installed is null)
            return;

        var fixtureDir = await SeedFixtureRepoAsync(misconfigured: false);

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

            var auditor = new PsScriptAnalyzerAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PsScriptAnalyzerAuditorPlugin.dll");
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
            PluginId: PsScriptAnalyzerAuditor.PluginId,
            PluginDisplayName: "CodeyBox: PSScriptAnalyzer PowerShell Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, PsScriptAnalyzerAuditor.DefaultExpectedVersion + "\n", "")
            : IsPwdProbe(exec)
                ? new SandboxExecResult(0, "/work\n", "")
                : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("Invoke-ScriptAnalyzer", StringComparer.Ordinal);

    // The VerifyToolAsync preparation exec: sh -c '<dir + empty settings
    // file script>' sh <per-run dir>. Identified by the generated filename
    // baked into the fixed script.
    private static bool IsSettingsPrepProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains(PsScriptAnalyzerAuditor.EmptySettingsFileName, StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "Invoke-ScriptAnalyzer" && exec.Argv[1] == "--version";

    private static bool IsScanExec(SandboxExec exec)
        => exec.Argv.Count >= 3 && exec.Argv[0] == "Invoke-ScriptAnalyzer" && exec.Argv[1] == "-Path";

    private static bool IsPwdProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    // The settings-file canonicalization probe: realpath -m -- <configured> .
    private static bool IsSettingsProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "realpath"
            && exec.Argv[1] == "-m";

    // Emulates the realpath probe: canonicalizes the configured path like
    // realpath -m (relative input resolved against the /work cwd, dot
    // segments collapsed) — or via the supplied override for simulated
    // symlink targets — then echoes it plus the canonical cwd, one per line.
    private static SandboxExecResult RealpathOk(SandboxExec exec, Func<string, string>? canonicalize = null)
    {
        var configured = exec.Argv[3];
        var canonical = canonicalize?.Invoke(configured) ?? FakeRealpathM(configured);
        return new SandboxExecResult(0, canonical + "\n/work\n", "");
    }

    private static string FakeRealpathM(string path)
    {
        var combined = path.StartsWith("/", StringComparison.Ordinal) ? path : "/work/" + path;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    private static async Task<string> SeedFixtureRepoAsync(bool misconfigured)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-pssa-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (misconfigured)
        {
            // Write-Host trips PSAvoidUsingWriteHost on any PSScriptAnalyzer
            // release; 'hi' at line 2 keeps the location deterministic.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "build.ps1"),
                "# fixture\nWrite-Host 'hello'\n");
        }
        else
        {
            // A clean PowerShell file: no diagnostics — the honest clean case.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "build.ps1"),
                "param([string]$Name)\nWrite-Output \"Hello, $Name\"\n");
        }

        return dir;
    }

    private static async Task<string?> ProbeInstalledModuleVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "Invoke-ScriptAnalyzer",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            await stderr;
            var version = (await stdout).Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
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
