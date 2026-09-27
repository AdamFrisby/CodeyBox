using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GrypeAuditorPlugin;

/// <summary>
/// Dependency and container vulnerability auditor wrapping <c>grype</c>
/// (Anchore's scanner for language dependencies, OS packages, and container
/// image content) on the shared <see cref="ExternalToolAuditorBase"/>: the
/// base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// repository-config gate via
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, the update-check
/// opt-out via <see cref="ExternalToolAuditorBase.BuildToolEnvironment"/>,
/// and the grype-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (blocking on error).</b> Grype's
/// SARIF levels map as <c>error</c> (grype <c>high</c>/<c>critical</c>) to
/// <see cref="AuditSeverity.Error"/> — those findings fail the audit;
/// <c>warning</c> (grype <c>medium</c>) is advisory and <c>note</c> (grype
/// <c>low</c>/<c>negligible</c>/<c>unknown</c>) is informational.
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against grype 0.119.0).</b> Grype
/// does NOT follow the common "1 = findings" convention: without
/// <c>--fail-on</c> it exits <c>0</c> whether or not it matched anything,
/// so findings and clean runs would be indistinguishable from a bare exit.
/// The auditor always passes <c>--fail-on negligible</c> (the lowest named
/// severity): <c>0</c> is then "ran, nothing at or above negligible",
/// <c>2</c> is "ran and matched at least one vulnerability at or above
/// negligible", and every other exit — <c>1</c> for bad flags, an
/// unscannable target, an unparseable config, or a vulnerability-database
/// load failure; <c>126</c>/<c>127</c> cannot-execute — is infrastructure.
/// Both <c>0</c> and <c>2</c> are verdicts whose findings come from parsing
/// the SARIF report, never from the exit code alone: an
/// <c>unknown</c>-severity match may not trip the threshold (exit <c>0</c>)
/// yet is still reported from the SARIF. The threshold flag only separates
/// "ran" from "could not run"; it never filters the report.</para>
///
/// <para><b>Stream note.</b> The SARIF report goes to <b>stdout</b>
/// (<c>-o sarif</c>); logs go to stderr and are silenced with <c>-q</c> so
/// the report stream stays clean. The shared
/// <see cref="SarifToolOutputParser"/> reads stdout.</para>
///
/// <para><b>Version pin.</b> Grype's matchers and severity assignments change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>grype --version</c>
/// before the scan; a missing binary, an unrecognised version string, or a
/// version other than <c>ExpectedVersion</c> is an infrastructure failure
/// naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Grype loads
/// <c>.grype.yaml</c> (or <c>.grype.yml</c>, <c>.grype/config.yaml</c>,
/// <c>.grype/config.yml</c>) from the working directory — the audited
/// worktree root — and its <c>ignore:</c> rules and <c>exclude:</c> globs
/// suppress matches the subject authors. Their presence fails closed as
/// infrastructure by default; operators who trust repo-authored config set
/// <c>TrustRepositorySuppression</c>, and operators who need a fixed
/// organizational policy pin an operator-owned file via <c>ConfigPath</c>
/// (which does not lift the gate — grype may merge discovered config — it
/// only replaces the default policy source).</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>dir:.</c> — the
/// whole worktree: resolved language dependencies and any OS packages with
/// enough distro context to match. Findings inside vendored or generated
/// trees (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) describe
/// upstream code and usually duplicate the manifest-declared finding for the
/// same package, so they are excluded by default; operators re-include a
/// path by overriding <c>ExcludePaths</c>. Container-image scanning
/// (<c>image:</c>/<c>registry:</c> targets) is out of scope — it needs a
/// container runtime or registry credentials the worktree audit does not
/// have.</para>
///
/// <para><b>Network and database.</b> Grype downloads and validates its
/// vulnerability database on first run and auto-updates it on later runs, so
/// the auditor declares <see cref="AuditCapabilities.Network"/> and the
/// database hosts must be in the deployment's
/// <c>AuditToolAllowedHosts</c> egress list. Fully offline deployments
/// pre-seed the database into the baseline image. The application
/// self-update phone-home is disabled via the environment (it is latency
/// and egress, not signal).</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Grype Dependency Vulnerabilities",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "grype",
    InstallHint = "provision the pinned grype release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — download the versioned "
        + "upstream release tarball (https://github.com/anchore/grype/releases) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions "
        + "and pre-seed the vulnerability database (`grype db update`); no distro apt package "
        + "carries a pinned grype")]
