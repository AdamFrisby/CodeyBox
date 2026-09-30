using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.TrivyAuditorPlugin;

/// <summary>
/// Dependency, config, and container-content vulnerability auditor wrapping
/// <c>trivy</c> (Aqua Security's scanner for language dependencies, OS
/// packages, misconfigurations, and secrets) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the repository-config
/// gate via <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, the
/// operator-config containment via
/// <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync(CodeyBox.Core.ISandbox, string, CodeyBox.Core.AuditContext, ExternalToolAuditorOptions, CancellationToken)"/>,
/// and the trivy-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (blocking on error).</b> Trivy's
/// SARIF levels map as <c>error</c> (trivy <c>HIGH</c>/<c>CRITICAL</c>) to
/// <see cref="AuditSeverity.Error"/> — those findings fail the audit;
/// <c>warning</c> (trivy <c>MEDIUM</c>) is advisory and <c>note</c> (trivy
/// <c>LOW</c>/<c>UNKNOWN</c>) is informational.
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against trivy 0.74.0).</b> Trivy
/// does NOT follow the common "1 = findings" convention in either
/// direction: without <c>--exit-code</c> it exits <c>0</c> whether or not
/// anything matched, and with <c>--exit-code 1</c> it exits <c>1</c> both
/// when findings trip the threshold AND on fatal errors (bad flags,
/// unscannable targets, database load failures) — the two are
/// indistinguishable from the exit alone. The auditor therefore never
/// passes <c>--exit-code</c>: <c>0</c> is "ran" (clean or not — the SARIF
/// is the verdict either way) and every other exit is infrastructure. The
/// <c>--exit-code</c> flag is reserved in <c>ExtraArguments</c> so an
/// operator cannot reintroduce the ambiguity.</para>
///
/// <para><b>Stream note.</b> The SARIF report goes to <b>stdout</b>
/// (<c>--format sarif</c>); progress and logs go to stderr and are silenced
/// with <c>--quiet</c> so the report stream stays clean. The shared
/// <see cref="SarifToolOutputParser"/> reads stdout.</para>
///
/// <para><b>Version pin.</b> Trivy's matchers, checks, and severity
/// assignments change between releases, so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>trivy --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Trivy loads
/// <c>trivy.yaml</c> (the default <c>--config</c> path),
/// <c>.trivyignore</c> (the default <c>--ignorefile</c>), and
/// <c>trivy-secret.yaml</c> (the default <c>--secret-config</c>) from the
/// working directory — the audited worktree root — and their severity
/// filters, skip globs, per-CVE ignores, and secret-rule exclusions
/// suppress matches the subject authors. Their presence fails closed as
/// infrastructure by default; operators who trust repo-authored config set
/// <c>TrustRepositorySuppression</c>, and operators who need a fixed
/// organizational policy pin an operator-owned file via <c>ConfigPath</c>
/// (which must resolve outside the worktree — an in-tree path is rejected
/// deterministically — because trivy resolves a relative
/// <c>--config</c> against the worktree).</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>fs .</c> — the
/// whole worktree — with <c>--scanners</c> pinned to dependency
/// vulnerabilities, misconfigurations, and secrets (trivy's own default
/// omits misconfigurations). Findings inside vendored or generated trees
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) describe
/// upstream code and usually duplicate the manifest-declared finding for
/// the same package, so they are excluded by default; operators re-include
/// a path by overriding <c>ExcludePaths</c>. Container-image scanning
/// (<c>image:</c>/<c>registry:</c> targets) is out of scope — it needs a
/// container runtime or registry credentials the worktree audit does not
/// have — but OS-package vulnerabilities reachable from the filesystem
/// scan are reported.</para>
///
/// <para><b>Network and database.</b> Trivy downloads and validates its
/// vulnerability database (and its misconfiguration checks bundle) on
/// first run and updates them on later runs, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> and the database hosts must be
/// in the deployment's <c>AuditToolAllowedHosts</c> egress list. Fully
/// offline deployments pre-seed the database into the baseline image. The
/// version-update notice is disabled via <c>--skip-version-check</c> (it
/// is latency and egress, not signal); database and check updates stay on
/// because they are the signal.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Trivy Dependency, Config and Container Vulnerabilities",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "trivy",
    InstallHint = "provision the pinned trivy release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — download the versioned "
        + "upstream release tarball (https://github.com/aquasecurity/trivy/releases) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions "
        + "and pre-seed the vulnerability database (`trivy fs --download-db-only`); no distro apt package "
        + "carries a pinned trivy")]
