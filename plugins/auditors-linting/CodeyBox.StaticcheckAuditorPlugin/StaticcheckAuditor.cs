using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.StaticcheckAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>staticcheck</c> (advanced Go static analysis —
/// bug finding, simplifications, and style checks: the <c>SA*</c>,
/// <c>S1*</c>, <c>ST*</c>, <c>U*</c> families) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the staticcheck JSON-stream parser
/// (<see cref="StaticcheckJsonOutputParser"/> — the <c>-f json</c> report,
/// one JSON object per line), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the <c>go</c>
/// toolchain precondition the package load shells out to, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: blocking by default — every finding fails the
/// audit, and this is stated here rather than implied.</b> Under the
/// default <c>-fail all</c> every diagnostic carries severity
/// <c>error</c>, which maps to <see cref="AuditSeverity.Error"/>. The
/// declared map still translates the full level vocabulary so an operator
/// narrowing <c>-fail</c> (e.g. <c>-fail none</c> in
/// <c>ExtraArguments</c>, which demotes every diagnostic to
/// <c>warning</c>) degrades to advisory instead of becoming a unique
/// dialect. <c>MinimumSeverity</c> only drops findings, it never raises
/// them: set <c>MinimumSeverity: error</c> to keep the audit failing only
/// on error-severity diagnostics while still reporting the rest.</para>
///
/// <para><b>Exit-code convention (verified empirically against staticcheck
/// 2025.1.1 — not assumed from the common "0 clean / 1 findings / 2
/// error" table).</b> <c>0</c> = ran clean (empty stdout) or ran with only
/// below-<c>-fail</c> diagnostics (a <c>warning</c>-severity JSON stream on
/// stdout — still a verdict). <c>1</c> = ran with diagnostics at or above
/// the <c>-fail</c> set, including package-load failures: an unloadable
/// tree (no <c>go.mod</c>, an unknown package pattern, a type error)
/// exits <c>1</c> carrying a <c>code: compile</c> JSON diagnostic with an
/// empty location — the exit code keeps that case a verdict, and the
/// location-less record surfaces as a finding with no location rather than
/// infrastructure. Both exits emit the JSON stream on stdout, so both are
/// findings-producing verdicts. Exit <c>1</c> with no JSON on stdout (a
/// crash before the report is written) fails closed as infrastructure
/// through the parser. <c>2</c> = could not run (bad flags — usage on
/// stderr, no JSON). <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails
/// loudly as infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> Check implementations change between releases,
/// so findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>staticcheck -version</c> before the scan;
/// a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool
/// — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> staticcheck honors
/// <c>//lint:ignore</c> directives authored inside the audited repository —
/// and the audit subject writes that repository. The tool offers no flag
/// that makes those directives inert, so the base cannot express that gate
/// and this auditor does not hand-roll one. Suppression directives are
/// honored and documented as a limitation (see the plugin README), as is
/// the repository's <c>staticcheck.conf</c> check selection. Operators who
/// need a fully operator-owned ruleset narrow the run with <c>-checks</c>
/// in <c>ExtraArguments</c> — which still does not disable
/// <c>//lint:ignore</c> — and treat a warnings-clean local run that
/// disagrees with the audit as a signal to inspect the diff's suppression
/// comments.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>staticcheck -f json ./...</c>: the whole audited tree as Go packages,
/// with the repository's <c>staticcheck.conf</c> files deciding which
/// checks run. <c>go list ./...</c> — which the package load goes through
/// — already skips <c>vendor/</c> at scan time. On top of that, findings
/// under vendored (<c>vendor/</c>) and upstream-mirror
/// (<c>third_party/</c>) prefixes are dropped by default: problems there
/// belong to upstream packages, not the change under audit, and reporting
/// them trains operators to ignore the auditor. Generated files carry no
/// directory prefix for a path filter to match (staticcheck analyzes files
/// with generated markers like any other source), so they stay visible and
/// are documented as such.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: staticcheck Go Analyzer",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "staticcheck",
    InstallHint = "provision the pinned staticcheck release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `go install "
        + "honnef.co/go/tools/cmd/staticcheck@" + DefaultExpectedVersion + "` (requires a Go "
        + "toolchain) or the pinned prebuilt binary from the dominikh/go-tools releases — no "
        + "distro apt package carries a version pin — through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "go",
    InstallHint = "staticcheck loads packages through `go list` — provision a Go toolchain in "
        + "the sandbox baseline (the go.dev release tarball, or a distro golang package recent "
        + "enough for the audited modules) via CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class StaticcheckAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.staticcheck";

    /// <summary>
    /// staticcheck release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2025.1.1";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran clean (or only below--fail diagnostics, still a JSON
        // verdict); 1 = ran with diagnostics, including package-load
        // failures reported as code:compile records. Both emit the JSON
        // stream — both are verdicts. 2 (usage error) and everything else
        // is infrastructure.
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

    /// <inheritdoc />
    public override string Name => "codeybox:staticcheck";

    /// <inheritdoc />
    protected override string ToolName => "staticcheck";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new StaticcheckJsonOutputParser();

    /// <summary>
    /// Declared mapping from staticcheck's severity vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. Under the default
    /// <c>-fail all</c> every diagnostic reports <c>error</c>, so ordinary
    /// findings fail the audit — blocking by default, stated explicitly.
    /// Diagnostics below the operator's <c>-fail</c> set report
    /// <c>warning</c> (advisory); <c>ignored</c> appears only when the
    /// operator passes <c>-show-ignored</c> and is informational. Raw
    /// strings never reach findings.
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
            ["ignored"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>();

        if (!ExtraArgumentsSupplyFlag(options, "-f"))
        {
            // Machine-readable report on stdout: one JSON object per line.
            // An operator -f flag would replace the JSON the parser expects
            // and break the run into infrastructure failure; let that
            // surface loudly. Matched as "-f" so the separated ("-f json"),
            // attached ("-f=json"), and joined ("-fjson") forms all defer.
            args.Add("-f");
            args.Add("json");
        }

        // The whole audited tree as Go packages; the repository's
        // staticcheck.conf files decide which checks run.
        args.Add("./...");
        return args;
    }

    /// <summary>
    /// staticcheck-specific precondition on the live path: the <c>go</c>
    /// toolchain the package load shells out to (<c>go list</c>), probed
    /// explicitly — its absence otherwise surfaces as a misleading
    /// <c>code:compile</c> finding. An infrastructure failure naming
    /// <c>go</c> — never a pass, never a finding against the diff.
    /// </summary>
    protected override Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ThrowIfBinaryMissingAsync(
            sandbox,
            workingDirectory,
            "go",
            options,
            ct,
            purpose: "audit tool 'staticcheck' requires the 'go' toolchain — staticcheck loads "
                + "packages through `go list`");

    /// <summary>
    /// staticcheck's report carries absolute <c>location.file</c> paths and
    /// no embedded working directory, so the parser relativizes against the
    /// directory the tool actually ran in — resolved through the shared
    /// <c>pwd</c> probe, since sandbox providers may translate
    /// <paramref name="workingDirectory"/> onto a host path.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => await ProbeSandboxWorkingDirectoryAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        context.Logger.LogInformation(
            "StaticcheckAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
