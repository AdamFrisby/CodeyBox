using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DevSkimAuditorPlugin;

/// <summary>
/// Insecure-API-usage auditor wrapping the DevSkim CLI (<c>devskim</c>) on
/// the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps, SARIF
/// parsing, severity mapping, exit-code classification, and per-auditor
/// configuration. This class adds the DevSkim invocation shape
/// (<c>devskim analyze</c> with SARIF on stdout), the declared severity map
/// over DevSkim's native vocabulary, the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> DevSkim severities go through a declared map, never raw:
/// <c>Critical</c> and <c>Important</c> (plus SARIF <c>error</c>) → <see
/// cref="AuditSeverity.Error"/> (fails the audit); <c>Moderate</c> (plus
/// SARIF <c>warning</c>) → <see cref="AuditSeverity.Warning"/> (advisory);
/// <c>BestPractice</c> and <c>ManualReview</c> (plus SARIF <c>note</c>/
/// <c>none</c>) → <see cref="AuditSeverity.Info"/> (informational); anything
/// unrecognised → <see cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c>
/// can only drop findings, it never raises them. The auditor is therefore a
/// merge gate for critical/important insecure API usage, not a blocker on
/// every BestPractice note.</para>
///
/// <para><b>Exit-code convention (verified against DevSkim 1.0.90).</b>
/// <c>devskim analyze</c> exits <c>0</c> whenever the scan completes — whether
/// or not any issue was produced; the verdict is in the SARIF document, not
/// the exit code. There is deliberately no separate "found something" exit:
/// only <c>0</c> is findings-producing, and every non-zero exit (verified:
/// <c>254</c> for an unreadable source path, <c>134</c> for a crash on an
/// invalid rules path; <c>126</c>/<c>127</c> for cannot-execute/not-found) is
/// infrastructure. DevSkim's <c>-E</c> flag redefines the exit code to the
/// issue count — never pass it (including via <c>ExtraArguments</c>): any
/// exit it produces outside the declared set fails closed as infrastructure.
/// If a future DevSkim release ever exits non-zero alongside results, the run
/// fails closed as infrastructure — loud, never a silent pass.</para>
///
/// <para><b>Version pin.</b> A scanner's rules change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>devskim --version</c> before every run (the
/// CLI prints <c>devskim X.Y.Z+commit.</c>); a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> DevSkim honors comment
/// suppressions authored inside the audited repository — and the audit
/// subject writes that repository. An auditor its subject can silence is not
/// a gate, so by default the scan passes the tool's
/// <c>--disable-supression</c> flag (the tool's own spelling), making
/// suppression comments inert; findings then surface for code the comments
/// would have suppressed. Operators who deliberately trust repo-authored
/// suppression set <c>TrustRepositorySuppression</c> in scoped config. No
/// DevSkim config file is needed or honored from the audited tree: the
/// default rules are embedded in the tool, and custom rules or language
/// definitions come only from operator-supplied paths outside the repository
/// via <c>ExtraArguments</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: DevSkim Insecure API Usage",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "devskim",
    InstallHint = "provision the pinned DevSkim release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a .NET global tool "
        + "(dotnet tool install --global Microsoft.CST.DevSkim.CLI --version "
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class DevSkimAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.devskim";

    /// <summary>
    /// DevSkim release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.0.90";

    /// <summary>
    /// Scoped-config key opting in to repository-authored comment
    /// suppressions. Default false: the audited repo must not be able to
    /// silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // devskim analyze exits 0 whenever the scan completes — with or
        // without issues. Every non-zero exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:devskim";

    /// <inheritdoc />
    protected override string ToolName => "devskim";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from DevSkim's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. DevSkim reports its native severities
    /// (<c>Critical</c>, <c>Important</c>, <c>Moderate</c>,
    /// <c>BestPractice</c>, <c>ManualReview</c>) in SARIF rule metadata and
    /// normalizes each result to a SARIF level (<c>error</c>, <c>warning</c>,
    /// <c>note</c>, <c>none</c>); both vocabularies are translated here so a
    /// severity means the same thing regardless of which scanner produced it
    /// — raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = AuditSeverity.Error,
            ["important"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["moderate"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["bestpractice"] = AuditSeverity.Info,
            ["best-practice"] = AuditSeverity.Info,
            ["manualreview"] = AuditSeverity.Info,
            ["manual-review"] = AuditSeverity.Info,
            ["recommendation"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
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
            "analyze",
            // The audited worktree: the base runs with WorkingDirectory set
            // to it, so the relative path keeps artifact URIs repo-relative.
            "-I", ".",
            // SARIF is the default file format, but pin it explicitly: the
            // parser reads stdout and any other format would fail closed in
            // the parser rather than silently produce no findings.
            "-f", "sarif",
            // Console logging shares stdout with the SARIF document on some
            // releases; disable it so the report is the whole of stdout.
            "--disable-console",
        };

        // The audit subject authors suppression comments; keep them inert
        // unless the operator opts in to repo-controlled suppression.
        // (The flag's spelling is the tool's own.)
        if (!_trustRepositorySuppression())
            args.Add("--disable-supression");

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
            "DevSkimAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
