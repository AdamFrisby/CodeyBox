using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SpotBugsAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>spotbugs</c> (Java bytecode analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (SpotBugs' built-in
/// SARIF emitter on stdout, parsed by the shared <see cref="SarifToolOutputParser"/>
/// — no custom parser), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> SpotBugs' SARIF levels go through a declared map, never raw:
/// <c>error</c> (scariest/scary bug ranks) maps to <see cref="AuditSeverity.Error"/>
/// and fails the audit; <c>warning</c> (troubling) maps to
/// <see cref="AuditSeverity.Warning"/> and is advisory; <c>note</c> (of concern)
/// and <c>none</c> map to <see cref="AuditSeverity.Info"/>. Anything
/// unrecognised maps to <see cref="AuditSeverity.Warning"/>.
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (read from the SpotBugs 4.10.4 source — not
/// assumed from the common table).</b> Without <c>-exitcode</c> the text UI
/// exits <c>0</c> even with bugs reported (<c>FindBugs.runMain</c> only calls
/// <c>System.exit</c> when <c>-exitcode</c> was passed), so the scan passes
/// <c>-exitcode</c> explicitly to get a verdict-bearing exit: <c>0</c> =
/// analysed clean, <c>1</c> = <c>BUGS_FOUND_FLAG</c> (SARIF report on stdout).
/// Both are findings-producing verdicts. Exits <c>2</c>/<c>3</c> carry
/// <c>MISSING_CLASS_FLAG</c> (classes needed for analysis were missing — the
/// analysis ran degraded, so its result is not a verdict) and exits with bit
/// <c>4</c> (<c>ERROR_FLAG</c>: serious analysis errors, usage failures) are
/// infrastructure. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A scanner's detectors change between releases, so
/// findings are only meaningful from the build the auditor must provision.
/// The auditor probes <c>spotbugs -version</c> (single dash — the spelling
/// <c>TextUICommandLine</c> registers; it prints <c>SpotBugs X.Y.Z</c> to
/// stdout) before the scan; a missing binary, an unrecognised version string,
/// or a version other than <c>ExpectedVersion</c> is an infrastructure failure
/// naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Bytecode in, findings out.</b> SpotBugs analyses compiled classes
/// and archives, not sources: the scan targets the worktree root and the tool
/// picks up whatever <c>.class</c> files and <c>.jar</c>s are checked in or
/// built there (nested archives are scanned — that default is kept so
/// shaded jars are not a blind spot). A tree with no analysable classes is a
/// clean pass (the scan passes <c>-noClassOk</c>, which emits an empty SARIF
/// report instead of failing), not an error. Source-only repositories
/// therefore always pass: provision a compile step (<c>mvn package</c>,
/// <c>gradle build</c>) into the audit sandbox baseline when the subject is
/// sources. SARIF locations carry the source file SpotBugs resolved from
/// debug info, so findings read as <c>path:startLine</c> in source terms.</para>
///
/// <para><b>Repository-controlled suppression.</b> SpotBugs honors
/// <c>-exclude</c> filter files and <c>@SuppressFBWarnings</c> annotations
/// authored inside the audited repository — and the audit subject writes that
/// repository. The scan never names a repo-local filter file: only the
/// operator-owned <c>FilterFilePath</c> (which must live outside the audited
/// tree) is passed as <c>-exclude</c>, and annotation-based suppression in
/// the subject cannot be switched off from the command line, so findings the
/// subject annotated still surface for triage against the checked-in source.
/// Operators who deliberately trust repo-authored filters pass
/// <c>-exclude &lt;path&gt;</c> in <c>ExtraArguments</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: SpotBugs Java Bytecode Analyser",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "spotbugs",
    AptPackage = "spotbugs",
    InstallHint = "install the pinned SpotBugs release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") — the declared apt package installs it into the sandbox baseline "
        + "automatically when this plugin is enabled; on baselines whose distro package differs, set "
        + "ExpectedVersion to the provisioned release, or stage the upstream release archive from "
        + "https://github.com/spotbugs/spotbugs/releases via CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions (a Java runtime is also required)")]
