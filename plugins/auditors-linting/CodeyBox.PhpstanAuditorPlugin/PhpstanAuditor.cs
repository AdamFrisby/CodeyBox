using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PhpstanAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>phpstan analyse</c> (PHP static analysis) on
/// the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the PHPStan JSON output parser
/// (<see cref="PhpstanJsonOutputParser"/> — PHPStan's built-in
/// <c>--error-format=json</c>, which needs no extra formatter package), the
/// pinned tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the scope and suppression posture below.
///
/// <para><b>Gate behaviour: blocking by default.</b> PHPStan has no severity
/// levels — every issue it reports is an analysis error — so each finding maps
/// to <see cref="AuditSeverity.Error"/> and fails the audit. Which checks run
/// (and therefore what counts) is decided by the level in force: the audited
/// repository's <c>phpstan.neon</c> by default, or an operator-pinned
/// config/level via <c>ConfigPath</c>/<c>Level</c>. <c>MinimumSeverity</c>
/// only drops findings, it never raises them, so a repo whose config weakens
/// checking keeps the gate weaker; harden it in the config itself
/// (<c>level: max</c>, <c>ignoreErrors</c> hygiene).</para>
///
/// <para><b>Exit-code convention (verified against PHPStan 2.2.x) —
/// deliberately NOT the usual linter table.</b> PHPStan uses exit <c>1</c> for
/// <em>both</em> verdicts and failures: <c>0</c> = analysis completed clean;
/// <c>1</c> = analysis completed with errors <em>or</em> could-not-run —
/// every inception failure (missing config file, invalid level, zero files to
/// analyse, bad autoload/bootstrap) and every internal error returns 1 with
/// plain text instead of the JSON report. The two cases are told apart by the
/// report itself: a run is a verdict only when stdout parses as the PHPStan
/// JSON document; anything else on a <c>0</c>/<c>1</c> exit fails closed as
/// infrastructure through the parser. <c>126</c>/<c>127</c> = cannot execute /
/// not found — infrastructure. Anything else (PHP fatals, Symfony-level
/// failures) is an unknown convention and fails loudly as infrastructure
/// rather than being guessed.</para>
///
/// <para><b>Version pin.</b> PHPStan's rules, defaults, and report shape
/// change between releases, so findings are only meaningful from the build
/// the auditor was verified against. The auditor probes
/// <c>phpstan --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> PHPStan honors suppression
/// surfaces authored inside the audited repository — <c>@phpstan-ignore</c>
/// and <c>@phpstan-ignore-line</c>/<c>-next-line</c> comments, its
/// configuration files (<c>phpstan.neon</c>, <c>phpstan.neon.dist</c> and
/// variants), <c>ignoreErrors</c>/<c>excludePaths</c>, and
/// <c>phpstan-baseline.neon</c> — and the audit subject writes that
/// repository. PHPStan offers no flag that makes suppression inert, so the
/// base cannot express that gate and this auditor does not hand-roll one;
/// suppressions are honored and documented as a limitation (see the plugin
/// README). The repo's config is honored because analysing against the
/// project's own level contract is the meaningful check; operators who need
/// an operator-owned ruleset pin one outside the repository via
/// <c>ConfigPath</c> or <c>-c</c> in <c>ExtraArguments</c> — noting that
/// inline ignores and baselines remain honored even then.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>phpstan analyse .</c>: the
/// whole audited tree — CLI path arguments replace any <c>paths</c> the
/// repository's config declares — minus whatever the config's
/// <c>excludePaths</c> suppress. <c>--no-progress</c> keeps the progress
/// renderer out of the report stream. On top of that, findings under vendored
/// (<c>vendor/</c> —
/// Composer dependencies — <c>third_party/</c>, <c>node_modules/</c>) and
/// generated (<c>dist/</c>, <c>build/</c>, <c>out/</c>, <c>coverage/</c>)
/// prefixes are dropped by default: problems there belong to upstream
/// packages or build output, not the change under audit, and reporting them
/// trains operators to ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: PHPStan PHP Static Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "phpstan",
    InstallHint = "provision the pinned PHPStan release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via Composer (composer global require "
        + "phpstan/phpstan:" + DefaultExpectedVersion + ") or as the signed phpstan.phar — no distro "
        + "apt package carries a version pin — through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "php",
    AptPackage = "php-cli",
    InstallHint = "phpstan is a PHP phar/binstub and needs a php CLI interpreter on PATH")]
public sealed class PhpstanAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.phpstan";

    /// <summary>
    /// PHPStan release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.2.14";

    /// <summary>Scoped-config key for an explicit PHPStan configuration file path (<c>-c</c>).</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key for the analysis level (<c>-l</c>): <c>0</c>–<c>9</c>
    /// or <c>max</c>. Overrides the level in the repository's config; when
    /// unset the config's <c>level</c> (or PHPStan's default <c>0</c> for a
    /// config-less repository) applies.
    /// </summary>
    public const string LevelKey = "Level";

    /// <summary>PHPStan levels the auditor accepts for <see cref="LevelKey"/>.</summary>
    private static readonly IReadOnlySet<string> AllowedLevels =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "max",
        };

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = analysis completed clean; 1 = analysis completed with errors
        // OR could not run — PHPStan maps every inception failure (missing
        // config, zero files, bad options) and internal error onto 1 too.
        // The JSON report on stdout is what separates verdict from failure:
        // the parser fails closed when it is absent or malformed. Everything
        // else is infrastructure.
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
    private Func<string?> _level = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:phpstan";

    /// <inheritdoc />
    protected override string ToolName => "phpstan";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PhpstanJsonOutputParser();

    /// <summary>
    /// Declared mapping from PHPStan's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. PHPStan reports no severity field — every
    /// reported issue is an analysis error, so the parser supplies the token
    /// <c>"error"</c>, which maps to <see cref="AuditSeverity.Error"/>; the
    /// warning/info tokens keep the meaning they carry for every other
    /// auditor so a future diagnostic shape carrying them is not a unique
    /// dialect. Raw strings never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
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
            "analyse",
            // Built-in JSON report on stdout; no extra formatter package to
            // pin.
            "--error-format", "json",
            // Keep the progress renderer out of the stream so the JSON
            // document stays the only thing on stdout.
            "--no-progress",
        };

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--configuration", "-c"))
        {
            args.Add("-c");
            args.Add(configPath.Trim());
        }

        var level = _level();
        if (!string.IsNullOrWhiteSpace(level))
        {
            var normalized = level.Trim();
            if (!AllowedLevels.Contains(normalized))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has an invalid {LevelKey} "
                    + $"('{TruncateForMessage(normalized)}'); accepted values are 0-9 or max. Set "
                    + $"CodeyBox:Plugins:{PluginId}:{LevelKey} to a supported level.")
                { IsDeterministic = true };
            if (!ExtraArgumentsSupplyFlag(options, "--level", "-l"))
            {
                args.Add("-l");
                args.Add(normalized);
            }
        }

        // Whole-tree default scope; a CLI path argument replaces any 'paths'
        // the repository's config declares.
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
        _level = () => scoped[LevelKey];
        context.Logger.LogInformation(
            "PhpstanAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
