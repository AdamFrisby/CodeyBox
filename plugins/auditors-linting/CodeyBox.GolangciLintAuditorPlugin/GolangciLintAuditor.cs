using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GolangciLintAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>golangci-lint</c> (aggregated Go analysis —
/// one run fans out to the enabled linters: <c>errcheck</c>,
/// <c>govet</c>, <c>staticcheck</c>, <c>ineffassign</c>, <c>unused</c>,
/// <c>typecheck</c> and the rest of the default set) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the golangci-lint native JSON output
/// parser (<see cref="GolangciLintJsonOutputParser"/> — the
/// <c>--output.json.path stdout</c> report, a single JSON object whose
/// <c>Issues</c> array carries one entry per diagnostic with the linter
/// name in <c>FromLinter</c> and the location in <c>Pos</c>; the SARIF
/// report the tool can also emit is a second rendering of the same
/// diagnostics, so the native format is used instead), the pinned
/// tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: advisory by default — not blocking.</b> An
/// issue's <c>Severity</c> is empty unless a <c>severity</c> rule in the
/// configuration assigns one, so the great majority of findings arrive
/// with no tool level and map to <see cref="AuditSeverity.Warning"/>
/// through the declared default: they are reported but the audit still
/// passes. Only issues carrying an explicit <c>error</c>-class severity
/// map to <see cref="AuditSeverity.Error"/> and fail the audit. To make
/// the gate fail on findings, assign severities in the configuration
/// (e.g. <c>severity.default-severity: error</c>); <c>MinimumSeverity</c>
/// only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against golangci-lint v2.14.0 —
/// not assumed from the common "0 clean / 1 findings / 2 error" table).</b>
/// <c>0</c> = ran clean; <c>1</c> = ran with issues found (the value is
/// the default of the tool's <c>--issues-exit-code</c> flag — an operator
/// who overrides that flag must declare the replacement in
/// <c>FindingsExitCodes</c>). Both emit the JSON report on stdout, so both
/// are findings-producing verdicts. <c>2</c>–<c>7</c> = could not run:
/// warning-in-test, failure (bad flags, analysis errors), timeout,
/// no-go-files, no-config-file-detected, error-was-logged — notably, a
/// directory with no Go files exits <c>7</c> with an (empty) JSON document
/// on stdout and the typechecking error on stderr, so the exit code, not
/// the presence of JSON, is what keeps that case infrastructure. Exit
/// <c>1</c> with no JSON on stdout (a crash before the report is written)
/// fails closed as infrastructure through the parser.
/// <c>126</c>/<c>127</c> = cannot execute / not found — infrastructure.
/// Anything else is an unknown convention and fails loudly as
/// infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> The bundled linters and their diagnostics
/// change between releases, so findings are only meaningful from the build
/// the auditor was verified against. The auditor probes
/// <c>golangci-lint version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> golangci-lint honors
/// <c>//nolint</c> directives (and the repository's
/// <c>.golangci.yml</c>-family configuration, including its
/// <c>issues.exclude-*</c> rules) authored inside the audited repository —
/// and the audit subject writes that repository. The tool offers no flag
/// that makes <c>//nolint</c> directives inert, so the base cannot express
/// that gate and this auditor does not hand-roll one. Suppression
/// directives are honored and documented as a limitation (see the plugin
/// README). Operators who need a fully operator-owned gate pin an
/// out-of-repo config via <c>ConfigPath</c> — which still does not disable
/// <c>//nolint</c> — and treat a warnings-clean local run that disagrees
/// with the audit as a signal to inspect the diff's suppression
/// comments.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>golangci-lint run --output.json.path stdout --show-stats=false
/// ./...</c>: the whole audited tree as Go packages, with the tool's own
/// configuration deciding which linters run and which files are analyzed.
/// <c>--show-stats=false</c> keeps stdout pure JSON — with stats enabled
/// the tool appends a human <c>N issues.</c> trailer after the report,
/// which is not parseable output. On top of that, findings under vendored
/// (<c>vendor/</c>) and upstream-mirror (<c>third_party/</c>) prefixes are
/// dropped by default: problems there belong to upstream packages, not the
/// change under audit, and reporting them trains operators to ignore the
/// auditor. File-level generated code (<c>*.pb.go</c>, mocks) has no
/// directory prefix for a path filter to match, so it stays visible and is
/// documented as such.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: golangci-lint Go Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "golangci-lint",
    InstallHint = "provision the pinned golangci-lint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — the Go toolchain plus the pinned "
        + "binary (e.g. curl -sSfL https://raw.githubusercontent.com/golangci/golangci-lint/HEAD/install.sh "
        + "| sh -s -- -b <bindir> v" + DefaultExpectedVersion + "); no distro apt package carries a "
        + "version pin — through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions")]
public sealed class GolangciLintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.golangci-lint";

    /// <summary>
    /// golangci-lint release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.14.0";

    /// <summary>Scoped-config key for an explicit golangci-lint configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran clean; 1 = ran with issues found (the default of the
        // tool's --issues-exit-code flag). Both emit the JSON report —
        // both are verdicts. 2-7 (could not run) and everything else is
        // infrastructure; notably exit 7 can carry an empty-issues JSON
        // document, so the exit code is the verdict, not the JSON shape.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings under Go's own dependency mirror and the conventional
        // upstream-mirror prefix describe code that is not the change under
        // audit — noise that trains operators to ignore the auditor.
        // Operators re-include a path by overriding ExcludePaths in scoped
        // config.
        ExcludePaths = ["vendor/", "third_party/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:golangci-lint";

    /// <inheritdoc />
    protected override string ToolName => "golangci-lint";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new GolangciLintJsonOutputParser();

    /// <summary>
    /// Declared mapping from golangci-lint's severity vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. An issue's severity is
    /// empty unless a <c>severity</c> rule in the configuration assigns
    /// one, so the declared default (<see cref="AuditSeverity.Warning"/>)
    /// is what the common unclassified finding maps to — advisory, not
    /// blocking. Explicit <c>error</c>-class levels fail the audit;
    /// <c>info</c>-class levels are informational. Raw strings never reach
    /// findings.
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
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["advice"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string> { "run" };

        if (!ExtraArgumentsSupplyFlag(options, "--output.json.path"))
        {
            // Machine-readable report on stdout. An operator output flag
            // would replace or corrupt the JSON the parser expects and
            // break the run into infrastructure failure; let that surface
            // loudly.
            args.Add("--output.json.path");
            args.Add("stdout");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--show-stats"))
        {
            // With stats enabled the tool appends a human "N issues."
            // trailer after the JSON report on stdout, which is not
            // parseable output. Attached form: pflag bool flags do not
            // consume a following arg, so "--show-stats false" would leak
            // "false" into the package patterns.
            args.Add("--show-stats=false");
        }

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config", "-c"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        // The whole audited tree as Go packages; the tool's own
        // configuration decides which linters run and which files count.
        args.Add("./...");
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
            "GolangciLintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
