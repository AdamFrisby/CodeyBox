using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.AstGrepAuditorPlugin;

/// <summary>
/// Structural SAST auditor wrapping the ast-grep CLI (<c>ast-grep</c>) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, SARIF parsing,
/// severity mapping, exit-code classification, and per-auditor configuration.
/// This class adds the ast-grep invocation shape (<c>ast-grep scan --format
/// sarif</c> with the project configuration resolved from operator config or
/// a repository <c>sgconfig</c> file), the declared severity map over
/// ast-grep's vocabulary, the pinned tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> ast-grep rule severities (<c>error</c>/<c>warning</c>/
/// <c>info</c>/<c>hint</c>) surface in SARIF results as <c>error</c>/
/// <c>warning</c>/<c>note</c> (<c>info</c> and <c>hint</c> both render as
/// <c>note</c>) and go through a declared map, never raw: <c>error</c> → <see
/// cref="AuditSeverity.Error"/> (fails the audit), <c>warning</c> → <see
/// cref="AuditSeverity.Warning"/> (advisory), <c>note</c>/<c>info</c>/
/// <c>hint</c> → <see cref="AuditSeverity.Info"/> (informational); anything
/// unrecognised → <see cref="AuditSeverity.Warning"/>.
/// <c>MinimumSeverity</c> can only drop findings, it never raises them. The
/// auditor is therefore a merge gate for rules the ruleset authors marked
/// error, not a blocker on every hint.</para>
///
/// <para><b>Exit-code convention (verified against ast-grep 0.45.3 — do not
/// assume the usual scanner convention).</b> <c>ast-grep scan</c> exits
/// <c>0</c> whenever the scan completes with no <em>error</em>-severity
/// diagnostics — clean trees and warning/info/hint-only matches alike — and
/// <c>1</c> when the scan completes with at least one error-severity match
/// ("Scan succeeded and found error level diagnostics"). Only <c>{0,
/// 1}</c> are findings-producing, and the verdict always comes from the
/// SARIF document, never the exit code alone: an exit <c>0</c> alongside
/// error-level results is still reported as findings. Every other exit means
/// "could not run" (verified: <c>2</c> CLI usage error, <c>3</c> no project
/// configuration found, <c>6</c> unreadable configuration file, <c>8</c>
/// invalid rule file; <c>126</c>/<c>127</c> cannot-execute/not-found) and
/// fails closed as infrastructure — loud, never a silent pass. Operators must
/// not pass <c>--json</c> or a second <c>--format</c> via
/// <c>ExtraArguments</c>: either would replace or divert the SARIF report the
/// parser expects, and the run fails closed as infrastructure.</para>
///
/// <para><b>Version pin.</b> A scanner's rules and output shape change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>ast-grep
/// --version</c> before every run (the CLI prints <c>ast-grep X.Y.Z</c>); a
/// missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Ruleset resolution.</b> ast-grep has no embedded rules: a scan
/// with no project configuration exits <c>3</c>. The auditor resolves the
/// project configuration in order — (1) the operator's scoped <c>Config</c>
/// path, passed as <c>-c</c> (a path outside the audited worktree is the way
/// to run a project the audit subject cannot edit), (2) the repository's own
/// <c>sgconfig.yml</c> or <c>sgconfig.yaml</c> at the worktree root, probed
/// each run and passed explicitly as <c>-c</c> so a configuration from a
/// parent of the worktree can never leak in. When neither supplies a source
/// the run fails closed as deterministic infrastructure — an auditor that
/// cannot scan is not a pass. The scan runs with
/// <see cref="AuditCapabilities.None"/>.</para>
///
/// <para><b>Repository-controlled suppression.</b> The suppressible surface
/// of an ast-grep scan is the project configuration and the rules
/// themselves — rule selection, severities, ignore globs — and the audit
/// subject authors them when they resolve from the repository. An auditor
/// its subject can reconfigure is not a gate, so operators who need the
/// ruleset to be independent of the audit subject set <c>Config</c> in
/// scoped config to a project outside the worktree. Target selection honors
/// the repository's ignore files (gitignore-style); that is standard target
/// enumeration, and the <c>ExcludePaths</c> finding filter applies on top of
/// it. Separately, inline <c>ast-grep-ignore</c> comments in scanned source
/// (<c>ast-grep-ignore</c> or <c>ast-grep-ignore: rule-id</c>, suppressing the
/// same or following line, or the whole file from the first line) suppress
/// findings, including error-severity (blocking) rules, and the pinned 0.45.3
/// <c>ast-grep scan</c> offers no flag disabling them (<c>--no-ignore</c> covers
/// only ignore files; the remaining scan flags are target and severity selectors).
/// The auditor therefore passes no such flag and documents the surface instead:
/// operator <c>Config</c> does not mitigate it because the comments live in the
/// scanned source, not the project.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ast-grep Structural SAST",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "ast-grep",
    InstallHint = "provision the pinned ast-grep release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — e.g. npm install --global "
        + "@ast-grep/cli@" + DefaultExpectedVersion + " or cargo install ast-grep --locked --version "
        + DefaultExpectedVersion + " — through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class AstGrepAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.ast-grep";

    /// <summary>
    /// ast-grep release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.45.3";

    /// <summary>
    /// Scoped-config key for the ast-grep project configuration file
    /// (<c>sgconfig.yml</c>) passed as <c>-c</c> — including a path outside
    /// the audited worktree, which is the way to run a project the audit
    /// subject cannot edit.
    /// </summary>
    internal const string ConfigKey = "Config";

    /// <summary>
    /// Repository-root candidates probed for the project configuration when
    /// the operator does not supply <see cref="ConfigKey"/>. Both spellings
    /// are discovered by the tool; the first present entry wins and is passed
    /// explicitly as <c>-c</c>.
    /// </summary>
    private static readonly IReadOnlyList<string> RepositoryConfigCandidates =
        ["sgconfig.yml", "sgconfig.yaml"];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // ast-grep scan exits 0 on completion with no error-severity
        // diagnostics (clean or warning/info/hint-only) and 1 with at least
        // one error-severity match. Every other exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:ast-grep";

    /// <inheritdoc />
    protected override string ToolName => "ast-grep";

    /// <inheritdoc />
    /// <summary>
    /// ast-grep's SARIF results carry their own <c>level</c>, so the shared
    /// <see cref="SarifToolOutputParser"/> reads the shape directly.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from ast-grep's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Rules carry <c>error</c>,
    /// <c>warning</c>, <c>info</c>, or <c>hint</c>, surfaced in SARIF results
    /// as <c>error</c>, <c>warning</c>, or <c>note</c> (<c>info</c> and
    /// <c>hint</c> both render as <c>note</c>); all spellings are translated
    /// here (plus the common scanner tokens) so a severity means the same
    /// thing regardless of which scanner produced it — raw tool levels never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
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
        // Structured argv, never a shell string: the base appends the -c
        // entry resolved in ResolveContextArgumentsAsync and then the
        // operator's ExtraArguments.
        return
        [
            "scan",
            // SARIF is the report the parser reads; it lands on stdout.
            "--format", "sarif",
            // Deterministic output regardless of the sandbox's TERM: a pipe
            // already disables color, this keeps it off even when it does not.
            "--color", "never",
            // The audited worktree: the base runs with WorkingDirectory set
            // to it, so the relative path keeps artifact URIs repo-relative.
            ".",
        ];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigKey];
        context.Logger.LogInformation(
            "AstGrepAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <summary>
    /// Resolves the <c>-c</c> project configuration the scan needs (and that
    /// <see cref="BuildToolArguments"/> cannot see because it may come from a
    /// bounded repository probe): the operator's <c>Config</c> path wins;
    /// otherwise the worktree's own <c>sgconfig.yml</c>/<c>sgconfig.yaml</c>
    /// is probed. Neither source resolving fails closed as deterministic
    /// infrastructure — ast-grep without a project is a scanner that did not
    /// run, never a pass.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Presence first, inside the resolver: the project resolution below
        // can fail before the base's own presence check runs, and a missing
        // ast-grep must still name ast-grep.
        await ThrowIfBinaryMissingAsync(sandbox, workingDirectory, ToolName, options, ct)
            .ConfigureAwait(false);

        var configured = ValidatedScopedValue(_configPath(), ConfigKey);
        if (configured is not null)
            return ["-c", configured];

        var present = await ProbeRepositoryFilesPresentAsync(
                sandbox, workingDirectory, ToolName, RepositoryConfigCandidates, options, ct)
            .ConfigureAwait(false);
        if (present.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' has no project to run — ast-grep exits "
                + "without one, so this is infrastructure, not a pass. Set "
                + $"CodeyBox:Plugins:{PluginId}:{ConfigKey} to an sgconfig file (a path "
                + "outside the audited worktree keeps the project away from the audit subject), "
                + "or commit sgconfig.yml or sgconfig.yaml to the repository root.")
            { IsDeterministic = true };

        return ["-c", present[0]];
    }
}
