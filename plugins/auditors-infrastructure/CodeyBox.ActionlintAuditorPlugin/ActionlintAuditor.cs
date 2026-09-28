using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ActionlintAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>actionlint</c> (GitHub Actions
/// workflow correctness) on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the actionlint JSON report
/// parser (<see cref="ActionlintJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// actionlint-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — advisory by default,
/// not blocking.</b> actionlint has no severity vocabulary — every error
/// carries only the rule name (<c>kind</c>) that reported it — so the
/// declared map assigns the meaning: <c>credentials</c> (a hardcoded
/// password in a container/services section — secret material in the tree)
/// and <c>syntax-check</c> (a workflow file the tool could not parse at all)
/// map to <see cref="AuditSeverity.Error"/> and fail the audit; every other
/// rule maps to <see cref="AuditSeverity.Warning"/> (advisory), as does any
/// unrecognised kind from a foreign build — visible, never silently
/// informational. Raw tool tokens never reach findings; the kind is preserved
/// in the finding description as proof the value flowed through the mapping.
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against actionlint 1.7.12 by
/// running the provisioned binary).</b> actionlint exits <c>0</c> when the
/// scan completes with no errors (printing <c>[]</c>), <c>1</c> when errors
/// were found (printing the JSON array), <c>2</c> for flag/usage errors, and
/// <c>3</c> when the scan could not run (an unreadable file argument, or no
/// <c>.github/workflows</c> directory found — "no project was found").
/// Exits <c>2</c> and <c>3</c> write plain text, not the JSON report. The
/// discriminator is therefore the report itself, not the exit code: exits
/// <c>0</c> and <c>1</c> are findings-producing, and an exit without a
/// parseable JSON error array on stdout fails closed as infrastructure
/// through the parser. <c>126</c>/<c>127</c> (cannot execute / not found)
/// and any other exit are infrastructure.</para>
///
/// <para><b>Repositories without workflows fail closed.</b> actionlint's
/// no-argument discovery exits <c>3</c> ("no project was found") when the
/// tree has no <c>.github/workflows</c> directory — that is infrastructure,
/// not a pass. Enable this auditor only on projects that use GitHub Actions;
/// a vacuous pass on a tree outside the tool's scope would train operators
/// to ignore it.</para>
///
/// <para><b>Version pin.</b> The check surface (rule names, popular-action
/// database, report shape) changes between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>actionlint -version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled configuration.</b> actionlint reads
/// <c>.github/actionlint.yaml</c> from the audited repository (self-hosted
/// runner labels, ignore patterns) and honors <c>actionlint-disable</c>
/// comments inside workflow files, so the audit subject can narrow what the
/// tool reports. That file is the tool's normal configuration surface, not
/// an auditor bypass: findings it suppresses are suppressed by the scanner
/// itself. Operators who want a fixed configuration regardless of
/// repository content pin it with <c>ConfigFile</c> (pointing outside the
/// audited tree).</para>
///
/// <para><b>Scope and defaults.</b> The default scan is <c>actionlint</c>
/// with no positional arguments from the work-tree root: the tool discovers
/// the nearest <c>.github/workflows</c> directory itself, so only workflow
/// files are ever inspected — application and vendored code is out of scope
/// by construction. On top of that, findings under vendored trees
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) are dropped
/// by default — a vendored snapshot carrying its own <c>.github</c>
/// directory describes upstream code, not the change under audit. Operators
/// re-include a path by overriding <c>ExcludePaths</c> in scoped config,
/// and narrow the scan itself to explicit workflow files with
/// <c>Targets</c>.</para>
///
/// <para><b>Network.</b> The default scan declares no network capability.
/// The shellcheck/pyflakes integrations (which shell out to unpinned
/// external binaries) are disabled with <c>-shellcheck=</c> and
/// <c>-pyflakes=</c>; action metadata for popular actions comes from the
/// database embedded in the binary. The auditor never fetches anything
/// during the audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Actionlint GitHub Actions",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "actionlint",
    InstallHint = "provision the pinned actionlint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "actionlint — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ActionlintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.actionlint";

    /// <summary>
    /// actionlint release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.7.12";

    /// <summary>
    /// Scoped-config key for actionlint's <c>-config-file</c> path. Unset →
    /// actionlint's own default (<c>.github/actionlint.yaml</c> in the
    /// audited repository). Point it outside the audited tree for a
    /// configuration the repository cannot narrow.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repo-relative workflow file paths, passed as positional arguments).
    /// Unset → actionlint's own discovery (the nearest
    /// <c>.github/workflows</c> directory from the work-tree root).
    /// </summary>
    public const string TargetsKey = "Targets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed scan, no errors; 1 = completed scan with errors.
        // 2 = flag/usage error and 3 = could not run (unreadable file, no
        // project found) both write plain text, not the JSON report — the
        // report on stdout is the discriminator, so an exit without a
        // parseable array fails closed through the parser. Every other exit
        // is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
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

    /// <inheritdoc />
    public override string Name => "codeybox:actionlint";

    /// <summary>
    /// The default scan uses only the checks embedded in the binary (the
    /// shellcheck/pyflakes integrations are disabled), so the auditor needs
    /// no network egress and runs in the most restrictive sandbox.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "actionlint";

    /// <summary>
    /// The plugin-local JSON parser: actionlint's
    /// <c>-format '{{json .}}'</c> report carries the rule name
    /// (<c>kind</c>), message, and file/line per error, so rule id and
    /// location are preserved with no plugin-local severity smuggling — the
    /// kind flows through the declared severity mapping below.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new ActionlintJsonOutputParser();

    /// <summary>
    /// Declared mapping from actionlint's rule-name vocabulary (the
    /// <c>kind</c> field — actionlint has no severity levels) to
    /// <see cref="AuditSeverity"/>: <c>credentials</c> (hardcoded password
    /// in a container/services section) and <c>syntax-check</c> (a workflow
    /// file that could not be parsed at all) fail the audit; every other
    /// rule is advisory. Anything unrecognised maps to
    /// <see cref="AuditSeverity.Warning"/> — visible, never silently
    /// informational and never raw. Raw tool tokens never reach findings;
    /// the kind is preserved in the finding description as proof the value
    /// flowed through the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["credentials"] = AuditSeverity.Error,
            ["syntax-check"] = AuditSeverity.Error,
            ["action"] = AuditSeverity.Warning,
            ["deprecated-commands"] = AuditSeverity.Warning,
            ["env-var"] = AuditSeverity.Warning,
            ["events"] = AuditSeverity.Warning,
            ["expression"] = AuditSeverity.Warning,
            ["glob"] = AuditSeverity.Warning,
            ["id"] = AuditSeverity.Warning,
            ["if-cond"] = AuditSeverity.Warning,
            ["job-needs"] = AuditSeverity.Warning,
            ["matrix"] = AuditSeverity.Warning,
            ["permissions"] = AuditSeverity.Warning,
            ["pyflakes"] = AuditSeverity.Warning,
            ["runner-label"] = AuditSeverity.Warning,
            ["shell-name"] = AuditSeverity.Warning,
            ["shellcheck"] = AuditSeverity.Warning,
            ["workflow-call"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // -format is the auditor's parsing contract (the JSON report the
        // plugin-local parser reads). -shellcheck/-pyflakes force the
        // hermetic default (unpinned external binaries must not run inside
        // the audit). -init-config writes .github/actionlint.yaml into the
        // audited tree — an auditor must never mutate what it audits. All
        // are rejected deterministically rather than left to misfire (a
        // non-JSON report would fail closed through the parser, but the
        // message would blame the tool instead of the configuration that
        // caused it). Go's flag package accepts single- and double-dash
        // spellings interchangeably, so both are guarded.
        if (ExtraArgumentsSupplyFlag(options, "-format", "--format"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a -format/--format ExtraArguments "
                + "entry — the JSON report shape is the auditor's parsing contract. Remove it; use "
                + $"CodeyBox:Plugins:{PluginId} rule and severity knobs to shape the verdict instead.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "-init-config", "--init-config"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a -init-config/--init-config "
                + "ExtraArguments entry — auditors report on the tree, they never rewrite it. Remove it.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "-shellcheck", "--shellcheck")
            || ExtraArgumentsSupplyFlag(options, "-pyflakes", "--pyflakes"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a -shellcheck/--shellcheck or "
                + "-pyflakes/--pyflakes ExtraArguments entry — the auditor disables those integrations "
                + "because they shell out to unpinned external binaries. Remove it.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "-format", "{{json .}}",
            "-shellcheck=",
            "-pyflakes=",
        };

        var configFile = ValidatedScopedValue(_configFile(), ConfigFileKey);
        AddValueFlag(args, "-config-file", configFile);

        foreach (var target in _targets())
        {
            if (string.IsNullOrWhiteSpace(target))
                continue;
            args.Add(ValidatedTarget(target));
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
        context.Logger.LogInformation(
            "ActionlintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string ValidatedTarget(string value)
    {
        var validated = ValidatedArgumentValue(value, $"{PluginId}:{TargetsKey}");
        var normalized = validated.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{PluginId}:{TargetsKey}' entry ('{TruncateForMessage(validated)}') "
                + "must be a repo-relative path inside the worktree.")
            { IsDeterministic = true };
        return validated;
    }
}
