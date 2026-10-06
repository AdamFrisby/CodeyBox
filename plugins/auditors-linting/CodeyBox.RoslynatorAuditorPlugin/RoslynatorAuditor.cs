using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.RoslynatorAuditorPlugin;

/// <summary>
/// Static-analysis auditor wrapping <c>roslynator analyze</c> (Roslyn-based
/// C# diagnostics, including compiler diagnostics) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the invocation shape (<c>analyze</c> with
/// the SARIF report streamed to stdout and console chatter suppressed),
/// the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// exit-<c>0</c>-without-report clean verdict (see
/// <see cref="RoslynatorOutputParser"/>), and the suppression posture
/// below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Roslynator SARIF levels go through a declared map, never
/// raw: <c>error</c> (and its aliases) → <see cref="AuditSeverity.Error"/>
/// (fails the audit); <c>warning</c> (and its aliases) → <see
/// cref="AuditSeverity.Warning"/> (advisory); <c>note</c>, <c>none</c>,
/// <c>info</c> and the suggestion/hint family → <see
/// cref="AuditSeverity.Info"/> (informational); anything unrecognised →
/// <see cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> only drops
/// findings, it never raises them, and the tool-side
/// <c>--severity-level</c> floor moves with it. This auditor is therefore a
/// merge gate for error-severity diagnostics (typically compiler errors),
/// not a blocker on every warning.</para>
///
/// <para><b>Exit-code convention (verified against roslynator 1.0.0 — not
/// assumed from the common table).</b> <c>0</c> = analyzed clean (no report
/// file is written — stdout carries only the console log, which the parser
/// reads as zero findings); <c>1</c> = diagnostics found (SARIF report on
/// stdout); <c>2</c> = could not run (verified: unknown flag, missing
/// project file, and no MSBuild project or solution under the working
/// directory). <c>126</c>/<c>127</c> = cannot execute / not found. Only
/// <c>0</c> and <c>1</c> are findings-producing; everything else is
/// infrastructure. An operator <c>--return-success-on-diagnostics</c> moves
/// diagnostics onto exit <c>0</c> but the SARIF payload still parses, so the
/// exit code alone can never silence the gate.</para>
///
/// <para><b>Version pin.</b> Analyzer and compiler diagnostics change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>roslynator --version</c> before
/// the scan (the CLI prints <c>1.0.0.0</c> for NuGet package
/// <c>roslynator.dotnet.cli 1.0.0</c>); a missing binary, an unrecognised
/// version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a
/// finding.</para>
///
/// <para><b>Unity analyzer preset (Microsoft.Unity.Analyzers, UNT*).</b>
/// Unity-specific C# diagnostics run through this same auditor — no separate
/// assembly or parser: set <c>AnalyzerAssemblies</c> to the pinned
/// <c>Microsoft.Unity.Analyzers.dll</c> (NuGet package
/// <c>Microsoft.Unity.Analyzers</c> 1.28.0, latest stable verified 2026-10-06)
/// and <c>ProjectPath</c> to the Unity-generated <c>.csproj</c>/<c>.sln</c>
/// carrying the correct Unity references. Assemblies are validated
/// (repository-relative <c>.dll</c> paths, bounded count), presence-probed in
/// the audited tree, and passed as structured
/// <c>--analyzer-assemblies</c> argv entries (deferred when
/// <c>ExtraArguments</c> already supplies the flag); Unity diagnostics (for
/// example <c>UNT0001</c>, empty Unity message) then flow through the same
/// SARIF parser and severity map as every other diagnostic. Unity coverage
/// is explicit: requesting <c>UNT*</c> rules without an analyzer assembly,
/// enabling the preset without <c>ProjectPath</c>, or naming an assembly
/// absent from the tree is coverage-unavailable (deterministic
/// infrastructure), never a pass. Full Unity compilation and editor
/// validation are out of scope — the auditor reports analyzer diagnostics
/// and needs no Unity account or editor provisioning.</para>
///
/// <para><b>Repository-controlled suppression.</b> Roslynator honors
/// suppression authored inside the audited repository — <c>#pragma warning
/// disable</c>, <c>[SuppressMessage]</c>, <c>.editorconfig</c> severity
/// lines, and <c>NoWarn</c> — and the audit subject writes that repository.
/// No roslynator flag defeats all of these (verified:
/// <c>--report-suppressed-diagnostics</c> does not resurface
/// <c>#pragma</c>-disabled compiler diagnostics), so this auditor does not
/// pretend otherwise: repo-authored suppression narrows what the tool
/// reports, and the plugin README states the residual surface. Operators who
/// need a fully operator-owned ruleset restrict the reported set
/// independently of the repository via <c>IncludedRules</c> (finding-level
/// exact-match filter) or pass <c>--supported-diagnostics</c> /
/// <c>--severity-level</c> in <c>ExtraArguments</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>roslynator analyze</c> run
/// in the repository root (or at an operator-pinned <c>ProjectPath</c>):
/// the tool discovers and analyzes the MSBuild projects it finds there.
/// The report streams SARIF to stdout (<c>--output /dev/stdout</c> on Linux
/// sandboxes) with console chatter suppressed (<c>--verbosity quiet</c>);
/// the parser reads the whole of stdout, so any non-SARIF stdout (for
/// example from an operator-supplied <c>--verbosity</c> override) fails
/// closed as infrastructure. Findings carry the tool's absolute
/// <c>file://</c> artifact URIs scheme-stripped (see the shared SARIF
/// parser), so the finding-level <c>ExcludePaths</c> backstop only matches
/// repository-relative paths — the defaults stay (they filter any relative
/// paths and document intent), and operators narrow scope further with the
/// tool's own <c>--include</c>/<c>--exclude</c> globs in
/// <c>ExtraArguments</c>. The scan writes nothing into the audited tree:
/// the report streams to stdout and the tool is never given a report path
/// inside the repository. Analysis loads projects through MSBuild, so the
/// sandbox baseline must carry a .NET SDK alongside the tool; trees whose
/// packages are not restored fail closed as infrastructure, not as a
/// pass.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Roslynator C# Static Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "roslynator",
    InstallHint = "provision the pinned roslynator release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via dotnet tool (dotnet tool install -g "
        + "roslynator.dotnet.cli --version 1.0.0 — the 1.0.0 package reports 1.0.0.0 via --version) — no "
        + "distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "dotnet",
    InstallHint = "analysis loads MSBuild projects, so the sandbox baseline needs a .NET SDK (8 or later) "
        + "alongside roslynator; trees whose NuGet packages are not restored fail closed as infrastructure")]
