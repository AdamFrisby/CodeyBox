using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.KubeLinterAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>kube-linter</c> (KubeLinter —
/// Kubernetes manifest policy analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/> and the kube-linter
/// arguments and knobs below. Output parsing is the shared
/// <see cref="SarifToolOutputParser"/> — kube-linter's
/// <c>--format sarif</c> report carries the check name as <c>ruleId</c>, the
/// message, and the file/line (object start) location per result, so no
/// plugin-local parser exists to drift.
///
/// <para><b>Gate behaviour: blocking.</b> kube-linter has no severity
/// vocabulary — a check either fired or it did not, and its SARIF results
/// carry no <c>level</c> (the shared parser supplies the SARIF-default
/// <c>warning</c> token). The declared mapping sends every reported
/// violation to <see cref="AuditSeverity.Error"/>: an enabled check that
/// fires is a policy failure, so each finding fails the audit. There is no
/// advisory mode; narrow the check set with <c>ExcludeChecks</c>,
/// <c>ExcludedRules</c>, a pinned <c>ConfigFile</c>, or the repository's own
/// <c>.kube-linter.yaml</c> instead.</para>
///
/// <para><b>Exit-code convention (verified against kube-linter 0.8.3 by
/// running the release binary).</b> kube-linter does NOT follow the common
/// "1 = findings, 2 = could not run" convention: every failure mode — an
/// unknown flag, a missing scan target, an unreadable <c>--config</c> —
/// exits <c>1</c> with a plain-text error on stderr and no report. A
/// completed scan also exits <c>1</c> when checks fire
/// (<c>Error: found N lint errors</c> on stderr), but it always writes the
/// SARIF report to stdout first. The discriminator is therefore the report
/// itself, not the exit code: exits <c>0</c> and <c>1</c> are
/// findings-producing, and an exit without a parseable SARIF document on
/// stdout fails closed as infrastructure through the parser.
/// <c>126</c>/<c>127</c> (cannot execute / not found) and any other exit are
/// infrastructure.</para>
///
/// <para><b>Empty scans fail closed.</b> kube-linter exits <c>0</c> with no
/// report when the targets contain no parseable objects, or when config
/// leaves zero checks enabled (<c>Warning: …</c> on stderr, empty stdout) —
/// indistinguishable from "ran clean" without the report contract. The
/// auditor passes <c>--fail-if-no-objects-found</c> so "nothing to check"
/// is kube-linter's own error (<c>Error: no valid objects found</c>, exit
/// 1, no report), and the empty-stdout cases fail closed through the parser
/// as infrastructure either way: an audit that inspected nothing, or ran no
/// checks, is never a pass.</para>
///
/// <para><b>Version pin.</b> The check catalogue (built-in checks, default
/// set, SARIF shape) changes between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>kube-linter version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled configuration.</b> kube-linter
/// auto-loads <c>.kube-linter.yaml</c>/<c>.kube-linter.yml</c> from the
/// audited repository root (check selection, custom checks, exclusions).
/// That file is the tool's normal configuration surface, not an auditor
/// bypass: checks it excludes are excluded by the scanner itself, and a
/// config that enables zero checks surfaces as an infrastructure failure
/// (empty stdout), never a silent pass. Operators who want a fixed check
/// set regardless of repository content pin it with <c>ConfigFile</c>
/// (pointing outside the audited tree) or the
/// <c>IncludeChecks</c>/<c>ExcludeChecks</c>/<c>DoNotAutoAddDefaults</c>
/// knobs.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is
/// <c>kube-linter lint .</c>: kube-linter walks the tree for
/// <c>*.yaml</c>/<c>*.yml</c> documents, renders Helm chart directories and
/// Kustomize roots in place (no network — templating is embedded in the
/// binary), and reports each object start as the finding location. On top
/// of that, findings under vendored and dependency trees (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) are dropped by default —
/// problems there describe upstream packages, not the change under audit.
/// Operators re-include a path by overriding <c>ExcludePaths</c> in scoped
/// config, exclude whole subtrees from the scan itself with
/// <c>IgnorePaths</c>, and narrow targets with <c>Targets</c>.</para>
///
/// <para><b>Network.</b> The default scan renders everything locally and
/// declares no network capability. A Helm chart whose dependencies were
/// never vendored (<c>helm dependency build</c> at authoring time) fails to
/// load — kube-linter warns on stderr and continues with the charts it can
/// render; the auditor never fetches during the audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: KubeLinter Kubernetes Manifests",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "kube-linter",
    InstallHint = "provision the pinned kube-linter release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "kube-linter — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class KubeLinterAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.kube-linter";

    /// <summary>
    /// kube-linter release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.8.3";

    /// <summary>
    /// Scoped-config key for kube-linter's <c>--config</c> file. Unset →
    /// kube-linter's own default (<c>.kube-linter.yaml</c>/
    /// <c>.kube-linter.yml</c> auto-loaded from the audited repository
    /// root). Point it outside the audited tree for a check set the
    /// repository cannot narrow.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for checks to add (comma-separated check names,
    /// each emitted as a repeatable <c>--include</c> argument). Adds to the
    /// default set unless <see cref="DoNotAutoAddDefaultsKey"/> is set —
    /// combine them for exclusive selection.
    /// </summary>
    public const string IncludeChecksKey = "IncludeChecks";

    /// <summary>
    /// Scoped-config key for checks to drop (comma-separated check names,
    /// each emitted as a repeatable <c>--exclude</c> argument). An unknown
    /// name is silently ignored by kube-linter (more findings than
    /// intended, never fewer).
    /// </summary>
    public const string ExcludeChecksKey = "ExcludeChecks";

    /// <summary>
    /// Scoped-config boolean for <c>--do-not-auto-add-defaults</c>: run only
    /// the checks named by <see cref="IncludeChecksKey"/> (or the pinned
    /// <c>ConfigFile</c>). An empty result set means zero enabled checks,
    /// which kube-linter reports as "no checks enabled" and the auditor
    /// fails closed as infrastructure — pair it with an include list.
    /// </summary>
    public const string DoNotAutoAddDefaultsKey = "DoNotAutoAddDefaults";

    /// <summary>
    /// Scoped-config boolean for <c>--add-all-built-in</c>: run every
    /// built-in check, not just the default set (opt individual checks out
    /// with <see cref="ExcludeChecksKey"/>).
    /// </summary>
    public const string AddAllBuiltInKey = "AddAllBuiltIn";

    /// <summary>
    /// Scoped-config key for kube-linter's <c>--ignore-paths</c>
    /// (comma-separated doublestar globs matched against absolute paths —
    /// e.g. <c>**/vendor/**</c> — each emitted as a repeatable argument).
    /// Unlike <c>ExcludePaths</c> (a post-scan finding filter), ignored
    /// paths are never loaded, so broken vendored manifests cannot stall
    /// the scan.
    /// </summary>
    public const string IgnorePathsKey = "IgnorePaths";

    /// <summary>
    /// Scoped-config key for scan targets (comma-separated files/folders,
    /// positional args). Unset → <c>.</c> (the whole work tree).
    /// </summary>
    public const string TargetsKey = "Targets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed scan, no findings; 1 = completed scan with findings
        // ("Error: found N lint errors") OR a run failure (unknown flag,
        // missing target, unreadable config — all exit 1 on stderr with no
        // report). The SARIF document on stdout is the discriminator: run
        // failures emit plain text and no report, so either exit without one
        // fails closed through the parser. Every other exit is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees describe upstream manifests,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configFile = static () => null;
    private Func<IReadOnlyList<string>> _includeChecks = static () => [];
    private Func<IReadOnlyList<string>> _excludeChecks = static () => [];
    private Func<IReadOnlyList<string>> _ignorePaths = static () => [];
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _doNotAutoAddDefaults = static () => false;
    private Func<bool> _addAllBuiltIn = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:kube-linter";

    /// <summary>
    /// kube-linter renders manifests, Helm charts, and Kustomize roots
    /// entirely in-process, so the auditor needs no network egress and runs
    /// in the most restrictive sandbox.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "kube-linter";

    /// <summary>
    /// The shared SARIF parser: kube-linter's <c>--format sarif</c> report
    /// carries the check name as <c>ruleId</c>, the diagnostic message, and
    /// the manifest path plus object-start line per result, so locations
    /// are preserved without a plugin-local parser.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from kube-linter's severity vocabulary to
    /// <see cref="AuditSeverity"/>. kube-linter has no levels — every result
    /// is a violation of an enabled check — so the SARIF-default
    /// <c>warning</c> token and every other spelling map to
    /// <see cref="AuditSeverity.Error"/>, as does anything unrecognised from
    /// a foreign build (fail closed: "a check fired" always means the
    /// policy failed). Raw tool tokens never reach findings; the token is
    /// preserved in the finding description as proof the value flowed
    /// through the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Error,
            ["note"] = AuditSeverity.Error,
            ["none"] = AuditSeverity.Error,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // --format is the auditor's parsing contract (the shared SARIF
        // parser) and is repeatable in kube-linter — a second --format
        // writes another report to stdout and corrupts the document; a
        // paired --output diverts it to a file (leaving stdout empty — and
        // writing into the audited tree, which an auditor must never do).
        // --config duplicates the scoped ConfigFile knob. All three are
        // rejected deterministically rather than left to misfire into an
        // infrastructure failure that blames the tool instead of the
        // configuration that caused it.
        if (ExtraArgumentsSupplyFlag(options, "--format"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --format ExtraArguments "
                + "entry — the SARIF report shape is the auditor's parsing contract. Remove it; use "
                + $"CodeyBox:Plugins:{PluginId} check and severity knobs to shape the verdict instead.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--output"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with an --output ExtraArguments "
                + "entry — it would divert the SARIF report off stdout and write into the audited "
                + "tree; auditors report, they never write. Remove it.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--config"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --config ExtraArguments "
                + $"entry — use the scoped ConfigFile key under CodeyBox:Plugins:{PluginId} so the "
                + "operator-pinned config is unambiguous.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "lint",
            "--format", "sarif",
            // "No valid objects found" / a zero-check config otherwise exits
            // 0 with empty stdout — a vacuous pass. Fail closed instead:
            // an audit that inspected nothing verified nothing.
            "--fail-if-no-objects-found",
        };

        AddValueFlag(args, "--config", ValidatedScopedValue(_configFile(), ConfigFileKey));

        if (_doNotAutoAddDefaults())
            args.Add("--do-not-auto-add-defaults");
        if (_addAllBuiltIn())
            args.Add("--add-all-built-in");

        foreach (var check in _includeChecks())
        {
            args.Add("--include");
            args.Add(ValidatedCheckName(check, IncludeChecksKey));
        }
        foreach (var check in _excludeChecks())
        {
            args.Add("--exclude");
            args.Add(ValidatedCheckName(check, ExcludeChecksKey));
        }
        foreach (var path in _ignorePaths())
        {
            args.Add("--ignore-paths");
            args.Add(ValidatedArgumentValue(path, $"{PluginId}:{IgnorePathsKey}"));
        }

        // kube-linter parses with pflag, which interleaves flags and
        // positionals, so ExtraArguments flags appended after the targets
        // still reach the flag parser. Targets are validated argv values
        // (never leading-dash, never control characters) so they cannot be
        // mistaken for flags.
        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ValidatedArgumentValue(t, $"{PluginId}:{TargetsKey}"))
            .ToList();
        if (targets.Count == 0)
            args.Add(".");
        else
            args.AddRange(targets);

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configFile = () => scoped[ConfigFileKey];
        _includeChecks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[IncludeChecksKey]);
        _excludeChecks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[ExcludeChecksKey]);
        _ignorePaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[IgnorePathsKey]);
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _doNotAutoAddDefaults = () =>
            bool.TryParse(scoped[DoNotAutoAddDefaultsKey], out var value) && value;
        _addAllBuiltIn = () => bool.TryParse(scoped[AddAllBuiltInKey], out var value) && value;
        context.Logger.LogInformation(
            "KubeLinterAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string ValidatedCheckName(string value, string key)
        => ValidatedArgumentValue(value, $"{PluginId}:{key}");
}
