using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BrakemanAuditorPlugin;

/// <summary>
/// Ruby on Rails security auditor wrapping the Brakeman CLI
/// (<c>brakeman</c>) on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout,
/// per-stream output caps, SARIF parsing, severity mapping, exit-code
/// classification, and per-auditor configuration. This class adds the
/// Brakeman invocation shape (<c>brakeman</c> with SARIF on stdout), the
/// declared severity map over Brakeman's confidence vocabulary, the pinned
/// tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Brakeman confidences go through a declared map, never raw:
/// <c>High</c> (plus SARIF <c>error</c>) → <see cref="AuditSeverity.Error"/>
/// (fails the audit); <c>Medium</c> (plus SARIF <c>warning</c>) → <see
/// cref="AuditSeverity.Warning"/> (advisory); <c>Weak</c> (plus SARIF
/// <c>note</c>/<c>none</c>) → <see cref="AuditSeverity.Info"/>
/// (informational); anything unrecognised → <see
/// cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> can only drop
/// findings, it never raises them. The auditor is therefore a merge gate
/// for high-confidence Rails vulnerabilities, not a blocker on every weak
/// hint.</para>
///
/// <para><b>Exit-code convention (verified against Brakeman 8.0.6 source:
/// <c>lib/brakeman.rb</c> exit-code constants and
/// <c>lib/brakeman/commandline.rb</c>).</b> Brakeman exits <c>0</c> when the
/// scan completes with no warnings, and <c>3</c>
/// (<c>Warnings_Found_Exit_Code</c>) when warnings were found — both carry
/// the SARIF report, so both are findings-producing. Every other exit means
/// "could not run" and is infrastructure: <c>7</c>
/// (<c>Errors_Found_Exit_Code</c>, scan errors with no warnings to report),
/// <c>4</c> (<c>No_App_Found_Exit_Code</c>, no Rails application detected),
/// <c>6</c> (<c>Missing_Checks_Exit_Code</c>, unknown check names),
/// <c>5</c>/<c>8</c>/<c>9</c> (version/ignore-note bookkeeping),
/// <c>-1</c> (invalid options), and <c>126</c>/<c>127</c>
/// (cannot-execute/not-found). In particular a repository that is not a
/// Rails application exits <c>4</c>: scope this auditor to Rails projects
/// rather than reading that as a pass.</para>
///
/// <para><b>Version pin.</b> A scanner's checks change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>brakeman --version</c> before every run
/// (the CLI prints <c>brakeman X.Y.Z</c>); a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Brakeman honors an ignore
/// file authored inside the audited repository
/// (<c>config/brakeman.ignore</c> by default) — and the audit subject writes
/// that repository. An auditor its subject can silence is not a gate, so by
/// default the scan passes <c>--show-ignored</c>, which keeps ignored
/// warnings in the report (marked with SARIF <c>suppressions</c>) without
/// letting them soften the exit code; findings then surface for code the
/// ignore file would have hidden. Operators who deliberately trust
/// repo-authored suppression set <c>TrustRepositorySuppression</c> in
/// scoped config. A Brakeman YAML config file
/// (<c>config/brakeman.yml</c>) in the audited tree is likewise read by the
/// tool when present — keep scanner-affecting options out of it, or point
/// the tool at an operator-owned file with <c>-c</c> via
/// <c>ExtraArguments</c>; paths there must resolve inside the sandbox, not
/// the audited tree.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Brakeman Rails SAST",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "brakeman",
    InstallHint = "provision the pinned Brakeman release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a Ruby gem "
        + "(gem install brakeman --version "
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class BrakemanAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.brakeman";

    /// <summary>
    /// Brakeman release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "8.0.6";

    /// <summary>
    /// Scoped-config key opting in to repository-authored ignore-file
    /// suppression. Default false: the audited repo must not be able to
    /// silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Brakeman exits 0 when the scan completes with no warnings and 3
        // (Warnings_Found_Exit_Code) when warnings were found — both carry
        // the SARIF report. Every other exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 3 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Brakeman already skips vendor/ at scan time unless
        // told otherwise; this filter drops findings under the remaining
        // vendored prefixes. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:brakeman";

    /// <inheritdoc />
    protected override string ToolName => "brakeman";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from Brakeman's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Brakeman assigns each warning a
    /// confidence (<c>High</c>, <c>Medium</c>, <c>Weak</c>) and normalizes it
    /// to a SARIF level (<c>error</c>, <c>warning</c>, <c>note</c>) in
    /// <c>lib/brakeman/report/report_sarif.rb</c>; both vocabularies are
    /// translated here so a severity means the same thing regardless of
    /// which scanner produced it — raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["weak"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Structured argv, never a shell string: the base appends the
        // operator's ExtraArguments after these entries.
        var args = new List<string>
        {
            // Quiet plus no pager: everything except the report already goes
            // to stderr, and the pager must never hold a sandbox exec open.
            "--quiet",
            "--no-pager",
            // SARIF is one of several formats; pin it explicitly because the
            // parser reads stdout and any other format would fail closed in
            // the parser rather than silently produce no findings.
            "--format", "sarif",
            // /dev/stdout (Linux sandboxes) is the documented stdout sink
            // for Brakeman reports.
            "--output", "/dev/stdout",
            // The audited worktree: the base runs with WorkingDirectory set
            // to it, so the relative path keeps report URIs repo-relative.
            "--path", ".",
        };

        // The audit subject authors the ignore file; keep its entries
        // visible in the report unless the operator opts in to
        // repo-controlled suppression.
        if (!_trustRepositorySuppression())
            args.Add("--show-ignored");

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "BrakemanAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
