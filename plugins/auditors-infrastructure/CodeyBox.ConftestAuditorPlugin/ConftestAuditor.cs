using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ConftestAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>conftest</c> (policy-as-code over
/// config and IaC) on the shared <see cref="ExternalToolAuditorBase"/>: the
/// base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the conftest JSON report
/// parser (<see cref="ConftestJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// conftest-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — policy denials fail
/// the audit by default.</b> conftest has no severity levels, only result
/// categories, so the declared map assigns the meaning: <c>failure</c>
/// (<c>deny</c>/<c>violation</c> rules) and <c>exception</c> (a policy that
/// could not be evaluated at all) map to <see cref="AuditSeverity.Error"/>
/// and fail the audit; <c>warning</c> (<c>warn</c> rules) maps to
/// <see cref="AuditSeverity.Warning"/> (advisory), as does anything
/// unrecognised from a foreign build — visible, never silently
/// informational. Raw tool tokens never reach findings; the category is
/// preserved in the finding description as proof the value flowed through
/// the mapping. <c>MinimumSeverity</c> only drops findings, it never raises
/// them. A clean evaluation — or one with only warnings — passes.</para>
///
/// <para><b>Exit-code convention (verified against conftest 0.70.1 by running
/// the provisioned binary across clean, failure-bearing, warnings-only,
/// missing-policy, and bad-flag invocations; consistent with the
/// <c>ExitCode</c>/<c>ExitCodeFailOnWarn</c> functions in
/// <c>output/result.go</c> and the <c>test</c> command in
/// <c>internal/commands/test.go</c>).</b> The default <c>conftest test</c>
/// exits <c>0</c> when the evaluation completes with no failures and
/// <c>1</c> when policy failures were found — but <c>1</c> is ALSO the exit
/// for run failures (no policies found, bad flags, unreadable input, an
/// unusable policy path), which take the cobra error path and write plain
/// text instead of the JSON report. The discriminator is therefore the
/// report itself, not the exit code: exits <c>0</c> and <c>1</c> are
/// findings-producing, and an exit without a parseable JSON check-result
/// array on stdout fails closed as infrastructure through the parser.
/// <c>--fail-on-warn</c> (which would introduce exit <c>2</c>) is rejected
/// below, so any other exit — including <c>126</c>/<c>127</c> (cannot
/// execute / not found) — is infrastructure.</para>
///
/// <para><b>Version pin.</b> The policy surface (supported config formats,
/// Rego version, report shape) changes between releases, so findings are
/// only meaningful from the build the auditor was verified against. The
/// auditor probes <c>conftest --version</c> before the scan (which prints
/// <c>Conftest: x.y.z</c> followed by the OPA version; the shared
/// first-token extraction pins the Conftest version); a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled configuration.</b> conftest reads Rego
/// policies from the audited repository (default <c>policy/</c>) and honors
/// <c>--namespace</c> selection, so the audit subject supplies what the
/// tool enforces. That directory is the tool's normal configuration
/// surface, not an auditor bypass: findings it suppresses are suppressed by
/// the scanner itself, and a repository with no policies (or no supported
/// config files) fails closed as infrastructure rather than passing
/// vacuously — the tool exits non-zero without a report. Operators who want
/// a fixed rule set regardless of repository content pin it with
/// <c>PolicyPath</c> (pointing outside the audited tree) and
/// <c>Namespace</c>/<c>AllNamespaces</c>.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is
/// <c>conftest test --output json .</c> from the work-tree root: conftest
/// walks the tree itself and evaluates only files with a supported config
/// format (YAML, JSON, TOML, HCL/Terraform, Dockerfiles, and the rest of its
/// parser list) — application source and the <c>policy/</c> directory
/// itself are out of scope by construction. On top of that, findings under
/// generated and vendored trees (<c>.terraform/</c>, <c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) are dropped by default —
/// problems there describe upstream code, not the change under audit.
/// Operators re-include a path by overriding <c>ExcludePaths</c> in scoped
/// config, and narrow the scan itself with <c>Targets</c> (or conftest's
/// own <c>--ignore</c>/<c>--parser</c>/<c>--data</c> via
/// <c>ExtraArguments</c>).</para>
///
/// <para><b>Network.</b> The default scan declares no network capability:
/// repo-local Rego evaluates hermetically. The auditor never fetches
/// anything during the audit — <c>--update</c> (which downloads unpinned
/// remote policies) is rejected — so policies using <c>http.send</c> fail
/// closed as exceptions under the default sandbox rather than silently
/// passing.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Conftest Policy-as-Code",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "conftest",
    InstallHint = "provision the pinned conftest release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "conftest — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ConftestAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.conftest";

    /// <summary>
    /// conftest release the invocation and its findings are verified against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.70.1";

    /// <summary>
    /// Scoped-config key for conftest's <c>--policy</c> path (repeatable on
    /// the tool; comma-separated here). Unset → conftest's own default
    /// (<c>policy/</c> in the audited repository). Point it outside the
    /// audited tree for a rule set the repository cannot narrow.
    /// </summary>
    public const string PolicyPathKey = "PolicyPath";

    /// <summary>
    /// Scoped-config key for conftest's <c>--namespace</c> selection
    /// (comma-separated; conftest also accepts glob wildcards such as
    /// <c>k8s.*</c>). Unset → conftest's own default (<c>main</c>). Ignored
    /// when <c>AllNamespaces</c> is true.
    /// </summary>
    public const string NamespaceKey = "Namespace";

    /// <summary>
    /// Scoped-config boolean for conftest's <c>--all-namespaces</c>: test
    /// policies in every namespace the policy bundle defines instead of the
    /// <c>Namespace</c> selection.
    /// </summary>
    public const string AllNamespacesKey = "AllNamespaces";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repo-relative files or directories, passed as positional arguments).
    /// Unset → <c>.</c> (the whole work tree; conftest evaluates only files
    /// with a supported config format).
    /// </summary>
    public const string TargetsKey = "Targets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed evaluation, no failures (warnings and exceptions may
        // still be present in the report — warnings never affect the exit
        // code); 1 = completed evaluation with failures OR a run failure
        // (no policies found, bad flags, unreadable input), which writes
        // plain text instead of the JSON report. The check-result array on
        // stdout is the discriminator: run failures emit no report, so exit
        // 1 without a report fails closed through the parser. Every other
        // exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in generated-module and vendored trees describe upstream
        // code, not the change under audit — noise that trains operators to
        // ignore the auditor. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = [".terraform/", "vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _policyPaths = static () => [];
    private Func<IReadOnlyList<string>> _namespaces = static () => [];
    private Func<bool> _allNamespaces = static () => false;
    private Func<IReadOnlyList<string>> _targets = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:conftest";

    /// <summary>
    /// The default scan evaluates repo-local Rego against repo-local config
    /// with no fetches, so the auditor needs no network egress and runs in
    /// the most restrictive sandbox.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "conftest";

    /// <summary>
    /// The plugin-local JSON parser: conftest's <c>--output json</c> report
    /// carries the result category, message, <c>metadata.query</c> rule id,
    /// and <c>loc</c> file/line per result, so rule id and location are
    /// preserved with no plugin-local severity smuggling — the category
    /// flows through the declared severity mapping below.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new ConftestJsonOutputParser();

    /// <summary>
    /// Declared mapping from conftest's result-category vocabulary to
    /// <see cref="AuditSeverity"/>: <c>failure</c> (a <c>deny</c> or
    /// <c>violation</c> rule fired) and <c>exception</c> (a policy that
    /// could not be evaluated at all) fail the audit; <c>warning</c> (a
    /// <c>warn</c> rule fired) is advisory. Anything unrecognised maps to
    /// <see cref="AuditSeverity.Warning"/> — visible, never silently
    /// informational and never raw. Raw tool tokens never reach findings;
    /// the category is preserved in the finding description as proof the
    /// value flowed through the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["failure"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["deny"] = AuditSeverity.Error,
            ["violation"] = AuditSeverity.Error,
            ["exception"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // --output is the auditor's parsing contract (the JSON report the
        // plugin-local parser reads). --fail-on-warn and --no-fail rewrite
        // the exit-code convention the base classifies on. --quiet
        // suppresses the clean-run report the parser expects. All are
        // rejected deterministically rather than left to misfire (a missing
        // report would fail closed through the parser, but the message would
        // blame the tool instead of the configuration that caused it).
        // conftest accepts single- and double-dash spellings, so both are
        // guarded where the tool defines them.
        if (ExtraArgumentsSupplyFlag(options, "--output", "-o"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --output/-o ExtraArguments "
                + "entry — the JSON report shape is the auditor's parsing contract. Remove it; use "
                + $"CodeyBox:Plugins:{PluginId} rule and severity knobs to shape the verdict instead.")
            { IsDeterministic = true };
        if (ExtraArgumentsSupplyFlag(options, "--fail-on-warn", "--no-fail", "--quiet", "--suppress-exceptions"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --fail-on-warn, --no-fail, --quiet, "
                + "or --suppress-exceptions ExtraArguments entry — those flags rewrite the exit-code and "
                + "report contract this auditor classifies on (failures must exit 1 with a full report, "
                + "including exceptions). Remove it.")
            { IsDeterministic = true };
        // --update downloads unpinned remote policies into the audited tree
        // during the audit — an auditor must never fetch code while
        // auditing. Provision policies at bake time instead.
        if (ExtraArgumentsSupplyFlag(options, "--update", "-u"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a --update/-u ExtraArguments "
                + "entry — the auditor never downloads policies during the audit because that would fetch "
                + "unpinned code. Provision policies at bake time or point "
                + $"CodeyBox:Plugins:{PluginId}:{PolicyPathKey} at them.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "test",
            "--output", "json",
        };

        foreach (var policyPath in _policyPaths())
        {
            if (string.IsNullOrWhiteSpace(policyPath))
                continue;
            args.Add("--policy");
            args.Add(ValidatedScopedValue(policyPath, $"{PluginId}:{PolicyPathKey}")!);
        }

        if (_allNamespaces())
        {
            args.Add("--all-namespaces");
        }
        else
        {
            foreach (var ns in _namespaces())
            {
                if (string.IsNullOrWhiteSpace(ns))
                    continue;
                args.Add("--namespace");
                args.Add(ValidatedScopedValue(ns, $"{PluginId}:{NamespaceKey}")!);
            }
        }

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(ValidatedTarget)
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
        _policyPaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[PolicyPathKey]);
        _namespaces = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[NamespaceKey]);
        _allNamespaces = () =>
            bool.TryParse(scoped[AllNamespacesKey], out var all) && all;
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        context.Logger.LogInformation(
            "ConftestAuditor initialized: pluginId={PluginId}", context.PluginId);
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
