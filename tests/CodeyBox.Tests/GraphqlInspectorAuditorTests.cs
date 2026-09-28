using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.GraphqlInspectorAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the graphql-inspector auditor plugin:
/// - Missing binary is an infrastructure failure naming graphql-inspector (never a pass or finding).
/// - Exit codes 0 and 1 are findings-producing candidates; the parser tells
///   "breaking changes found" apart from "could not run" by report content.
/// - Exit 1 without change lines, exit 1 with only non-breaking lines, and
///   empty output all fail closed as infrastructure.
/// - Breaking/dangerous/safe lines map to findings with rule ids, the
///   audited schema file as location, and mapped (never raw) severities.
/// - Unset schema pointers, unknown diff rules, and code-loading extra
///   arguments are deterministic infrastructure failures.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_graphql_inspector", "true")].
/// </summary>
public sealed class GraphqlInspectorAuditorTests
{
    private static readonly string? InstalledGraphqlInspectorVersion = ProbeInstalledGraphqlInspectorVersion();

    // Captured verbatim from `@graphql-inspector/cli` 7.0.0 (piped, non-TTY):
    // `graphql-inspector diff schemas/oldA.graphql schemas/newA.graphql`, exit 1.
    private const string ReportWithBreakingAndDangerous = """
        [log]
        Detected the following changes (5) between schemas:

        [log] ✖  Field user was removed from object type Query
        [log] ✖  Enum value ADMIN was removed from enum Role
        [log] ✖  Input field limit was removed from input object type UserFilter
        [log] ⚠  Argument extra: String added to field Query.search
        [log] ⚠  Enum value GUEST was added to enum Role
        [error] Detected 3 breaking changes
        """;

    // `diff schemas/new2.graphql schemas/new3.graphql`, exit 0.
    private const string ReportSafeOnly = """
        [log]
        Detected the following changes (1) between schemas:

        [log] ✔  Field extra was added to object type Query
        [success] No breaking changes detected
        """;

    // `diff schemas/old4.graphql schemas/new4.graphql`, exit 0.
    private const string ReportDangerousOnly = """
        [log]
        Detected the following changes (1) between schemas:

        [log] ⚠  Argument extra: String added to field Query.search
        [success] No breaking changes detected
        """;

    // `diff schemas/new2.graphql schemas/new2.graphql`, exit 0.
    private const string ReportClean = """
        [success] No changes detected
        """;

    // `diff schemas/missing.graphql schemas/new2.graphql`, exit 1: the tool
    // could not load the pointer. Same exit as breaking changes, but no
    // change lines — must be infrastructure, never findings.
    private const string ReportLoadError = """
        [error] NoTypeDefinitionsFound:
              Unable to find any GraphQL type definitions for the following pointers:

                - schemas/missing.graphql

            at prepareResult (node_modules/@graphql-tools/load/cjs/load-typedefs.js:82:15)
        """;

