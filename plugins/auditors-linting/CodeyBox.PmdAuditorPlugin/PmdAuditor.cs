using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PmdAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>pmd check</c> (Java-first, multi-language
/// static analysis) on the shared <see cref="ExternalToolAuditorBase"/>: the
/// base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding
/// identity, and per-auditor configuration. This class adds the report
/// selection (PMD's built-in XML emitter on stdout, parsed by
/// <see cref="PmdXmlOutputParser"/>), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// worktree-relativized report paths via
/// <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync"/>, and
/// the default ruleset / exclusion / suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on
/// every finding.</b> PMD rule priorities 1 (high) and 2 (medium-high) map
/// to <see cref="AuditSeverity.Error"/> and fail the audit; priority 3 maps
/// to <see cref="AuditSeverity.Warning"/> (advisory); priorities 4 and 5
/// map to <see cref="AuditSeverity.Info"/>. This mirrors PMD's own SARIF
/// level derivation (high/medium-high → error, medium → warning,
/// medium-low/low → note), so a rule keeps the same gate meaning whether an
/// operator inspects the XML findings or a SARIF export. With the default
/// <c>rulesets/java/quickstart.xml</c> most findings are advisory; enabling
/// higher-priority rulesets (e.g. category/java/security.xml) is what turns
/// the auditor into a hard gate. <c>MinimumSeverity</c> only drops
/// findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (from the PMD 7 CLI reference — PMD does
/// NOT follow the common "1 = findings" table).</b> <c>0</c> = ran clean;
/// <c>4</c> = ran and found at least one violation (the default
/// <c>--fail-on-violation</c> behaviour is kept deliberately, so "found
/// something" is distinguishable from "ran clean"); both emit the XML
/// report on stdout and are findings-producing verdicts. <c>5</c> = at
/// least one recoverable error (a file failed to parse, or a rule threw) —
/// the report may carry partial violations but coverage is incomplete by
/// PMD's own contract, so it is classified as infrastructure ("could not
/// fully run"), never a pass. <c>1</c> = exception during execution;
/// <c>2</c> = usage error — infrastructure. <c>126</c>/<c>127</c> = cannot
/// execute / not found — infrastructure. Anything else is an unknown
/// convention and fails loudly rather than being guessed. Operators who
/// accept partial analysis pass <c>--no-fail-on-error</c> in
/// <c>ExtraArguments</c>: recoverable errors then exit 0/4 and surface as
/// advisory <c>processing-error</c> findings parsed from the report's
/// <c>&lt;error&gt;</c> elements.</para>
///
/// <para><b>Stream note.</b> The XML report goes to <b>stdout</b>
/// (<c>-f xml</c> with no <c>--report-file</c>); slf4j logs and the
/// progress bar go to stderr — the scan passes <c>--no-progress</c> to keep
/// the captured log stream readable.</para>
///
/// <para><b>Version pin.</b> A scanner's rule implementations change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>pmd --version</c>
/// before the scan; a missing binary, an unrecognised banner, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming
/// the tool — never a pass, never a finding. The banner carries two version
/// tokens (<c>PMD x.y.z</c> and the Java runtime's); the declared
/// <see cref="ToolVersionPin.VersionExtractor"/> reads the
/// <c>PMD</c>-anchored token specifically so the Java runtime version can
/// never satisfy the pin.</para>
///
/// <para><b>Java runtime.</b> <c>pmd</c> is a launcher script that execs
/// <c>java</c>; the plugin declares that runtime as a second tool
/// requirement (<c>default-jre-headless</c> apt package) so baseline
/// provisioning installs it alongside PMD — but only when this plugin is
/// enabled. A missing runtime surfaces as the pinned version probe's
/// infrastructure failure, never a pass.</para>
///
/// <para><b>Repository-controlled suppression.</b> PMD honors
/// <c>// NOPMD</c>-style comment markers and <c>@SuppressWarnings("PMD…")</c>
/// annotations authored inside the audited repository — and the audit
/// subject writes that repository. By default the scan passes
/// <c>--suppress-marker</c> with an unguessable token generated per
/// invocation (a CSPRNG suffix on <see cref="DisabledSuppressMarkerPrefix"/>),
/// which makes the comment marker inert: only a line containing the token
/// would suppress, and repo code cannot contain a token it cannot predict —
/// a fixed token would be forgeable because the audit subject can read this
/// assembly. PMD offers no CLI switch to ignore
/// <c>@SuppressWarnings</c> — that annotation channel stays honored and is
/// documented as a limitation (a rule-level
/// <c>violationSuppressXPath</c>/<c>violationSuppressRegex</c> in an
/// operator-pinned ruleset can further narrow it). Operators who
/// deliberately trust repo-authored suppression set
/// <c>TrustRepositorySuppression</c> in scoped config, restoring PMD's
/// default <c>NOPMD</c> marker. PMD reads no configuration file from the
/// audited tree — the ruleset is always supplied explicitly
/// (<c>-R</c>).</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>pmd check -d . -f xml -R rulesets/java/quickstart.xml -z &lt;worktree&gt;
/// --no-progress --suppress-marker &lt;token&gt;</c>: the whole worktree is
/// collected and PMD's language detection skips anything its rulesets do not
/// cover, so the effective scope is "the ruleset's languages" — Java by
/// default (the curated quickstart ruleset), more via an operator ruleset
/// (<c>RulesetPath</c> or <c>-R</c> in <c>ExtraArguments</c>). The
/// <c>--relativize-paths-with</c> root keeps reported paths
/// worktree-relative — the XML report renders display names (unlike SARIF,
/// which emits absolute <c>file:///</c> URIs), and the argument is the
/// absolute normalized working directory because PMD resolves the
/// relativize root without normalizing it. Findings under vendored and
/// generated prefixes are then dropped by the finding-level
/// <c>ExcludePaths</c> backstop. The scan writes nothing into the audited
/// tree (no <c>--report-file</c>, no <c>--cache</c>).</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: PMD Java/Multi-Language Analyser",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "pmd",
    InstallHint = "provision the pinned PMD 7 release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — download the versioned "
        + "pmd-dist-<version>-bin.zip from https://github.com/pmd/pmd/releases and put its bin/ "
        + "directory on PATH via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions. No distro apt package carries a pinned PMD 7 — Debian's 'pmd' "
        + "package is PMD 6, whose CLI predates the 'check' subcommand this auditor invokes")]
