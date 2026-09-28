using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CfnlintAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>cfn-lint</c> (AWS CloudFormation
/// template validation) on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/> and the
/// cfn-lint-specific arguments and knobs below. Output parsing is the shared
/// <see cref="SarifToolOutputParser"/> — cfn-lint's <c>--format sarif</c>
/// report carries the rule id, message, severity level, and file/line
/// location per result, so no plugin-local parser exists to drift.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — not blocking by
/// default.</b> cfn-lint severities go through the declared map, never raw:
/// <c>error</c> → <see cref="AuditSeverity.Error"/> (fails the audit),
/// <c>warning</c> → <see cref="AuditSeverity.Warning"/> (advisory),
/// <c>note</c>/<c>informational</c> → <see cref="AuditSeverity.Info"/>
/// (informational). A clean tree, or one with only warnings and informationals,
/// passes; a tree with an error-severity result fails. <c>MinimumSeverity</c>
/// only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against cfn-lint 1.57.0 by running
/// the provisioned binary).</b> cfn-lint returns a bitwise OR over the
/// severities it found: <c>0</c> clean, <c>2</c> error, <c>4</c> warning,
/// <c>8</c> informational, and the combinations <c>6</c>/<c>10</c>/
/// <c>12</c>/<c>14</c> — every one of those writes the SARIF report to
/// stdout and is findings-producing. Usage errors (unknown flags) exit
/// <c>1</c> with plain-text usage on stderr and no report. The discriminator
/// is therefore the report itself, not the exit code: the declared
/// findings-producing exits carry a parseable SARIF document, and an exit
/// without one fails closed as infrastructure through the parser.
/// <c>126</c>/<c>127</c> (cannot execute / not found) and any other exit are
/// infrastructure. Repository problems the tool can still report — an
/// unparseable template (<c>E0000</c>), a missing template file
/// (<c>E0003</c>), a non-CloudFormation YAML document (<c>E1001</c>), and a
/// run with no templates at all (<c>E1001</c> with no location) — arrive as
/// error-severity SARIF results that fail the audit rather than a silent
/// pass.</para>
///
/// <para><b>Version pin.</b> The rule surface (rule ids, severity
/// assignments, SARIF shape) changes between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>cfn-lint --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Variadic-flag ordering constraint.</b> cfn-lint's rule and region
/// selectors (<c>--include-checks</c>, <c>--ignore-checks</c>,
/// <c>--regions</c>) accept a variable number of values, so a template path
/// following one is swallowed into the flag's values and silently never
/// linted (verified: <c>-c I template.yaml</c> lints nothing). The auditor
/// therefore emits every flag in <see cref="BuildToolArguments"/> and every
/// scan target after a <c>--</c> separator in
/// <see cref="ResolveContextArgumentsAsync"/>, and rejects flag-looking
/// <c>ExtraArguments</c> entries outright (deterministic infrastructure
/// failure with a pointer to the right knob) rather than letting them
/// misfire after the separator as bogus template paths. Operator-facing
/// flags are exposed as scoped keys: <c>ConfigFile</c>,
/// <c>IncludeChecks</c>, <c>IgnoreChecks</c>, <c>Regions</c>,
/// <c>Targets</c>.</para>
///
/// <para><b>Repository-controlled configuration.</b> cfn-lint reads
/// <c>.cfnlintrc</c> (<c>.cfnlintrc.yaml</c>/<c>.cfnlintrc.yml</c>) from the
/// audited repository (rule toggles, regions, template lists) and honors
/// per-resource <c>Metadata: cfn-lint: config: ignore_checks:</c> overrides
/// inside templates, so the audit subject can narrow what the tool reports.
/// That file is the tool's normal configuration surface, not an auditor
/// bypass: findings it suppresses are suppressed by the scanner itself, and
/// a tree with no templates to scan surfaces as an error-severity
/// <c>E1001</c> result that fails the audit rather than a silent pass.
/// Explicit <c>Targets</c> replace the config file's template discovery
/// (verified: CLI template arguments win over <c>templates:</c>), and an
/// explicit <c>ConfigFile</c> should live outside the audited tree for a
/// rule set the repository cannot narrow.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is <c>cfn-lint --format
/// sarif --</c> from the work-tree root with no template arguments: the
/// tool falls back to the repository <c>.cfnlintrc</c> <c>templates:</c>
/// list, and with neither it reports <c>E1001</c> and fails the audit — a
/// tree outside the tool's scope is never a vacuous pass. The auditor does
/// no filename-glob discovery of its own: a non-CloudFormation YAML
/// document passed as a template reports <c>E1001</c>, so scanning every
/// <c>*.yaml</c> in the tree would manufacture findings against files the
/// tool was never meant to read. On top of the scan, findings under
/// vendored trees (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) are dropped by default — problems there describe
/// upstream code, not the change under audit. Operators re-include a path
/// by overriding <c>ExcludePaths</c> in scoped config.</para>
///
/// <para><b>Network.</b> The default scan uses only the resource-provider
/// schemas bundled inside the installed package and declares no network
/// capability. The auditor never passes <c>--update-specs</c> (which would
/// fetch unpinned schemas during the audit); refresh schemas at bake time
/// instead.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Cfn-Lint CloudFormation",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cfn-lint",
    InstallHint = "provision the pinned cfn-lint release with the sarif extra (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — pip install "
        + "\"cfn-lint[sarif]==" + DefaultExpectedVersion + "\" — via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class CfnlintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.cfnlint";

    /// <summary>
    /// cfn-lint release the invocation and its findings are verified against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.57.0";

    /// <summary>
    /// Scoped-config key for cfn-lint's <c>--config-file</c> path. Unset →
    /// cfn-lint's own discovery (<c>.cfnlintrc</c> in the audited repository
    /// or the home directory). Point it outside the audited tree for a rule
    /// set the repository cannot narrow.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for rules to include (comma-separated rule ids or
    /// id prefixes, e.g. <c>I</c> for all informationals, emitted as the
    /// values of a single <c>--include-checks</c> flag). Prefix matching is
    /// the tool's own semantics; the shared <c>IncludedRules</c> knob stays
    /// the exact-match finding-level filter.
    /// </summary>
    public const string IncludeChecksKey = "IncludeChecks";

    /// <summary>
    /// Scoped-config key for rules to ignore (comma-separated rule ids or id
    /// prefixes, e.g. <c>W</c> for all warnings, emitted as the values of a
    /// single <c>--ignore-checks</c> flag). Prefix matching is the tool's own
    /// semantics; the shared <c>ExcludedRules</c> knob stays the exact-match
    /// finding-level filter.
    /// </summary>
    public const string IgnoreChecksKey = "IgnoreChecks";

    /// <summary>
    /// Scoped-config key for regions to validate against (comma-separated,
    /// e.g. <c>us-east-1,eu-west-1</c>, emitted as the values of a single
    /// <c>--regions</c> flag). Unset → cfn-lint's own default region set.
    /// </summary>
    public const string RegionsKey = "Regions";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repo-relative template paths, passed as positional arguments after a
    /// <c>--</c> separator). Unset → cfn-lint's own discovery (the
    /// <c>templates:</c> list in the repository <c>.cfnlintrc</c>). Set →
    /// discovery is replaced: CLI template arguments win over the config
    /// file list.
    /// </summary>
    public const string TargetsKey = "Targets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Bitwise OR over severities found: 0 = clean; 2 = error; 4 =
        // warning; 8 = informational; 6/10/12/14 = combinations. Every one
        // writes the SARIF report to stdout and is findings-producing. Exit
        // 1 (usage errors) writes plain text and no report, so it fails
        // closed through the parser. Every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2, 4, 6, 8, 10, 12, 14 },
        // Findings in vendored trees describe upstream templates, not the
        // change under audit — noise that trains operators to ignore the
        // auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configFile = static () => null;
    private Func<IReadOnlyList<string>> _includeChecks = static () => [];
    private Func<IReadOnlyList<string>> _ignoreChecks = static () => [];
    private Func<IReadOnlyList<string>> _regions = static () => [];
    private Func<IReadOnlyList<string>> _targets = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:cfn-lint";

    /// <summary>
    /// The default scan uses only the resource-provider schemas bundled in
    /// the installed package, so the auditor needs no network egress and
    /// runs in the most restrictive sandbox. Schema refreshes
    /// (<c>--update-specs</c>) happen at bake time, never during the audit.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "cfn-lint";

    /// <summary>
    /// The shared SARIF parser: cfn-lint's <c>--format sarif</c> report
    /// carries the rule id, level, message, and file/line per result, so
    /// rule identifiers and locations are preserved wherever the tool
    /// supplies them. One quirk the parser already handles: warning results
    /// (e.g. <c>W2001</c>) carry no <c>level</c> at all — SARIF defaults an
    /// absent level to <c>warning</c>, which the declared map sends to
    /// advisory.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from cfn-lint's severity vocabulary
    /// (<c>error</c>/<c>warning</c>/<c>informational</c>, rendered in SARIF
    /// as <c>error</c>/<c>warning</c>/<c>note</c>) to
    /// <see cref="AuditSeverity"/>: <c>error</c> fails the audit,
    /// <c>warning</c> is advisory, <c>note</c>/<c>informational</c> is
    /// informational. Anything unrecognised maps to
    /// <see cref="AuditSeverity.Warning"/> — visible, never silently
    /// informational and never raw. Raw tool tokens never reach findings;
    /// the tool level is preserved in the finding description as proof the
    /// value flowed through the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
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
        // parser); --output-file would divert the report into a file and
        // leave stdout empty; --update-specs fetches unpinned schemas over
        // the network during the audit. Flag-looking ExtraArguments as a
        // whole are rejected below because they would land after the --
        // separator as bogus template paths.
        var flagLike = options.ExtraArguments
            .Where(static arg => arg.StartsWith("-", StringComparison.Ordinal))
            .ToList();
        if (flagLike.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with flag-like ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', flagLike))}') — cfn-lint's rule and region "
                + "selectors take a variable number of values and scan targets travel after a -- "
                + "separator, so flags there would be treated as template file names. Use the "
                + $"scoped keys (ConfigFile, IncludeChecks, IgnoreChecks, Regions, Targets) under "
                + $"CodeyBox:Plugins:{PluginId} and ExtraArguments for additional template paths only.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "--format", "sarif",
        };

        var configFile = ValidatedScopedValue(_configFile(), $"{PluginId}:{ConfigFileKey}");
        AddValueFlag(args, "--config-file", configFile);

        AddMultiValueFlag(args, "--include-checks", _includeChecks(), IncludeChecksKey);
        AddMultiValueFlag(args, "--ignore-checks", _ignoreChecks(), IgnoreChecksKey);
        AddMultiValueFlag(args, "--regions", _regions(), RegionsKey);

        return args;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // The -- separator is unconditional: the rule/region selectors above
        // take a variable number of values, so without it a template path
        // (from Targets or ExtraArguments) following one would be swallowed
        // into the flag's values and silently never linted. Everything after
        // -- is positional, so dash-leading template names stay paths.
        var args = new List<string> { "--" };
        foreach (var target in _targets())
        {
            if (string.IsNullOrWhiteSpace(target))
                continue;
            args.Add(ValidatedTarget(target));
        }

        return Task.FromResult<IReadOnlyList<string>>(args);
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
        _ignoreChecks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[IgnoreChecksKey]);
        _regions = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[RegionsKey]);
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        context.Logger.LogInformation(
            "CfnlintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static void AddMultiValueFlag(
        List<string> args, string flag, IReadOnlyList<string> values, string key)
    {
        var validated = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ValidatedArgumentValue(value, $"{PluginId}:{key}"))
            .ToList();
        if (validated.Count == 0)
            return;
        args.Add(flag);
        args.AddRange(validated);
    }

    private static string ValidatedTarget(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        const int maxChars = 1024;
        if (normalized.Length == 0
            || normalized.Length > maxChars
            || normalized.Any(char.IsControl)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{PluginId}:{TargetsKey}' entry ('{TruncateForMessage(value)}') "
                + "must be a repo-relative path inside the worktree.")
            { IsDeterministic = true };
        return normalized;
    }
}