public sealed class GrypeAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.grype";

    /// <summary>
    /// Grype release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.119.0";

    /// <summary>
    /// Scoped-config key for an explicit grype configuration file
    /// (<c>--config</c>). Set it to pin an operator-owned policy; unset,
    /// grype resolves the audited repository's own config chain (gated by
    /// default — see <see cref="TrustRepositorySuppressionKey"/>).
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored grype config
    /// (<c>.grype.yaml</c> and its variants). Default false: the audited
    /// repo must not be able to silence the scan with <c>ignore:</c> rules
    /// or <c>exclude:</c> globs.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // Grype's config discovery, in order: ./.grype.yaml, ./.grype/config.yaml
    // (each with a .yml twin), then home/XDG locations outside the repo.
    // Only the worktree-rooted load sites are the audit subject's to abuse.
    private static readonly string[] RepositoryConfigFiles =
    [
        ".grype.yaml",
        ".grype.yml",
        ".grype/config.yaml",
        ".grype/config.yml",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against grype 0.119.0 with --fail-on negligible: 0 is
        // "ran" (clean or below-threshold matches — the SARIF decides), 2 is
        // "ran and matched at/above negligible". Every other exit (1 for bad
        // flags, unscannable targets, bad config, database load failures;
        // 126/127 cannot-execute) means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 2 },
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

    /// <inheritdoc />
    public override string Name => "codeybox:grype";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "grype";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from grype's SARIF severity vocabulary to
    /// <see cref="AuditSeverity"/> (grype's own severities collapse onto
    /// SARIF levels upstream: <c>critical</c>/<c>high</c> to <c>error</c>,
    /// <c>medium</c> to <c>warning</c>, <c>low</c>/<c>negligible</c>/
    /// <c>unknown</c> to <c>note</c>). Raw tool levels never reach findings.
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
        var args = new List<string>
        {
            // Whole-worktree scan: language dependencies plus any OS packages
            // with enough distro context to match.
            "dir:.",
            "-o", "sarif",
            // Quiet keeps logs off stderr so the report stream stays clean.
            "-q",
            // The lowest named severity: any match trips exit 2, separating
            // "ran with matches" from "could not run" (exit 1). The SARIF —
            // not the exit code — is the verdict; unknown-severity matches
            // may exit 0 yet are still reported.
            "--fail-on", "negligible",
        };

        AddValueFlag(args, "--config", _configPath());
        return args;
    }

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? BuildToolEnvironment(
        ExternalToolAuditorOptions options)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Grype phones home to check for application updates on every run
            // (config: check-for-app-update). That is latency and egress, not
            // signal — and the version under audit is the pinned baseline
            // build, not whatever is newest. Author-chosen constant, never
            // untrusted data.
            ["GRYPE_CHECK_FOR_APP_UPDATE"] = "false",
        };

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
        context.Logger.LogInformation(
            "GrypeAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Grype-specific precondition on the live path: grype loads
    /// <c>.grype.yaml</c> (and its <c>.yml</c> / <c>.grype/config.*</c>
    /// twins) from the working directory, and the <c>ignore:</c> rules and
    /// <c>exclude:</c> globs inside can suppress matches the audit subject
    /// authors. Unless the operator opted in via
    /// <see cref="TrustRepositorySuppressionKey"/>, their presence at the
    /// worktree root fails closed as infrastructure before the scan runs.
    /// Pinning an operator-owned policy via <see cref="ConfigPathKey"/>
    /// does not lift the gate — grype may merge discovered config — it only
    /// replaces the default policy source.
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
                + "grype loads them from the working directory and their ignore rules and "
                + "exclude globs suppress matches, so the audit subject could hide a "
                + "vulnerability. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-authored grype config.")
            { IsDeterministic = true };
    }
}
