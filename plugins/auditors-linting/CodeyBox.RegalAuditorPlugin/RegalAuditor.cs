using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.RegalAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>regal</c> (Rego policy-source linter) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed invocation
/// with a bounded timeout, per-stream output caps, SARIF parsing, severity mapping,
/// exit-code classification, and per-auditor configuration. This class adds Regal-specific
/// argument assembly and declares the pinned tool version through
/// <see cref="ExternalToolAuditorBase.VersionPin"/>.
///
/// <para><b>Gate behaviour: hybrid / severity-driven.</b> Regal classifies each rule
/// violation as <c>error</c> or <c>warning</c> (a rule set to <c>ignore</c> in Regal
/// configuration is not reported at all). Violations at <c>error</c> map to
/// <see cref="AuditSeverity.Error"/> and fail the audit, whereas <c>warning</c>
/// violations map to <see cref="AuditSeverity.Warning"/> and are advisory, and
/// <c>note</c>-level results map to <see cref="AuditSeverity.Info"/>. Operators can
/// change rule severities in Regal configuration or adjust <c>MinimumSeverity</c> in
/// scoped configuration.</para>
///
/// <para><b>Exit-code convention (verified against the official Regal CLI documentation;
/// never a generic 0/1 assumption).</b> With the default <c>--fail-level error</c>,
/// <c>0</c> means no errors were found (warnings may still be present) and <c>3</c>
/// means one or more errors were found. With <c>--fail-level warning</c>, <c>0</c> means
/// no errors or warnings, <c>2</c> means one or more warnings, and <c>3</c> means one or
/// more errors. Exits <c>0</c>, <c>2</c>, and <c>3</c> all carry the SARIF report on
/// stdout and are findings-producing verdicts. Any other exit — usage errors, missing
/// inputs, or an exit with no SARIF on stdout — is an infrastructure failure. Execution
/// failures like exit <c>126</c>/<c>127</c> (cannot execute / not found) are likewise
/// infrastructure.</para>
///
/// <para><b>Version pin.</b> A linter's rule set changes between releases, so findings are
/// only consistent from the build the auditor was verified against. Regal is probed with
/// <c>regal version</c> before the scan; a missing binary, an unrecognised version string,
/// or a version other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass and never a finding.</para>
///
/// <para><b>Repository-controlled configuration.</b> Regal automatically discovers
/// <c>.regal/config.yaml</c> (or <c>.regal.yaml</c>) in the audited repository and honors
/// its rule severities and rule options — and the audit subject writes that repository.
/// That file is the scanner's own configuration surface (suppressing a rule there
/// suppresses it in the scan itself), documented as a limitation in the plugin README.
/// Operators who want a fixed rule set regardless of repository content point
/// <c>ConfigPath</c> at an operator-owned file (passed as <c>--config-file</c>).</para>
///
/// <para><b>Scope and defaults.</b> The default scan is <c>regal lint --format sarif .</c>
/// from the work-tree root: Regal walks the tree itself and lints only Rego policy
/// source (<c>*.rego</c>) — every other file kind is out of scope by construction.
/// Auditing the Rego policy source is distinct from evaluating those policies against
/// other configuration (the Conftest auditor's job). On top of that, the finding-level
/// <c>ExcludePaths</c> backstop drops findings under vendored prefixes by default.
/// This auditor never lints syntax through <c>opa check --strict</c>: strict-mode coverage
/// is a separate concern, not claimed here.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Regal Rego Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "regal",
    InstallHint = "provision the pinned regal release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline by fetching the versioned upstream GitHub release binary "
        + "(https://github.com/open-policy-agent/regal/releases) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class RegalAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.regal";

    /// <summary>
    /// Regal release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.40.0";

    /// <summary>Scoped-config key for an explicit Regal configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated repo-relative files or
    /// directories, passed as positional arguments). Unset → <c>.</c> (the whole work tree;
    /// Regal lints only <c>*.rego</c> files).
    /// </summary>
    public const string TargetsKey = "Targets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = no errors (warnings may be present); 2 = warnings found under
        // --fail-level warning; 3 = errors found. All three emit the SARIF
        // report — all are verdicts. Any other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2, 3 },
        // Findings in vendored/dependency trees describe upstream policy,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<IReadOnlyList<string>> _targets = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:regal";

    /// <inheritdoc />
    protected override string ToolName => "regal";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from Regal's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Regal reports rule violations at
    /// <c>error</c> or <c>warning</c> (SARIF levels <c>error</c>,
    /// <c>warning</c>, <c>note</c>); the wider map keeps related tokens from
    /// becoming an unmapped dialect. Raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // --format is the auditor's parsing contract (the SARIF report the
        // shared parser reads). --fix would mutate the audited tree during
        // the audit — an auditor is read-only. Both are rejected
        // deterministically rather than left to misfire.
        if (ExtraArgumentsSupplyFlag(options, "--format", "-f"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --format/-f ExtraArguments "
                + "entry — the SARIF report shape is the auditor's parsing contract. Remove it; use "
                + $"CodeyBox:Plugins:{PluginId} rule and severity knobs to shape the verdict instead.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--fix"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --fix ExtraArguments "
                + "entry — the auditor runs read-only and never rewrites policy source. Remove it.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "lint",
            "--format", "sarif",
        };

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config-file", "-c"))
        {
            args.Add("--config-file");
            args.Add(ValidatedScopedValue(configPath, $"{PluginId}:{ConfigPathKey}")!);
        }

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ValidatedRepoRelativeTarget(t, $"{PluginId}:{TargetsKey}"))
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
        _configPath = () => scoped[ConfigPathKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        context.Logger.LogInformation(
            "RegalAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
