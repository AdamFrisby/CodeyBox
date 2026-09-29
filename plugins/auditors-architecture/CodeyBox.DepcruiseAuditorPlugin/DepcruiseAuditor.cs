using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DepcruiseAuditorPlugin;

/// <summary>
/// Architecture auditor wrapping <c>depcruise</c> (dependency-cruiser —
/// JavaScript/TypeScript dependency architecture rules) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the JSON report parser
/// (<see cref="DepcruiseJsonOutputParser"/> — depcruise's built-in
/// <c>--output-type json</c>, whose <c>summary.violations</c> array carries
/// rule name, rule severity, and the violating <c>from</c>/<c>to</c>
/// modules), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the defaults below.
///
/// <para><b>Gate behaviour: blocking for error-severity rule violations;
/// advisory below that.</b> dependency-cruiser rule severities are
/// <c>error</c>, <c>warn</c>, <c>info</c>, and <c>ignore</c>: <c>error</c>
/// maps to <see cref="AuditSeverity.Error"/> and fails the audit, while
/// <c>warn</c> maps to <see cref="AuditSeverity.Warning"/> and
/// <c>info</c>/<c>ignore</c> map to <see cref="AuditSeverity.Info"/> and
/// stay advisory. Whether a rule is error or warn is decided by the ruleset
/// in force — the audited repository's dependency-cruiser config by default,
/// or an operator-pinned config via <c>ConfigPath</c>/<c>--config</c>.
/// <c>MinimumSeverity</c> can only lower this posture (dropping info, then
/// warnings), never raise it.</para>
///
/// <para><b>Exit-code convention (verified against dependency-cruiser 18.4.0
/// source and CLI — deliberately NOT the common linter table).</b> The JSON
/// reporter's contract is <c>exitCode: 0</c> on every completed run
/// (<c>src/report/json.mjs</c>); violations of any severity — including
/// <c>error</c> — still exit 0, so the violations array, not the exit code,
/// is the verdict. Only the human-facing <c>err</c> reporters exit non-zero
/// on findings (<c>src/report/error.mjs</c> exits with the error count), and
/// this auditor never selects those. Exit <c>1</c> therefore means "could
/// not run" in practice — missing or unreadable config file, unreadable
/// cruise target, usage error — all of which print plain error text with no
/// JSON on stdout. Exit <c>1</c> is still a declared findings-producing code
/// so that a completed run whose stdout <em>is</em> a JSON report is read as
/// a verdict either way; the parser — not the exit code — carries the
/// classification, and exit <c>1</c> without that JSON fails closed as
/// infrastructure. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> dependency-cruiser's rule implementations and
/// JSON report shape change between releases, so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>depcruise --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> The ruleset is entirely
/// repo-authored — dependency-cruiser reads <c>.dependency-cruiser.js</c> /
/// <c>.cjs</c> / <c>.mjs</c> / <c>.json</c> (or the file named by
/// <c>--config</c>) plus per-rule <c>ignore</c> and <c>--ignore-known</c>
/// baselines, and the project's own declared rules are the meaningful check
/// (changes to them are visible in the audited diff). The auditor runs with
/// <see cref="AuditCapabilities.None"/>. Operators who need an
/// operator-owned ruleset pin an out-of-repo file via <c>ConfigPath</c> — a
/// repository without any dependency-cruiser configuration is an
/// infrastructure failure ("Can't open … Does it exist?"), not a pass. The
/// scan never passes <c>--no-config</c>: cruising without rules reports zero
/// violations, which would manufacture a pass.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>depcruise --output-type
/// json --progress none .</c>: the whole audited repository, with progress
/// reporting pinned off so only the JSON report lands on stdout. Findings
/// under vendored (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>) prefixes are dropped by default:
/// violations there belong to upstream packages or build output, not the
/// change under audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Depcruise JS/TS Dependency Architecture Rules",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "depcruise",
    InstallHint = "provision the pinned dependency-cruiser release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g dependency-cruiser@"
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class DepcruiseAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.depcruise";

    /// <summary>
    /// dependency-cruiser release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what they
    /// provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "18.4.0";

    /// <summary>Scoped-config key for an explicit dependency-cruiser configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // The JSON reporter exits 0 on every completed run — clean or with
        // violations — so 0 is the verdict code. 1 is included so a completed
        // run whose stdout is a JSON report still reads as a verdict; the
        // parser fails closed on the no-JSON "could not run" text (missing
        // config, unreadable target, usage error). Everything else is
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
    public override string Name => "codeybox:depcruise";

    /// <inheritdoc />
    protected override string ToolName => "depcruise";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new DepcruiseJsonOutputParser();

    /// <summary>
    /// Declared mapping from dependency-cruiser's severity vocabulary
    /// (<c>error</c>, <c>warn</c>, <c>info</c>, <c>ignore</c>) to CodeyBox's
    /// <see cref="AuditSeverity"/>. <c>error</c> blocks; <c>warn</c> is
    /// advisory; <c>info</c> and <c>ignore</c> are informational and never
    /// block. The wider level vocabulary is mapped the same way every other
    /// auditor maps it, so a future report shape carrying those tokens is not
    /// a unique dialect. Raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["ignore"] = AuditSeverity.Info,
            ["ignored"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
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
            // The report the parser reads: { modules, summary: { violations } }.
            // A repeated --output-type/-T would replace it and break the run
            // into an infrastructure failure — documented, not guarded.
            "--output-type", "json",
        };

        // Progress reporting must not land on stdout next to the JSON report.
        if (!ExtraArgumentsSupplyFlag(options, "--progress", "-p"))
        {
            args.Add("--progress");
            args.Add("none");
        }

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config", "-c"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        // Cruise the whole audited repository. Never --no-config: cruising
        // without rules reports zero violations, manufacturing a pass.
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
            "DepcruiseAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
