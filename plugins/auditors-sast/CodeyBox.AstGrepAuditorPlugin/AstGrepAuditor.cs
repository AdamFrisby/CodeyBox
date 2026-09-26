using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.AstGrepAuditorPlugin;

/// <summary>
/// SAST auditor wrapping <c>ast-grep scan</c> (structural AST pattern rules)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the ast-grep JSON report parser
/// (<see cref="AstGrepJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// repository-suppression and dynamic-language gates via
/// <see cref="VerifyToolAsync"/>, and the ast-grep-specific arguments and
/// knobs below.
///
/// <para><b>Gate behaviour: severity-driven, not blocking on every finding.</b>
/// ast-grep rules carry a severity (<c>error</c>, <c>warning</c>,
/// <c>info</c>, <c>hint</c>); only <c>error</c> diagnostics map to
/// <see cref="AuditSeverity.Error"/> and fail the audit — the same semantics
/// ast-grep itself encodes in its exit code. Warnings and below are
/// advisory. To make every match blocking, set <c>severity: error</c> in the
/// rules or pass a bare <c>--error</c> via <c>ExtraArguments</c> (no RULE_ID
/// raises all rules to error); <c>MinimumSeverity</c> only drops findings,
/// it never raises them. One deliberate exception: bare
/// <c>ast-grep-ignore</c> comments (suppress-everything directives the audit
/// subject can write) are reported as <see cref="AuditSeverity.Error"/>
/// findings by default via <c>--error=no-suppress-all</c>; ast-grep has no
/// flag to disable suppression outright, so rule-scoped suppressions
/// (<c>ast-grep-ignore: rule-id</c>) remain honored — operators who also
/// want to allow suppress-alls override with
/// <c>--warning=no-suppress-all</c> or <c>--off=no-suppress-all</c> in
/// <c>ExtraArguments</c>.</para>
///
/// <para><b>Exit-code convention (verified against ast-grep 0.45.3
/// source).</b> ast-grep does NOT follow the common "1 = findings,
/// 2 = could not run" convention cleanly: a completed scan exits <c>0</c>
/// when no error-severity diagnostic fired — even when warning/info/hint
/// findings exist — and exits <c>1</c> when at least one <c>error</c>
/// diagnostic fired. But <c>1</c> is also the fallback exit for
/// unclassified anyhow failures, which print <c>Error: …</c> to stderr and
/// write no JSON. The discriminator is therefore the report itself, not the
/// exit code: exits <c>0</c> and <c>1</c> are findings-producing, and an
/// exit without a parseable JSON array on stdout fails closed as
/// infrastructure through the parser. Every other exit is a typed failure —
/// <c>2</c> clap usage error, <c>3</c> no project/rules (including a missing
/// <c>sgconfig.yml</c>), <c>6</c> config/rule read failure, <c>8</c> config
/// or rule parse failure, <c>9</c> glob errors — all infrastructure.
/// <c>126</c>/<c>127</c> (cannot execute / not found) are infrastructure via
/// the base.</para>
///
/// <para><b>Version pin.</b> Rule evaluation and the JSON report shape
/// change between releases, so findings are only meaningful from the build
/// the auditor was verified against. The auditor probes
/// <c>ast-grep --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding. The probe runs with an explicitly pinned
/// empty project config (<c>--config /dev/null</c>): ast-grep resolves the
/// project config — and dlopens its <c>customLanguages</c> — for EVERY
/// invocation, before argument dispatch, so a bare <c>--version</c> run in
/// the worktree would already execute repository-controlled native code
/// ahead of the <c>sgconfig</c> gate.</para>
///
/// <para><b>Rules source — and the trust boundary it crosses.</b>
/// <c>scan</c> requires a ruleset: the audited repository's
/// <c>sgconfig.yml</c>/<c>sgconfig.yaml</c> + <c>ruleDirs</c> by default (the
/// project's own structural-lint contract, like an ESLint flat config), an
/// operator-pinned project config via <c>ConfigFile</c> (<c>-c</c>), or a
/// single operator-pinned rule file via <c>RuleFile</c> (<c>-r</c>). With
/// none of these the scan cannot run and fails closed as infrastructure. A
/// repo-authored ruleset is the meaningful check for a structural-pattern
/// auditor — but a repo-root <c>sgconfig</c> is more than rules: its
/// <c>customLanguages</c>/<c>libraryPath</c> entries are resolved relative
/// to the config file (or absolute) and <b>dlopen'd inside the ast-grep
/// process</b>. That is arbitrary native code execution supplied by the
/// audited repository, and the loaded code can write a clean report to
/// stdout — forged gate evidence. So by default a repo-root
/// <c>sgconfig</c> that declares dynamic-load keys fails closed as
/// deterministic infrastructure; the opt-ins are an operator-pinned
/// <c>ConfigFile</c> (repo discovery is replaced entirely) or the explicit
/// <c>TrustRepositoryCustomLanguages</c> consent flag. A repo
/// <c>sgconfig</c> without dynamic loading is used as-is: rules, rule
/// severity, and <c>util</c> definitions are the project's own
/// structural-lint contract. Config discovery also walks the worktree's
/// ancestor directories — an ancestor <c>sgconfig</c> is baseline-owned
/// (the operator controls the sandbox filesystem), not
/// repository-controlled.</para>
///
/// <para><b>Repository-controlled suppression surfaces.</b> The audit
/// subject writes the repository, and ast-grep honors two surfaces authored
/// inside it: the walker skips files matched by <c>.gitignore</c>,
/// <c>.ignore</c>, parent-directory and global ignore files, and skips
/// hidden (dot) paths entirely — a repo <c>.ignore</c> containing
/// <c>src/**</c>, or code hidden under a dot-directory, would silently
/// produce an empty clean report. And <c>ast-grep-ignore: rule-id</c>
/// comments suppress specific findings with no trace in the report. By
/// default the scan neutralizes both: it passes <c>--no-ignore</c> for all
/// six ignore classes (hidden, dot, exclude, global, parent, vcs) so ignore
/// rules cannot hide code from the crawl, and a bounded pre-scan probe maps
/// every file containing an <c>ast-grep-ignore</c> marker into a visible
/// Warning finding so suppressed diagnostics are never invisible.
/// <c>TrustRepositorySuppression</c> opts back in to the repository's own
/// suppression surfaces — it drops the <c>--no-ignore</c> flags and the
/// marker probe. Because <c>--no-ignore</c> also walks gitignored build
/// output and vendored trees, the <c>ExcludePaths</c> defaults double as
/// the crawl boundary via <c>--globs</c> exclusion patterns.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is the whole work tree
/// (<c>.</c>): ast-grep walks only files whose extension maps to a language
/// its rules target. Findings under vendored (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) and generated (<c>dist/</c>,
/// <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes plus VCS internals
/// (<c>.git/</c>) are dropped by default — problems there describe upstream
/// packages, build output, or repository metadata, not the change under
/// audit — and the same prefixes keep those trees out of the crawl via
/// <c>--globs</c>. ast-grep reports a missing scan target as an exit-0
/// empty report rather than an error — a silent pass on a typo'd path — so
/// configured <c>Targets</c> are probed for existence before the scan and a
/// missing one fails closed as deterministic infrastructure; targets are
/// rejected when any ancestor component is a symlink, since a repo-committed
/// link would let the crawl escape the worktree. The marker sweep itself is
/// fail-closed: a truncated or over-cap sweep is infrastructure, never a
/// quiet "no suppressions".</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ast-grep Structural Patterns",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "ast-grep",
    InstallHint = "provision the pinned ast-grep release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "ast-grep — via npm (@ast-grep/cli@" + DefaultExpectedVersion + "), cargo, or the "
        + "versioned upstream GitHub release binary through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class AstGrepAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.ast-grep";

    /// <summary>
    /// ast-grep release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.45.3";

    /// <summary>
    /// Scoped-config key for an explicit ast-grep project config path
    /// (<c>-c/--config</c>). Use it to pin an operator-owned
    /// <c>sgconfig.yml</c> provisioned into the baseline, instead of the
    /// audited repository's — pinning it also replaces the repository's
    /// config entirely, so the <c>customLanguages</c> gate does not apply.
    /// Conflicts with <see cref="RuleFileKey"/>. Unset → ast-grep's default
    /// <c>sgconfig.yml</c>/<c>sgconfig.yaml</c> lookup.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for a single rule file (<c>-r/--rule</c>): scans
    /// the targets with just that rule. Conflicts with
    /// <see cref="ConfigFileKey"/>.
    /// </summary>
    public const string RuleFileKey = "RuleFile";

    /// <summary>
    /// Scoped-config key for scan targets (comma-separated
    /// repository-relative files/folders, positional args). Unset →
    /// <c>.</c> (the whole work tree). Entries must exist — ast-grep answers
    /// a missing target with an empty report instead of an error, so absent
    /// paths are rejected deterministically before the scan — and must not
    /// resolve through a symlink at any ancestor component, which would let
    /// the repository redirect the scan root outside the worktree.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// walker ignore files (<c>.gitignore</c>/<c>.ignore</c>/parent/global
    /// ignores, hidden-path skipping) and <c>ast-grep-ignore</c> comment
    /// directives. Default false: the audited repo must not be able to
    /// silence the audit. When true the scan drops the
    /// <c>--no-ignore</c> flags, the <c>no-suppress-all</c> escalation, and
    /// the suppression-marker sweep.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Scoped-config key consenting to repository-authored dynamic language
    /// loading: a repo-root <c>sgconfig.yml</c>/<c>sgconfig.yaml</c> with
    /// <c>customLanguages</c>/<c>libraryPath</c> entries dlopens a
    /// repository-pathed native library inside the ast-grep process — code
    /// execution the audit subject controls, which can also write a clean
    /// report and forge a pass. Default false: such a config fails closed as
    /// infrastructure. Set true only when the repository's sgconfig is
    /// already trusted to run code; pinning an operator-owned
    /// <see cref="ConfigFileKey"/> is the stronger option.
    /// </summary>
    internal const string TrustRepositoryCustomLanguagesKey = "TrustRepositoryCustomLanguages";

    /// <summary>
    /// ast-grep's built-in rule that reports bare <c>ast-grep-ignore</c>
    /// suppress-everything comments. Off by default upstream; the auditor
    /// raises it to error so the audit subject cannot blanket-silence a
    /// node without a finding.
    /// </summary>
    internal const string NoSuppressAllRuleId = "no-suppress-all";

    /// <summary>
    /// Synthesized rule id for suppression-site visibility findings: one per
    /// file that contains an <c>ast-grep-ignore</c> marker, so rule-scoped
    /// suppressions — which ast-grep honors silently — are still auditable.
    /// </summary>
    internal const string SuppressionSiteRuleId = "ast-grep/suppression-site";

    // The project config names ast-grep auto-loads when no -c/--config is
    // given, in per-directory precedence order (sgconfig.yml wins over
    // sgconfig.yaml). Discovery also walks the worktree's ancestors; only
    // the worktree-root files are repository-controlled, ancestors are
    // baseline-owned.
    private static readonly string[] RepositoryProjectConfigNames = ["sgconfig.yml", "sgconfig.yaml"];

    // Comment marker both suppression forms share: bare "ast-grep-ignore" and
    // rule-scoped "ast-grep-ignore: rule-id".
    private const string SuppressionMarker = "ast-grep-ignore";

    // Empty project config for the --version probe: sgconfig discovery is
    // skipped entirely when -c/--config is present in raw argv, so the probe
    // can never reach a repository-controlled config — ast-grep loads the
    // project config (and dlopens customLanguages) before argument dispatch,
    // even for --version. /dev/null exists on every POSIX sandbox and reads
    // as empty: whether it parses as an empty config or fails parsing, no
    // customLanguages register and the version still prints (clap's
    // --version short-circuits before the project result is consulted).
    private const string EmptyProbeConfig = "/dev/null";

    // YAML keys in sgconfig that register a dlopen'd tree-sitter library:
    // customLanguages is the top-level map; libraryPath carries the library
    // path inside it. Matched word-bounded anywhere in the file text with no
    // colon requirement — serde_yaml accepts flow-style documents
    // ('{customLanguages: ...}'), quoted keys, and '? key' explicit-key form
    // where the colon sits on a following line. A word character (letter,
    // digit, '_') adjacent to the key disqualifies the match; anything else
    // that greps — a comment or string mentioning the word — fails closed,
    // which is the safe direction.
    private const string DynamicLanguageKeyPattern =
        @"(^|[^[:alnum:]_])['""]?(customLanguages|libraryPath)['""]?([^[:alnum:]_]|$)";

    // One visibility finding per marker-bearing file, bounded independently
    // of the report cap. Exceeding it — like a truncated sweep — fails
    // closed: a partial map of suppression sites is indistinguishable from
    // full coverage in the report.
    private const int MaxSuppressionSiteFindings = 200;

    // Every ignore class ast-grep's --no-ignore enum accepts: repository
    // ignore files must never shrink the audited surface, and hidden paths
    // are code too.
    private static readonly string[] AllIgnoreClasses =
        ["hidden", "dot", "exclude", "global", "parent", "vcs"];

    private static ExternalToolAuditorOptions CreateDefaults() => new()
    {
        // 0 = scan completed, no error-severity diagnostics (warnings and
        // below may still be in the report); 1 = error diagnostics found —
        // OR an unclassified anyhow failure, which emits no JSON report.
        // The array on stdout is the discriminator: exit 1 without a
        // parseable report fails closed through the parser. Typed failures
        // (2 usage, 3 no project/rules, 6 read, 8 parse, 9 glob, …) and
        // 126/127 are infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in VCS internals, vendored/dependency trees and generated
        // build output describe code that is not the change under audit —
        // noise that trains operators to ignore the auditor. Under
        // --no-ignore nothing keeps such trees out of the crawl, so each
        // entry is also passed to ast-grep as a --globs exclusion. Operators
        // re-include a path by overriding ExcludePaths in scoped config.
        ExcludePaths = [".git/", "vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = CreateDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configFile = static () => null;
    private Func<string?> _ruleFile = static () => null;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _trustRepositorySuppression = static () => false;
    private Func<bool> _trustRepositoryCustomLanguages = static () => false;

    /// <summary>
    /// Gate-relevant scoped config resolved once per run, when argv is built
    /// — <see cref="BuildToolArguments"/> stores it and
    /// <see cref="VerifyToolAsync"/> (which the base invokes afterwards in the
    /// same logical flow) reads the same values, so a mid-run scoped-config
    /// reload can never make the gates disagree with the argv already built.
    /// </summary>
    private sealed record RunGateContext(
        IReadOnlyList<string> Targets,
        string? ConfigFile,
        bool TrustRepositorySuppression,
        bool TrustRepositoryCustomLanguages);

    // Plugin auditors are DI singletons shared by concurrent workers, so
    // per-run state flows through AsyncLocal — an instance field would let
    // one run's snapshot bleed into another's gates or report. Established
    // in-repo pattern (WorkSandboxContext, PipelineRunner ambient lifecycle).
    private readonly AsyncLocal<RunGateContext?> _runGateContext = new();

    /// <inheritdoc />
    public override string Name => "codeybox:ast-grep";

    /// <inheritdoc />
    protected override string ToolName => "ast-grep";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new AstGrepJsonOutputParser();

    /// <summary>
    /// Declared mapping from ast-grep's severity vocabulary to
    /// <see cref="AuditSeverity"/>. <c>error</c> rules are blocking;
    /// <c>warning</c> is advisory-warning; <c>info</c>, <c>hint</c>
    /// (ast-grep's default rule severity) and <c>off</c> are informational.
    /// Unknown levels from a foreign build map to <see
    /// cref="AuditSeverity.Warning"/> rather than passing raw tokens through.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["off"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        // --config points ast-grep's pre-dispatch project setup at an empty
        // file so repository sgconfig discovery — and its customLanguages
        // dlopen — can never fire inside a mere version probe (see
        // EmptyProbeConfig).
        new(PluginId, _expectedVersion, DefaultExpectedVersion,
            ["--version", "--config", EmptyProbeConfig]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var configFile = _configFile();
        var ruleFile = _ruleFile();
        var targets = ResolveTargets();
        // Freeze the gate inputs alongside the argv build: VerifyToolAsync
        // must probe exactly what this argv bakes, even if scoped config
        // reloads between the two calls.
        var gate = new RunGateContext(
            targets, configFile, _trustRepositorySuppression(), _trustRepositoryCustomLanguages());
        _runGateContext.Value = gate;
        if (!string.IsNullOrWhiteSpace(configFile) && !string.IsNullOrWhiteSpace(ruleFile))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has both ConfigFile and RuleFile set — ast-grep "
                + $"rejects -c/--config together with -r/--rule. Set one under "
                + $"CodeyBox:Plugins:{PluginId}.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            "scan",
            // clap requires the '=' form for --json's optional STYLE value.
            "--json=compact",
        };

        // Repository-authored ignore files (.gitignore, .ignore, parent and
        // global ignores), hidden-path skipping, and ast-grep-ignore comment
        // directives would let the audit subject hide code or findings — an
        // empty report would read as a pass. All six --no-ignore classes are
        // emitted unconditionally: an ExtraArguments --no-ignore is additive
        // (clap appends), and the deliberate opt-out is
        // TrustRepositorySuppression.
        if (!gate.TrustRepositorySuppression)
        {
            // Surface suppress-everything comments as blocking findings;
            // ast-grep has no flag to disable suppression outright, and a
            // bare ast-grep-ignore silences every rule on its node.
            args.Add("--error=" + NoSuppressAllRuleId);
            foreach (var ignoreClass in AllIgnoreClasses)
                args.Add("--no-ignore=" + ignoreClass);
        }

        // Keep excluded trees out of the crawl entirely — under --no-ignore
        // nothing else keeps gitignored vendored/generated output out, and
        // the finding-level ExcludePaths filter only fires after the match.
        // Only directory-prefix entries (trailing '/') become globs: an exact
        // entry like "vendor" is a path match in the base filter, but as a
        // gitignore-style glob it would also prune a vendor/ subtree —
        // silently widening the exclusion. Entries carrying glob
        // metacharacters are likewise kept at the findings level only.
        foreach (var entry in options.ExcludePaths)
        {
            var pattern = ToCrawlExclusionGlob(entry);
            if (pattern is not null)
            {
                args.Add("--globs");
                args.Add(pattern);
            }
        }

        // The --config emission and the customLanguages gate must agree on
        // what ast-grep's pre-dispatch config scan honors: only the exact
        // -c/--config spellings (see ExtraArgumentsPinProjectConfig). A
        // joined "-cFILE" in ExtraArguments therefore does NOT suppress our
        // --config — the pinned file must still reach argv as a spelling
        // ast-grep will honor, or repository discovery would stay live.
        if (!string.IsNullOrWhiteSpace(configFile)
            && !ExtraArgumentsPinProjectConfig(options))
        {
            args.Add("--config");
            args.Add(configFile.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(ruleFile)
            && !ExtraArgumentsSupplyFlag(options, "--rule", "-r"))
        {
            args.Add("--rule");
            args.Add(ruleFile.Trim());
        }

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
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, CreateDefaults());
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configFile = () => scoped[ConfigFileKey];
        _ruleFile = () => scoped[RuleFileKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        _trustRepositoryCustomLanguages = () =>
            bool.TryParse(scoped[TrustRepositoryCustomLanguagesKey], out var trust) && trust;
        context.Logger.LogInformation(
            "AstGrepAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// ast-grep-specific preconditions on the live path, each fail-closed as
    /// infrastructure before the scan runs:
    /// a repository <c>sgconfig.yml</c>/<c>sgconfig.yaml</c> that declares
    /// <c>customLanguages</c>/<c>libraryPath</c> (native code the audited
    /// repository would execute inside the scanner process — and which could
    /// write a clean report, forging a pass); configured <c>Targets</c> that
    /// do not exist in the worktree or resolve through symlinks (ast-grep's
    /// silent-pass trap / a scan root escaping the worktree); and the
    /// suppression-marker sweep, returned as supplemental findings so
    /// silently-honored <c>ast-grep-ignore</c> directives stay visible.
    /// Every gate input comes from the run-scoped snapshot built with argv
    /// (<see cref="RunGateContext"/>), never re-read from live config.
    /// </summary>
    protected override async Task<IReadOnlyList<ExternalToolFinding>> VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // RunAsync always builds argv (BuildToolArguments) before this hook,
        // so the snapshot exists; resolving again covers only a caller that
        // skipped argv construction.
        var gate = _runGateContext.Value ?? ResolveRunGateContext();

        if (!gate.TrustRepositoryCustomLanguages
            && !OperatorPinnedProjectConfig(gate, options))
            await ThrowIfRepoConfigLoadsNativeCodeAsync(sandbox, workingDirectory, tool, options, ct)
                .ConfigureAwait(false);

        if (gate.Targets.Count > 0)
            await VerifyScanTargetsAsync(sandbox, workingDirectory, tool, gate.Targets, options, ct)
                .ConfigureAwait(false);

        if (gate.TrustRepositorySuppression)
            return [];
        return await FindSuppressionSitesAsync(
            sandbox, workingDirectory, tool, gate.Targets, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when an operator-pinned <c>-c/--config</c> (via
    /// <c>ConfigFile</c> or <c>ExtraArguments</c>) replaces ast-grep's
    /// sgconfig discovery: the repository's own project config is then not
    /// loaded at all. A set <c>ConfigFile</c> counts because
    /// <see cref="BuildToolArguments"/> emits it as a recognized
    /// <c>--config</c> whenever ExtraArguments don't already supply one —
    /// the emitted argv and this credit are the same decision. Not
    /// exempted: <c>-r/--rule</c> and <c>--inline-rules</c>, which need no
    /// project but still leave project-config discovery — and therefore a
    /// repo sgconfig's customLanguages — reachable.
    /// </summary>
    private static bool OperatorPinnedProjectConfig(RunGateContext gate, ExternalToolAuditorOptions options)
        => !string.IsNullOrWhiteSpace(gate.ConfigFile)
            || ExtraArgumentsPinProjectConfig(options);

    /// <summary>
    /// True when <c>ExtraArguments</c> carries a config flag spelled the way
    /// ast-grep's own pre-dispatch raw-argv scan recognizes: <c>-c VALUE</c>,
    /// <c>-c=VALUE</c>, <c>--config VALUE</c>, <c>--config=VALUE</c>. clap's
    /// joined short form (<c>-cFILE</c>) is parsed by clap but NOT by that
    /// scan, so it must not count — repository discovery would still fire.
    /// </summary>
    private static bool ExtraArgumentsPinProjectConfig(ExternalToolAuditorOptions options)
        => options.ExtraArguments.Any(static arg =>
            string.Equals(arg, "-c", StringComparison.Ordinal)
            || string.Equals(arg, "--config", StringComparison.Ordinal)
            || arg.StartsWith("--config=", StringComparison.Ordinal)
            || arg.StartsWith("-c=", StringComparison.Ordinal));

    private RunGateContext ResolveRunGateContext() => new(
        ResolveTargets(),
        _configFile(),
        _trustRepositorySuppression(),
        _trustRepositoryCustomLanguages());

    /// <summary>
    /// One bounded precondition exec: wraps <see
    /// cref="ExecToolBoundedAsync"/> with the shared probe output caps and
    /// converts an unavailable exec transport into
    /// <see cref="AuditUnavailableException"/>. Callers keep only their own
    /// exit-code classification.
    /// </summary>
    private static async Task<SandboxExecResult> ExecProbeOrThrowAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        string operation,
        IReadOnlyList<string> argv,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            operation,
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            timeout,
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' {operation} could not run: the sandbox exec "
                + "transport was unavailable.");
        return result;
    }

    private async Task ThrowIfRepoConfigLoadsNativeCodeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, "repository config check",
            RepositoryProjectConfigNames, RepositoryFileProbe.Present,
            options, ct).ConfigureAwait(false);
        if (present.Count == 0)
            return;

        // The names are probed in ast-grep's own precedence order, so the
        // first present entry is the config ast-grep would actually load
        // (sgconfig.yml wins over sgconfig.yaml at the same directory).
        var config = present[0];

        // The sgconfig is the audited repository's own file; grep -q answers
        // the one question that matters — does it register a dlopen'd
        // language library — without echoing file contents anywhere.
        var probe = await ExecProbeOrThrowAsync(
            sandbox, workingDirectory, tool, "repository config check",
            ["grep", "-qEe", DynamicLanguageKeyPattern, "--", "./" + config],
            ProbeTimeout(options), ct).ConfigureAwait(false);

        if (probe.ExitCode == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: the audited repository's '{config}' declares "
                + "customLanguages/libraryPath — ast-grep resolves that library path relative to the "
                + "config and loads it into the scan process (dlopen), so repository-committed native "
                + "code would run inside the auditor and could write a clean report to forge a pass. "
                + $"Remove the dynamic-language entries, pin an operator-owned config via "
                + $"CodeyBox:Plugins:{PluginId}:{ConfigFileKey}, or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositoryCustomLanguagesKey} to true to consent "
                + "to repository-controlled code execution in the audit sandbox.")
            { IsDeterministic = true };
        if (probe.ExitCode != 1)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' repository config check could not inspect "
                + $"'{config}' (exit {probe.ExitCode}) — a failed probe is "
                + "infrastructure, not evidence that dynamic-language entries are absent.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
    }

    /// <summary>
    /// Configured <c>Targets</c> must exist in the worktree (ast-grep's
    /// silent-pass trap answers a missing one with an empty report) and must
    /// not reach the crawl through a symlink — <c>[ -L ]</c> tests only the
    /// final path component, so every ancestor component of each target is
    /// probed too: a repo-committed link at <c>a</c> would otherwise let
    /// <c>a/b</c> escape the worktree.
    /// </summary>
    private async Task VerifyScanTargetsAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> targets,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, "target check", targets,
            RepositoryFileProbe.Present, options, ct).ConfigureAwait(false);
        var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
        var missing = targets.Where(t => !presentSet.Contains(t)).ToList();
        if (missing.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has Targets that do not exist in the worktree "
                + $"('{TruncateForMessage(string.Join(' ', missing))}') — ast-grep would report them as "
                + $"an empty pass instead of an error. Fix CodeyBox:Plugins:{PluginId}:{TargetsKey}.")
            { IsDeterministic = true };

        // A symlinked component is followed by ast-grep even without
        // --follow: a repo-committed link would redirect the scan root
        // outside the worktree and leak foreign paths into the report.
        var components = targets
            .SelectMany(SelfAndAncestors)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var linked = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, "target check", components,
            RepositoryFileProbe.Symlinked, options, ct).ConfigureAwait(false);
        if (linked.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has Targets that resolve through repository "
                + $"symlinks ('{TruncateForMessage(string.Join(' ', linked))}') — ast-grep follows a "
                + "symlinked scan root, so the target could escape the worktree. Fix "
                + $"CodeyBox:Plugins:{PluginId}:{TargetsKey}.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Every component of a repository-relative path — <c>a/b/c</c> probes
    /// <c>a</c>, <c>a/b</c>, <c>a/b/c</c> — because <c>[ -L ]</c> only tests
    /// the final component while ancestors are followed silently. Targets
    /// are already validated free of <c>..</c>, leading <c>/</c>, and
    /// backslashes, so plain <c>/</c> splitting is exact.
    /// </summary>
    private static IEnumerable<string> SelfAndAncestors(string path)
    {
        var index = 0;
        while ((index = path.IndexOf('/', index)) >= 0)
            yield return path[..index++];
        yield return path;
    }

    /// <summary>
    /// Maps every scanned file carrying an <c>ast-grep-ignore</c> marker into
    /// a supplemental Warning finding: ast-grep honors rule-scoped
    /// suppressions silently, so without this sweep a "clean" report is
    /// indistinguishable from a suppressed one. Runs as a bounded
    /// <c>grep -l</c> over the same targets the scan will walk —
    /// <c>-a</c> so files grep would classify as binary are still swept,
    /// matching ast-grep's tolerant parser. Any truncation or a cap
    /// overflow fails closed: partial coverage must never read as "no
    /// suppressions". Skipped under
    /// <see cref="TrustRepositorySuppressionKey"/>.
    /// </summary>
    private async Task<IReadOnlyList<ExternalToolFinding>> FindSuppressionSitesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> targets,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var scanRoots = targets.Count > 0 ? targets : ["."];
        IReadOnlyList<string> argv = ["grep", "-rlae", SuppressionMarker, "--", .. scanRoots];

        // Not a liveness probe — it sweeps the same tree the scan walks, so
        // it shares the scan's timeout budget rather than the probe cap.
        var result = await ExecProbeOrThrowAsync(
            sandbox, workingDirectory, tool, "suppression-marker check", argv,
            EffectiveTimeout(options), ct).ConfigureAwait(false);

        if (result.StdoutLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression-marker check exceeded its output "
                + "bound — a truncated sweep is infrastructure, not evidence that no suppression "
                + "directives exist.")
            { IsDeterministic = true };
        if (result.ExitCode == 1)
            return [];
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression-marker check could not inspect the "
                + $"scan targets (exit {result.ExitCode}) — a failed sweep is infrastructure, not "
                + "evidence that no suppression directives exist.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        var findings = new List<ExternalToolFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in lines)
        {
            var path = raw.StartsWith("./", StringComparison.Ordinal) ? raw[2..] : raw;
            if (path.Length == 0 || !seen.Add(path))
                continue;
            findings.Add(new ExternalToolFinding(
                SeverityLevel: "warning",
                RuleId: SuppressionSiteRuleId,
                Message: "file contains ast-grep-ignore suppression directive(s); diagnostics it "
                    + "suppresses never reach this report",
                Path: path));
            if (findings.Count >= MaxSuppressionSiteFindings)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{tool}' suppression-marker check found more than "
                    + $"{MaxSuppressionSiteFindings} files carrying '{SuppressionMarker}' markers — "
                    + "beyond the reporting bound the sweep can no longer guarantee every suppressed "
                    + "diagnostic stays visible. Narrow the scan via "
                    + $"CodeyBox:Plugins:{PluginId}:{TargetsKey}, or consent to repository "
                    + $"suppression via CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey}.")
                { IsDeterministic = true };
        }
        return findings;
    }

    private List<string> ResolveTargets()
    {
        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();

        // Shape validation runs here — before the entries are baked into
        // argv — so the scan can never receive an unvalidated target: a
        // leading '-' entry would be parsed by clap as an option rather than
        // scanned, and an absolute/parent-escaping path leaves the worktree.
        var invalid = targets
            .Where(static t =>
                t.StartsWith('-')
                || t.StartsWith('/')
                || t.Contains('\\')
                || t.IndexOf('\n') >= 0
                || t.Split('/').Contains("..", StringComparer.Ordinal))
            .ToList();
        if (invalid.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with Targets entries that are not "
                + $"repository-relative paths inside the worktree ('{TruncateForMessage(string.Join(' ', invalid))}'). "
                + $"Fix CodeyBox:Plugins:{PluginId}:{TargetsKey}.")
            { IsDeterministic = true };
        return targets;
    }

    /// <summary>
    /// Translates a directory-prefix <see
    /// cref="ExternalToolAuditorOptions.ExcludePaths"/> entry (trailing
    /// <c>/</c>) into a <c>--globs</c> exclusion for the crawl, or null when
    /// the entry cannot be expressed without widening it: exact-path entries
    /// (no trailing slash — a bare <c>vendor</c> glob would also prune a
    /// vendor/ subtree) and entries carrying glob metacharacters keep their
    /// findings-level filtering only.
    /// </summary>
    private static string? ToCrawlExclusionGlob(string? entry)
    {
        var normalized = NormalizeExcludePathEntry(entry);
        if (normalized is null || !normalized.EndsWith('/'))
            return null;
        if (normalized.IndexOfAny(GlobMetacharacters) >= 0)
            return null;
        return "!" + normalized + "**";
    }

    private static readonly char[] GlobMetacharacters = ['*', '?', '[', ']', '{', '}', '!', '\\'];
}
