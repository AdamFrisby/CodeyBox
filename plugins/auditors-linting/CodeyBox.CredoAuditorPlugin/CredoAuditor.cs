using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CredoAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>credo</c> (Elixir static analysis) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the credo JSON output parser
/// (<see cref="CredoJsonOutputParser"/> — credo's built-in
/// <c>--format json</c>, which needs no extra formatter package), the pinned
/// tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the scope and
/// suppression posture below.
///
/// <para><b>Gate behaviour: partially blocking — stated explicitly.</b>
/// Credo's issue categories go through a declared map, never raw:
/// <c>"warning"</c> (likely mistakes — credo's own red, exit-status-16
/// category) maps to <see cref="AuditSeverity.Error"/> and fails the audit;
/// <c>"design"</c> and <c>"refactor"</c> map to
/// <see cref="AuditSeverity.Warning"/>; <c>"consistency"</c> and
/// <c>"readability"</c> (naming and style consistency) map to
/// <see cref="AuditSeverity.Info"/>. Design/refactor/style findings are
/// advisory: they are reported but never fail the audit. Operators who want
/// a fully blocking gate lower nothing — <c>MinimumSeverity</c> only drops
/// findings, it never raises them — and instead enforce the advisory
/// categories through the repository's <c>.credo.exs</c> (enabling
/// <c>strict</c> checks) or a separate policy. This auditor is therefore
/// NOT blocking by default for non-warning findings, by design: a style nit
/// must not fail an audit the way a likely bug does.</para>
///
/// <para><b>Exit-code convention (verified against credo 1.7.19 sources —
/// not assumed from the common table).</b> <c>0</c> = analysis completed
/// clean (empty <c>issues</c> array). <c>1</c>–<c>31</c> = analysis completed
/// with issues: the exit status is the bitwise OR of the per-category bits
/// (<c>consistency: 1</c>, <c>design: 2</c>, <c>readability: 4</c>,
/// <c>refactor: 8</c>, <c>warning: 16</c>), so every value in that range is
/// a findings-producing verdict emitting the JSON report on stdout.
/// <c>128</c> = generic error, <c>129</c> = config parser error,
/// <c>130</c> = config loaded but invalid — none completed analysis, so all
/// are infrastructure. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed. An operator
/// <c>--mute-exit-status</c> collapses findings exits to <c>0</c>, but the
/// JSON report still carries the issues: findings still fail or advise
/// exactly as mapped — the flag cannot silence the gate.</para>
///
/// <para><b>Version pin.</b> Credo's checks, defaults, and the JSON report
/// shape change between releases, so findings are only meaningful from the
/// build the auditor was verified against. The auditor probes
/// <c>credo --version</c> before the scan; a missing binary, an unrecognised
/// version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a
/// finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Credo honors suppression
/// surfaces authored inside the audited repository — inline
/// <c># credo:disable-for-this-file</c> / <c>-next-line</c> /
/// <c>-previous-line</c> / <c>-lines</c> comments and its configuration file
/// (<c>.credo.exs</c>), which can weaken or disable checks — and the audit
/// subject writes that repository. Credo offers no flag that makes inline
/// disables inert, so the base cannot express that gate and this auditor
/// does not hand-roll one; suppressions are honored and documented as a
/// limitation (see the plugin README). The repo's config is honored because
/// analysis against the project's own contract is the meaningful check;
/// operators who need an operator-owned ruleset pin one outside the
/// repository via <c>ConfigPath</c> (passed as <c>--config-file</c>) — noting
/// that inline disables remain honored even then.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>credo suggest .</c>: the
/// audited repository's own <c>.credo.exs</c> decides what gets checked
/// through its <c>files.included</c> / <c>files.excluded</c> settings — the
/// project's own declaration of checkable scope — with <c>.</c> as the
/// fallback target. On top of that, findings under vendored
/// (<c>deps/</c>, <c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) and generated (<c>_build/</c>, <c>dist/</c>,
/// <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes are dropped by
/// default: problems there belong to upstream packages or build output, not
/// the change under audit, and reporting them trains operators to ignore the
/// auditor. Credo reports scan-relative paths, so unlike absolute-URI tools
/// this finding-level backstop matches normally.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Credo Elixir Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "credo",
    InstallHint = "provision Elixir and the pinned credo escript release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline (mix escript.install hex credo "
        + DefaultExpectedVersion + " --force, ensuring the escript directory is on PATH) "
        + "through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class CredoAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.credo";

    /// <summary>
    /// Credo release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.7.19";

    /// <summary>Scoped-config key for an explicit credo configuration file path (passed as <c>--config-file</c>).</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = analysis completed clean; 1-31 = analysis completed with
        // issues, one bit per category (consistency:1, design:2,
        // readability:4, refactor:8, warning:16). Every value in that range
        // emits the JSON report — all are verdicts. 128-130 (generic,
        // config-parser, invalid-config errors) completed no analysis, so
        // they and everything else are infrastructure.
        FindingsExitCodes = new HashSet<int>(Enumerable.Range(0, 32)),
        // Findings in vendored/dependency trees (Hex packages under deps/,
        // vendored sources) and generated build output describe code that is
        // not the change under audit: noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths =
        [
            "deps/", "_build/", "vendor/", "third_party/", "node_modules/",
            "dist/", "build/", "out/", "coverage/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:credo";

    /// <inheritdoc />
    protected override string ToolName => "credo";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new CredoJsonOutputParser();

    /// <summary>
    /// Declared mapping from credo's issue-category vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Credo reports no high/medium/low grades —
    /// its severity dimension is the category, with <c>"warning"</c> (likely
    /// mistakes) above <c>"design"</c>/<c>"refactor"</c> (structure and
    /// complexity) above <c>"consistency"</c>/<c>"readability"</c> (naming
    /// and style). The numeric <c>priority</c> is per-check tuning within a
    /// category, not a cross-check grade, so it does not drive the map; it
    /// is preserved in the finding message. Raw strings never reach
    /// findings; the declared default is <see cref="AuditSeverity.Warning"/>.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["warning"] = AuditSeverity.Error,
            ["design"] = AuditSeverity.Warning,
            ["refactor"] = AuditSeverity.Warning,
            ["consistency"] = AuditSeverity.Info,
            ["readability"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // The suggest command is credo's default analysis; naming it keeps
        // the invocation stable if that default ever changes. Unknown
        // positional args are treated as scan targets, so "." scopes the run
        // to the audited tree.
        var args = new List<string> { "suggest" };

        if (!ExtraArgumentsSupplyFlag(options, "--format"))
        {
            // Machine-readable JSON report for the dedicated parser. An
            // operator --format would replace the JSON the parser expects
            // and break the run into infrastructure failure; let that
            // surface loudly.
            args.Add("--format");
            args.Add("json");
        }

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config-file"))
        {
            args.Add("--config-file");
            args.Add(configPath.Trim());
        }

        // The repository's .credo.exs decides checkable scope; "." is the
        // fallback target.
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
        _configPath = () => scoped[ConfigPathKey];
        context.Logger.LogInformation(
            "CredoAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
