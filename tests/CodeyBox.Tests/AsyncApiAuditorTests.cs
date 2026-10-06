using System.Diagnostics;
using CodeyBox.AsyncApiAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the AsyncAPI auditor plugin:
/// - Missing binary or wrong-version binary is an infrastructure failure naming asyncapi (never a pass or finding).
/// - Both specs must be existing local repository-relative documents; URLs, context names, absolute
///   paths, ".." escapes, and identical old/new pointers are deterministic infrastructure failures.
/// - Both documents must pass `asyncapi validate`; an invalid baseline or candidate is coverage
///   unavailable (never a pass, never a finding) and the scan never runs.
/// - Exit codes 0 and 1 are findings-producing candidates; the parser tells
///   "breaking changes found" apart from "could not run" by report content.
/// - Breaking/unclassified/non-breaking entries map to findings with rule ids, the
///   candidate document as location, and mapped (never raw) severities.
/// - Malformed, truncated, and wrong-shape output all fail closed as infrastructure.
/// - The configured compatibility policy selects `--type`; contract-altering extra arguments
///   (`--no-error`, `--watch`, `--overrides`, `--save-output`, diagnostics/proxy flags, ...)
///   are rejected before any exec.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_asyncapi", "true")].
/// </summary>
public sealed class AsyncApiAuditorTests
{
    private static readonly string? InstalledAsyncApiVersion = ProbeInstalledAsyncApiVersion();

    // Shaped like the verified `@asyncapi/cli` 5.0.7 contract:
    // `asyncapi diff old.yaml new.yaml --format json --type all` prints
    // `{"changes": [...]}` on stdout, then exits 1 via DiffBreakingChangeError.
    private const string DiffWithBreakingChanges = """
        {
          "changes": [
            { "action": "remove", "path": "/channels/UserRegistered/publish", "type": "breaking" },
            { "action": "edit", "path": "/channels/UserRegistered/publish/message/payload/properties/name", "type": "breaking" },
            { "action": "edit", "path": "/info/description", "type": "unclassified" },
            { "action": "add", "path": "/channels/UserRenamed", "type": "non-breaking" }
          ]
        }
        """;

    // Exit 0: no breaking changes; advisory changes may still be listed.
    private const string DiffAdvisoryOnly = """
        {
          "changes": [
            { "action": "edit", "path": "/info/description", "type": "unclassified" },
            { "action": "add", "path": "/channels/UserRenamed", "type": "non-breaking" }
          ]
        }
        """;

    // Exit 0: identical documents.
    private const string DiffClean = """{ "changes": [] }""";

    // `--type breaking` prints the bare changes array instead of the object wrapper.
    private const string DiffBareBreakingArray = """
        [
          { "action": "remove", "path": "/channels/UserRegistered", "type": "breaking" }
        ]
        """;

    // Exit 1 with no JSON report: a load/parse/validation failure.
    private const string DiffLoadError = """
        AsyncAPIError: There was a problem with the specification file.
        """;

    private const string DefaultOldSpec = "events/asyncapi.baseline.yaml";
    private const string DefaultNewSpec = "events/asyncapi.yaml";

