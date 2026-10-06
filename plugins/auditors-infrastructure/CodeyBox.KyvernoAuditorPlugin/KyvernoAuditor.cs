using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.KyvernoAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping the <c>kyverno</c> CLI (Kubernetes policy
/// evaluation) on the shared <see cref="ExternalToolAuditorBase"/>: the base
/// supplies sandboxed invocation with a bounded timeout, per-stream output
/// caps, exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the JSON policy-report parser
/// (<see cref="KyvernoPolicyReportParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// kyverno-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — policy failures fail
/// the audit, warnings are advisory.</b> kyverno reports one outcome per
/// policy rule per resource: <c>fail</c> (a validate rule fired) and
/// <c>error</c> (the rule could not be evaluated at all — unsupported
/// policy, missing context, engine failure) map to
/// <see cref="AuditSeverity.Error"/> and fail the audit; <c>warn</c> maps to
/// <see cref="AuditSeverity.Warning"/> (advisory). <c>skip</c> (the rule
/// chose not to evaluate) also maps to <see cref="AuditSeverity.Error"/>: a
/// skipped rule is coverage the audit did not get, and uncovered resources
/// must never read as clean. Anything unrecognised from a foreign build is
/// <see cref="AuditSeverity.Error"/> — fail closed. Raw tool tokens never
/// reach findings; the outcome is preserved in the finding description as
/// proof the value flowed through the mapping. <c>MinimumSeverity</c> only
/// drops findings, it never raises them. A clean evaluation — every result
/// <c>pass</c> — passes.</para>
///
/// <para><b>Exit-code convention.</b> <c>kyverno apply</c> exits <c>0</c> when
/// every evaluated rule passed and non-zero when policy failures were found;
/// the same non-zero exit covers run failures (bad flags, unreadable policy
/// or resource paths), which write plain text instead of the JSON policy
/// report. The discriminator is therefore the report itself, not the exit
/// code: exits <c>0</c> and <c>1</c> are findings-producing, and an exit
/// without a parseable policy report on stdout fails closed as
/// infrastructure through the parser. Warning-only exits are pinned to the
/// default convention by rejecting <c>--warn-exit-code</c>,
/// <c>--warn-no-pass</c>, and <c>--audit-warn</c> below, so any other exit —
/// including <c>126</c>/<c>127</c> (cannot execute / not found) — is
/// infrastructure. Verified against the <c>kyverno apply</c> reference
/// (kyverno.io/docs/kyverno-cli/reference/kyverno_apply): <c>--policy-report</c>
/// generates the report, <c>--output-format</c> selects its <c>json</c>
/// shape, and <c>--warn-exit-code</c> exists precisely because warnings
/// otherwise leave the exit code alone.</para>
///
/// <para><b>Version pin.</b> The policy surface (supported rule types,
/// JMESPath version, report shape) changes between releases, so findings are
/// only meaningful from the build the auditor was verified against. The
/// auditor probes <c>kyverno version</c> before the scan (which prints
/// <c>Version: vX.Y.Z</c> plus platform lines; the shared first-token
/// extraction pins the CLI version); a missing binary, an unrecognised
/// version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a
/// finding.</para>
///
/// <para><b>Operator-owned policy.</b> kyverno enforces whatever policies it
/// is pointed at, so the policy set is the gate itself: policies are
/// operator configuration that must resolve outside the audited worktree.
/// <c>PolicyPaths</c> is required — an unconfigured auditor fails closed as
/// infrastructure rather than passing vacuously — and every entry is
/// canonicalized inside the sandbox before the scan: a path resolving
/// inside the worktree fails closed, because a candidate that can edit the
/// policies it is audited against can silence its own violations. The
/// candidate tree supplies only the manifests under audit, never policy,
/// variables, exceptions, or API context.</para>
///
/// <para><b>Offline, validate-only scope.</b> The scan is <c>kyverno apply
/// --policy … --resource … --policy-report --output-format json
/// --continue-on-fail</c>: local manifests evaluated against local policies,
/// reporting every violation instead of stopping at the first. The auditor
/// declares no network capability and never emits a flag that reaches past
/// the sandbox: no <c>--cluster</c> (live-cluster admission), no
/// <c>--kubeconfig</c>/<c>--context</c> (API identity), no
/// <c>--registry</c> (image-registry fetch), no <c>--output</c>/<c>-o</c>
/// (writing mutated resources back into the tree), no <c>--stdin</c>
/// (piping mutations to kubectl), no <c>--target-resource(s)</c>
/// (mutate-existing targets), no <c>--generate-exceptions</c> (writing
/// exception files), no <c>--exception(s)</c> or
/// <c>--exceptions-with-*</c> (candidate-supplied suppressions), and no
/// <c>--values-file</c>/<c>--set</c>/<c>--context-file</c>/<c>--userinfo</c>/
/// <c>--http-payload</c>/<c>--envoy-payload</c>/<c>--json</c>/
/// <c>--parameter-resource</c> (candidate-supplied variables and API
/// context). kyverno parses flags after positionals too, so — like the
/// kubeconform auditor — this auditor rejects every flag-looking
/// <c>ExtraArguments</c> entry outright (deterministic infrastructure
/// failure naming the scoped keys) rather than letting one re-enable a
/// rejected path. The only operator surface is the scoped keys below, so a
/// future caller cannot smuggle a live-cluster flag through a distant
/// boundary: the sink carries its own guard.</para>
///
/// <para><b>Scope and defaults.</b> <c>Targets</c> selects the candidate
/// manifests (repo-relative files or directories, each a repeatable
/// <c>--resource</c>); unset scans <c>.</c>. PolicyReport results identify
/// resources by coordinates (<c>kind</c>/<c>name</c>/<c>namespace</c>), not
/// by file — kyverno does not record which file a resource came from — so
/// finding messages carry the coordinates and locations carry no file path;
/// narrow the scan itself with <c>Targets</c>. <c>--audit-warn</c> is never
/// passed: audit-type policies must fail, not warn.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Kyverno Policy Audit",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "kyverno",
    InstallHint = "provision the pinned kyverno release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "kyverno — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class KyvernoAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.kyverno";

    /// <summary>
    /// kyverno release the invocation and its findings are verified against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.19.1";

    /// <summary>
    /// Scoped-config key for the operator-owned policy inputs
    /// (comma-separated paths to policy files or directories, each emitted
    /// as a repeatable <c>--policy</c> argument). Required: an unconfigured
    /// auditor fails closed rather than passing vacuously. Every entry must
    /// resolve outside the audited worktree — an in-tree policy is
    /// repository-controlled by another name and fails closed, because the
    /// candidate must not be able to weaken the gate it is audited against.
    /// Point these at validate-only policies: the auditor never enables
    /// mutate/generate/image-fetch behaviour, but a policy bundle
    /// containing such rules is outside the validate-only contract this
    /// auditor reports on.
    /// </summary>
    public const string PolicyPathsKey = "PolicyPaths";

    /// <summary>
    /// Scoped-config key for the candidate manifests under audit
    /// (comma-separated repo-relative files or directories, each emitted as
    /// a repeatable <c>--resource</c> argument). Unset → <c>.</c> (the whole
    /// work tree). Entries must be repo-relative paths inside the worktree —
    /// absolute paths and <c>..</c> segments are rejected deterministically.
    /// </summary>
    public const string TargetsKey = "Targets";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed evaluation with no failures; 1 = completed
        // evaluation with failures OR a run failure (bad flags, unreadable
        // policy/resource path), which writes plain text instead of the
        // JSON policy report. The policy report on stdout is the
        // discriminator: run failures emit no report, so exit 1 without a
        // report fails closed through the parser. Every other exit is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Post-scan filter for file-located findings. Kyverno PolicyReport
        // results carry resource coordinates rather than file paths, so this
        // only bites if a future report shape adds locations; scope the scan
        // itself with Targets instead.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _policyPaths = static () => [];
    private Func<IReadOnlyList<string>> _targets = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:kyverno";

    /// <summary>
    /// The default scan evaluates local policies against local manifests
    /// with no fetches, so the auditor needs no network egress and runs in
    /// the most restrictive sandbox.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "kyverno";

    /// <summary>
    /// The plugin-local policy-report parser: kyverno's
    /// <c>--policy-report --output-format json</c> report carries the
    /// per-rule outcome, message, <c>policy</c>/<c>rule</c> pair, and
    /// resource coordinates per result, so rule identity survives with no
    /// plugin-local severity smuggling — the outcome flows through the
    /// declared severity mapping below.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new KyvernoPolicyReportParser();

    /// <summary>
    /// Declared mapping from kyverno's result vocabulary to
    /// <see cref="AuditSeverity"/>: <c>fail</c> (a validate rule fired) and
    /// <c>error</c> (a rule that could not be evaluated — unsupported
    /// policy or missing context) fail the audit, as does <c>skip</c> (a
    /// rule that chose not to evaluate — uncovered is not clean) and
    /// anything unrecognised from a foreign build. <c>warn</c> is advisory.
    /// Raw tool tokens never reach findings; the outcome is preserved in
    /// the finding description as proof the value flowed through the
    /// mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["skip"] = AuditSeverity.Error,
            ["skipped"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // kyverno parses flags after positionals, so a flag-looking
        // ExtraArguments entry would reach its parser — including the
        // cluster, mutation, exception, and API-context flags this auditor
        // exists to keep off. Reject every flag-looking entry
        // deterministically rather than letting one re-enable a rejected
        // path; the scoped keys below are the whole operator surface.
        var flagLike = options.ExtraArguments
            .Where(static arg => arg.StartsWith("-", StringComparison.Ordinal))
            .ToList();
        if (flagLike.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with flag-like ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', flagLike))}') — kyverno would parse these as "
                + "live flags (cluster access, mutation outputs, exceptions, API context), which this "
                + "offline validate-only auditor never enables. Use the scoped keys "
                + $"(PolicyPaths, Targets) under CodeyBox:Plugins:{PluginId} and ExtraArguments for "
                + "additional resource paths only.")
            { IsDeterministic = true };

        var policies = _policyPaths()
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => ValidatedArgumentValue(p, $"{PluginId}:{PolicyPathsKey}"))
            .ToList();
        if (policies.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has no PolicyPaths configured — without an "
                + $"operator-owned policy set there is nothing to evaluate, and an unevaluated tree "
                + $"must never read as clean. Set CodeyBox:Plugins:{PluginId}:{PolicyPathsKey} to "
                + "validate-only policy file(s) or directories outside the audited worktree.")
            { IsDeterministic = true };

        var args = new List<string> { "apply" };

        foreach (var policy in policies)
        {
            args.Add("--policy");
            args.Add(policy);
        }

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ValidatedRepoRelativeTarget(t, $"{PluginId}:{TargetsKey}"))
            .ToList();
        if (targets.Count == 0)
            targets.Add(".");
        foreach (var target in targets)
        {
            args.Add("--resource");
            args.Add(target);
        }

        // The parsing contract: the JSON policy report on stdout is what
        // the plugin-local parser reads. --continue-on-fail keeps kyverno
        // reporting every violation instead of stopping at the first
        // failing resource — an audit must see all of them.
        args.Add("--policy-report");
        args.Add("--output-format");
        args.Add("json");
        args.Add("--continue-on-fail");

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _policyPaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[PolicyPathsKey]);
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        context.Logger.LogInformation(
            "KyvernoAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Operator-owned policy precondition on the live path: every
    /// <see cref="PolicyPathsKey"/> entry is canonicalized inside the
    /// sandbox and must resolve outside the audited worktree. A policy the
    /// candidate tree can edit is a gate the candidate can weaken — an
    /// in-tree policy fails closed as infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        foreach (var policy in _policyPaths().Where(static p => !string.IsNullOrWhiteSpace(p)))
        {
            await CanonicalizeOutsideWorktreeAsync(
                sandbox,
                workingDirectory,
                policy.Trim(),
                $"{PluginId}:{PolicyPathsKey}",
                options,
                ct).ConfigureAwait(false);
        }
    }
}
