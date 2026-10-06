using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.KyvernoAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the kyverno auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming kyverno (never a pass or finding).
/// - Missing PolicyPaths fails closed deterministically (an unevaluated tree never reads as clean).
/// - In-tree PolicyPaths fail closed: the candidate must not weaken operator-owned policy.
/// - Exits 0 and 1 are verdicts; a run failure also exits 1 but writes no JSON report —
///   the parser fails closed so "could not run" is infrastructure, not findings.
/// - Vacuous reports (zero evaluated results) fail closed as infrastructure — never a pass.
/// - Policy-report results map to findings with kyverno/&lt;policy&gt;/&lt;rule&gt; rule ids and
///   resource-identity messages (kyverno reports coordinates, not file paths).
/// - Outcomes flow through the declared severity mapping: fail/error/skip (and unknown) are
///   Error, warn is Warning, pass is dropped — raw tool tokens never reach the severity field.
/// - Flag-looking ExtraArguments are rejected deterministically (they could re-enable the
///   cluster/mutation/exception/API-context paths this offline auditor never emits).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_kyverno", "true")] use fixture-local
///   policies outside the mounted worktree, so they need the binary but no network.
/// </summary>
public sealed class KyvernoAuditorTests
{
    private static readonly string? InstalledKyvernoVersion = ProbeInstalledKyvernoVersion();

    private const string OutsidePolicyPath = "/opt/codeybox/policies";

    private const string ReportWithMixedResults = """
        {
          "apiVersion": "wgpolicyk8s.io/v1alpha2",
          "kind": "PolicyReport",
          "results": [
            {
              "policy": "require-labels",
              "rule": "require-app-label",
              "result": "fail",
              "message": "label 'app' is required",
              "resources": [
                { "apiVersion": "apps/v1", "kind": "Deployment", "name": "web", "namespace": "default" }
              ]
            },
            {
              "policy": "require-labels",
              "rule": "require-owner-label",
              "result": "warn",
              "message": "label 'owner' should be set",
              "resources": [
                { "apiVersion": "apps/v1", "kind": "Deployment", "name": "web", "namespace": "default" }
              ]
            },
            {
              "policy": "disallow-latest-tag",
              "rule": "require-digest",
              "result": "error",
              "message": "rule could not be evaluated: registry access is unavailable offline",
              "resources": [
                { "apiVersion": "v1", "kind": "Pod", "name": "worker" }
              ]
            },
            {
              "policy": "pod-security",
              "rule": "run-as-non-root",
              "result": "skip",
              "message": "rule skipped: no securityContext present",
              "resources": [
                { "apiVersion": "v1", "kind": "Pod", "name": "worker" }
              ]
            },
            {
              "policy": "require-labels",
              "rule": "require-team-label",
              "result": "pass",
              "message": "label 'team' is present",
              "resources": [
                { "apiVersion": "v1", "kind": "ConfigMap", "name": "cfg", "namespace": "default" }
              ]
            }
          ],
          "summary": { "pass": 1, "fail": 1, "warn": 1, "error": 1, "skip": 1 }
        }
        """;

    private const string ReportClean = """
        {
          "apiVersion": "wgpolicyk8s.io/v1alpha2",
          "kind": "PolicyReport",
          "results": [
            {
              "policy": "require-labels",
              "rule": "require-app-label",
              "result": "pass",
              "message": "label 'app' is present",
              "resources": [
                { "apiVersion": "apps/v1", "kind": "Deployment", "name": "web", "namespace": "default" }
              ]
            }
          ],
          "summary": { "pass": 1, "fail": 0, "warn": 0, "error": 0, "skip": 0 }
        }
        """;

    private const string ReportCleanPassOnlySummary = """
        {
          "apiVersion": "wgpolicyk8s.io/v1alpha2",
          "kind": "PolicyReport",
          "results": [],
          "summary": { "pass": 3, "fail": 0, "warn": 0, "error": 0, "skip": 0 }
        }
        """;

