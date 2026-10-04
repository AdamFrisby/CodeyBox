using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ZizmorAuditorPlugin;

/// <summary>
/// GitHub Actions security auditor wrapping <c>zizmor</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the SARIF report parser, the pinned
/// tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the zizmor-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — advisory by default,
/// not blocking.</b> zizmor severities surface in SARIF as <c>level</c>
/// values (<c>error</c> for high, <c>warning</c> for medium, <c>note</c> for
/// informational/low) and go through the declared map below, never raw:
/// <c>error</c> → <see cref="AuditSeverity.Error"/> (fails the audit);
/// <c>warning</c> → <see cref="AuditSeverity.Warning"/> (advisory);
/// <c>note</c>/<c>none</c> → <see cref="AuditSeverity.Info"/>
/// (informational); anything unrecognised → <see cref="AuditSeverity.Warning"/>
/// — visible, never silently informational. The tool severity is preserved in
/// the finding description as proof the value flowed through the mapping.
/// <c>MinimumSeverity</c> only drops findings, it never raises them. The
/// auditor is therefore a merge gate for high-severity findings
/// (e.g. template injection), not a blocker on every note.</para>
///
/// <para><b>Exit-code convention (verified against zizmor 1.30.1 by running
/// the provisioned binary).</b> With <c>--format sarif</c> zizmor exits
/// <c>0</c> whenever the scan completes — <em>with or without
/// findings</em>; the SARIF document on stdout is the discriminator, not the
/// exit code. Exits <c>11</c>–<c>14</c> are the dedicated "ran and found
/// problems" verdicts (highest finding informational/low/medium/high) but
/// only occur in non-SARIF modes; they are declared findings-producing so a
/// build that reports SARIF alongside them still yields findings, while a
/// non-SARIF body on those exits fails closed through the parser. Exits
/// <c>1</c> (error during audit), <c>2</c> (argument parsing failure), and
/// <c>3</c> (no inputs collected — e.g. a tree with no workflows or actions)
/// write diagnostics, not the SARIF report, so the parser fails closed and
/// "could not run" is infrastructure, not findings. <c>126</c>/<c>127</c>
/// (cannot execute / not found) and any other exit are infrastructure.</para>
///
/// <para><b>Repositories without GitHub Actions fail closed.</b> zizmor exits
/// <c>3</c> ("no inputs collected") when the tree yields no auditable
/// inputs — that is infrastructure, not a pass. Enable this auditor only on
/// projects that use GitHub Actions; a vacuous pass on a tree outside the
/// tool's scope would train operators to ignore it.</para>
///
/// <para><b>Version pin.</b> The audit surface (rule set, severity
/// assignments, SARIF shape) changes between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>zizmor --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> zizmor honors ignore
/// comments inside workflow files and <c>ignore</c> rules in its
/// configuration file, both authored inside the audited repository. An
/// auditor its subject can silence at finding granularity is not a gate, so
/// the default scan passes <c>--no-ignores</c>, making both surfaces inert.
/// Whole-rule <c>disable</c> entries in a repository <c>zizmor.yml</c> still
/// apply — that is the tool's normal configuration surface, documented in
/// the plugin README. Operators who deliberately trust repository-authored
/// suppression set <c>TrustRepositorySuppression</c> in scoped config, and
/// operators who want a fixed configuration regardless of repository content
/// pin it with <c>ConfigFile</c> (pointing outside the audited tree).</para>
///
/// <para><b>Scope and defaults.</b> The default scan targets the work-tree
/// root (<c>.</c>), where zizmor collects workflows and composite actions
/// while respecting <c>.gitignore</c> — application code outside those
/// inputs is out of scope by construction, as are Dependabot and pre-commit
/// inputs (collected only with an explicit operator <c>--collect</c>). On top
/// of that, findings under vendored trees (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) are dropped by default — a
/// vendored snapshot carrying its own <c>.github</c> directory describes
/// upstream code, not the change under audit. Operators re-include a path by
/// overriding <c>ExcludePaths</c> in scoped config, and narrow the scan
/// itself to explicit files or directories with <c>Targets</c>.</para>
///
/// <para><b>Network.</b> The default scan declares no network capability.
/// <c>--offline</c> disables all online audit rules and remote repository
/// fetches, so the auditor never contacts the network during the audit and
/// needs no API token. Audits that require GitHub API access do not run —
/// their absence is a documented blind spot, not a pass over that
/// surface.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Zizmor GitHub Actions Security",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "zizmor",
    InstallHint = "provision the pinned zizmor release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "a version-pinned zizmor — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ZizmorAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.zizmor";

    /// <summary>
    /// zizmor release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.30.1";

    /// <summary>
    /// Scoped-config key for a global zizmor configuration file
    /// (<c>--config</c>). Unset → zizmor's own local discovery (a
    /// <c>zizmor.yml</c>/<c>zizmor.yaml</c> in the audited repository or its
    /// <c>.github</c> directory). Point it outside the audited tree for a
    /// configuration the repository cannot narrow.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repo-relative file or directory paths, passed as positional inputs).
    /// Unset → the work-tree root (<c>.</c>), where zizmor collects
    /// workflows and composite actions itself.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key opting back in to repository-authored suppression
    /// (ignore comments in workflow files, <c>ignore</c> rules in
    /// <c>zizmor.yml</c>). Default false: the audited repository must not be
    /// able to silence findings at finding granularity.
    /// </summary>
    public const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed scan, with or without findings (SARIF mode): the
        // SARIF document on stdout is the discriminator. 11-14 = completed
        // scan whose highest finding is informational/low/medium/high —
        // only emitted in non-SARIF modes, but declared so a build that
        // reports SARIF alongside them still yields findings; a non-SARIF
        // body on those exits fails closed through the parser. 1 (audit
        // error), 2 (usage error) and 3 (no inputs collected) carry no
        // SARIF report and fail closed as infrastructure. Every other exit
        // is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 11, 12, 13, 14 },
        // A vendored snapshot carrying its own .github directory describes
        // upstream workflows, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configFile = static () => null;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:zizmor";

    /// <summary>
    /// The default scan passes <c>--offline</c>: no online audit rules, no
    /// remote fetches, no API token — the auditor needs no network egress
    /// and runs in the most restrictive sandbox.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "zizmor";

    /// <summary>
    /// The shared SARIF parser: zizmor's <c>--format sarif</c> report
    /// carries the rule id (<c>zizmor/&lt;audit&gt;</c>), the severity as
    /// <c>level</c>, the message, and the first physical location's artifact
    /// URI plus <c>region.startLine</c>, so rule id and file/line are
    /// preserved with no plugin-local severity smuggling — the level flows
    /// through the declared severity mapping below.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from zizmor's severity vocabulary (surfaced in SARIF
    /// as <c>error</c> for high, <c>warning</c> for medium, <c>note</c> for
    /// informational/low) to <see cref="AuditSeverity"/>, so its "high"
    /// means what every other auditor's "high" means: <c>error</c> fails the
    /// audit, <c>warning</c> is advisory, <c>note</c>/<c>none</c> are
    /// informational. Both the SARIF spellings and the tool's native words
    /// are translated (plus the common scanner tokens) so a severity means
    /// the same thing regardless of which scanner produced it; anything
    /// unrecognised maps to <see cref="AuditSeverity.Warning"/> — visible,
    /// never silently informational and never raw. Raw tool tokens never
    /// reach findings; the level is preserved in the finding description as
    /// proof the value flowed through the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
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
        // --format is the auditor's parsing contract (the SARIF report the
        // shared parser reads). --fix rewrites the audited tree — an auditor
        // reports on the tree, it never mutates it. --config/-c/--no-config
        // are managed by the ConfigFile knob below. All are rejected
        // deterministically rather than left to misfire (a non-SARIF report
        // would fail closed through the parser, but the message would blame
        // the tool instead of the configuration that caused it).
        if (ExtraArgumentsSupplyFlag(options, "--format"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --format ExtraArguments "
                + "entry — the SARIF report shape is the auditor's parsing contract. Remove it; use "
                + $"CodeyBox:Plugins:{PluginId} rule and severity knobs to shape the verdict instead.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--fix"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --fix ExtraArguments "
                + "entry — auditors report on the tree, they never rewrite it. Remove it.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--config", "-c", "--no-config"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --config/-c/--no-config "
                + "ExtraArguments entry — configuration file resolution is managed by the "
                + $"{ConfigFileKey} scoped-config knob. Remove it.")
            { IsDeterministic = true };
        if (options.ExtraArguments.Contains("-", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a '-' ExtraArguments "
                + "entry — the stdin sentinel would make the tool read its input from stdin "
                + "instead of the audited worktree. Remove it.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--gh-token"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --gh-token ExtraArguments "
                + "entry — the default scan is offline so a token is inert, and API credentials must "
                + "never ride auditor argv. Remove it.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            // Offline first: no online audit rules, no remote repository
            // fetches, no token — the hermetic default the No-network
            // capability declaration rests on.
            "--offline",
            // No progress bars on stderr: bounded, machine-readable streams.
            "--no-progress",
            // SARIF is the report the parser reads; it lands on stdout while
            // human diagnostics stay on stderr.
            "--format", "sarif",
        };

        // The audit subject authors ignore comments and zizmor.yml ignore
        // rules; keep both inert unless the operator opts in to
        // repo-controlled suppression.
        if (!_trustRepositorySuppression())
            args.Add("--no-ignores");

        var configFile = ValidatedScopedValue(_configFile(), ConfigFileKey);
        AddValueFlag(args, "--config", configFile);

        var targets = _targets().Where(static target => !string.IsNullOrWhiteSpace(target)).ToList();
        if (targets.Count == 0)
        {
            // zizmor requires at least one positional input: the work-tree
            // root, where it collects workflows and composite actions while
            // respecting .gitignore.
            args.Add(".");
        }
        else
        {
            foreach (var target in targets)
                args.Add(ValidatedRepoRelativeTarget(target, $"{PluginId}:{TargetsKey}"));
        }

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
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "ZizmorAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