public sealed class TrivyAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.trivy";

    /// <summary>
    /// Trivy release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.74.0";

    /// <summary>
    /// Scoped-config key for an explicit trivy configuration file
    /// (<c>--config</c>). Set it to pin an operator-owned policy; unset,
    /// trivy resolves the audited repository's own <c>trivy.yaml</c> (gated
    /// by default — see <see cref="TrustRepositorySuppressionKey"/>).
    /// Must resolve outside the audited worktree.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored trivy config
    /// (<c>trivy.yaml</c>, <c>.trivyignore</c>, <c>trivy-secret.yaml</c>).
    /// Default false: the audited repo must not be able to silence the scan
    /// with severity filters, skip globs, per-CVE ignores, or secret-rule
    /// exclusions.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Scoped-config key for the trivy scanners to run (comma-separated;
    /// each entry becomes part of the single <c>--scanners</c> value).
    /// Unset → dependency vulnerabilities, misconfigurations, and secrets.
    /// Entries are exact-matched against trivy's scanner vocabulary.
    /// </summary>
    public const string ScannersKey = "Scanners";

    /// <summary>Default scanners: trivy's own default omits misconfigurations.</summary>
    public const string DefaultScanners = "vuln,misconfig,secret";

    private static readonly HashSet<string> KnownScanners = new(StringComparer.OrdinalIgnoreCase)
    {
        "vuln",
        "misconfig",
        "secret",
        "license",
    };

    // Trivy loads these from the working directory by default (--config,
    // --ignorefile, --secret-config defaults): trivy.yaml binds severity
    // filters and skip globs, .trivyignore carries per-CVE ignores, and
    // trivy-secret.yaml carries secret-rule exclusions. All three are the
    // audit subject's to abuse when the worktree root is the scan cwd.
    private static readonly string[] RepositoryConfigFiles =
    [
        "trivy.yaml",
        ".trivyignore",
        "trivy-secret.yaml",
    ];

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // change the declared exit-code contract, silently merge with (pflag
    // string-slice append) or fight the pinned scan scope, or re-open the
    // repository-controlled config surface. Rejected deterministically with
    // a pointer to the scoped key covering the same need.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--format", "-f"), // report sink — pinned to sarif on stdout
        ("--output", "-o"), // report sink — ours is stdout
        ("--exit-code", ""), // exit contract — never passed (ambiguous 1)
        ("--config", "-c"), // repo-config surface — use ConfigPath
        ("--scanners", ""), // scan scope — use Scanners
        ("--severity", "-s"), // pinned to all severities; use MinimumSeverity
        ("--ignorefile", ""), // per-CVE suppression surface — use ExcludedRules
        ("--secret-config", ""), // secret-rule surface — builtins only
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // --exit-code is never passed, so a completed scan exits 0 whether
        // or not the report carries findings. Verified against trivy 0.74.0:
        // 0 with findings, 0 clean; 1 for bad flags, unscannable targets,
        // config load failures, and database failures. Only 0 is "ran".
        FindingsExitCodes = new HashSet<int> { 0 },
        // Findings inside vendored/dependency trees describe upstream code and
        // usually duplicate the manifest-declared match for the same package —
        // noise that trains operators to ignore the auditor. Operators
        // re-include a path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;
    private Func<string?> _scanners = static () => DefaultScanners;

    /// <inheritdoc />
    public override string Name => "codeybox:trivy";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "trivy";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from trivy's SARIF severity vocabulary to
    /// <see cref="AuditSeverity"/> (trivy's own severities collapse onto
    /// SARIF levels upstream: <c>CRITICAL</c>/<c>HIGH</c> to <c>error</c>,
    /// <c>MEDIUM</c> to <c>warning</c>, <c>LOW</c>/<c>UNKNOWN</c> to
    /// <c>note</c>). Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        RejectReservedExtraArguments(options);

        var args = new List<string>
        {
            // Whole-worktree filesystem scan.
            "fs",
            // SARIF on stdout; the shared parser reads the report stream.
            "--format", "sarif",
            // Quiet keeps progress and logs off stderr so the report stream
            // stays clean.
            "--quiet",
            // The version-update notice is latency and egress, not signal —
            // and the version under audit is the pinned baseline build, not
            // whatever is newest. Database and check-bundle updates stay on:
            // they are the signal. Author-chosen constant, never untrusted
            // data.
            "--skip-version-check",
            // Trivy's own default scanners omit misconfigurations; pin the
            // full dependency/config/secret scope. Severities stay pinned to
            // all levels — MinimumSeverity filters after mapping.
            "--scanners", ValidatedScanners(_scanners()),
            "--severity", "UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL",
            ".",
        };

        return args;
    }

    /// <summary>
    /// Emits the <c>--config</c> pair — the one argv element that needs a
    /// bounded sandbox probe to compute. Unset <c>ConfigPath</c> → no flag:
    /// trivy would then resolve the audited repository's own
    /// <c>trivy.yaml</c>, which the <c>VerifyToolAsync</c> gate already
    /// fails closed on. An operator's <c>ConfigPath</c> is canonicalized in
    /// the sandbox and rejected when it resolves inside the worktree —
    /// trivy resolves a relative <c>--config</c> against the worktree, so
    /// an in-tree path would hand the diff author flag control — and the
    /// validated value is exactly the value argv carries.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = ValidatedScopedValue(_configPath(), ConfigPathKey);
        if (configured is null)
            return [];

        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, configured, options, ct).ConfigureAwait(false);
        return ["--config", canonical];
    }

    /// <summary>
    /// Canonicalizes the operator's <c>ConfigPath</c> inside the sandbox
    /// and fails closed when it resolves inside the audited worktree, using
    /// one <c>realpath -m</c> call over the configured path and
    /// <c>"."</c>: relative paths, <c>..</c> segments, and symlinked
    /// components all collapse to the path trivy would actually open, and
    /// containment is judged against the same canonicalized scan root.
    /// </summary>
    private async Task<string> CanonicalizeOutsideWorktreeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string configured,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "config-file check",
            new SandboxExec
            {
                Argv = ["realpath", "-m", "--", configured, "."],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' {ConfigPathKey} canonicalization could "
                + "not run: the sandbox exec transport was unavailable.");

        var lines = probe.Stdout.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (probe.ExitCode != 0 || lines.Length != 2)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not canonicalize {ConfigPathKey} "
                + $"'{TruncateForMessage(configured)}' (exit {probe.ExitCode}) — an unchecked config "
                + "path is never trusted, so this is infrastructure, not a verdict on the diff.",
                probe.ExitCode,
                probe.Stderr);

        var canonicalConfig = ValidatedArgumentValue(lines[0], ConfigPathKey);
        var canonicalWorktree = lines[1];
        if (HostPathPolicy.IsWithinDirectory(canonicalConfig, canonicalWorktree))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor 'codeybox:trivy' {ConfigPathKey} "
                + $"'{TruncateForMessage(configured)}' resolves to '{TruncateForMessage(canonicalConfig)}' "
                + "inside the audited worktree — a repository-controlled config can bind severity "
                + "filters, skip globs, and ignores that silently empty the report. Set an absolute "
                + "path outside the repository, or unset it.")
            { IsDeterministic = true };

        return canonicalConfig;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        _scanners = () => scoped[ScannersKey];
        context.Logger.LogInformation(
            "TrivyAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Trivy-specific precondition on the live path: trivy loads
    /// <c>trivy.yaml</c>, <c>.trivyignore</c>, and
    /// <c>trivy-secret.yaml</c> from the working directory, and the
    /// severity filters, skip globs, per-CVE ignores, and secret-rule
    /// exclusions inside can suppress matches the audit subject authors.
    /// Unless the operator opted in via
    /// <see cref="TrustRepositorySuppressionKey"/>, their presence at the
    /// worktree root fails closed as infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (_trustRepositorySuppression())
            return;

        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            RepositoryConfigFiles,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled config "
                + $"file(s) '{string.Join("', '", present)}' in the audited repository — "
                + "trivy loads them from the working directory and their severity filters, "
                + "skip globs, per-CVE ignores, and secret-rule exclusions suppress matches, "
                + "so the audit subject could hide a vulnerability. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-authored trivy config.")
            { IsDeterministic = true };
    }

    private static string ValidatedScanners(string? configured)
    {
        var entries = ExternalToolAuditorOptions.SplitCommaSeparatedList(configured);
        if (entries.Count == 0)
            entries = ExternalToolAuditorOptions.SplitCommaSeparatedList(DefaultScanners);
        var unknown = entries.Where(static e => !KnownScanners.Contains(e)).ToList();
        if (unknown.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{ScannersKey}' carries unknown scanner(s) "
                + $"'{string.Join("', '", unknown)}' — allowed scanners are "
                + "'vuln', 'misconfig', 'secret', 'license'.")
            { IsDeterministic = true };
        return string.Join(",", entries);
    }

    private static void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
    {
        var offenders = new List<string>();
        foreach (var (longFlag, shortFlag) in ReservedFlags)
        {
            var flags = shortFlag.Length == 0 ? new[] { longFlag } : new[] { longFlag, shortFlag };
            if (ExtraArgumentsSupplyFlag(options, flags))
                offenders.Add(longFlag);
        }

        if (offenders.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor 'codeybox:trivy' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would redirect the report "
            + "sink, change the declared exit-code contract, silently merge with the pinned scan "
            + "scope, or re-open the repository-controlled config surface. Use the scoped keys under "
            + $"CodeyBox:Plugins:{PluginId} (Scanners, ConfigPath) or the shared knobs "
            + "(MinimumSeverity, IncludedRules, ExcludedRules); ExtraArguments is for everything else "
            + "(e.g. --skip-dirs, --skip-files, --skip-db-update).")
        { IsDeterministic = true };
    }
}
