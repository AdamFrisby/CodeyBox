using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SemgrepAuditorPlugin;

/// <summary>
/// Structural SAST auditor wrapping the Semgrep CLI (<c>semgrep</c>) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, SARIF parsing,
/// severity mapping, exit-code classification, and per-auditor configuration.
/// This class adds the Semgrep invocation shape (<c>semgrep scan --sarif
/// --error</c> with rules resolved from operator config or a repository
/// <c>.semgrep</c> source), the declared severity map over Semgrep's
/// vocabulary, the pinned tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Semgrep rule severities (<c>ERROR</c>/<c>WARNING</c>/<c>INFO</c>)
/// surface in SARIF as <c>error</c>/<c>warning</c>/<c>note</c> and go through a
/// declared map, never raw: <c>error</c> → <see cref="AuditSeverity.Error"/>
/// (fails the audit), <c>warning</c> → <see cref="AuditSeverity.Warning"/>
/// (advisory), <c>note</c>/<c>none</c> → <see cref="AuditSeverity.Info"/>
/// (informational); anything unrecognised → <see cref="AuditSeverity.Warning"/>.
/// <c>MinimumSeverity</c> can only drop findings, it never raises them. The
/// auditor is therefore a merge gate for rules the ruleset authors marked
/// ERROR, not a blocker on every INFO note.</para>
///
/// <para><b>Exit-code convention (from Semgrep's own EXIT STATUS — do not
/// assume the usual scanner convention).</b> <c>semgrep scan</c> exits
/// <c>0</c> whenever the scan completes — <em>with or without findings</em>;
/// unlike gitleaks or eslint it never uses a non-zero exit for findings
/// unless told to. The auditor passes <c>--error</c> so <c>1</c> becomes the
/// dedicated "ran and found problems" exit, and declares only <c>{0, 1}</c>
/// findings-producing: every other exit means "could not run" (verified
/// conventions: <c>2</c> fatal error, <c>3</c> invalid target code under
/// <c>--strict</c>, <c>4</c> invalid pattern, <c>5</c> unparseable YAML
/// config, <c>7</c> missing/invalid configuration, <c>8</c> unknown language,
/// <c>13</c> invalid API key, <c>99</c> osemgrep-unimplemented, <c>126</c>/
/// <c>127</c> cannot-execute/not-found) and fails closed as infrastructure —
/// loud, never a silent pass. Operators must not pass <c>--no-error</c> or
/// override the convention via <c>ExtraArguments</c>: any exit outside the
/// declared set fails closed anyway, and dropping <c>--error</c> only widens
/// the pass surface.</para>
///
/// <para><b>Version pin.</b> A scanner's rules and output shape change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>semgrep --version</c>
/// before every run (the CLI prints the bare <c>X.Y.Z</c>); a missing binary,
/// an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Ruleset resolution.</b> Semgrep has no embedded rules: a scan
/// without <c>--config</c> exits <c>7</c> (missing configuration). The
/// auditor resolves configuration in order — (1) the operator's scoped
/// <c>Config</c> list (each entry becomes its own <c>--config</c>; a path
/// outside the audited worktree is the way to run a ruleset the audit
/// subject cannot edit), (2) the repository's own <c>.semgrep</c>
/// directory/file, <c>.semgrep.yml</c>, or <c>.semgrep.yaml</c>, probed at
/// the worktree root per run. When neither supplies a source the run fails
/// closed as deterministic infrastructure — an auditor that cannot scan is
/// not a pass. Registry shorthands (<c>p/default</c>, <c>auto</c>) work only
/// where the sandbox has network; the auditor declares
/// <see cref="AuditCapabilities.None"/>.</para>
///
/// <para><b>Repository-controlled suppression.</b> Semgrep honors two
/// suppression surfaces authored inside the audited repository — inline
/// <c>nosemgrep</c> comments and <c>.semgrepignore</c> files (consulted
/// gitignore-style throughout the tree) — and the audit subject is the
/// repository's author. An auditor its subject can silence is not a gate, so
/// by default the scan passes <c>--disable-nosem</c> and
/// <c>--x-ignore-semgrepignore-files</c>, making both inert at every depth.
/// The latter is an internal flag (Semgrep marks <c>--x-*</c> as unstable);
/// the version pin keeps the invocation honest — a build that drops it fails
/// loudly as infrastructure rather than silently re-honoring suppression
/// files. Operators who deliberately trust repo-authored suppression set
/// <c>TrustRepositorySuppression</c> in scoped config.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Semgrep Structural SAST",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "semgrep",
    InstallHint = "provision the pinned semgrep release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a Python package "
        + "(pipx install semgrep==" + DefaultExpectedVersion
        + ", or pip install semgrep==" + DefaultExpectedVersion + " — no distro apt package "
        + "carries a version pin) through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class SemgrepAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.semgrep";

    /// <summary>
    /// Semgrep release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.177.0";

    /// <summary>
    /// Scoped-config key for the comma-separated semgrep <c>--config</c>
    /// sources (rules file or directory paths — including paths outside the
    /// audited worktree — or registry names such as <c>p/default</c>, which
    /// require sandbox network).
    /// </summary>
    internal const string ConfigKey = "Config";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression
    /// surfaces (<c>nosemgrep</c> comments, <c>.semgrepignore</c> files).
    /// Default false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>Upper bound on operator-supplied <c>--config</c> sources.</summary>
    private const int MaxConfigSources = 64;

    /// <summary>
    /// Repository-root candidates probed for rules when the operator does not
    /// supply <see cref="ConfigKey"/>: semgrep's conventional rules directory
    /// (<c>.semgrep/</c>, also loadable as a single YAML file) and the two
    /// file spellings. Each present entry becomes its own <c>--config</c>;
    /// semgrep merges multiple sources.
    /// </summary>
    private static readonly IReadOnlyList<string> RepositoryConfigCandidates =
        [".semgrep", ".semgrep.yml", ".semgrep.yaml"];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // semgrep scan exits 0 on completion regardless of findings; --error
        // makes exit 1 the dedicated "found something" verdict. Every other
        // exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _configSources = static () => [];
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:semgrep";

    /// <inheritdoc />
    protected override string ToolName => "semgrep";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from Semgrep's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Semgrep rules carry <c>ERROR</c>,
    /// <c>WARNING</c>, or <c>INFO</c>, surfaced in SARIF results as
    /// <c>error</c>, <c>warning</c>, or <c>note</c>; both spellings are
    /// translated here (plus the common scanner tokens) so a severity means
    /// the same thing regardless of which scanner produced it — raw tool
    /// levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Structured argv, never a shell string: the base appends the
        // operator's ExtraArguments after the --config entries resolved in
        // ResolveContextArgumentsAsync.
        var args = new List<string>
        {
            "scan",
            // SARIF is the report the parser reads; it lands on stdout.
            "--sarif",
            // Make exit 1 the dedicated "found findings" verdict — semgrep
            // otherwise exits 0 whether or not it reported anything, and only
            // this declared set is findings-producing.
            "--error",
            // No telemetry or update chatter: the sandbox has no network, and
            // neither belongs in an audit boundary anyway.
            "--metrics=off",
            "--disable-version-check",
            // Pin the OSS engine: a logged-in or Pro-enabled baseline would
            // change findings (and need network the audit sandbox lacks).
            "--oss-only",
            // Report authored rule ids verbatim — the ids operators write in
            // their YAML are what IncludedRules/ExcludedRules and findings
            // match, not semgrep's default path-derived rewrites.
            "--no-rewrite-rule-ids",
        };

        // The audit subject authors nosemgrep comments and .semgrepignore
        // files; keep both inert unless the operator opts in to
        // repo-controlled suppression.
        if (!_trustRepositorySuppression())
        {
            args.Add("--disable-nosem");
            args.Add("--x-ignore-semgrepignore-files");
        }

        // The audited worktree: the base runs with WorkingDirectory set to
        // it, so the relative path keeps artifact URIs repo-relative.
        args.Add(".");
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configSources = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[ConfigKey]);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "SemgrepAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <summary>
    /// Resolves the <c>--config</c> entries the scan needs (and that <see
    /// cref="BuildToolArguments"/> cannot see because they may come from a
    /// bounded repository probe): the operator's <c>Config</c> list wins;
    /// otherwise the worktree's own <c>.semgrep</c>/<c>.semgrep.yml</c>/
    /// <c>.semgrep.yaml</c> are probed. Neither source resolving fails closed
    /// as deterministic infrastructure — semgrep without rules is a scanner
    /// that did not run, never a pass.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Presence first, inside the resolver: the ruleset resolution below
        // can fail before the base's own presence check runs, and a missing
        // semgrep must still name semgrep.
        await ThrowIfBinaryMissingAsync(sandbox, workingDirectory, ToolName, options, ct)
            .ConfigureAwait(false);

        var configured = _configSources();
        if (configured.Count > 0)
        {
            var args = new List<string>(configured.Count * 2);
            for (var i = 0; i < configured.Count; i++)
            {
                if (i >= MaxConfigSources)
                    throw new AuditUnavailableException(
                        $"could-not-verify: audit tool '{ToolName}' was configured with more than "
                        + $"{MaxConfigSources} {ConfigKey} entries.")
                    { IsDeterministic = true };
                args.Add("--config");
                args.Add(ValidatedArgumentValue(configured[i], ConfigKey));
            }

            return args;
        }

        var present = await ProbeRepositoryFilesPresentAsync(
                sandbox, workingDirectory, ToolName, RepositoryConfigCandidates, options, ct)
            .ConfigureAwait(false);
        if (present.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' has no ruleset to run — semgrep exits "
                + "without one, so this is infrastructure, not a pass. Set "
                + $"CodeyBox:Plugins:{PluginId}:{ConfigKey} to a rules file or directory (a path "
                + "outside the audited worktree keeps the ruleset away from the audit subject), "
                + "or commit one of .semgrep/, .semgrep.yml, .semgrep.yaml to the repository.")
            { IsDeterministic = true };

        var repoArgs = new List<string>(present.Count * 2);
        foreach (var path in present)
        {
            repoArgs.Add("--config");
            repoArgs.Add(path);
        }

        return repoArgs;
    }
}