    private const string DefaultOldSchema = "schema/old.graphql";
    private const string DefaultNewSchema = "schema/new.graphql";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChanges_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, ReportWithBreakingAndDangerous, ""));
        });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(5, result.Findings.Count);

        var removed = Assert.Single(result.Findings, f => f.Title.StartsWith("FIELD_REMOVED:", StringComparison.Ordinal));
        Assert.Equal("codeybox:graphql-inspector", removed.AuditorName);
        Assert.Equal(AuditSeverity.Error, removed.Severity);
        Assert.Equal(DefaultNewSchema, removed.Location);
        // The tool's raw level ("breaking") is mapped to Error, not passed through.
        Assert.Contains("Severity (tool): breaking", removed.Description, StringComparison.Ordinal);

        var enumRemoved = Assert.Single(result.Findings, f => f.Title.StartsWith("ENUM_VALUE_REMOVED:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, enumRemoved.Severity);
        Assert.Equal(DefaultNewSchema, enumRemoved.Location);

        var inputRemoved = Assert.Single(result.Findings, f => f.Title.StartsWith("INPUT_FIELD_REMOVED:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, inputRemoved.Severity);

        var argAdded = Assert.Single(result.Findings, f => f.Title.StartsWith("FIELD_ARGUMENT_ADDED:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, argAdded.Severity);

        var enumAdded = Assert.Single(result.Findings, f => f.Title.StartsWith("ENUM_VALUE_ADDED:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, enumAdded.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("graphql-inspector", scanExec!.Argv[0]);
        Assert.Equal("diff", scanExec.Argv[1]);
        Assert.Equal(DefaultOldSchema, scanExec.Argv[2]);
        Assert.Equal(DefaultNewSchema, scanExec.Argv[3]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task SafeOnlyChanges_AreAdvisoryInfo_AndPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportSafeOnly, ""));
        });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("FIELD_ADDED", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Equal(DefaultNewSchema, finding.Location);
    }

    [Fact]
    public async Task DangerousOnlyChanges_AreAdvisoryWarning_AndPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportDangerousOnly, ""));
        });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("FIELD_ARGUMENT_ADDED", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task Exit1_WithoutChangeLines_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportLoadError, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit1_WithOnlyNonBreakingLines_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 claims breaking changes, but none are listed: contradiction.
            return Task.FromResult(new SandboxExecResult(1, ReportSafeOnly, ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit1_BreakingSummaryWithoutChangeLines_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "[error] Detected 2 breaking changes\n", ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "graphql-inspector: command not found"));
        });

        var auditor = await CreateAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMessageShape_KeepsLevelDerivedRuleId_NeverDropped()
    {
        Assert.Equal(
            "BREAKING_CHANGE",
            GraphqlInspectorReportParser.ChangeRuleId("Field frobnicate relicensed under moon terms", "breaking"));
        Assert.Equal(
            "DANGEROUS_CHANGE",
            GraphqlInspectorReportParser.ChangeRuleId("Field frobnicate relicensed under moon terms", "dangerous"));
        Assert.Equal(
            "NON_BREAKING_CHANGE",
            GraphqlInspectorReportParser.ChangeRuleId("Field frobnicate relicensed under moon terms", "non-breaking"));
    }

    [Fact]
    public void KnownTemplates_RecoverUpstreamChangeTypes()
    {
        Assert.Equal("FIELD_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Field user was removed from object type Query", "breaking"));
        Assert.Equal("FIELD_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Field age (deprecated) was removed from object type User", "breaking"));
        Assert.Equal("FIELD_TYPE_CHANGED", GraphqlInspectorReportParser.ChangeRuleId("Field User.name changed type from String to String!", "breaking"));
        Assert.Equal("FIELD_ARGUMENT_ADDED", GraphqlInspectorReportParser.ChangeRuleId("Argument extra: String added to field Query.search", "dangerous"));
        Assert.Equal("FIELD_ARGUMENT_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Argument id: ID! was removed from field Query.user", "breaking"));
        Assert.Equal("TYPE_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Type Legacy was removed", "breaking"));
        Assert.Equal("ENUM_VALUE_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Enum value ADMIN was removed from enum Role", "breaking"));
        Assert.Equal("INPUT_FIELD_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Input field limit was removed from input object type UserFilter", "breaking"));
        Assert.Equal("UNION_MEMBER_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Member Admin was removed from Union type Actor", "breaking"));
        Assert.Equal("OBJECT_TYPE_INTERFACE_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("User object type no longer implements Node interface", "breaking"));
        Assert.Equal("SCHEMA_QUERY_TYPE_CHANGED", GraphqlInspectorReportParser.ChangeRuleId("Schema query root has changed from Query to QueryV2", "breaking"));
        Assert.Equal("DIRECTIVE_REMOVED", GraphqlInspectorReportParser.ChangeRuleId("Directive tag was removed", "breaking"));
    }

    [Fact]
    public void NewSchemaPointer_NormalizesToRepoRelativeFile_OrNull()
    {
        Assert.Equal("schema/new.graphql", GraphqlInspectorReportParser.NormalizeNewSchemaPath("schema/new.graphql"));
        Assert.Equal("schema/new.graphql", GraphqlInspectorReportParser.NormalizeNewSchemaPath("./schema/new.graphql"));
        Assert.Null(GraphqlInspectorReportParser.NormalizeNewSchemaPath("https://api.example.com/graphql"));
        Assert.Null(GraphqlInspectorReportParser.NormalizeNewSchemaPath("git:main:./schema.graphql"));
        Assert.Null(GraphqlInspectorReportParser.NormalizeNewSchemaPath("github:org/repo#main:schema.graphql"));
        Assert.Null(GraphqlInspectorReportParser.NormalizeNewSchemaPath("/abs/schema.graphql"));
        Assert.Null(GraphqlInspectorReportParser.NormalizeNewSchemaPath(null));
        Assert.Null(GraphqlInspectorReportParser.NormalizeNewSchemaPath("   "));
    }

    [Fact]
    public async Task TtyStyleOutput_WithAnsiAndWithoutTags_ParsesTheSame()
    {
        // A TTY logger emits no [tag] prefixes and bold-wraps names in ANSI.
        var ttyReport = "Detected the following changes (2) between schemas:\n"
            + "\n"
            + "✖  Field \u001b[1muser\u001b[22m was removed from object type \u001b[1mQuery\u001b[22m\n"
            + "✔  Field \u001b[1mextra\u001b[22m was added to object type \u001b[1mQuery\u001b[22m\n"
            + "Detected 1 breaking change\n";

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ttyReport, ""));
        });

        var auditor = await CreateAuditorAsync();
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Title.StartsWith("FIELD_REMOVED:", StringComparison.Ordinal) && f.Severity == AuditSeverity.Error);
        Assert.Contains(result.Findings, f => f.Title.StartsWith("FIELD_ADDED:", StringComparison.Ordinal) && f.Severity == AuditSeverity.Info);
    }

    [Fact]
    public async Task MissingPointers_AreDeterministicInfrastructureFailures()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(GraphqlInspectorAuditor.OldSchemaKey, ex.Message, StringComparison.Ordinal);

        auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:OldSchema"] = DefaultOldSchema }),
            CancellationToken.None);
        ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(GraphqlInspectorAuditor.NewSchemaKey, ex.Message, StringComparison.Ordinal);

        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnknownDiffRule_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
                ["Scoped:DiffRules"] = "considerUsage",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains(GraphqlInspectorAuditor.DiffRulesKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task CodeLoadingExtraArguments_AreRejected_BeforeExec()
    {
        foreach (var extra in new[] { "--onComplete", "--onComplete=./handler.js", "--onUsage", "-r", "--require", "--rule", "--rule=dangerousBreaking" })
        {
            var scanExecs = 0;
            var sandbox = new FakeSandbox((exec, _) =>
            {
                if (IsPresenceProbe(exec))
                    return Task.FromResult(Ok(exec));
                scanExecs++;
                return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
            });

            var auditor = new GraphqlInspectorAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:OldSchema"] = DefaultOldSchema,
                    ["Scoped:NewSchema"] = DefaultNewSchema,
                    ["Scoped:ExtraArguments"] = extra,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains("graphql-inspector", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, scanExecs);
        }
    }

    [Fact]
    public async Task DiffRules_AppendAsRuleFlagPairs_AfterPointers()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
                ["Scoped:DiffRules"] = "suppressRemovalOfDeprecatedField, safeUnreachable",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        Assert.Equal(
            new[] { "graphql-inspector", "diff", DefaultOldSchema, DefaultNewSchema },
            argv.Take(4).ToArray());
        Assert.Contains("--rule", argv);
        Assert.Contains("suppressRemovalOfDeprecatedField", argv);
        Assert.Contains("safeUnreachable", argv);
    }

    [Fact]
    public async Task BenignExtraArguments_AreAppended_Verbatim_AsArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
                ["Scoped:ExtraArguments"] = "--federation",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--federation", scanExec!.Argv);
        // Structured argv: no shell string is ever constructed.
        Assert.Equal("graphql-inspector", scanExec.Argv[0]);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithBreakingAndDangerous, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_SelectsByRuleId()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithBreakingAndDangerous, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
                ["Scoped:IncludedRules"] = "FIELD_REMOVED",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("FIELD_REMOVED", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersSchemaFileFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithBreakingAndDangerous, ""));
        });

        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
                ["Scoped:ExcludePaths"] = "schema/",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void PinnedRelease_IsDeclared_OnRequirement_AndCode()
    {
        Assert.Equal("7.0.0", GraphqlInspectorAuditor.DefaultExpectedVersion);

        var attribute = Assert.Single(
            typeof(GraphqlInspectorAuditor).GetCustomAttributes(typeof(CodeyBoxPluginRequiresToolAttribute), inherit: false));
        var requirement = Assert.IsType<CodeyBoxPluginRequiresToolAttribute>(attribute);
        Assert.Equal("graphql-inspector", requirement.Binary);
        Assert.Null(requirement.AptPackage);
        Assert.Contains("@graphql-inspector/cli@7.0.0", requirement.InstallHint, StringComparison.Ordinal);
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
            s => s.PluginId == GraphqlInspectorAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("graphql-inspector", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGraphqlInspectorRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GraphqlInspectorAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GraphqlInspectorAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("graphql-inspector", tool.Binary);
        // Verify-only by design: the tool is provisioned via npm, so no distro package is specified.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("graphql-inspector", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_graphql_inspector", "true")]
    public async Task RealGraphqlInspector_BreakingFixture_YieldsFieldRemoved()
    {
        if (InstalledGraphqlInspectorVersion is null)
            return;

        var fixtureDir = await SeedGraphqlFixtureRepoAsync(breaking: true);
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

            var auditor = new GraphqlInspectorAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:OldSchema"] = "old.graphql",
                    ["Scoped:NewSchema"] = "new.graphql",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings,
                f => f.Title.StartsWith("FIELD_REMOVED:", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("new.graphql", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_graphql_inspector", "true")]
    public async Task RealGraphqlInspector_CleanFixture_Passes()
    {
        if (InstalledGraphqlInspectorVersion is null)
            return;

        var fixtureDir = await SeedGraphqlFixtureRepoAsync(breaking: false);
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

            var auditor = new GraphqlInspectorAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:OldSchema"] = "old.graphql",
                    ["Scoped:NewSchema"] = "new.graphql",
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

    private static async Task<GraphqlInspectorAuditor> CreateAuditorAsync()
    {
        var auditor = new GraphqlInspectorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:OldSchema"] = DefaultOldSchema,
                ["Scoped:NewSchema"] = DefaultNewSchema,
            }),
            CancellationToken.None);
        return auditor;
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GraphqlInspectorAuditorPlugin.dll");
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
            PluginId: GraphqlInspectorAuditor.PluginId,
            PluginDisplayName: "CodeyBox: GraphQL Inspector Schema Compatibility",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => new(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("graphql-inspector", StringComparer.Ordinal);

    private static async Task<string> SeedGraphqlFixtureRepoAsync(bool breaking)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-graphql-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "old.graphql"), """
            type Query {
              user: User
              version: String
            }

            type User {
              id: ID!
              name: String
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(dir, "new.graphql"), breaking
            ? """
              type Query {
                version: String
              }

              type User {
                id: ID!
                name: String
              }
              """
            : """
              type Query {
                user: User
                version: String
              }

              type User {
                id: ID!
                name: String
              }
              """);

        return dir;
    }

    private static string? ProbeInstalledGraphqlInspectorVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "graphql-inspector",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            // The binary reports "unknown" rather than a semver token; the
            // probe only establishes presence, never a version.
            return process.ExitCode == 0 ? "present" : null;
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