    private const string ReportVacuous = """
        {
          "apiVersion": "wgpolicyk8s.io/v1alpha2",
          "kind": "PolicyReport",
          "results": [],
          "summary": { "pass": 0, "fail": 0, "warn": 0, "error": 0, "skip": 0 }
        }
        """;

    private const string ReportWithOutcomes = """
        {
          "apiVersion": "wgpolicyk8s.io/v1alpha2",
          "kind": "PolicyReport",
          "results": [
            { "policy": "p", "rule": "r-fail", "result": "fail", "message": "denied",
              "resources": [{ "kind": "Deployment", "name": "a" }] },
            { "policy": "p", "rule": "r-error", "result": "error", "message": "could not evaluate",
              "resources": [{ "kind": "Pod", "name": "b" }] },
            { "policy": "p", "rule": "r-skip", "result": "skip", "message": "not evaluated",
              "resources": [{ "kind": "Service", "name": "c" }] },
            { "policy": "p", "rule": "r-warn", "result": "warn", "message": "advisory",
              "resources": [{ "kind": "ConfigMap", "name": "d" }] },
            { "policy": "p", "rule": "r-future", "result": "somethingNew", "message": "unknown future outcome",
              "resources": [{ "kind": "Secret", "name": "e" }] },
            { "policy": "p", "rule": "r-pass", "result": "pass", "message": "clean",
              "resources": [{ "kind": "ConfigMap", "name": "f" }] }
          ],
          "summary": { "pass": 1, "fail": 1, "warn": 1, "error": 1, "skip": 1 }
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingKyverno_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "kyverno: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingKyverno()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "unknown command \"version\""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Version: v0.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(KyvernoAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingPolicyPaths_FailsClosedDeterministically_NeverAPass()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new KyvernoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("PolicyPaths", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task InTreePolicyPath_FailsClosed_BeforeScan()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work/policies\n/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:PolicyPaths"] = "policies",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("codeybox.kyverno:PolicyPaths", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithMixedResults_YieldsFindings_WithRuleIdAndResourceIdentity()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, ReportWithMixedResults, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);

        var fail = Assert.Single(
            result.Findings, f => f.Title.Contains("kyverno/require-labels/require-app-label", StringComparison.Ordinal));
        Assert.Equal("codeybox:kyverno", fail.AuditorName);
        Assert.Equal(AuditSeverity.Error, fail.Severity);
        Assert.Contains("Deployment 'web'", fail.Description, StringComparison.Ordinal);
        Assert.Contains("label 'app' is required", fail.Description, StringComparison.Ordinal);

        var warn = Assert.Single(
            result.Findings, f => f.Title.Contains("kyverno/require-labels/require-owner-label", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);

        var error = Assert.Single(
            result.Findings, f => f.Title.Contains("kyverno/disallow-latest-tag/require-digest", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var skip = Assert.Single(
            result.Findings, f => f.Title.Contains("kyverno/pod-security/run-as-non-root", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, skip.Severity);

        // The passing rule is dropped, never a finding.
        Assert.DoesNotContain(result.Findings,
            f => f.Title.Contains("require-team-label", StringComparison.Ordinal));

        Assert.NotNull(scanExec);
        Assert.Equal("kyverno", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("apply", argv);
        var policyIndex = argv.ToList().IndexOf("--policy");
        Assert.True(policyIndex >= 0 && policyIndex + 1 < argv.Count);
        Assert.Equal(OutsidePolicyPath, argv[policyIndex + 1]);
        var resourceIndex = argv.ToList().IndexOf("--resource");
        Assert.True(resourceIndex >= 0 && resourceIndex + 1 < argv.Count);
        Assert.Equal(".", argv[resourceIndex + 1]);
        var formatIndex = argv.ToList().IndexOf("--output-format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("json", argv[formatIndex + 1]);
        Assert.Contains("--policy-report", argv);
        Assert.Contains("--continue-on-fail", argv);
        // Offline contract: no cluster, kubeconfig, registry, output, or exception flags.
        Assert.DoesNotContain("--cluster", argv);
        Assert.DoesNotContain("--kubeconfig", argv);
        Assert.DoesNotContain("--registry", argv);
        Assert.DoesNotContain("--output", argv);
        Assert.DoesNotContain("--exception", argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task CleanPassOnlySummary_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportCleanPassOnlySummary, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task SeverityMapping_MapsOutcomes_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithOutcomes, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // fail, error, skip, and the unrecognized outcome are findings at
        // Error; warn is advisory at Warning; pass is never reported.
        Assert.Equal(5, result.Findings.Count);
        Assert.All(result.Findings.Where(f => !f.Title.Contains("r-warn", StringComparison.Ordinal)),
            f => Assert.Equal(AuditSeverity.Error, f.Severity));
        var warn = Assert.Single(result.Findings, f => f.Title.Contains("r-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("r-pass", StringComparison.Ordinal));
        Assert.False(result.Passed);

        // The raw tool outcome is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than the
        // severity field itself.
        var fail = Assert.Single(result.Findings, f => f.Title.Contains("r-fail", StringComparison.Ordinal));
        Assert.Contains("fail", fail.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportArray_ParsesEachDocument()
    {
        var arrayReport = "[" + ReportWithMixedResults + "," + ReportClean + "]";
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, arrayReport, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);
    }

    [Fact]
    public async Task VacuousReport_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportVacuous, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdout_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonStdout_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "this is not json", "Error: unknown flag: --bogus"));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedStdout_IsInfrastructureFailure()
    {
        var truncated = ReportWithMixedResults[..(ReportWithMixedResults.Length / 2)];
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, truncated, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "Error: policy not found: /opt/codeybox/policies"));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "unexpected exit"));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "kyverno: command not found"));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagLikeExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:ExtraArguments"] = "--cluster",
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_OverrideDefaultScope()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Targets"] = "manifests/, deploy/app.yaml",
        });
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain(".", argv);
        Assert.Contains("manifests/", argv);
        Assert.Contains("deploy/app.yaml", argv);
        var resourceIndexes = argv
            .Select((value, index) => (value, index))
            .Where(pair => pair.value == "--resource")
            .Select(pair => pair.index)
            .ToList();
        Assert.Equal(2, resourceIndexes.Count);
    }

    [Fact]
    public async Task ScopedConfiguration_MultiplePolicyPaths_EmitRepeatablePolicyFlags()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/opt/policies-a\n/work\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:PolicyPaths"] = "/opt/policies-a, /opt/policies-b",
        });
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var policyIndexes = argv
            .Select((value, index) => (value, index))
            .Where(pair => pair.value == "--policy")
            .Select(pair => pair.index)
            .ToList();
        Assert.Equal(2, policyIndexes.Count);
        Assert.Equal("/opt/policies-a", argv[policyIndexes[0] + 1]);
        Assert.Equal("/opt/policies-b", argv[policyIndexes[1] + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersMatchingFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithMixedResults, ""));
        });

        var auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:ExcludedRules"] = "kyverno/require-labels/require-app-label",
        });
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);
        Assert.DoesNotContain(result.Findings,
            f => f.Title.Contains("require-app-label", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledToken_PropagatesCancellation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec))
                return Ok(exec);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            ct.ThrowIfCancellationRequested();
            return new SandboxExecResult(0, ReportClean, "");
        });

        var auditor = await ConfiguredAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:TimeoutSeconds"] = "1",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kyverno", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == KyvernoAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("kyverno", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresKyvernoRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [KyvernoAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == KyvernoAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("kyverno", tool.Binary);
        // Verify-only by design: no distro package carries kyverno, so the
        // baseline must be provisioned with the pinned upstream release.
        Assert.True(string.IsNullOrWhiteSpace(tool.AptPackage), "kyverno must not declare an AptPackage.");
    }

    [Fact]
    [Trait("requires_kyverno", "true")]
    public async Task RealKyverno_ViolatingFixture_ProducesFinding()
    {
        var installed = InstalledKyvernoVersion;
        if (installed is null)
            return;

        var fixture = await SeedKyvernoFixtureRepoAsync(labeled: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixture.RepoDir }],
                },
                CancellationToken.None);

            var auditor = new KyvernoAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    // Operator-owned policies live outside the mounted
                    // worktree; the host path resolves identically inside
                    // the process sandbox.
                    ["Scoped:PolicyPaths"] = fixture.PolicyDir,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings,
                f => f.Title.Contains("kyverno/require-labels/require-app-label", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("Deployment 'web'", finding.Description, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixture.RootDir);
        }
    }

    [Fact]
    [Trait("requires_kyverno", "true")]
    public async Task RealKyverno_CleanFixture_Passes()
    {
        var installed = InstalledKyvernoVersion;
        if (installed is null)
            return;

        var fixture = await SeedKyvernoFixtureRepoAsync(labeled: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixture.RepoDir }],
                },
                CancellationToken.None);

            var auditor = new KyvernoAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:PolicyPaths"] = fixture.PolicyDir,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixture.RootDir);
        }
    }

    private static async Task<IAuditor> ConfiguredAuditorAsync(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var merged = new Dictionary<string, string?>(scopedValues);
        if (!scopedValues.ContainsKey("Scoped:PolicyPaths"))
            merged["Scoped:PolicyPaths"] = OutsidePolicyPath;
        var auditor = new KyvernoAuditor();
        await auditor.InitializeAsync(BuildPluginContext(merged), CancellationToken.None);
        return auditor;
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.KyvernoAuditorPlugin.dll");
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
            PluginId: KyvernoAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Kyverno Policy Audit",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Version: v" + KyvernoAuditor.DefaultExpectedVersion + "\n", "")
            : IsRealpathProbe(exec)
                ? new SandboxExecResult(0, OutsidePolicyPath + "\n/work\n", "")
                : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("kyverno", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "kyverno" && exec.Argv[1] == "version";

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count > 0 && exec.Argv[0] == "realpath";

    private static async Task<(string RootDir, string RepoDir, string PolicyDir)> SeedKyvernoFixtureRepoAsync(bool labeled)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "codeybox-kyverno-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var repo = Path.Combine(root, "repo");
        var policies = Path.Combine(root, "policies");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(policies);

        var policy = """
            apiVersion: kyverno.io/v1
            kind: ClusterPolicy
            metadata:
              name: require-labels
            spec:
              validationFailureAction: Audit
              rules:
              - name: require-app-label
                match:
                  resources:
                    kinds:
                    - Deployment
                validate:
                  message: "label 'app' is required"
                  pattern:
                    metadata:
                      labels:
                        app: "?*"
            """;
        await File.WriteAllTextAsync(Path.Combine(policies, "require-labels.yaml"), policy);

        var manifest = labeled
            ? """
              apiVersion: apps/v1
              kind: Deployment
              metadata:
                name: web
                namespace: default
                labels:
                  app: web
              spec:
                selector:
                  matchLabels:
                    app: web
                template:
                  metadata:
                    labels:
                      app: web
                  spec:
                    containers:
                    - name: web
                      image: nginx:1.25.3
              """
            : """
              apiVersion: apps/v1
              kind: Deployment
              metadata:
                name: web
                namespace: default
              spec:
                selector:
                  matchLabels:
                    app: web
                template:
                  metadata:
                    labels:
                      app: web
                  spec:
                    containers:
                    - name: web
                      image: nginx:1.25.3
              """;
        await File.WriteAllTextAsync(Path.Combine(repo, "deploy.yaml"), manifest);

        return (root, repo, policies);
    }

    private static string? ProbeInstalledKyvernoVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kyverno",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            stderr.GetAwaiter().GetResult();
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
