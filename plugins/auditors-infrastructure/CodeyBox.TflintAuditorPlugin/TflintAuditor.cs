using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.TflintAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>tflint</c> (Terraform static analysis)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/> and the
/// tflint-specific arguments and knobs below. Output parsing is the shared
/// <see cref="SarifToolOutputParser"/> — tflint's <c>--format sarif</c>
/// report carries the rule id, message, severity level, and file/line
/// location per result, so no plugin-local parser exists to drift.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — not blocking by
/// default.</b> tflint severities go through the declared map, never raw:
/// <c>error</c> → <see cref="AuditSeverity.Error"/> (fails the audit),
/// <c>warning</c> → <see cref="AuditSeverity.Warning"/> (advisory),
/// <c>notice</c> → <see cref="AuditSeverity.Info"/> (informational).
/// A clean tree, or one with only warnings and notices, passes; a tree with
/// an error-severity result fails. <c>MinimumSeverity</c> only drops
/// findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against tflint 0.64.0 by running
/// the provisioned binary).</b> tflint exits <c>0</c> when the scan completes
/// with no issues, <c>2</c> when issues were found, and <c>1</c> for run
/// failures (unknown flags, unreadable config) — but <c>1</c> is ALSO the
/// exit for repository problems tflint still reports as SARIF: an
/// unparseable <c>.tf</c> file and an unusable plugin setup (e.g. a
/// <c>.tflint.hcl</c> plugin never installed via <c>tflint --init</c>) both
/// exit <c>1</c> with a <c>tflint-errors</c> run in the SARIF document. The
/// discriminator is therefore the report itself, not the exit code: exits
/// <c>0</c>, <c>1</c>, and <c>2</c> are findings-producing, and an exit
/// without a parseable SARIF document on stdout fails closed as
/// infrastructure through the parser. <c>126</c>/<c>127</c> (cannot execute /
/// not found) and any other exit are infrastructure.</para>
///
/// <para><b>Version pin.</b> The rule surface (bundled terraform ruleset,
/// severity assignments, SARIF shape) changes between releases, so findings
/// are only meaningful from the build the auditor was verified against. The
/// auditor probes <c>tflint --version</c> before the scan; a missing binary,
/// an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled configuration.</b> tflint reads
/// <c>.tflint.hcl</c> from the audited repository (rule toggles, plugin
/// requirements, presets) and honors <c>tflint-ignore: &lt;rule&gt;</c>
/// comments inside <c>.tf</c> files, so the audit subject can narrow what the
/// tool reports. That file is the tool's normal configuration surface, not
/// an auditor bypass: findings it suppresses are suppressed by the scanner
/// itself, and a config the tool cannot satisfy (missing plugin, unreadable
/// file) surfaces as an error-severity SARIF result that fails the audit
/// rather than a silent pass. Operators who want a fixed rule set regardless
/// of repository content pin it with <c>ConfigFile</c> (pointing outside the
/// audited tree) or the <c>EnableRules</c>/<c>DisableRules</c>/
/// <c>OnlyRules</c> knobs.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is
/// <c>tflint --format sarif --recursive</c> from the work-tree root: without
/// <c>--recursive</c> tflint inspects only the top-level directory, so a
/// Terraform tree nested under <c>infra/</c> or <c>terraform/</c> would pass
/// vacuously. On top of that, findings under generated and vendored trees
/// (<c>.terraform/</c>, <c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) are dropped by default — <c>.terraform/</c> holds
/// modules downloaded by <c>terraform init</c>, so problems there describe
/// upstream code, not the change under audit. Operators re-include a path by
/// overriding <c>ExcludePaths</c> in scoped config, and narrow the scan
/// itself with tflint's own <c>--filter</c> via <c>ExtraArguments</c>.</para>
///
/// <para><b>Network.</b> The default scan uses only the bundled
/// <c>terraform</c> ruleset shipped inside the binary and declares no network
/// capability. A repository <c>.tflint.hcl</c> that requires external plugins
/// needs those plugins installed at bake time (via <c>tflint --init</c>
/// against the plugin releases); an uninstalled plugin is an error-severity
/// finding, not a silent skip — the auditor never runs <c>--init</c> itself
/// because that would fetch unpinned code during the audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: TFLint Terraform",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "tflint",
    InstallHint = "provision the pinned tflint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "tflint — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class TflintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.tflint";

    /// <summary>
    /// tflint release the invocation and its findings are verified against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.64.0";

    /// <summary>
    /// Scoped-config key for tflint's <c>--config</c> file. Unset → tflint's
    /// own default (<c>.tflint.hcl</c> in the audited repository). Point it
    /// outside the audited tree for a rule set the repository cannot narrow.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for rules to enable (comma-separated rule names,
    /// each emitted as a repeatable <c>--enable-rule</c> argument).
    /// </summary>
    public const string EnableRulesKey = "EnableRules";

    /// <summary>
    /// Scoped-config key for rules to disable (comma-separated rule names,
    /// each emitted as a repeatable <c>--disable-rule</c> argument).
    /// </summary>
    public const string DisableRulesKey = "DisableRules";

    /// <summary>
    /// Scoped-config key for exclusive rule selection (comma-separated rule
    /// names, each emitted as a repeatable <c>--only</c> argument; all other
    /// defaults are disabled by the tool).
    /// </summary>
    public const string OnlyRulesKey = "OnlyRules";

    /// <summary>
    /// Scoped-config boolean for recursive scanning (<c>--recursive</c>).
    /// Default true: without it tflint inspects only the top-level directory
    /// and a nested Terraform tree would pass vacuously. Set to
    /// <c>false</c> only for repositories whose Terraform lives at the root.
    /// </summary>
    public const string RecursiveKey = "Recursive";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed scan, no issues; 2 = completed scan with issues;
        // 1 = run failure OR a repository problem still reported as SARIF
        // (tflint-errors run: broken HCL, missing plugin, unreadable
        // config). The SARIF document on stdout is the discriminator: usage
        // failures emit plain text and no report, so exit 1 without a report
        // fails closed through the parser. Every other exit is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1, 2 },
        // Findings under downloaded-module and vendored trees describe
        // upstream code, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = [".terraform/", "vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configFile = static () => null;
    private Func<IReadOnlyList<string>> _enableRules = static () => [];
    private Func<IReadOnlyList<string>> _disableRules = static () => [];
    private Func<IReadOnlyList<string>> _onlyRules = static () => [];
    private Func<bool> _recursive = static () => true;

    /// <inheritdoc />
    public override string Name => "codeybox:tflint";

    /// <summary>
    /// The default scan uses only the ruleset bundled inside the binary, so
    /// the auditor needs no network egress and runs in the most restrictive
    /// sandbox. Repositories whose <c>.tflint.hcl</c> requires external
    /// plugins must have those plugins provisioned at bake time; the auditor
    /// never fetches them during the audit.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "tflint";

    /// <summary>
    /// The shared SARIF parser: tflint's <c>--format sarif</c> report carries
    /// rule id, level, message, and file/line per result across its
    /// <c>tflint</c> and <c>tflint-errors</c> runs, so both lint findings and
    /// repository problems (broken HCL, unusable config) become findings with
    /// their locations preserved.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from tflint's severity vocabulary
    /// (<c>error</c>/<c>warning</c>/<c>notice</c>, as listed by
    /// <c>--minimum-failure-severity</c>) to <see cref="AuditSeverity"/>:
    /// <c>error</c> fails the audit, <c>warning</c> is advisory,
    /// <c>notice</c> (and its SARIF <c>note</c> spelling) is informational.
    /// Anything unrecognised maps to <see cref="AuditSeverity.Warning"/> —
    /// visible, never silently informational and never raw. Raw tool tokens
    /// never reach findings; the tool level is preserved in the finding
    /// description as proof the value flowed through the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["notice"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // --format is the auditor's parsing contract (the shared SARIF
        // parser) and --fix rewrites the audited tree — an auditor must never
        // mutate what it audits. Both are rejected deterministically rather
        // than left to misfire (a non-SARIF report would fail closed through
        // the parser, but the message would blame the tool instead of the
        // configuration that caused it).
        if (ExtraArgumentsSupplyFlag(options, "--format", "-f"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --format/-f ExtraArguments "
                + "entry — the SARIF report shape is the auditor's parsing contract. Remove it; use "
                + $"CodeyBox:Plugins:{PluginId} rule and severity knobs to shape the verdict instead.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--fix"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --fix ExtraArguments "
                + "entry — auditors report on the tree, they never rewrite it. Remove it.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "--format", "sarif",
        };

        if (_recursive())
            args.Add("--recursive");

        var configFile = ValidatedScopedValue(_configFile(), ConfigFileKey);
        AddValueFlag(args, "--config", configFile);

        foreach (var rule in _enableRules())
        {
            args.Add("--enable-rule");
            args.Add(ValidatedRuleName(rule, EnableRulesKey));
        }
        foreach (var rule in _disableRules())
        {
            args.Add("--disable-rule");
            args.Add(ValidatedRuleName(rule, DisableRulesKey));
        }
        foreach (var rule in _onlyRules())
        {
            args.Add("--only");
            args.Add(ValidatedRuleName(rule, OnlyRulesKey));
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
        _enableRules = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[EnableRulesKey]);
        _disableRules = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[DisableRulesKey]);
        _onlyRules = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[OnlyRulesKey]);
        _recursive = () => !bool.TryParse(scoped[RecursiveKey], out var recursive) || recursive;
        context.Logger.LogInformation(
            "TflintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string ValidatedRuleName(string value, string key)
        => ValidatedArgumentValue(value, $"{PluginId}:{key}");
}