    private const string PinnedAsyncApiVersion = "5.0.7";
    private const string PinnedVersionBanner = "@asyncapi/cli/5.0.7 linux-x64 node-v20.19.0";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, DiffClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "@asyncapi/cli/4.9.0 linux-x64 node-v20.19.0", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, DiffClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChanges_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = StandardSandbox(
            onScan: exec =>
            {
                scanExec = exec;
                return new SandboxExecResult(1, DiffWithBreakingChanges, "DiffBreakingChangeError: Breaking changes detected");
            });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);

        var removed = Assert.Single(result.Findings, f => f.Title.StartsWith("BREAKING_REMOVE:", StringComparison.Ordinal));
        Assert.Equal("codeybox:asyncapi", removed.AuditorName);
        Assert.Equal(AuditSeverity.Error, removed.Severity);
        Assert.Equal(DefaultNewSpec, removed.Location);
        // The tool's raw level ("breaking") is mapped to Error, not passed through.
        Assert.Contains("Severity (tool): breaking", removed.Description, StringComparison.Ordinal);
        Assert.Contains("/channels/UserRegistered/publish", removed.Description, StringComparison.Ordinal);

        var edited = Assert.Single(result.Findings, f => f.Title.StartsWith("BREAKING_EDIT:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, edited.Severity);

        var unclassified = Assert.Single(result.Findings, f => f.Title.StartsWith("UNCLASSIFIED_EDIT:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unclassified.Severity);

        var added = Assert.Single(result.Findings, f => f.Title.StartsWith("NON_BREAKING_ADD:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, added.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal(
            new[] { "asyncapi", "diff", DefaultOldSpec, DefaultNewSpec, "--format", "json", "--type", "all" },
            scanExec!.Argv.ToArray());
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, DiffClean, ""));

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task AdvisoryOnlyChanges_AreAdvisory_AndPass()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, DiffAdvisoryOnly, ""));

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Title.StartsWith("UNCLASSIFIED_EDIT:", StringComparison.Ordinal) && f.Severity == AuditSeverity.Warning);
        Assert.Contains(result.Findings, f => f.Title.StartsWith("NON_BREAKING_ADD:", StringComparison.Ordinal) && f.Severity == AuditSeverity.Info);
    }

    [Fact]
    public async Task BareArray_WithBreakingPolicy_Parses()
    {
        SandboxExec? scanExec = null;
        var sandbox = StandardSandbox(
            onScan: exec =>
            {
                scanExec = exec;
                return new SandboxExecResult(1, DiffBareBreakingArray, "");
            });

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
                ["Scoped:CompatibilityPolicy"] = "breaking",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.StartsWith("BREAKING_REMOVE:", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.NotNull(scanExec);
        Assert.Contains("--type", scanExec!.Argv);
        Assert.Contains("breaking", scanExec.Argv);
    }

    [Fact]
    public async Task LogChatter_WrappedJson_StillParses()
    {
        // Tolerate non-JSON log lines around the single JSON document.
        var wrapped = "Skipping submitting anonymous metrics due to offline sandbox\n"
            + DiffWithBreakingChanges + "\n";
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, wrapped, ""));

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);
    }

    [Fact]
    public async Task Exit1_WithoutJsonReport_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, DiffLoadError, "ValidationError: invalid-file"));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit1_WithOnlyAdvisoryChanges_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            // Exit 1 claims breaking changes, but none are listed: contradiction.
            onScan: _ => new SandboxExecResult(1, DiffAdvisoryOnly, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit0_WithBreakingEntry_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            // Exit 0 claims no breaking changes, but one is listed: contradiction.
            onScan: _ => new SandboxExecResult(0, DiffWithBreakingChanges, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(0, "", ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedJson_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, "{ this is not json", ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedJson_IsInfrastructureFailure()
    {
        // A report cut mid-document by the capture cap must never parse partially.
        var truncated = DiffWithBreakingChanges[..(DiffWithBreakingChanges.Length / 2)];
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, truncated, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCategory_IsInfrastructureFailure_NeverGuessed()
    {
        var report = """{ "changes": [{ "action": "remove", "path": "/channels/X", "type": "cataclysmic" }] }""";
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, report, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonObjectEntry_IsInfrastructureFailure()
    {
        var report = """{ "changes": ["breaking"] }""";
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, report, ""));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit127_IsInfrastructureFailure()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(127, "", "asyncapi: command not found"));

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPointers_AreDeterministicInfrastructureFailures()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, DiffClean, ""));
        });

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(AsyncApiAuditor.OldSpecKey, ex.Message, StringComparison.Ordinal);

        auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:OldSpec"] = DefaultOldSpec }),
            CancellationToken.None);
        ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(AsyncApiAuditor.NewSpecKey, ex.Message, StringComparison.Ordinal);

        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("https://example.com/asyncapi.yaml")]
    [InlineData("http://example.com/asyncapi.yaml")]
    [InlineData("production")]
    [InlineData("my-context")]
    [InlineData("/abs/asyncapi.yaml")]
    [InlineData("../outside/asyncapi.yaml")]
    [InlineData("events/../../etc/asyncapi.yaml")]
    [InlineData("git:main:events/asyncapi.yaml")]
    [InlineData("github:org/repo#main:asyncapi.yaml")]
    [InlineData("C:\\specs\\asyncapi.yaml")]
    [InlineData("events/spec.txt")]
    [InlineData("events/asyncapi")]
    public async Task NonLocalPointers_AreRejected_BeforeAnyProbe(string pointer)
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = pointer,
                ["Scoped:NewSpec"] = DefaultNewSpec,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(AsyncApiAuditor.OldSpecKey, ex.Message, StringComparison.Ordinal);
        // Rejected from config: no probe and no scan may run.
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task IdenticalPointers_AreRejected_AsVacuous()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultNewSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("vacuously", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task UnknownPolicy_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, DiffClean, ""));
        });

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
                ["Scoped:CompatibilityPolicy"] = "lenient",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(AsyncApiAuditor.CompatibilityPolicyKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ContractAlteringExtraArguments_AreRejected_BeforeExec()
    {
        foreach (var extra in new[]
            {
                "--type", "--type=breaking", "-tbreaking", "-t=breaking",
                "--format", "--format=json", "-fjson",
                "--markdownSubtype", "--markdownSubtype=json",
                "--overrides", "--overrides=./overrides.json", "-o./overrides.json",
                "--save-output", "--save-output=./out.json", "-s./out.json",
                "--no-error", "--no-error=true",
                "--watch", "-w",
                "--log-diagnostics", "--diagnostics-format", "--diagnostics-format=json",
                "--fail-severity", "--fail-severity=warn",
                "--proxyHost", "--proxyHost=proxy.example", "--proxyPort=8080",
            })
        {
            var scanExecs = 0;
            var sandbox = new FakeSandbox((exec, _) =>
            {
                if (IsPresenceProbe(exec))
                    return Task.FromResult(Ok(exec));
                if (IsVersionProbe(exec))
                    return Task.FromResult(VersionOk());
                scanExecs++;
                return Task.FromResult(new SandboxExecResult(0, DiffClean, ""));
            });

            var auditor = new AsyncApiAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:OldSpec"] = DefaultOldSpec,
                    ["Scoped:NewSpec"] = DefaultNewSpec,
                    ["Scoped:ExtraArguments"] = extra,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, scanExecs);
        }
    }

    [Fact]
    public async Task BenignExtraArguments_AreAppended_Verbatim_AsArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = StandardSandbox(
            onScan: exec =>
            {
                scanExec = exec;
                return new SandboxExecResult(0, DiffClean, "");
            });

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
                ["Scoped:ExtraArguments"] = "--verbose",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--verbose", scanExec!.Argv);
        // Structured argv: no shell string is ever constructed.
        Assert.Equal("asyncapi", scanExec.Argv[0]);
    }

    [Fact]
    public async Task InvalidBaselineDocument_IsCoverageUnavailable_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsValidateProbe(exec))
            {
                // The baseline document fails validation.
                if (exec.Argv.Contains(DefaultOldSpec, StringComparer.Ordinal))
                    return Task.FromResult(new SandboxExecResult(1, "", "Validation failed: info.version is required"));
                return Task.FromResult(Ok(exec));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, DiffWithBreakingChanges, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(AsyncApiAuditor.OldSpecKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task InvalidCandidateDocument_IsCoverageUnavailable_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsValidateProbe(exec))
            {
                if (exec.Argv.Contains(DefaultNewSpec, StringComparer.Ordinal))
                    return Task.FromResult(new SandboxExecResult(1, "", "Validation failed: channels must be an object"));
                return Task.FromResult(Ok(exec));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, DiffWithBreakingChanges, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(AsyncApiAuditor.NewSpecKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingBaselineFile_FailsClosed_BeforeValidation()
    {
        var validateExecs = 0;
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                // Neither configured document can be confirmed as a regular
                // file: the tool would fall through to context lookup and
                // auto-detection, so the run must stop here.
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsValidateProbe(exec))
            {
                validateExecs++;
                return Task.FromResult(Ok(exec));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, DiffWithBreakingChanges, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regular files", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, validateExecs);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ValidateProbe_UsesFailSeverityError_AndNamesSpec()
    {
        var validateArgvs = new List<IReadOnlyList<string>>();
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsValidateProbe(exec))
            {
                validateArgvs.Add(exec.Argv.ToList());
                return Task.FromResult(Ok(exec));
            }
            return Task.FromResult(new SandboxExecResult(0, DiffClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, validateArgvs.Count);
        Assert.Contains(validateArgvs, argv =>
            argv.SequenceEqual(["asyncapi", "validate", DefaultOldSpec, "--fail-severity", "error"]));
        Assert.Contains(validateArgvs, argv =>
            argv.SequenceEqual(["asyncapi", "validate", DefaultNewSpec, "--fail-severity", "error"]));
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, DiffWithBreakingChanges, ""));

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_SelectsByRuleId()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, DiffWithBreakingChanges, ""));

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
                ["Scoped:IncludedRules"] = "BREAKING_REMOVE",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("BREAKING_REMOVE", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersNewSpecFindings()
    {
        var sandbox = StandardSandbox(
            onScan: _ => new SandboxExecResult(1, DiffWithBreakingChanges, ""));

        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
                ["Scoped:ExcludePaths"] = "events/",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void CanonicalCategory_NormalizesKnownLevels_AndRejectsUnknown()
    {
        Assert.Equal("breaking", AsyncApiDiffParser.CanonicalCategory("breaking"));
        Assert.Equal("breaking", AsyncApiDiffParser.CanonicalCategory(" BREAKING "));
        Assert.Equal("non-breaking", AsyncApiDiffParser.CanonicalCategory("non-breaking"));
        Assert.Equal("unclassified", AsyncApiDiffParser.CanonicalCategory("unclassified"));
        Assert.Null(AsyncApiDiffParser.CanonicalCategory("cataclysmic"));
        Assert.Null(AsyncApiDiffParser.CanonicalCategory(null));
        Assert.Null(AsyncApiDiffParser.CanonicalCategory("   "));
    }

    [Fact]
    public void ChangeRuleId_CombinesCategoryAndAction_WithFallback()
    {
        Assert.Equal("BREAKING_REMOVE", AsyncApiDiffParser.ChangeRuleId("breaking", "remove"));
        Assert.Equal("NON_BREAKING_ADD", AsyncApiDiffParser.ChangeRuleId("non-breaking", "add"));
        Assert.Equal("UNCLASSIFIED_EDIT", AsyncApiDiffParser.ChangeRuleId("unclassified", "edit"));
        Assert.Equal("BREAKING_CHANGE", AsyncApiDiffParser.ChangeRuleId("breaking", null));
        Assert.Equal("BREAKING_CHANGE", AsyncApiDiffParser.ChangeRuleId("breaking", "   "));
    }

    [Fact]
    public void NewSpecPointer_NormalizesToRepoRelativeFile_OrNull()
    {
        Assert.Equal("events/asyncapi.yaml", AsyncApiDiffParser.NormalizeNewSpecPath("events/asyncapi.yaml"));
        Assert.Equal("events/asyncapi.yaml", AsyncApiDiffParser.NormalizeNewSpecPath("./events/asyncapi.yaml"));
        Assert.Null(AsyncApiDiffParser.NormalizeNewSpecPath(null));
        Assert.Null(AsyncApiDiffParser.NormalizeNewSpecPath("   "));
    }

    [Fact]
    public void ValidateSpecPointer_AcceptsLocalDocuments_RejectsRemote()
    {
        Assert.Equal(
            "events/asyncapi.yaml",
            AsyncApiAuditor.ValidateSpecPointer("events/asyncapi.yaml", AsyncApiAuditor.OldSpecKey, "asyncapi"));
        Assert.Equal(
            "./spec.json",
            AsyncApiAuditor.ValidateSpecPointer("./spec.json", AsyncApiAuditor.OldSpecKey, "asyncapi"));

        foreach (var bad in new[]
            {
                null, "", "   ",
                "https://example.com/asyncapi.yaml",
                "production",
                "/abs/asyncapi.yaml",
                "../outside/asyncapi.yaml",
                "events/spec.txt",
                new string('a', 2000) + ".yaml",
            })
        {
            Assert.Throws<AuditUnavailableException>(
                () => AsyncApiAuditor.ValidateSpecPointer(bad, AsyncApiAuditor.OldSpecKey, "asyncapi"));
        }
    }

    [Fact]
    public void PinnedRelease_IsDeclared_OnRequirement_AndCode()
    {
        Assert.Equal(PinnedAsyncApiVersion, AsyncApiAuditor.DefaultExpectedVersion);

        var attributes = typeof(AsyncApiAuditor)
            .GetCustomAttributes(typeof(CodeyBoxPluginRequiresToolAttribute), inherit: false)
            .Cast<CodeyBoxPluginRequiresToolAttribute>()
            .ToList();
        var asyncapi = Assert.Single(attributes, a => a.Binary == "asyncapi");
        Assert.Null(asyncapi.AptPackage);
        Assert.Contains("@asyncapi/cli@5.0.7", asyncapi.InstallHint, StringComparison.Ordinal);
        var node = Assert.Single(attributes, a => a.Binary == "node");
        Assert.Null(node.AptPackage);
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
            s => s.PluginId == AsyncApiAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("asyncapi", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [AsyncApiAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == AsyncApiAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        Assert.Contains(tools, t => t.Binary == "asyncapi");
        Assert.Contains(tools, t => t.Binary == "node");
        // Verify-only by design: the tool is provisioned via npm, so no distro package is specified.
        Assert.All(tools, t => Assert.Null(t.AptPackage));

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Empty(contributions.InstallCommands);
        Assert.Contains(
            contributions.VerificationCommands,
            v => v.Argv.Any(a => a.Contains("asyncapi", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("requires_asyncapi", "true")]
    public async Task RealAsyncApi_BreakingFixture_YieldsBreakingFinding()
    {
        if (InstalledAsyncApiVersion is null)
            return;

        var fixtureDir = await SeedAsyncApiFixtureRepoAsync(breaking: true);
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

            var auditor = new AsyncApiAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:OldSpec"] = "old.yaml",
                    ["Scoped:NewSpec"] = "new.yaml",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
            Assert.All(result.Findings, f => Assert.Equal("new.yaml", f.Location));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_asyncapi", "true")]
    public async Task RealAsyncApi_CleanFixture_Passes()
    {
        if (InstalledAsyncApiVersion is null)
            return;

        var fixtureDir = await SeedAsyncApiFixtureRepoAsync(breaking: false);
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

            var auditor = new AsyncApiAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:OldSpec"] = "old.yaml",
                    ["Scoped:NewSpec"] = "new.yaml",
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

    private static FakeSandbox StandardSandbox(
        Func<SandboxExec, SandboxExecResult> onScan)
        => new((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(VersionOk());
            if (IsFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsValidateProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(onScan(exec));
        });

    private static async Task<AsyncApiAuditor> CreateAuditorAsync()
    {
        var auditor = new AsyncApiAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSpec"] = DefaultOldSpec,
                ["Scoped:NewSpec"] = DefaultNewSpec,
            }),
            CancellationToken.None);
        return auditor;
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.AsyncApiAuditorPlugin.dll");
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
            PluginId: AsyncApiAuditor.PluginId,
            PluginDisplayName: "CodeyBox: AsyncAPI Contract Compatibility",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => new(0, "", "");

    private static SandboxExecResult VersionOk()
        => new(0, PinnedVersionBanner, "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("asyncapi", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2
            && exec.Argv[0] == "asyncapi"
            && exec.Argv[1] == "--version";

    private static bool IsFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("test -f", StringComparison.Ordinal);

    private static bool IsValidateProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "asyncapi"
            && exec.Argv[1] == "validate";

    private static async Task<string> SeedAsyncApiFixtureRepoAsync(bool breaking)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-asyncapi-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "old.yaml"), """
            asyncapi: 2.6.0
            info:
              title: Streetlights API
              version: 1.0.0
            channels:
              lightMeasured:
                publish:
                  message:
                    payload:
                      type: object
                      properties:
                        lumens:
                          type: integer
            """);

        await File.WriteAllTextAsync(Path.Combine(dir, "new.yaml"), breaking
            ? """
              asyncapi: 2.6.0
              info:
                title: Streetlights API
                version: 1.0.0
              channels: {}
              """
            : """
              asyncapi: 2.6.0
              info:
                title: Streetlights API
                version: 1.0.0
              channels:
                lightMeasured:
                  publish:
                    message:
                      payload:
                        type: object
                        properties:
                          lumens:
                            type: integer
              """);

        return dir;
    }

    private static string? ProbeInstalledAsyncApiVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "asyncapi",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 60_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            return process.ExitCode == 0 && output.Contains("asyncapi", StringComparison.Ordinal)
                ? "present"
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