public sealed class RoslynatorAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.roslynator";

    /// <summary>
    /// Roslynator release the invocation and its findings are verified
    /// against, as reported by <c>roslynator --version</c> (the
    /// <c>roslynator.dotnet.cli 1.0.0</c> NuGet package reports
    /// <c>1.0.0.0</c>). Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.0.0.0";

    /// <summary>
    /// Scoped-config key for an explicit repository-relative project or
    /// solution path analyzed instead of the tool's working-directory
    /// discovery (e.g. <c>src/App.sln</c>). Must be relative, without
    /// <c>..</c> segments, ending in <c>.sln</c>, <c>.slnx</c> or
    /// <c>.csproj</c>; a missing file fails closed as deterministic
    /// infrastructure. Required by the Unity analyzer preset: Unity
    /// diagnostics are only meaningful in their Unity-generated project
    /// context.
    /// </summary>
    public const string ProjectPathKey = "ProjectPath";

    /// <summary>
    /// Scoped-config key for additional analyzer assemblies loaded into the
    /// analysis, as comma-separated repository-relative <c>.dll</c> paths
    /// (e.g. <c>tools/analyzers/Microsoft.Unity.Analyzers.dll</c> for the
    /// Unity preset). Each entry must be relative, without <c>..</c>
    /// segments, ending in <c>.dll</c>; at most
    /// <see cref="MaxAnalyzerAssemblies"/> entries. Entries are
    /// presence-probed in the audited tree — a missing file fails closed as
    /// deterministic infrastructure — and passed as structured
    /// <c>--analyzer-assemblies</c> argv entries, unless
    /// <c>ExtraArguments</c> already supplies that flag (the knob then
    /// defers to the operator spelling).
    /// </summary>
    public const string AnalyzerAssembliesKey = "AnalyzerAssemblies";

    /// <summary>
    /// Maximum <see cref="AnalyzerAssembliesKey"/> entries per run: the
    /// analyzer set is bounded before any sandbox probe or argv build.
    /// </summary>
    internal const int MaxAnalyzerAssemblies = 16;

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = analyzed clean (console log on stdout, no report — the parser
        // reads that as zero findings); 1 = diagnostics found (SARIF on
        // stdout). Both are verdicts. 2 (usage/load error) and everything
        // else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // MSBuild project load plus whole-tree analysis routinely takes
        // minutes: bound each phase.
        Timeout = TimeSpan.FromMinutes(10),
        // Findings in vendored/dependency trees and compiler or build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config. Note the
        // tool-imposed limit (see the class summary): roslynator reports
        // absolute file:// artifact URIs, so the shared SARIF parser
        // preserves sandbox-absolute paths and a repo-relative prefix entry
        // cannot match them; narrow the scan itself with --include/--exclude
        // in ExtraArguments.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "obj/", "bin/", "artifacts/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _projectPath = static () => null;
    private Func<string?> _analyzerAssemblies = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:roslynator";

    /// <inheritdoc />
    protected override string ToolName => "roslynator";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new RoslynatorOutputParser();

    /// <summary>
    /// Declared mapping from roslynator's SARIF level vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. Verified levels against
    /// 1.0.0.0 are <c>error</c> (compiler errors), <c>warning</c> (compiler
    /// warnings) and <c>none</c> (hidden-severity diagnostics surfaced via
    /// <c>--severity-level hidden</c>; info-severity diagnostics surface as
    /// <c>note</c> per the Roslyn SARIF convention). The wider map keeps any
    /// other level the tool can emit from becoming an unmapped dialect. Raw
    /// levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["suggestion"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Structured argv, never a shell string: the base appends the
        // per-run project positional (ResolveContextArgumentsAsync) and the
        // operator's ExtraArguments after these entries.
        var args = new List<string> { "analyze" };

        if (!ExtraArgumentsSupplyFlag(options, "--output", "-o"))
        {
            // Machine-readable SARIF report for the parser. An operator
            // --output would divert the report into a file and leave stdout
            // without SARIF; deferring avoids a duplicate flag (exit 2) and
            // the run then fails closed in the parser instead.
            args.Add("--output");
            args.Add("/dev/stdout");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--output-format"))
        {
            // The parser reads the SARIF document from stdout. An operator
            // --output-format would replace the SARIF the parser expects and
            // break the run into infrastructure failure; let that surface
            // loudly.
            args.Add("--output-format");
            args.Add("sarif");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--verbosity", "-v"))
        {
            // Console chatter shares stdout with the SARIF document; quiet
            // keeps the report the whole of stdout so it stays parseable
            // (verified: without this, runs under sandbox capture interleave
            // the progress log before the report). An operator --verbosity
            // override breaks the run into infrastructure failure; let that
            // surface loudly.
            args.Add("--verbosity");
            args.Add("quiet");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--severity-level"))
        {
            // The tool-side severity floor moves with the finding-side
            // MinimumSeverity so the two never disagree about what is
            // reported: info keeps everything the tool reports by default,
            // warning drops info/hidden diagnostics, error keeps only
            // errors (verified: --severity-level error drops a CS0219
            // warning to "0 diagnostics found", exit 0).
            args.Add("--severity-level");
            args.Add(MinimumSeverityFlag(options.MinimumSeverity));
        }

        return args;
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var analyzerAssemblies = NormalizeAnalyzerAssemblies(_analyzerAssemblies());
        var unityViaAssemblies = analyzerAssemblies.Any(IsUnityAnalyzerAssembly);
        var unityViaExtraArguments = ExtraArgumentsMentionUnityAnalyzer(options);
        var unityRulesRequested = options.IncludedRules.Any(
            static rule => rule.StartsWith("UNT", StringComparison.OrdinalIgnoreCase));

        // The Unity preset is an explicit declaration, not an inference:
        // UNT* rules without a Unity analyzer assembly would silently
        // report zero findings, so requesting them alone is
        // coverage-unavailable, never a pass. A Unity-generated project
        // that already <Analyzer Include>s the package still declares the
        // same DLL here, so coverage stays verifiable instead of trusting
        // repository-authored project content.
        if (unityRulesRequested && !unityViaAssemblies && !unityViaExtraArguments)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' IncludedRules requests Unity diagnostics (UNT*) but "
                + $"no Microsoft.Unity.Analyzers assembly is configured. Set CodeyBox:Plugins:{PluginId}:"
                + $"{AnalyzerAssembliesKey} to the repository-relative Microsoft.Unity.Analyzers.dll path "
                + $"(see the plugin README Unity preset) alongside a Unity-generated project in {ProjectPathKey}; "
                + "without the analyzer Unity coverage is unavailable, never a pass.")
            { IsDeterministic = true };

        // Unity diagnostics are only meaningful in their Unity-generated
        // project context (correct Unity references); the preset — via the
        // knob, via raw ExtraArguments, or via UNT* rule selection — always
        // names that project explicitly instead of relying on discovery.
        var unityPreset = unityViaAssemblies || unityViaExtraArguments || unityRulesRequested;
        var configured = _projectPath();
        if (unityPreset && string.IsNullOrWhiteSpace(configured))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' Unity analyzer preset requires {ProjectPathKey} — "
                + "Unity diagnostics are only meaningful in their Unity-generated project context. Set "
                + $"CodeyBox:Plugins:{PluginId}:{ProjectPathKey} to that repository-relative .sln, .slnx or "
                + ".csproj path; without the project Unity coverage is unavailable, never a pass.")
            { IsDeterministic = true };

        var contextArguments = new List<string>();
        if (analyzerAssemblies.Count > 0 && !ExtraArgumentsSupplyFlag(options, "--analyzer-assemblies"))
        {
            var present = await ProbeRepositoryFilesPresentAsync(
                sandbox, workingDirectory, ToolName, analyzerAssemblies, options, ct).ConfigureAwait(false);
            var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
            var missing = analyzerAssemblies.Where(a => !presentSet.Contains(a)).ToList();
            if (missing.Count > 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' found no analyzer assemblies "
                    + $"'{string.Join("', '", missing.Take(8).Select(TruncateForMessage))}' in the audited "
                    + $"repository. Set CodeyBox:Plugins:{PluginId}:{AnalyzerAssembliesKey} to "
                    + "repository-relative .dll paths present in the tree (for the Unity preset: the pinned "
                    + "Microsoft.Unity.Analyzers.dll); without the analyzer there is no coverage, never a pass.")
                { IsDeterministic = true };
            // Config order, not probe-echo order, so argv is deterministic.
            foreach (var assembly in analyzerAssemblies)
            {
                contextArguments.Add("--analyzer-assemblies");
                contextArguments.Add(ToSafePositional(assembly));
            }
        }

        if (string.IsNullOrWhiteSpace(configured))
            return contextArguments;

        var normalized = NormalizeProjectPath(configured);
        var projectPresent = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, ToolName, [normalized], options, ct).ConfigureAwait(false);
        if (projectPresent.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' found no project file '{normalized}' in the "
                + $"audited repository. Set CodeyBox:Plugins:{PluginId}:{ProjectPathKey} to a "
                + "repository-relative .sln, .slnx or .csproj path.")
            { IsDeterministic = true };
        contextArguments.Add(ToSafePositional(normalized));
        return contextArguments;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _projectPath = () => scoped[ProjectPathKey];
        _analyzerAssemblies = () => scoped[AnalyzerAssembliesKey];
        context.Logger.LogInformation(
            "RoslynatorAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string MinimumSeverityFlag(AuditSeverity minimum) => minimum switch
    {
        AuditSeverity.Error => "error",
        AuditSeverity.Warning => "warning",
        _ => "info",
    };

    private static string NormalizeProjectPath(string? configured)
    {
        var normalized = (configured ?? string.Empty).Replace('\\', '/').Trim();
        var stripped = normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
        if (stripped.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains('\n', StringComparison.Ordinal)
            || normalized.Contains('\r', StringComparison.Ordinal)
            || stripped.Split('/').Contains("..", StringComparer.Ordinal)
            || (!stripped.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !stripped.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                && !stripped.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' was configured with an invalid {ProjectPathKey} "
                + $"('{TruncateForMessage(configured)}'): expected a repository-relative .sln, .slnx or "
                + ".csproj path without '..' segments.")
            { IsDeterministic = true };
        return stripped;
    }

    private static IReadOnlyList<string> NormalizeAnalyzerAssemblies(string? configured)
    {
        var entries = ExternalToolAuditorOptions.SplitCommaSeparatedList(configured);
        if (entries.Count > MaxAnalyzerAssemblies)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' was configured with {entries.Count} "
                + $"{AnalyzerAssembliesKey} entries (maximum {MaxAnalyzerAssemblies}); keep the analyzer set bounded.")
            { IsDeterministic = true };

        var normalized = new List<string>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var slashed = entry.Replace('\\', '/').Trim();
            var stripped = slashed.StartsWith("./", StringComparison.Ordinal) ? slashed[2..] : slashed;
            // Argv guard plus worktree containment: rooted paths and '..'
            // segments never reach the probe or the tool argv.
            var validated = ValidatedRepoRelativeTarget(stripped, AnalyzerAssembliesKey);
            if (!validated.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{PluginId}' was configured with an invalid {AnalyzerAssembliesKey} "
                    + $"entry ('{TruncateForMessage(entry)}'): expected a repository-relative .dll path without "
                    + "'..' segments (for the Unity preset: the pinned Microsoft.Unity.Analyzers.dll).")
                { IsDeterministic = true };
            if (seen.Add(validated))
                normalized.Add(validated);
        }
        return normalized;
    }

    // The Unity preset is recognized by the official assembly file name as
    // shipped in the Microsoft.Unity.Analyzers NuGet package — a basename
    // match, not a bare "unity" substring, so unrelated names cannot
    // accidentally opt a tree into the preset's ProjectPath requirement.
    private static bool IsUnityAnalyzerAssembly(string relativePath)
        => Path.GetFileName(relativePath).Contains(
            "Microsoft.Unity.Analyzers", StringComparison.OrdinalIgnoreCase);

    // Raw --analyzer-assemblies values in ExtraArguments bypass the knob, so
    // the Unity ProjectPath requirement still sees them: scan the flag's
    // "--flag value" and "--flag=value" spellings (values may carry several
    // ';'- or ','-separated assemblies).
    private static bool ExtraArgumentsMentionUnityAnalyzer(ExternalToolAuditorOptions options)
    {
        const string flag = "--analyzer-assemblies";
        var args = options.ExtraArguments;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;
            if (arg.Equals(flag, StringComparison.Ordinal))
            {
                if (i + 1 < args.Count)
                    value = args[i + 1];
            }
            else if (arg.StartsWith(flag + "=", StringComparison.Ordinal))
            {
                value = arg[(flag.Length + 1)..];
            }
            if (value is null)
                continue;
            foreach (var piece in value.Split(
                [',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsUnityAnalyzerAssembly(piece))
                    return true;
            }
        }
        return false;
    }

    // A configured name comes from operator config — it is passed as one
    // argv entry (never through a shell), and the "./" prefix keeps a
    // leading-dash name from being read as a tool flag.
    private static string ToSafePositional(string relativePath)
        => "./" + relativePath;
}
