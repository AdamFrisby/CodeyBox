using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.DetektAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>detekt</c> (Kotlin static analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (detekt's built-in
/// SARIF emitter streamed on stderr via <c>--report sarif:/dev/stderr</c>,
/// parsed by <see cref="DetektSarifOutputParser"/> — a thin stream adapter over
/// the shared <see cref="SarifToolOutputParser"/>, no new format), the pinned
/// tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the config-trust / exclusion posture below.
///
/// <para><b>Gate behaviour: blocking on findings by default — the tool's own
/// semantics, stated plainly.</b> detekt assigns severity <c>error</c> to every
/// finding whose rule or ruleset does not declare one (detekt docs: "the
/// default severity is error"), and its own gate fails the build on any issue.
/// The declared map sends SARIF <c>error</c> to <see cref="AuditSeverity.Error"/>,
/// so under the stock detekt configuration ANY reported finding fails the
/// audit. Operators who want advisory findings lower them in the detekt config
/// (<c>severity: warning</c>/<c>info</c> per rule or ruleset — the designed
/// mechanism) or raise <c>MinimumSeverity</c>/use <c>ExcludedRules</c>;
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (read from the 1.23.8 source — NOT the common
/// "0 clean / 1 findings / 2 error" table; detekt inverts 1 and 2).</b>
/// <c>AnalysisResult.exitCode()</c> maps <c>UnexpectedError → 1</c> (crashes
/// and CLI argument violations alike), <c>IssuesFound → 2</c> (ran, issue count
/// over <c>build.maxIssues</c> — 0 in the default config, i.e. any finding),
/// <c>InvalidConfig → 3</c>, success <c>→ 0</c>. So <c>0</c> and <c>2</c> are
/// the findings-producing verdicts — both carry the SARIF report, since
/// issues under a raised <c>maxIssues</c> threshold still exit 0 with findings
/// in the report. Exit <c>1</c> is "the tool could not run" (the common
/// convention would misread it as findings — it is not), <c>3</c> is a bad
/// config, <c>126</c>/<c>127</c> are missing/not-executable — all
/// infrastructure. Anything else is an unknown convention and fails loudly as
/// infrastructure rather than being guessed.</para>
///
/// <para><b>Why stderr.</b> detekt routes console reports (the active-by-default
/// <c>LiteFindingsReport</c>) and the <c>IssuesFound</c> message
/// (<c>println(error.message)</c> in <c>Main.kt</c>, not suppressible) to
/// stdout — <c>sarif:/dev/stdout</c> would interleave the report with human
/// output exactly on the findings path. stderr stays clean on verdict exits:
/// only <c>settings.error()</c> writes there, and those paths abort the run to
/// exit 1 anyway. A report written to <c>/dev/stderr</c> is therefore the only
/// channel that arrives unpolluted; stderr noise on a 0/2 exit fails the parse
/// closed as infrastructure, never silently clean.</para>
///
/// <para><b>Version pin.</b> A scanner's rules change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>detekt --version</c> before the scan; a
/// missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-authored configuration.</b> Unlike the Gradle plugin,
/// the detekt CLI does not auto-discover a repo <c>detekt.yml</c> — the
/// auditor probes the conventional locations and passes one explicitly when
/// <c>TrustRepositoryConfig</c> is true (the default): linting against the
/// project's own lint contract is the meaningful check — and the load is
/// announced at info level so the gate posture is auditable. The contract is
/// executable configuration: a repo-authored config can deactivate rules or
/// lower severities, and an <c>output-reports</c> exclude of
/// <c>SarifOutputReport</c> would stop the report — which fails closed as
/// infrastructure, never a pass. Operators who need a fully operator-owned
/// run set <c>TrustRepositoryConfig</c> to <c>false</c> and pin a config via
/// <c>ConfigPath</c> (which must live outside the audited tree). Repo
/// <c>detekt-baseline.xml</c> files are never loaded: baselines are only read
/// via <c>--baseline</c>, which the scan never passes and operators may add
/// deliberately through <c>ExtraArguments</c>.</para>
///
/// <para><b>Scope and limits.</b> The scan is
/// <c>detekt --input . --base-path . --report sarif:/dev/stderr</c>: the
/// whole audited tree, every <c>.kt</c>/<c>.kts</c>. <c>--base-path .</c> is
/// load-bearing: detekt only records a path relative to the base path when
/// one is given (<c>CliArgs.basePath</c> → <c>KtCompiler.createKtFile</c>'s
/// <c>RELATIVE_PATH</c>); without it the SARIF emitter (<c>Results.kt</c>)
/// writes absolute <c>file:///…</c> URIs, producing host-shaped finding
/// locations and paths the repository-relative <c>ExcludePaths</c> prefixes
/// could never match. Resolved against the scan's working directory,
/// <c>.</c> makes artifact URIs repository-relative wherever the sandbox
/// runs the tool. detekt CLI runs light analysis (no type resolution) —
/// rules annotated <c>@RequiresTypeResolution</c> do not run without a
/// <c>--classpath</c> the operator can pass via <c>ExtraArguments</c>. A tree
/// with no Kotlin sources is a clean pass, not an error. Findings under
/// vendored and generated-output prefixes are dropped by the default
/// <c>ExcludePaths</c>; operators re-include by overriding it. The scan
/// writes nothing into the audited tree.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: detekt Kotlin Analyser",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "detekt",
    InstallHint = "provision the pinned detekt CLI release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — the "
        + "detekt-cli distribution from https://github.com/detekt/detekt/releases — via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions; "
        + "no distro apt package carries a version pin")]
// The detekt CLI launcher is a shell script that execs java — the runtime is
// a real dependency of the scan, so it is declared and verified (and
// apt-installed) alongside the launcher rather than left implicit.
[CodeyBoxPluginRequiresTool(
    "java",
    AptPackage = "default-jre-headless",
    InstallHint = "the detekt CLI launcher execs a JVM — install a Java runtime "
        + "(the declared apt package covers it) or provision a JDK/JRE and set JAVA_HOME")]
public sealed class DetektAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.detekt";

    /// <summary>
    /// detekt release the invocation and its findings are pinned to — the
    /// latest stable 1.x line; 2.x is still alpha. Operators running a
    /// different pinned build set <c>ExpectedVersion</c> in the plugin's
    /// scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.23.8";

    /// <summary>
    /// Scoped-config key for an operator-owned detekt configuration file,
    /// passed as <c>--config</c>. It should live outside the audited
    /// repository: a repo-authored file lets the audit subject shape the gate
    /// (deactivating rules, lowering severities). When set it is the only
    /// config passed — repository config discovery is skipped. Ignored when
    /// <c>ExtraArguments</c> already supplies <c>--config</c>.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting out of repository-authored tool configuration.
    /// Default true: the conventional repo config file (first of
    /// <c>detekt.yml</c>, <c>detekt.yaml</c>, <c>config/detekt/detekt.yml</c>,
    /// <c>config/detekt/detekt.yaml</c>) is discovered and passed as
    /// <c>--config</c>, because linting against the project's own lint
    /// contract is the meaningful check. Set to <c>false</c> so the repo's
    /// config is not loaded at all; pair with <see cref="ConfigPathKey"/>.
    /// </summary>
    internal const string TrustRepositoryConfigKey = "TrustRepositoryConfig";

    /// <summary>Conventional repository locations probed for a detekt config, in precedence order.</summary>
    private static readonly IReadOnlyList<string> RepositoryConfigCandidates =
    [
        "detekt.yml",
        "detekt.yaml",
        "config/detekt/detekt.yml",
        "config/detekt/detekt.yaml",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // detekt's own exitCode() (1.23.8): 0 = clean, 2 = IssuesFound — both
        // carry the SARIF report, so both are verdicts. 1 = UnexpectedError
        // (the inverted convention — argument violations and crashes, not
        // findings), 3 = InvalidConfig: both are infrastructure. 126/127 are
        // handled by the base; anything else fails loudly as infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositoryConfig = static () => true;
    private ILogger _logger = NullLogger.Instance;

    /// <inheritdoc />
    public override string Name => "codeybox:detekt";

    /// <inheritdoc />
    protected override string ToolName => "detekt";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new DetektSarifOutputParser();

    /// <summary>
    /// Declared mapping from detekt's SARIF level vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. detekt's <c>SeverityLevel</c> serializes to
    /// SARIF <c>error</c>/<c>warning</c>/<c>note</c> (<c>none</c> completes the
    /// SARIF vocabulary); the tool's default severity is <c>error</c>, so
    /// unconfigured findings block — mirroring detekt's own gate. Only
    /// <c>error</c>-class levels fail the audit; raw levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
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
            // Whole-tree source analysis: every .kt/.kts under the worktree.
            "--input",
            ".",
        };

        if (!ExtraArgumentsSupplyFlag(options, "--base-path", "-bp"))
        {
            // detekt records a file's path relative to the base path only when
            // one is given; with none, its SARIF emitter writes absolute
            // file:/// URIs and repository-relative ExcludePaths could never
            // match. "." resolves against the scan's working directory — the
            // worktree root as the tool sees it — so artifact URIs come out
            // repository-relative. An operator --base-path wins outright.
            args.Add("--base-path");
            args.Add(".");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--report", "-r"))
        {
            // Machine-readable SARIF 2.1.0 on stderr: console reports and the
            // IssuesFound summary go to stdout, so the report stream stays
            // clean for the parser. An operator --report replaces the report
            // the parser reads and fails the run closed as infrastructure.
            args.Add("--report");
            args.Add("sarif:/dev/stderr");
        }

        return args;
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // An operator-supplied --config in ExtraArguments wins outright: the
        // auditor never stacks a second config selection on it.
        if (ExtraArgumentsSupplyFlag(options, "--config", "-c"))
            return [];

        var configured = _configPath();
        if (!string.IsNullOrWhiteSpace(configured))
            return ["--config", configured.Trim()];

        if (!_trustRepositoryConfig())
            return [];

        // The CLI does not auto-discover a repo detekt.yml; probe the
        // conventional names so the project's own lint contract applies.
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, ToolName, RepositoryConfigCandidates, options, ct)
            .ConfigureAwait(false);
        if (present.Count == 0)
            return [];

        // Repo-authored config is executable configuration: announce the load
        // so the audit trail shows the gate ran under the repository's own
        // lint contract rather than silently inheriting it. The path is one
        // of the fixed candidates, never arbitrary input.
        _logger.LogInformation(
            "detekt: loading repository-authored config {ConfigFile} (--config); "
            + "set {TrustKey}=false for a fully operator-owned run",
            present[0], TrustRepositoryConfigKey);
        return ["--config", present[0]];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositoryConfig = () =>
            !bool.TryParse(scoped[TrustRepositoryConfigKey], out var trust) || trust;
        _logger = context.Logger;
        context.Logger.LogInformation(
            "DetektAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