public sealed class SpotBugsAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.spotbugs";

    /// <summary>
    /// SpotBugs release the invocation and its findings are pinned to.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "4.10.4";

    /// <summary>
    /// Scoped-config key for an operator-owned SpotBugs exclude-filter file,
    /// passed as <c>-exclude</c>. It must live outside the audited
    /// repository: a repo-authored file would let the audit subject silence
    /// the gate. Ignored when <c>ExtraArguments</c> already supplies
    /// <c>-exclude</c>.
    /// </summary>
    public const string FilterFilePathKey = "FilterFilePath";

    /// <summary>
    /// Scoped-config key for the SpotBugs confidence threshold
    /// (<c>high</c>, <c>medium</c>, <c>low</c>). Selects the tool-native
    /// <c>-high</c> / <c>-medium</c> / <c>-low</c> reporting flag; the tool
    /// default is <c>medium</c>.
    /// </summary>
    public const string ConfidenceLevelKey = "ConfidenceLevel";

    /// <summary>
    /// Scoped-config key for the SpotBugs analysis effort
    /// (<c>min</c>, <c>less</c>, <c>default</c>, <c>more</c>, <c>max</c>).
    /// Passed as <c>-effort:&lt;level&gt;</c>; the tool default is
    /// <c>default</c>.
    /// </summary>
    public const string EffortLevelKey = "EffortLevel";

    /// <summary>Confidence threshold used when the operator does not configure one.</summary>
    public const string DefaultConfidenceLevel = "medium";

    /// <summary>Analysis effort used when the operator does not configure one.</summary>
    public const string DefaultEffortLevel = "default";

    private static readonly HashSet<string> KnownConfidenceLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "high", "medium", "low",
    };

    private static readonly HashSet<string> KnownEffortLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "min", "less", "default", "more", "max",
    };

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = analysed clean (empty SARIF results); 1 = BUGS_FOUND_FLAG
        // (SARIF report on stdout). Both are verdicts — the scan always
        // passes -exitcode explicitly because without it SpotBugs exits 0
        // even with bugs reported. Exits 2/3 (MISSING_CLASS_FLAG: degraded
        // analysis) and 4+ (ERROR_FLAG) are infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Whole-tree bytecode analysis is the slow case: a full-tree scan
        // routinely takes minutes. Operators tune it with TimeoutSeconds;
        // the base still caps it at MaxTimeoutSeconds.
        Timeout = TimeSpan.FromMinutes(10),
        // Findings in vendored trees and dependency checkouts describe
        // upstream code, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config. Build-output directories
        // (target/, build/, out/) are deliberately NOT excluded: they hold
        // the project's own compiled classes under analysis, and excluding
        // them would blind a bytecode auditor.
        ExcludePaths = ["vendor/", "third_party/", "external/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _filterFilePath = static () => null;
    private Func<string?> _confidenceLevel = static () => DefaultConfidenceLevel;
    private Func<string?> _effortLevel = static () => DefaultEffortLevel;

    /// <inheritdoc />
    public override string Name => "codeybox:spotbugs";

    /// <inheritdoc />
    protected override string ToolName => "spotbugs";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from SpotBugs' SARIF level vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. The tool emits <c>error</c> for the
    /// scariest/scary bug ranks, <c>warning</c> for troubling,
    /// <c>note</c> for of-concern (see <c>Level.fromBugRank</c> in the
    /// SpotBugs SARIF reporter); <c>none</c> completes the SARIF level
    /// vocabulary. Only <c>error</c> fails the audit — raw levels never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>
        {
            // First option selects the command-line UI (the default entry
            // point would open the GUI).
            "-textui",
        };

        if (!ExtraArgumentsSupplyFlag(options, "-sarif"))
        {
            // Machine-readable SARIF 2.1.0 for the shared parser. The
            // `=path` suffix is SpotBugs' documented file sink; /dev/stdout
            // (Linux sandboxes) streams the document into the captured
            // stdout the parser reads. An operator -sarif would replace the
            // report the parser expects and break the run into
            // infrastructure failure; let that surface loudly.
            args.Add("-sarif=/dev/stdout");
        }

        if (!ExtraArgumentsSupplyFlag(options, "-exitcode"))
        {
            // Without this SpotBugs exits 0 even with bugs reported; the
            // base could not tell "ran and found problems" from "clean".
            // With it the exit is a bit set: 1 = bugs found, 2 =
            // missing classes, 4 = analysis errors.
            args.Add("-exitcode");
        }

        if (!ExtraArgumentsSupplyFlag(options, "-noClassOk"))
        {
            // A tree with no analysable classes emits an empty report
            // instead of failing: nothing to check is a clean pass.
            args.Add("-noClassOk");
        }

        if (!ExtraArgumentsSupplyFlag(options, "-quiet"))
        {
            // Error chatter would pollute the captured stdout the SARIF
            // parser reads; errors still set the exit-code bits.
            args.Add("-quiet");
        }

        if (!ExtraArgumentsSupplyFlag(options, "-low", "-medium", "-high"))
            args.Add("-" + ResolveConfidenceLevel(_confidenceLevel()));

        if (!ExtraArgumentsSupplyEffortFlag(options))
            args.Add("-effort:" + ResolveEffortLevel(_effortLevel()));

        var filterFilePath = _filterFilePath();
        if (!string.IsNullOrWhiteSpace(filterFilePath)
            && !ExtraArgumentsSupplyFlag(options, "-exclude"))
        {
            args.Add("-exclude");
            args.Add(filterFilePath.Trim());
        }

        args.Add(".");
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _filterFilePath = () => scoped[FilterFilePathKey];
        _confidenceLevel = () => scoped[ConfidenceLevelKey];
        _effortLevel = () => scoped[EffortLevelKey];
        context.Logger.LogInformation(
            "SpotBugsAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private string ResolveConfidenceLevel(string? configured)
    {
        var level = configured?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(level))
            return DefaultConfidenceLevel;
        if (!KnownConfidenceLevels.Contains(level))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with unknown {ConfidenceLevelKey} "
                + $"'{TruncateForMessage(configured)}'. Set CodeyBox:Plugins:{PluginId}:{ConfidenceLevelKey} "
                + "to high, medium, or low.")
            { IsDeterministic = true };
        return level;
    }

    private string ResolveEffortLevel(string? configured)
    {
        var level = configured?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(level))
            return DefaultEffortLevel;
        if (!KnownEffortLevels.Contains(level))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with unknown {EffortLevelKey} "
                + $"'{TruncateForMessage(configured)}'. Set CodeyBox:Plugins:{PluginId}:{EffortLevelKey} "
                + "to min, less, default, more, or max.")
            { IsDeterministic = true };
        return level;
    }

    private static bool ExtraArgumentsSupplyEffortFlag(ExternalToolAuditorOptions options)
    {
        // SpotBugs spells the effort flag `-effort:<level>` (colon form),
        // which the shared `=`-attached check does not recognise — check
        // the colon form here so an operator setting never stacks on ours.
        if (ExtraArgumentsSupplyFlag(options, "-effort"))
            return true;
        foreach (var arg in options.ExtraArguments)
        {
            if (arg.StartsWith("-effort:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
