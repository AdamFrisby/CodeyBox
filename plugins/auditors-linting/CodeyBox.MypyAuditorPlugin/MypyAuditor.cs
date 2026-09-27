using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.MypyAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>mypy</c> (Python static type analysis) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the mypy JSON-lines output parser
/// (<see cref="MypyJsonOutputParser"/> — mypy's built-in
/// <c>--output json</c>, which needs no extra formatter package), the pinned
/// tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the scope and suppression posture below.
///
/// <para><b>Gate behaviour: blocking by default.</b> mypy's diagnostic
/// severities go through a declared map, never raw: <c>"error"</c> — every
/// type error — maps to <see cref="AuditSeverity.Error"/> and fails the
/// audit; <c>"note"</c> (reveal_type output, supplementary context) maps to
/// <see cref="AuditSeverity.Info"/> and is advisory. Which checks count as
/// errors is decided by the mypy configuration in force — the audited
/// repository's <c>mypy.ini</c>/<c>[tool.mypy]</c> by default, or an
/// operator-pinned config via <c>ConfigPath</c>/<c>--config-file</c>.
/// <c>MinimumSeverity</c> only drops findings, it never raises them, so a
/// repo whose config weakens checking keeps the gate weaker; harden it in
/// the config itself (<c>strict = True</c>, <c>enable_error_code</c>).</para>
///
/// <para><b>Exit-code convention (verified against mypy 1.18.x) — deliberately
/// NOT the usual linter table.</b> <c>0</c> = analysis completed clean or
/// with notes only; <c>1</c> = analysis completed with error-level
/// diagnostics. Both are findings-producing verdicts and emit JSONL on
/// stdout. <c>2</c> is ambiguous upstream and therefore declared
/// infrastructure, never a verdict: it covers "could not run" (bad flags,
/// missing config, no Python sources found, crashes) <em>and</em> "blocker"
/// diagnostics such as a syntax error — but blocker messages bypass the JSON
/// formatter and arrive as plain text, so an exit-2 run cannot be trusted to
/// have produced its declared report, and a partially-checked tree is not a
/// verdict on the diff anyway ("errors prevented further checking"). A
/// missing-config or no-Python-files repo surfaces as
/// <see cref="AuditUnavailableException"/> naming the tool, with the tool's
/// own error text in the failure detail. <c>126</c>/<c>127</c> = cannot
/// execute / not found — infrastructure. Anything else is an unknown
/// convention and fails loudly as infrastructure rather than being
/// guessed.</para>
///
/// <para><b>Version pin.</b> mypy's checks, defaults, and the JSON report
/// shape change between releases, so findings are only meaningful from the
/// build the auditor was verified against. The auditor probes
/// <c>mypy --version</c> before the scan; a missing binary, an unrecognised
/// version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a
/// finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> mypy honors suppression
/// surfaces authored inside the audited repository — <c># type: ignore</c>
/// comments, <c># mypy:</c> file-level directives, and its configuration
/// files (<c>mypy.ini</c>, <c>.mypy.ini</c>, <c>setup.cfg</c>
/// <c>[mypy]</c>, <c>pyproject.toml</c> <c>[tool.mypy]</c>), which can
/// weaken or disable checks (<c>ignore_errors</c>,
/// <c>disable_error_code</c>, <c>exclude</c>) — and the audit subject writes
/// that repository. mypy offers no flag that makes suppression comments
/// inert, so the base cannot express that gate and this auditor does not
/// hand-roll one; suppressions are honored and documented as a limitation
/// (see the plugin README). The repo's config is honored because
/// type-checking against the project's own contract is the meaningful check;
/// operators who need an operator-owned ruleset pin one outside the
/// repository via <c>ConfigPath</c> or <c>--config-file</c> in
/// <c>ExtraArguments</c> — noting that inline ignores remain honored even
/// then.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>mypy .</c>: the audited
/// repository's own configuration decides what gets checked through its
/// <c>files</c>/<c>packages</c>/<c>exclude</c> settings — the project's own
/// declaration of checkable scope — with <c>.</c> as the fallback target.
/// <c>--cache-dir /dev/null</c> keeps mypy from writing
/// <c>.mypy_cache</c> into the audited tree, and
/// <c>--no-color-output</c> prevents a baseline <c>FORCE_COLOR</c>/
/// <c>MYPY_FORCE_COLOR</c> environment variable from corrupting the JSON
/// stream. On top of that, findings under vendored (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) and generated
/// (<c>dist/</c>, <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes —
/// plus Python environment and cache trees (<c>.venv/</c>, <c>venv/</c>,
/// <c>.tox/</c>, <c>.mypy_cache/</c>) — are dropped by default: problems
/// there belong to upstream packages or build output, not the change under
/// audit, and reporting them trains operators to ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Mypy Python Type Checker",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "mypy",
    InstallHint = "provision the pinned mypy release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via pipx (pipx install mypy=="
        + DefaultExpectedVersion + ") or pip — the distro apt package is unpinned and typically "
        + "too old for --output=json — together with a Python 3 interpreter — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class MypyAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.mypy";

    /// <summary>
    /// mypy release the invocation and its findings are verified against;
    /// <c>--output json</c> requires mypy ≥ 1.11. Operators running a
    /// different pinned build set <c>ExpectedVersion</c> in the plugin's
    /// scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.18.1";

    /// <summary>Scoped-config key for an explicit mypy configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    // --cache-dir /dev/null is mypy's documented way to disable the cache —
    // it must never write .mypy_cache into the audited tree.
    private const string DisabledCacheDir = "/dev/null";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean or notes-only; 1 = diagnostics found. Both emit the JSONL
        // report — both are verdicts. 2 covers both "could not run" and
        // blocker errors whose report is not JSON; analysis did not complete,
        // so 2 and everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output —
        // including Python env/cache trees — describe code that is not the
        // change under audit: noise that trains operators to ignore the
        // auditor. Operators re-include a path by overriding ExcludePaths in
        // scoped config.
        ExcludePaths =
        [
            "vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/",
            ".venv/", "venv/", ".tox/", ".mypy_cache/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:mypy";

    /// <inheritdoc />
    protected override string ToolName => "mypy";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new MypyJsonOutputParser();

    /// <summary>
    /// Declared mapping from mypy's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. mypy reports <c>"error"</c> and
    /// <c>"note"</c>; <c>"fatal"</c>/<c>"high"</c> and
    /// <c>"info"</c>/<c>"hint"</c> are mapped the same way every other
    /// auditor maps them, so a future diagnostic shape carrying those tokens
    /// is not a unique dialect. Raw strings never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
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
            // Built-in JSONL diagnostics on stdout (mypy ≥ 1.11); no extra
            // formatter package to pin.
            "--output", "json",
            // A baseline FORCE_COLOR/MYPY_FORCE_COLOR env var must not wrap
            // JSON lines in escapes and corrupt the report.
            "--no-color-output",
            // Keep the human "Found N errors" summary out of the JSONL
            // stream; the finding count is the summary.
            "--no-error-summary",
            // mypy's documented cache-off value; the audit must not write
            // .mypy_cache into the audited tree.
            "--cache-dir", DisabledCacheDir,
        };

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config-file"))
        {
            args.Add("--config-file");
            args.Add(configPath.Trim());
        }

        // The repository's mypy configuration decides checkable scope; "." is
        // the fallback target.
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
            "MypyAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