[CodeyBoxPluginRequiresTool(
    "java",
    AptPackage = "default-jre-headless",
    InstallHint = "the pmd launcher execs java — provision any Java 8+ runtime (the declared "
        + "apt package covers this when the plugin is enabled; a leaner JRE such as "
        + "openjdk-17-jre-headless is equally fine)")]
public sealed class PmdAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.pmd";

    /// <summary>
    /// PMD release the invocation and its report shape are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "7.26.0";

    /// <summary>
    /// Ruleset the scan runs when the operator configures none: PMD's
    /// built-in curated Java starter set, resolved from the tool's own
    /// classpath — never from the audited repository.
    /// </summary>
    public const string DefaultRuleset = "rulesets/java/quickstart.xml";

    /// <summary>
    /// Scoped-config key for an operator-chosen ruleset passed as
    /// <c>--rulesets</c>: a built-in resource path (e.g.
    /// <c>rulesets/java/errorprone.xml</c>) or an operator-owned file. A
    /// repo-authored ruleset lets the audit subject redefine the gate —
    /// point this at a path outside the audited tree. Ignored when
    /// <c>ExtraArguments</c> already supplies <c>-R</c>/<c>--rulesets</c>.
    /// </summary>
    public const string RulesetPathKey = "RulesetPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// PMD's <c>// NOPMD</c> comment marker (the default
    /// <c>--suppress-marker</c> is restored). Default false: the audited
    /// repo must not be able to silence findings by comment. The
    /// <c>@SuppressWarnings("PMD…")</c> annotation channel cannot be
    /// disabled from the CLI either way.
    /// </summary>
    public const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Prefix of the per-invocation token passed to <c>--suppress-marker</c>
    /// when repository suppression is not trusted. The full token carries a
    /// random suffix generated at scan-argument build time, so
    /// <c>// NOPMD</c> comments in the audited tree are inert and the
    /// subject cannot pre-embed the token: a fixed compile-time token would
    /// be forgeable — this assembly's source is readable to the subject.
    /// </summary>
    internal const string DisabledSuppressMarkerPrefix = "CODEYBOX-PMD-NOSUPPRESS-";

    // 128 bits of CSPRNG entropy per scan: unguessable, and short enough for
    // one argv token.
    private const int SuppressMarkerEntropyBytes = 16;

    // Version banner line the pin anchors on: `pmd --version` prints the
    // ASCII banner, then "PMD <version> (<commit>, <timestamp>)", then a
    // "Java version: <jre>" line. Only the PMD-anchored token is the tool's.
    private static readonly Regex PmdVersionToken = new(
        @"PMD\s+(\d+\.\d+\.\d+[\w.\-]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // PMD 7 documented convention: 0 = clean; 4 = violations found
        // (default --fail-on-violation kept). 5 = recoverable errors — the
        // report is partial by contract, so it is "could not fully run":
        // infrastructure, not a verdict. 1 = exception, 2 = usage error;
        // anything else fails closed as an unknown convention.
        FindingsExitCodes = new HashSet<int> { 0, 4 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. `target/`/`build/`/`out/`
        // cover Maven/Gradle/IDE generated sources; `generated/` covers
        // committed codegen output. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths =
        [
            "vendor/", "third_party/", "external/", "node_modules/",
            "target/", "build/", "out/", "dist/", "generated/", "coverage/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _rulesetPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:pmd";

    /// <inheritdoc />
    protected override string ToolName => "pmd";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PmdXmlOutputParser();

    /// <summary>
    /// Declared mapping from PMD's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. PMD's XML report carries the rule's
    /// numeric <c>priority</c> (<c>1</c> high … <c>5</c> low); the map aligns
    /// with PMD's own SARIF level derivation (priorities 1–2 → error, 3 →
    /// warning, 4–5 → note) so "high" means what every other auditor's
    /// "high" means. <c>&lt;error&gt;</c>/<c>&lt;configerror&gt;</c> report
    /// elements (partial-coverage signals under
    /// <c>--no-fail-on-error</c>) map to advisory. Raw levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = AuditSeverity.Error,
            ["2"] = AuditSeverity.Error,
            ["3"] = AuditSeverity.Warning,
            ["4"] = AuditSeverity.Info,
            ["5"] = AuditSeverity.Info,
            [PmdXmlOutputParser.ProcessingErrorLevel] = AuditSeverity.Warning,
            [PmdXmlOutputParser.ConfigErrorLevel] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"], ExtractPmdVersion);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string> { "check" };

        // Machine-readable report on stdout for the XML parser. The choice
        // of XML over SARIF is deliberate: PMD's SARIF emitter writes
        // absolute file:/// URIs (FileId.getUriString) that ignore
        // --relativize-paths-with, while the XML report renders display
        // names — the only channel with repo-relative paths. An operator
        // --format override replaces the report the parser expects and the
        // mismatch surfaces loudly as infrastructure.
        if (!ExtraArgumentsSupplyFlag(options, "--format", "-f"))
        {
            args.Add("-f");
            args.Add("xml");
        }

        // Progress bar and logs belong on stderr; keep them there.
        if (!ExtraArgumentsSupplyFlag(options, "--no-progress", "--progress"))
            args.Add("--no-progress");

        // An operator -R/--rulesets in ExtraArguments wins over both the
        // scoped RulesetPath and the built-in default: PMD accepts a
        // repeated --rulesets as additive, so emitting ours alongside the
        // operator's would merge rather than replace.
        if (!ExtraArgumentsSupplyFlag(options, "--rulesets", "-R"))
        {
            var rulesetPath = _rulesetPath();
            args.Add("--rulesets");
            args.Add(string.IsNullOrWhiteSpace(rulesetPath) ? DefaultRuleset : rulesetPath.Trim());
        }

        // The audited repository must not silence the gate: the marker is a
        // fresh unguessable token per invocation, so `// NOPMD` comments are
        // inert and the subject cannot pre-embed the token even though this
        // assembly is readable to it. Operators opt in to repo-authored
        // suppression via TrustRepositorySuppression (or by supplying their
        // own --suppress-marker).
        if (!_trustRepositorySuppression()
            && !ExtraArgumentsSupplyFlag(options, "--suppress-marker"))
        {
            args.Add("--suppress-marker");
            args.Add(NewSuppressMarkerToken());
        }

        // Scan input: the whole worktree, collected with automatic language
        // detection. An operator-supplied --dir/--file-list/--uri replaces
        // this wholesale rather than stacking a duplicate "." scan on top.
        if (!ExtraArgumentsSupplyFlag(options, "--dir", "-d", "--file-list", "--uri", "-u"))
        {
            args.Add("--dir");
            args.Add(".");
        }

        return args;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Report paths must be repo-relative for finding locations and for
        // the ExcludePaths backstop. PMD resolves each relativize root via
        // Path.toAbsolutePath() WITHOUT normalize() — a "." argument would
        // relativize against "<workdir>/." and emit "../"-prefixed display
        // names — so the normalized absolute working directory is passed
        // explicitly. The option is repeatable (shortest relative path
        // wins), so an operator's own -z never conflicts: both apply.
        return Task.FromResult<IReadOnlyList<string>>(
            ["--relativize-paths-with", Path.GetFullPath(workingDirectory)]);
    }

    // Unguessable suppress marker for one invocation — see
    // DisabledSuppressMarkerPrefix.
    private static string NewSuppressMarkerToken()
        => DisabledSuppressMarkerPrefix
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(SuppressMarkerEntropyBytes));

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _rulesetPath = () => scoped[RulesetPathKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "PmdAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Extracts the tool's version from the <c>pmd --version</c> banner —
    /// the <c>PMD x.y.z</c> token on its own line. The banner also prints the
    /// Java runtime's version (<c>Java version: …</c>), so the default
    /// first-semver extraction could pin the wrong component; the
    /// PMD-anchored token is extracted instead. Returns null when the banner
    /// carries no such token.
    /// </summary>
    internal static string? ExtractPmdVersion(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = PmdVersionToken.Match(output);
        if (!match.Success)
            return null;
        var version = match.Groups[1].Value.TrimEnd('.');
        return version.Length == 0 ? null : version;
    }
}
