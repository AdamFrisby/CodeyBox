using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.KnipAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>knip</c> (unused JavaScript/TypeScript files,
/// exports, and dependencies) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the knip JSON output parser
/// (<see cref="KnipJsonOutputParser"/> — knip's built-in
/// <c>--reporter json</c>), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: blocking by default</b> for the issue types this
/// auditor reports. knip's own default <c>rules</c> classify unused files,
/// unused exports, and unused dependencies as <c>error</c>; those map to
/// <see cref="AuditSeverity.Error"/> and fail the audit. Warning-class knip
/// types such as <c>cycles</c> are not in the default <c>--include</c> set
/// — if an operator adds them they map to <see cref="AuditSeverity.Warning"/>
/// and stay advisory. <c>MinimumSeverity</c> only drops findings, it never
/// raises them. This auditor is therefore a merge gate for unused JS/TS
/// files, exports, and dependencies, not an advisory scanner, unless the
/// operator narrows the mapped severity or the include set.</para>
///
/// <para><b>Exit-code convention (verified against knip v6.38.0).</b>
/// knip's published table is <c>0</c> = ran clean, <c>1</c> = ran with
/// lint issues, <c>2</c> = did not run (bad input, plugin/config load
/// error, internal error). That table does not fully hold: unknown CLI
/// flags also <c>process.exit(1)</c> with the help text on stdout and no
/// JSON report (see <c>packages/knip/src/cli.ts</c> <c>parseArgs</c>
/// catch). So <c>0</c> and <c>1</c> are findings-producing <em>only when
/// stdout is a knip JSON report</em> — both emit
/// <c>{"issues":[…]}</c> on a successful analysis (empty array when
/// clean). Exit <c>1</c> without that JSON (usage/help dump) fails closed
/// as infrastructure through the parser. Exit <c>2</c> is infrastructure
/// (missing <c>package.json</c>, unreadable <c>knip.json</c>, internal
/// error) even if a hint leaked onto stdout. <c>126</c>/<c>127</c> =
/// cannot execute / not found — infrastructure. Anything else is an
/// unknown convention and fails loudly as infrastructure rather than
/// being guessed.</para>
///
/// <para><b>Version pin.</b> knip's issue types, default rules, and JSON
/// reporter shape change between releases, so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>knip --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>knip --reporter json --include files,exports,dependencies</c>:
/// unused files, unused exported values, and unused
/// <c>dependencies</c>/<c>devDependencies</c> (knip expands
/// <c>dependencies</c> to those automatically). That is the advertised
/// report surface; other knip types (unlisted, unresolved, binaries,
/// types, duplicates, cycles, …) stay off unless an operator adds them
/// via <c>ExtraArguments</c>. On top of knip's own project/gitignore
/// filters, findings under vendored (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>) prefixes are dropped by default:
/// problems there belong to upstream packages or build output, not the
/// change under audit, and reporting them trains operators to ignore the
/// auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Knip Unused JS/TS Files, Exports and Dependencies",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "knip",
    InstallHint = "provision the pinned knip release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g knip@"
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class KnipAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.knip";

    /// <summary>
    /// knip release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "6.38.0";

    /// <summary>Scoped-config key for an explicit knip configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Default <c>--include</c> value: unused files, unused exports, and
    /// unused dependencies. knip treats a <c>dependencies</c> include as
    /// also covering <c>devDependencies</c> and
    /// <c>optionalPeerDependencies</c>.
    /// </summary>
    internal const string DefaultIncludeIssueTypes = "files,exports,dependencies";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean (or warning-only issue types); 1 = error-level findings.
        // Both emit the JSON report on a successful analysis — both are
        // verdicts. Exit 1 *without* JSON is a usage failure and fails
        // closed in the parser. 2 (could not run) and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:knip";

    /// <inheritdoc />
    protected override string ToolName => "knip";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new KnipJsonOutputParser();

    /// <summary>
    /// Declared mapping from knip's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. knip's rules are <c>error</c> /
    /// <c>warn</c> / <c>off</c>; the JSON reporter does not currently emit
    /// a per-item level, so the parser supplies knip's documented default
    /// for the issue type (see <see cref="KnipJsonOutputParser"/>).
    /// <c>high</c>/<c>medium</c>/<c>low</c> are mapped the same way every
    /// other auditor maps them, so a future reporter shape or an item that
    /// does carry those tokens is not a unique dialect. Raw strings never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["off"] = AuditSeverity.Info,
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
            // Built-in JSON reporter: { "issues": [ { file, <issueType>: [...] } ] }.
            "--reporter", "json",
            // Progress bars must not land on stdout next to the JSON report.
            "--no-progress",
        };

        if (!ExtraArgumentsSupplyFlag(options, "--include", "--files", "--exports", "--dependencies"))
        {
            args.Add("--include");
            args.Add(DefaultIncludeIssueTypes);
        }

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config", "-c"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        context.Logger.LogInformation(
            "KnipAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
