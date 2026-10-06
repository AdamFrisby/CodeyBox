using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PlutoAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>pluto detect-files</c> (Fairwinds
/// Pluto — Kubernetes apiVersion deprecation/removal detection) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pluto JSON report parser
/// (<see cref="PlutoJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// pluto-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: mixed.</b> Removed apiVersions (and deprecated
/// ones with no replacement available on the target) map to
/// <see cref="AuditSeverity.Error"/> and fail the audit: a manifest carrying
/// them cannot be applied to the target cluster. Merely deprecated
/// apiVersions map to <see cref="AuditSeverity.Warning"/>: they are still
/// served on the target, so they surface as visible warnings without failing
/// an otherwise clean audit. There is no advisory-only mode for removals;
/// narrow scope with <c>Targets</c>, <c>ExcludedRules</c>,
/// <c>ExcludePaths</c>, or <c>OnlyShowRemoved</c> instead.</para>
///
/// <para><b>Exit-code convention (verified against the pluto source,
/// <c>pkg/api/output.go</c> <c>GetReturnCode</c>).</b> Exit <c>0</c> means no
/// deprecated/removed apiVersions were found; <c>2</c> means at least one
/// deprecation, <c>3</c> at least one removal, <c>4</c> at least one
/// unavailable replacement (later checks overwrite earlier ones, so <c>4</c>
/// wins when several classes are present). All three are findings-producing:
/// a completed scan always writes the JSON report to stdout. Exit <c>1</c>
/// is the execution error — a finder failure, a flag-validation failure, or
/// "you must specify a sub-command" — and writes plain text with no report,
/// so the parser fails it closed as infrastructure.
/// <c>126</c>/<c>127</c> (cannot execute / not found) and any other exit are
/// infrastructure.</para>
///
/// <para><b>Required target version (inclusion gate).</b> pluto falls back to
/// its embedded default target when <c>--target-versions</c> is omitted, but
/// this auditor never relies on that default: <c>TargetKubernetesVersion</c>
/// is required, and a missing or malformed value is a deterministic
/// configuration failure naming the scoped key — a version-aware verdict
/// against an assumed target would be worse than no verdict. The value must
/// be semver with a leading <c>v</c> (e.g. <c>v1.29.0</c>), which is also
/// what pluto's own semver check demands.</para>
///
/// <para><b>Local manifests only.</b> The scan is always
/// <c>detect-files -d &lt;dir&gt;</c> over the audited worktree. The live
/// subcommands (<c>detect-helm</c>, <c>detect-api-resources</c>,
/// <c>detect-all-in-cluster</c>) are never invoked, so the auditor declares
/// no network capability and reads no kubeconfig. pluto's
/// <c>-d/--directory</c> flag takes a single directory (verified
/// <c>StringVarP</c> in the official <c>cmd/root.go</c> — a repeated flag
/// would silently keep only the last value), so at most one
/// <c>Targets</c> entry is accepted; several entries are a deterministic
/// configuration failure rather than a silently narrowed scan.</para>
///
/// <para><b>Empty scans fail closed.</b> A clean scan omits <c>items</c> from
/// the JSON entirely, so "no output" and "no findings" look alike without
/// the report contract. Two guards close the gap: the parser requires the
/// <c>target-versions</c> block (proof the report came from a completed run
/// with a target), and <see cref="VerifyToolAsync"/> probes the scan root
/// for candidate manifest files (<c>*.yaml</c>/<c>*.yml</c>/<c>*.json</c>)
/// before the scan runs — a tree with nothing pluto could inspect is
/// infrastructure ("inspected nothing"), never a pass. Residual limit, also
/// documented in the README: pluto's JSON carries no scanned-file count, so
/// a tree whose YAML files are all non-manifest documents still passes
/// clean.</para>
///
/// <para><b>Version pin.</b> The deprecation catalogue (known apiVersions,
/// removal versions, replacement availability) changes between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>pluto version</c> before the scan (which
/// prints <c>Version:x.y.z Commit:…</c>); a missing binary, an unrecognised
/// version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled inputs are gated.</b> pluto auto-reads no
/// configuration from the audited tree for <c>detect-files</c>, but two
/// channels could still let the diff reshape the gate: extra version data
/// via <c>-f/--additional-versions</c> (a repository file handed to the tool
/// as ground truth) and scope/verdict-shaping flags smuggled through
/// <c>ExtraArguments</c>. The former knob is intentionally not exposed; the
/// latter entries are rejected deterministically (see
/// <see cref="BuildToolArguments"/>). pluto additionally honors
/// <c>PLUTO_*</c> environment variables for the same flags (verified in the
/// official <c>cmd/root.go</c>: explicit argv wins, but an unset flag falls
/// back to the environment), so the auditor unsets every documented
/// <c>PLUTO_*</c> variable on the scan exec — argv is the sole control
/// surface.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Pluto Kubernetes Deprecations",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "pluto",
    InstallHint = "provision the pinned pluto release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "pluto — by fetching the versioned upstream GitHub release archive via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class PlutoAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.pluto";

    /// <summary>
    /// pluto release the invocation and its findings are verified against
    /// (exit codes from <c>GetReturnCode</c>, JSON shape from
    /// <c>pkg/api/output.go</c> + <c>versions.go</c>). Operators running a
    /// different pinned build set <c>ExpectedVersion</c> in the plugin's
    /// scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "5.24.4";

    /// <summary>
    /// Scoped-config key for the intended Kubernetes version (REQUIRED, e.g.
    /// <c>v1.29.0</c>): becomes <c>--target-versions k8s=&lt;value&gt;</c>.
    /// Unset or malformed values are deterministic configuration failures —
    /// the auditor never assumes a target.
    /// </summary>
    public const string TargetKubernetesVersionKey = "TargetKubernetesVersion";

    /// <summary>
    /// Scoped-config key for the scan root (a single repo-relative directory,
    /// passed as pluto's <c>-d</c>). Unset → <c>.</c> (the whole work tree).
    /// At most one entry: pluto's <c>-d</c> keeps only the last of several.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config boolean for pluto's <c>--only-show-removed</c>: report
    /// only apiVersions removed (not merely deprecated) on the target.
    /// Default false.
    /// </summary>
    public const string OnlyShowRemovedKey = "OnlyShowRemoved";

    // pluto validates target versions with golang.org/x/mod/semver, which
    // requires a leading 'v'; checked here so a bad value is a deterministic
    // configuration failure naming the scoped key, not a bare exit 1.
    private static readonly Regex TargetVersionPattern = new(
        @"^v\d+\.\d+\.\d+([-+][0-9A-Za-z.\-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean; 2 = deprecations, 3 = removals, 4 = unavailable
        // replacements — all findings-producing with a JSON report on
        // stdout. 1 is the execution error (finder/flag failure, no report)
        // and fails closed through the parser. Every other exit is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2, 3, 4 },
        // Findings in vendored/dependency trees describe upstream manifests,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    /// <summary>
    /// Every <c>PLUTO_*</c> variable pluto's viper binding honors (verified
    /// against the official <c>cmd/root.go</c> flag set): unset on the scan
    /// exec so explicit argv is the sole control surface and a baseline
    /// environment cannot reshape the gate.
    /// </summary>
    private static readonly IReadOnlyList<string> PlutoEnvironmentRemovals =
    [
        "PLUTO_IGNORE_DEPRECATIONS",
        "PLUTO_IGNORE_REMOVALS",
        "PLUTO_IGNORE_UNAVAILABLE_REPLACEMENTS",
        "PLUTO_ONLY_SHOW_REMOVED",
        "PLUTO_ADDITIONAL_VERSIONS",
        "PLUTO_TARGET_VERSIONS",
        "PLUTO_OUTPUT",
        "PLUTO_COLUMNS",
        "PLUTO_COMPONENTS",
        "PLUTO_NO_HEADERS",
        "PLUTO_DIRECTORY",
    ];

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _targetKubernetesVersion = static () => null;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _onlyShowRemoved = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:pluto";

    /// <summary>
    /// <c>detect-files</c> reads local manifests only — no Helm, no cluster,
    /// no schema registry — so the auditor needs no network egress.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "pluto";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PlutoJsonOutputParser();

    /// <summary>
    /// Declared mapping from the parser's classification to
    /// <see cref="AuditSeverity"/>. Removed apiVersions — and deprecated ones
    /// with no available replacement — no longer exist (or have no upgrade
    /// path) on the target, so they fail the audit; merely deprecated
    /// apiVersions are still served on the target and warn. Unknown shapes
    /// from a foreign build fail closed through the declared default. Raw
    /// tool tokens never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            [PlutoJsonOutputParser.DeprecatedLevel] = AuditSeverity.Warning,
            [PlutoJsonOutputParser.RemovedLevel] = AuditSeverity.Error,
            [PlutoJsonOutputParser.ReplacementUnavailableLevel] = AuditSeverity.Error,
            [PlutoJsonOutputParser.UnknownLevel] = AuditSeverity.Error,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(ExternalToolAuditorOptions options)
        => PlutoEnvironmentRemovals;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Scope/verdict-shaping flags must come from scoped config (or stay
        // unavailable), never smuggled through ExtraArguments: --target-versions
        // would silently replace the required target, --output would divert the
        // JSON parsing contract, -d would repoint the scan, -f would hand the
        // tool repository-controlled version data as ground truth, and the
        // ignore/components/only-show-removed flags would reshape the verdict
        // off the record.
        if (ExtraArgumentsSupplyFlag(
                options,
                "--target-versions", "-t",
                "--output", "-o",
                "--directory", "-d",
                "--additional-versions", "-f",
                "--only-show-removed", "-r",
                "--ignore-deprecations",
                "--ignore-removals",
                "--ignore-unavailable-replacements",
                "--components"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with a verdict-shaping ExtraArguments "
                + "entry (target-versions, output, directory, additional-versions, only-show-removed, "
                + "ignore-*, or components) — these would silently reshape the scan the scoped config "
                + $"describes. Use the scoped keys (TargetKubernetesVersion, Targets, OnlyShowRemoved) "
                + $"under CodeyBox:Plugins:{PluginId} and ExtraArguments for nothing pluto consumes.")
            { IsDeterministic = true };

        var targetVersion = _targetKubernetesVersion();
        if (string.IsNullOrWhiteSpace(targetVersion))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' requires CodeyBox:Plugins:{PluginId}:"
                + $"{TargetKubernetesVersionKey} (the intended Kubernetes version, e.g. \"v1.29.0\"); "
                + "reporting deprecations against an assumed target would be worse than no verdict.")
            { IsDeterministic = true };
        var trimmedTarget = targetVersion.Trim();
        if (!TargetVersionPattern.IsMatch(trimmedTarget))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an invalid {TargetKubernetesVersionKey} "
                + $"('{TruncateForMessage(trimmedTarget)}'); set CodeyBox:Plugins:{PluginId}:"
                + $"{TargetKubernetesVersionKey} to semver with a leading 'v' (e.g. \"v1.29.0\").")
            { IsDeterministic = true };

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ValidatedRepoRelativeTarget(t, $"{PluginId}:{TargetsKey}"))
            .ToList();
        if (targets.Count > 1)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with {targets.Count} {TargetsKey} "
                + "entries — pluto's -d/--directory takes a single directory (a repeated flag keeps "
                + "only the last value), so several entries would silently narrow the scan. Set one "
                + $"directory under CodeyBox:Plugins:{PluginId}:{TargetsKey}, or leave it unset to scan "
                + "the whole work tree.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "detect-files",
            "-d", targets.Count == 0 ? "." : targets[0],
            "--target-versions", "k8s=" + trimmedTarget,
            "-o", "json",
        };
        if (_onlyShowRemoved())
            args.Add("--only-show-removed");

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targetKubernetesVersion = () => scoped[TargetKubernetesVersionKey];
        _targets = () => SplitList(scoped[TargetsKey]);
        _onlyShowRemoved = () =>
            bool.TryParse(scoped[OnlyShowRemovedKey], out var onlyRemoved) && onlyRemoved;
        context.Logger.LogInformation(
            "PlutoAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resource-coverage precondition on the live path: pluto's JSON carries
    /// no scanned-file count, so a completed scan over a tree with no
    /// manifests is indistinguishable from a clean scan. Probe the scan root
    /// for candidate manifest files before the scan runs; when none exist,
    /// pluto would inspect nothing, and that is infrastructure — never a
    /// pass.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var scanRoot = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .FirstOrDefault();
        if (string.IsNullOrEmpty(scanRoot))
            scanRoot = ".";

        var globs = new List<string>();
        var exactFiles = new List<string>();
        if (IsManifestFileName(scanRoot))
        {
            exactFiles.Add(scanRoot);
        }
        else
        {
            var prefix = scanRoot is "." or "./" or "/" ? string.Empty : scanRoot.TrimEnd('/') + "/";
            globs.Add(prefix + "*.yaml");
            globs.Add(prefix + "*.yml");
            globs.Add(prefix + "*.json");
        }

        var globbed = await ProbeRepositoryPathGlobsPresentAsync(
            sandbox, workingDirectory, tool, globs, options, ct).ConfigureAwait(false);
        var exact = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, exactFiles, options, ct).ConfigureAwait(false);
        if (globbed.Count == 0 && exact.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found no candidate Kubernetes manifest files "
                + $"(*.yaml/*.yml/*.json) under '{TruncateForMessage(scanRoot)}' — pluto would inspect "
                + "nothing, and an empty result is not a clean verdict. Point "
                + $"CodeyBox:Plugins:{PluginId}:{TargetsKey} at a directory containing manifests, or "
                + "remove the auditor from the project's audit set.")
            { IsDeterministic = true };
    }

    private static bool IsManifestFileName(string target)
        => target.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => item.Length > 0)
                .ToList();
}
