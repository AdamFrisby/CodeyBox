using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PsalmAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>psalm</c> (vimeo/psalm — PHP static analysis)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the psalm JSON output parser
/// (<see cref="PsalmJsonOutputParser"/> — psalm's built-in
/// <c>--output-format=json</c> report, a flat array of <c>IssueData</c>
/// objects whose <c>file_path</c> values are absolute paths; the report
/// embeds no cwd, so the parser relativizes against the scan root resolved
/// per run by <see cref="ResolveScanRootAsync"/> and carried to it on
/// <see cref="ExternalToolParseInput.ScanRoot"/> — an explicit
/// per-invocation input, never shared state on this (singleton) auditor),
/// the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-configuration posture below.
///
/// <para><b>Gate behaviour: blocking on error-severity issues — the same
/// verdict psalm itself returns.</b> Psalm reports each issue with severity
/// <c>error</c> or <c>info</c>: <c>error</c> maps to
/// <see cref="AuditSeverity.Error"/> and fails the audit; <c>info</c> maps
/// to <see cref="AuditSeverity.Info"/> and is advisory. Which issue types
/// are errors is decided by the analysis contract in force — the audited
/// repository's <c>psalm.xml</c>/<c>psalm.xml.dist</c> (its
/// <c>errorLevel</c> and per-issue <c>issueHandlers</c>), or an
/// operator-pinned config via <c>ConfigPath</c>. <c>MinimumSeverity</c>
/// only drops findings, it never raises them; to make more issue types
/// blocking, raise their reporting level in the config.</para>
///
/// <para><b>Exit-code convention (verified against the vimeo/psalm source —
/// deliberately NOT the common "0 clean / 1 findings / 2 error" table;
/// psalm inverts it).</b> Since psalm 4.5 (upstream #5087),
/// <c>0</c> = analysis completed, no <c>error</c>-severity issues
/// (<c>info</c>-severity issues may still be in the report);
/// <c>2</c> = analysis completed, at least one <c>error</c>-severity issue —
/// <c>IssueBuffer::finish</c> writes the report to stdout and then exits 2,
/// so both <c>0</c> and <c>2</c> emit the JSON report and are
/// findings-producing verdicts. <c>1</c> = could not run: bad arguments,
/// missing or unparseable <c>psalm.xml</c>, or any uncaught exception
/// (psalm's top-level error handler exits 1); PHP engine fatals surface as
/// <c>255</c>. <c>126</c>/<c>127</c> = cannot execute / not found — all of
/// these are "could not run" and classified as infrastructure. Exit
/// <c>0</c>/<c>2</c> with no parseable JSON array on stdout fails closed as
/// infrastructure through the parser. Anything else is an unknown
/// convention and fails loudly rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A static analyzer's issue implementations and
/// its report shape change between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>psalm --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding. Because psalm resolves the project's
/// composer autoloader even for <c>--version</c>, the probe runs in a
/// fresh directory outside the audited tree
/// (<see cref="ExternalToolAuditorBase.VersionProbeRunsOutsideWorktree"/>)
/// and the reported version is anchored on the <c>Psalm </c> banner —
/// repository code can neither execute during the probe nor print a
/// forged token that satisfies the pin.</para>
///
/// <para><b>Repository-controlled code execution — stated, not hidden.</b>
/// Psalm is not a passive reader of the audited repository: it requires the
/// project's composer autoloader (<c>vendor/autoload.php</c> — which itself
/// runs any <c>autoload.files</c> entries the repo declares) and a
/// repo-authored <c>psalm.xml</c> can name <c>&lt;pluginClass&gt;</c>
/// entries whose code psalm loads and executes during every scan. That is
/// the same exposure class as ESLint's executable config and
/// golangci-lint's custom <c>.so</c> linters, and it is contained the same
/// way: this auditor runs with <see cref="AuditCapabilities.None"/> — no
/// agent credentials and, on providers that enforce egress, no network
/// (the process provider has no network isolation) — inside the
/// provider's scrubbed environment. Note the autoloader would be loaded
/// even by <c>psalm --version</c> (psalm resolves it before printing the
/// version), so the version probe runs from a fresh directory outside the
/// worktree where no repo autoloader is reachable. Operators who need the
/// <em>config</em> half of this surface closed set
/// <c>TrustRepositoryConfig</c> to <c>false</c> — which requires pinning
/// an out-of-repo config via <c>ConfigPath</c>, whose location the
/// auditor does not verify; the operator is responsible for pointing it
/// at a file the audited repository cannot influence — but the
/// autoloader surface is inherent to psalm and remains either way.</para>
///
/// <para><b>Repository-controlled suppression.</b> Psalm honors
/// <c>@psalm-suppress</c> docblocks and <c>@psalm-ignore-*</c> annotations
/// authored inside the audited repository, plus two suppression files the
/// repo's config selects: the <c>errorBaseline</c> file
/// (<c>psalm-baseline.xml</c>) and per-issue <c>issueHandlers</c> entries —
/// a diff can hide its own new issues by adding baseline entries or
/// lowering a handler's error level. Psalm offers no flag that makes
/// docblock suppressions inert, so the base cannot express that gate and
/// this auditor does not hand-roll one; <c>--ignore-baseline</c> (accepted
/// via <c>ExtraArguments</c>) neutralizes the baseline file specifically.
/// The default posture matches the sibling linters — the project's own
/// analysis contract is the meaningful check — and a warnings-clean local
/// run that disagrees with the audit is a signal to inspect the diff's
/// config, baseline, and suppression comments.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>psalm --output-format=json --show-info=true --no-cache --no-progress</c>:
/// no positional file arguments, so the <c>projectFiles</c> of the config
/// in force decide the analyzed scope — the project's own declaration of
/// checkable code. <c>--show-info=true</c> keeps info-severity issues in
/// the report (psalm filters them from every report format otherwise) so
/// the declared severity mapping sees psalm's full vocabulary;
/// <c>--no-cache</c> makes each run self-contained — a stale shared
/// analysis cache could otherwise serve findings for code that has changed;
/// <c>--no-progress</c> keeps the progress bar out of stderr so a failure
/// tail stays readable. On top of that, findings under vendored
/// (<c>vendor/</c>, <c>node_modules/</c>), framework-generated
/// (<c>var/cache/</c>, <c>storage/framework/</c>, <c>bootstrap/cache/</c>,
/// <c>generated/</c>), and build/coverage output (<c>dist/</c>,
/// <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes are dropped by
/// default: problems there belong to installed packages or generated code,
/// not the change under audit, and reporting them trains operators to
/// ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Psalm PHP Static Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "psalm",
    InstallHint = "provision the pinned psalm release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — psalm needs PHP >= 8.1 CLI on PATH "
        + "and ships via composer (composer global require vimeo/psalm:" + DefaultExpectedVersion
        + " with the composer global bin-dir on PATH), phive (phive install psalm@"
        + DefaultExpectedVersion + " — phive verifies the release's GPG signature), or the "
        + "release psalm.phar verified against psalm.phar.asc and renamed to 'psalm' — never "
        + "install an unverified phar; no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class PsalmAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.psalm";

    /// <summary>
    /// psalm release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "6.17.2";

    /// <summary>
    /// Scoped-config key for an explicit psalm configuration file — passed
    /// to <c>--config</c> (<c>-c</c>). Psalm derives its base directory (and
    /// therefore <c>file_name</c> relativity and the autoloader lookup) from
    /// the config location when <c>resolveFromConfigFile</c> applies, so an
    /// out-of-repo config must retarget the repository through its own
    /// <c>projectFiles</c> entries.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting out of repository-authored tool
    /// configuration. Default true: the audited repo's
    /// <c>psalm.xml</c>/<c>psalm.xml.dist</c> is loaded by psalm's default
    /// discovery, because analyzing against the project's own contract is
    /// the meaningful check. Psalm has no <c>--no-config</c> equivalent —
    /// it cannot run without a config — so <c>false</c> is not "no config"
    /// but "operator-owned config only": it fails closed (infrastructure,
    /// deterministic) unless <see cref="ConfigPathKey"/> supplies a
    /// configuration the audit subject does not control.
    /// </summary>
    internal const string TrustRepositoryConfigKey = "TrustRepositoryConfig";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Psalm inverts the common linter table (verified upstream, psalm
        // >= 4.5): 0 = no error-severity issues; 2 = error-severity issues
        // found — the report is written to stdout before exit(2), so both
        // are verdicts. 1 = could not run (bad args, missing/unparseable
        // psalm.xml, uncaught exception via psalm's error handler); 255 =
        // PHP engine fatal; everything else including 126/127 is
        // infrastructure. Exit code, not output shape, is the verdict.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // Whole-tree static analysis of a PHP codebase is slower than a
        // lint pass; bound it generously but finitely. Probes share the 30s
        // cap.
        Timeout = TimeSpan.FromMinutes(10),
        // The report is one JSON document — a truncated stdout cannot be
        // partially parsed and fails closed as infrastructure. 4 MiB keeps
        // ordinary finding volumes parseable instead of turning a flooded
        // report into a transport failure.
        MaxOutputBytesPerStream = 4 * 1024 * 1024,
        // Findings in vendored/dependency trees and framework-generated or
        // build/coverage output describe code that is not the change under
        // audit — noise that trains operators to ignore the auditor.
        // Operators re-include a path by overriding ExcludePaths in scoped
        // config.
        ExcludePaths =
        [
            "vendor/", "node_modules/", "var/cache/", "storage/framework/",
            "bootstrap/cache/", "generated/", "dist/", "build/", "out/", "coverage/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositoryConfig = static () => true;

    /// <inheritdoc />
    public override string Name => "codeybox:psalm";

    /// <inheritdoc />
    protected override string ToolName => "psalm";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PsalmJsonOutputParser();

    /// <summary>
    /// Declared mapping from psalm's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/> — the shared
    /// <see cref="ExternalToolSeverityMapping.Default"/> extended with the
    /// extra dialect words (<c>fatal</c>/<c>information</c>/<c>advice</c>/
    /// <c>hint</c>) so the entries every auditor shares cannot drift.
    /// Psalm reports <c>error</c> and <c>info</c> only (issues below the
    /// configured error level are reported as <c>info</c>); the
    /// neighbouring levels common to other scanners are mapped identically
    /// so a future issue shape carrying them is not a unique dialect.
    /// Unclassified issues default to <see cref="AuditSeverity.Warning"/>:
    /// advisory rather than silently absent. Raw tool levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        ExternalToolSeverityMapping.Default.Extend(
            new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
            {
                ["fatal"] = AuditSeverity.Error,
                ["information"] = AuditSeverity.Info,
                ["advice"] = AuditSeverity.Info,
                ["hint"] = AuditSeverity.Info,
            });

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"], ExtractPsalmVersion);

    /// <inheritdoc />
    protected override bool VersionProbeRunsOutsideWorktree => true;

    /// <summary>
    /// Extracts the reported version anchored on the <c>Psalm </c> banner —
    /// psalm's <c>--version</c> output is <c>Psalm X.Y.Z@&lt;sha&gt;</c>.
    /// The probe already runs outside the audited tree
    /// (<see cref="VersionProbeRunsOutsideWorktree"/>) so repository code
    /// cannot write a forged banner ahead of the real one; the anchor
    /// additionally keeps PHP/composer startup notices printed before the
    /// banner on a healthy install from feeding the pin check. Returns null
    /// when the banner is absent — the pin then fails closed.
    /// </summary>
    internal static string? ExtractPsalmVersion(string output)
    {
        const string banner = "Psalm ";
        ArgumentNullException.ThrowIfNull(output);
        var index = output.IndexOf(banner, StringComparison.Ordinal);
        return index < 0 ? null : ExtractToolVersion(output[(index + banner.Length)..]);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // TrustRepositoryConfig=false means the audited repository's
        // psalm.xml must not be loaded — and psalm cannot run without a
        // config — so the operator must pin one. Deterministic
        // misconfiguration: fail closed before any sandbox exec.
        var configPath = _configPath();
        var extrasSupplyConfig = ExtraArgumentsSupplyFlag(options, "--config", "-c");
        var configSupplied = !string.IsNullOrWhiteSpace(configPath) || extrasSupplyConfig;
        if (!_trustRepositoryConfig() && !configSupplied)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has TrustRepositoryConfig=false but no "
                + $"{ConfigPathKey}: psalm cannot run without a config file, so the hardened "
                + "posture requires an operator-pinned configuration outside the audited tree.")
            { IsDeterministic = true };

        // Structured argv, never a shell string: the base appends the
        // operator's ExtraArguments after these entries.
        var args = new List<string>();

        // Machine-readable report on stdout; psalm keeps progress and
        // chatter on stderr in this mode, so stdout stays pure JSON. An
        // operator --output-format would replace the JSON the parser
        // expects and break the run into infrastructure failure; let that
        // surface loudly rather than emit an ambiguous duplicate flag.
        if (!ExtraArgumentsSupplyFlag(options, "--output-format"))
        {
            args.Add("--output-format=json");
        }

        // Include info-severity issues in the report: psalm filters them
        // from every report format unless --show-info is true, which would
        // silently drop the advisory half of the declared severity mapping.
        if (!ExtraArgumentsSupplyFlag(options, "--show-info"))
        {
            args.Add("--show-info=true");
        }

        // Self-contained runs: a persistent sandbox could otherwise serve
        // cached analysis for files that changed, and psalm's cache lives
        // outside the audited tree either way.
        args.Add("--no-cache");

        // Keep the progress bar out of stderr so a failure tail stays a
        // readable reason, not carriage-return noise.
        args.Add("--no-progress");

        if (!string.IsNullOrWhiteSpace(configPath) && !extrasSupplyConfig)
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        // No positional file arguments: the projectFiles of the config in
        // force decide the analyzed scope.
        return args;
    }

    /// <inheritdoc />
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        // Psalm's report carries absolute `file_path` values and no embedded
        // cwd, so the parser relativizes against the directory the scan
        // actually ran in — the shared bounded `pwd` probe resolves it.
        => await ResolveScanRootViaPwdAsync(sandbox, workingDirectory, ToolName, options, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        // Unset keeps the default: the repo's own psalm.xml is trusted
        // (loaded by psalm's config discovery). A present-but-unparseable
        // value is a deterministic infrastructure failure — a security
        // posture the operator intended but the auditor cannot read must
        // fail closed, not silently revert to trusting repository config.
        _trustRepositoryConfig = () =>
        {
            var raw = scoped[TrustRepositoryConfigKey];
            if (string.IsNullOrWhiteSpace(raw))
                return true;
            if (bool.TryParse(raw, out var trust))
                return trust;
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable {TrustRepositoryConfigKey} "
                + $"('{TruncateForMessage(raw)}'); set CodeyBox:Plugins:{PluginId}:{TrustRepositoryConfigKey} "
                + "to 'true' or 'false'.")
            { IsDeterministic = true };
        };
        context.Logger.LogInformation(
            "PsalmAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
