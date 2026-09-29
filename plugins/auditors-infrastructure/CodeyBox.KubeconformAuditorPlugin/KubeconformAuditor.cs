using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.KubeconformAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>kubeconform</c> (Kubernetes manifest
/// schema validation) on the shared <see cref="ExternalToolAuditorBase"/>: the
/// base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the kubeconform JSON report
/// parser (<see cref="KubeconformJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// kubeconform-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: blocking.</b> kubeconform has no severity
/// vocabulary — a resource is valid, skipped, or a problem. Every reported
/// resource (<c>statusInvalid</c> schema violations and <c>statusError</c>
/// unvalidatable resources alike) maps to <see cref="AuditSeverity.Error"/>
/// and fails the audit. There is no advisory mode; narrow scope with
/// <c>Targets</c>, <c>ExcludedRules</c>, <c>ExcludePaths</c>, or kubeconform's
/// own <c>-skip</c> knobs instead.</para>
///
/// <para><b>Exit-code convention (verified against kubeconform v0.8.x
/// source).</b> kubeconform does NOT follow the common "1 = findings,
/// 2 = could not run" convention: every failure mode — flag-parse errors,
/// an unknown <c>-output</c> format, a validator init failure — exits
/// <c>1</c> on stderr <em>without writing a report</em>. A completed scan
/// also exits <c>1</c> when any resource is invalid or in error, but it
/// always writes the JSON report to stdout. The discriminator is therefore
/// the report itself, not the exit code: exits <c>0</c> and <c>1</c> are
/// findings-producing, and an exit without a parseable
/// <c>{"resources":[…]}</c> document on stdout fails closed as
/// infrastructure through the parser. <c>126</c>/<c>127</c> (cannot execute /
/// not found) and any other exit are infrastructure.</para>
///
/// <para><b>Schema sources.</b> kubeconform validates each manifest's
/// <c>kind</c>/<c>apiVersion</c> against a JSON schema resolved from
/// <c>-schema-location</c>. With no location configured it fetches schemas
/// from the upstream <c>kubernetes-json-schema</c> repository
/// (<c>raw.githubusercontent.com</c>) — which is why the auditor declares
/// <see cref="AuditCapabilities.Network"/>: the schema registry host must be
/// in the deployment's audit-tool egress allowlist, or the operator must
/// provision schemas into the baseline and point <c>SchemaLocations</c> at
/// the local directory. Without either, every resource reports
/// <c>statusError</c> and fails the audit — loud, never a silent pass.
/// <c>-ignore-missing-schemas</c> is deliberately off by default: it turns
/// "no schema for this kind" (e.g. unprovisioned CRDs) into a silent skip.
/// Operators with CRD-bearing repos opt in via <c>IgnoreMissingSchemas</c>
/// or provision their CRD schemas.</para>
///
/// <para><b>Version pin.</b> The schema-validation surface changes between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>kubeconform -v</c> before the
/// scan; a missing binary, an unrecognised version string, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass, never a finding.</para>
///
/// <para><b>kubeconform flag-ordering constraint.</b> kubeconform parses
/// arguments with Go's <c>flag</c> package, which stops at the first
/// positional argument — a flag appended after the scan targets is silently
/// treated as a file name and surfaces as a bogus
/// <c>statusError</c> finding. <c>ExtraArguments</c> is appended after the
/// targets by the shared base, so this auditor rejects flag-looking
/// <c>ExtraArguments</c> entries outright (deterministic infrastructure
/// failure with a pointer to the right knob) rather than letting them
/// misfire. Operator-facing flags are exposed as scoped keys:
/// <c>SchemaLocations</c>, <c>KubernetesVersion</c>, <c>Strict</c>,
/// <c>IgnoreMissingSchemas</c>, <c>Targets</c>.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is <c>kubeconform .</c>:
/// kubeconform walks the tree for <c>*.yaml</c>/<c>*.yml</c>/<c>*.json</c>
/// and skips any document without a <c>kind</c> field, so ordinary
/// non-manifest YAML (CI configs, compose files) is out of scope by
/// construction. On top of that, findings under vendored and dependency
/// trees (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) are
/// dropped by default — problems there describe upstream packages, not the
/// change under audit. Helm-style template files that are not plain YAML
/// surface as <c>statusError</c> findings; exclude chart directories via
/// <c>ExcludePaths</c>. kubeconform reads no configuration file from the
/// audited repository, so there is no repo-controlled suppression surface
/// to gate.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Kubeconform Kubernetes Manifests",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "kubeconform",
    InstallHint = "provision the pinned kubeconform release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "kubeconform — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class KubeconformAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.kubeconform";

    /// <summary>
    /// kubeconform release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.8.0";

    /// <summary>
    /// Scoped-config key for schema locations (comma-separated; each entry
    /// becomes a repeatable <c>-schema-location</c> argument). Accepts any
    /// value kubeconform accepts: <c>default</c>, a URL or filesystem path,
    /// optionally templated. Unset → kubeconform's built-in default (the
    /// upstream <c>kubernetes-json-schema</c> repo — requires egress to
    /// <c>raw.githubusercontent.com</c>).
    /// </summary>
    public const string SchemaLocationsKey = "SchemaLocations";

    /// <summary>
    /// Scoped-config key for <c>-kubernetes-version</c>: <c>master</c> or a
    /// full <c>x.y.z</c> version. Unset → kubeconform's own default
    /// (<c>master</c>); pin it for stable schema revisions.
    /// </summary>
    public const string KubernetesVersionKey = "KubernetesVersion";

    /// <summary>
    /// Scoped-config key for scan targets (comma-separated files/folders,
    /// positional args). Unset → <c>.</c> (the whole work tree).
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>Scoped-config boolean for kubeconform's <c>-strict</c> mode.</summary>
    public const string StrictKey = "Strict";

    /// <summary>
    /// Scoped-config boolean for kubeconform's <c>-ignore-missing-schemas</c>.
    /// Default false: a kind with no resolvable schema reports
    /// <c>statusError</c> and fails the audit rather than silently skipping.
    /// </summary>
    public const string IgnoreMissingSchemasKey = "IgnoreMissingSchemas";

    // Same shape as kubeconform's own -kubernetes-version UnmarshalText
    // validation; checked here so a bad value is a deterministic
    // configuration failure naming the scoped key, not a bare exit 1.
    private static readonly Regex KubernetesVersionPattern = new(
        @"^(master|\d+\.\d+\.\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = all resources valid/skipped; 1 = completed scan with invalid or
        // error resources — OR a run failure. The JSON report on stdout is
        // the discriminator: run failures emit none, so exit 1 without a
        // report fails closed through the parser. Every other exit is
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
    private Func<IReadOnlyList<string>> _schemaLocations = static () => [];
    private Func<string?> _kubernetesVersion = static () => null;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _strict = static () => false;
    private Func<bool> _ignoreMissingSchemas = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:kubeconform";

    /// <summary>
    /// kubeconform's default schema source is a remote registry, so the
    /// auditor requests network egress — bounded by the deployment's
    /// audit-tool allowed-hosts list. Operators using a fully offline
    /// baseline provision schemas locally and set <c>SchemaLocations</c>; the
    /// declared capability stays (it permits egress, it does not force it).
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "kubeconform";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new KubeconformJsonOutputParser();

    /// <summary>
    /// Declared mapping from kubeconform's status vocabulary to
    /// <see cref="AuditSeverity"/>. kubeconform has no severity levels — only
    /// per-resource validation states — so every reported resource is an
    /// <see cref="AuditSeverity.Error"/>: a schema violation, a resource that
    /// could not be validated, and even an unrecognized status from a
    /// foreign build all mean "not proven valid" and fail closed. Raw tool
    /// tokens never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["statusInvalid"] = AuditSeverity.Error,
            ["statusError"] = AuditSeverity.Error,
            ["invalid"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-v"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Go's flag package stops at the first positional argument, so a
        // flag-looking ExtraArguments token (appended after the targets by
        // the shared base) would silently become a file name and surface as
        // a bogus statusError finding. Reject it deterministically instead;
        // the scoped keys cover kubeconform's flag surface.
        var flagLike = options.ExtraArguments
            .Where(static arg => arg.StartsWith("-", StringComparison.Ordinal))
            .ToList();
        if (flagLike.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with flag-like ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', flagLike))}') — kubeconform parses flags only "
                + "before its positional targets, so these would be treated as file names. Use the "
                + $"scoped keys (SchemaLocations, KubernetesVersion, Strict, IgnoreMissingSchemas) "
                + $"under CodeyBox:Plugins:{PluginId} and ExtraArguments for additional paths only.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "-output", "json",
            "-summary",
        };

        var kubernetesVersion = _kubernetesVersion();
        if (!string.IsNullOrWhiteSpace(kubernetesVersion))
        {
            var trimmed = kubernetesVersion.Trim();
            if (!KubernetesVersionPattern.IsMatch(trimmed))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has an invalid KubernetesVersion "
                    + $"('{TruncateForMessage(trimmed)}'); set CodeyBox:Plugins:{PluginId}:{KubernetesVersionKey} "
                    + "to \"master\" or a full version x.y.z (e.g. \"1.32.0\").")
                { IsDeterministic = true };
            args.Add("-kubernetes-version");
            args.Add(trimmed);
        }

        if (_strict())
            args.Add("-strict");
        if (_ignoreMissingSchemas())
            args.Add("-ignore-missing-schemas");

        foreach (var location in _schemaLocations())
        {
            if (string.IsNullOrWhiteSpace(location))
                continue;
            args.Add("-schema-location");
            args.Add(location.Trim());
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
        _schemaLocations = () => SplitList(scoped[SchemaLocationsKey]);
        _kubernetesVersion = () => scoped[KubernetesVersionKey];
        _targets = () => SplitList(scoped[TargetsKey]);
        _strict = () => bool.TryParse(scoped[StrictKey], out var strict) && strict;
        _ignoreMissingSchemas = () =>
            bool.TryParse(scoped[IgnoreMissingSchemasKey], out var ignore) && ignore;
        context.Logger.LogInformation(
            "KubeconformAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => item.Length > 0)
                .ToList();
}
