using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.OxlintAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>oxlint</c> (fast JavaScript and TypeScript
/// analysis) on the shared <see cref="ExternalToolAuditorBase"/>: the base
/// supplies sandboxed invocation with a bounded timeout, per-stream output
/// caps, exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the oxlint JSON output parser
/// (<see cref="OxlintJsonOutputParser"/> — oxlint's built-in
/// <c>--format json</c>, which emits paths relative to the audited worktree
/// root so no relativization is needed), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> oxlint diagnostics at severity <c>error</c> (denied rules,
/// syntax errors) map to <see cref="AuditSeverity.Error"/> and fail the
/// audit; diagnostics at <c>warning</c> map to
/// <see cref="AuditSeverity.Warning"/> and are advisory, and
/// <c>advice</c>-level diagnostics map to <see cref="AuditSeverity.Info"/>.
/// Whether a rule is error or warning is decided by the ruleset in force —
/// the default <c>correctness</c> category plus the audited repository's
/// <c>.oxlintrc.json</c> by default, or an operator-pinned config via
/// <c>ConfigPath</c>. <c>MinimumSeverity</c> only drops findings, it never
/// raises them.</para>
///
/// <para><b>Exit-code convention (verified against oxlint v1.85.0).</b>
/// <c>0</c> = linted clean or warnings only; <c>1</c> = linted with
/// error-level diagnostics. Both emit the JSON report on stdout, so both are
/// findings-producing verdicts. Exit <c>1</c> with no JSON on stdout (usage
/// errors print text such as
/// <c>Error: `--bogus-flag` is not expected in this context</c>, not a
/// report) fails closed as infrastructure through the parser.
/// <c>126</c>/<c>127</c> = cannot execute / not found — infrastructure.
/// Anything else is an unknown convention and fails loudly as infrastructure
/// rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A linter's rule implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>oxlint --version</c> before the
/// scan; a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> oxlint honors inline
/// suppression comments (<c>// oxlint-disable-line</c>,
/// <c>/* oxlint-disable */</c>, and the ESLint-compatible
/// <c>eslint-disable</c> comments) authored inside the audited repository —
/// and the audit subject writes that repository. Unlike ESLint, oxlint
/// offers no <c>--no-inline-config</c> equivalent: there is no flag that
/// makes suppression comments inert, so the base cannot express that gate
/// and this auditor does not hand-roll one. Suppression comments are honored
/// and documented as a limitation (see the plugin README). Operators who
/// need a fully operator-owned gate pin an out-of-repo config via
/// <c>ConfigPath</c> — which still does not disable inline suppressions —
/// and treat a warnings-clean local run that disagrees with the audit as a
/// signal to inspect the diff's suppression comments.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>oxlint --format json
/// --no-error-on-unmatched-pattern .</c>: oxlint's own configuration decides
/// which files are linted. The
/// <c>--no-error-on-unmatched-pattern</c> flag makes a repository with no
/// lintable files a clean pass rather than an exit-1 "No files found to
/// lint" verdict. On top of that, findings under vendored
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) and generated
/// (<c>dist/</c>, <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes are
/// dropped by default: problems there belong to upstream packages or build
/// output, not the change under audit, and reporting them trains operators
/// to ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Oxlint JavaScript/TypeScript Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "oxlint",
    InstallHint = "provision the pinned oxlint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g oxlint@"
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class OxlintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.oxlint";

    /// <summary>
    /// oxlint release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.85.0";

    /// <summary>Scoped-config key for an explicit oxlint configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean or warnings only; 1 = error-level diagnostics. Both
        // emit the JSON report — both are verdicts. 1 without JSON is a
        // usage failure and fails closed in the parser. Anything else is
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
    public override string Name => "codeybox:oxlint";

    /// <inheritdoc />
    protected override string ToolName => "oxlint";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new OxlintJsonOutputParser();

    /// <summary>
    /// Declared mapping from oxlint's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. oxlint reports <c>error</c> and
    /// <c>warning</c> (and <c>advice</c> for lower-severity diagnostics);
    /// <c>fatal</c>/<c>high</c> and <c>hint</c>/<c>low</c> are mapped the
    /// same way every other auditor maps them, so a future diagnostic shape
    /// carrying those tokens is not a unique dialect. Raw strings never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["advice"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>();

        if (!ExtraArgumentsSupplyFlag(options, "--format", "-f"))
        {
            // Machine-readable report with worktree-relative paths. An
            // operator --format would replace the JSON the parser expects
            // and break the run into infrastructure failure; let that
            // surface loudly.
            args.Add("--format");
            args.Add("json");
        }

        // A repository with no lintable files is a clean pass, not an
        // exit-1 "No files found to lint" verdict.
        args.Add("--no-error-on-unmatched-pattern");

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config", "-c"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

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
            "OxlintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
