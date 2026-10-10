using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PsalmAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>psalm</c> (PHP static analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the Psalm JSON output parser
/// (<see cref="PsalmJsonOutputParser"/> — Psalm's built-in
/// <c>--output-format=json</c>, which needs no extra formatter package), the
/// pinned tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the scope and suppression posture below.
///
/// <para><b>Gate behaviour: severity-driven — not blocking on every
/// finding.</b> Psalm's issue severities go through a declared map, never
/// raw: <c>"error"</c> issues map to <see cref="AuditSeverity.Error"/> and
/// fail the audit; <c>"info"</c> issues map to
/// <see cref="AuditSeverity.Info"/> and are advisory. Which checks count as
/// errors is decided by the configuration in force: the audited repository's
/// <c>psalm.xml</c> by default (its <c>errorLevel</c> plus
/// <c>issueHandlers</c>), or an operator-pinned config via
/// <c>ConfigPath</c>. <c>MinimumSeverity</c> only drops findings, it never
/// raises them, so a repo whose config weakens checking keeps the gate
/// weaker; harden it in the config itself.</para>
///
/// <para><b>Exit-code convention (per Psalm's documented contract).</b>
/// <c>0</c> = analysis completed with no issues; <c>2</c> = analysis
/// completed and found issues — both emit the JSON report on stdout, so both
/// are findings-producing verdicts. <c>1</c> = could not run: missing or
/// invalid config, bad flags, or an internal error — printed as text, no
/// report — and is infrastructure, never a verdict. <c>126</c>/<c>127</c> =
/// cannot execute / not found — infrastructure. Anything else is an unknown
/// convention and fails loudly as infrastructure rather than being guessed.
/// A findings exit whose report parses but contains zero findings is a
/// corrupt report, not a clean pass: the parser fails closed.</para>
///
/// <para><b>Version pin.</b> Psalm's rules, defaults, and report shape change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>psalm --version</c>
/// before the scan; a missing binary, an unrecognised version string, or a
/// version other than <c>ExpectedVersion</c> is an infrastructure failure
/// naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Psalm honors suppression
/// surfaces authored inside the audited repository —
/// <c>@psalm-suppress</c> annotations, its configuration file
/// (<c>psalm.xml</c> and variants, including <c>issueHandlers</c> and
/// <c>excludeFiles</c>), and <c>psalm-baseline.xml</c> — and the audit
/// subject writes that repository. Psalm offers no flag that makes
/// suppression inert, so the base cannot express that gate and this auditor
/// does not hand-roll one; suppressions are honored and documented as a
/// limitation (see the plugin README). The repo's config is honored because
/// analysing against the project's own level contract is the meaningful
/// check; operators who need an operator-owned ruleset pin one outside the
/// repository via <c>ConfigPath</c> or <c>--config</c> in
/// <c>ExtraArguments</c> — noting that inline suppressions and baselines
/// remain honored even then.</para>
///
/// <para><b>Scope and defaults.</b> The scan runs Psalm with no positional
/// path arguments, so the repository's own <c>psalm.xml</c>
/// <c>projectFiles</c> section decides what gets analysed — the project's own
/// declaration of checkable scope. <c>--no-cache</c> keeps Psalm from writing
/// its cache into the audited tree. On top of that, findings under vendored
/// (<c>vendor/</c> — Composer dependencies — <c>third_party/</c>,
/// <c>node_modules/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>) prefixes are dropped by default: problems
/// there belong to upstream packages or build output, not the change under
/// audit, and reporting them trains operators to ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Psalm PHP Static Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "psalm",
    InstallHint = "provision the pinned Psalm release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via Composer (composer global require "
        + "vimeo/psalm:" + DefaultExpectedVersion + ") or as the signed psalm.phar from the upstream "
        + "release — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "php",
    AptPackage = "php-cli",
    InstallHint = "psalm is a PHP phar/binstub and needs a php CLI interpreter on PATH")]
public sealed class PsalmAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.psalm";

    /// <summary>
    /// Psalm release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "6.20.0";

    /// <summary>Scoped-config key for an explicit Psalm configuration file path (<c>--config</c>).</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = analysis completed with no issues; 2 = analysis completed and
        // found issues. Both emit the JSON report — both are verdicts.
        // 1 (missing/invalid config, bad flags, internal error) prints text
        // instead of the report and is infrastructure, as is everything else.
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

    /// <inheritdoc />
    public override string Name => "codeybox:psalm";

    /// <inheritdoc />
    protected override string ToolName => "psalm";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PsalmJsonOutputParser();

    /// <summary>
    /// Declared mapping from Psalm's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Psalm reports <c>"error"</c> and
    /// <c>"info"</c> severities; they map to <see cref="AuditSeverity.Error"/>
    /// and <see cref="AuditSeverity.Info"/> respectively, so error-level
    /// issues fail the audit while informational ones stay advisory. The
    /// warning tokens keep the meaning they carry for every other auditor.
    /// Raw strings never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
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
            // Built-in JSON report on stdout; no extra formatter package to
            // pin.
            "--output-format", "json",
            // Keep Psalm's cache out of the audited tree.
            "--no-cache",
        };

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        // No positional path arguments: the repository's psalm.xml
        // projectFiles section declares the analysed scope.
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
            "PsalmAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
