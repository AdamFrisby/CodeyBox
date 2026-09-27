using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.RubocopAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>rubocop</c> (Ruby analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (RuboCop's built-in
/// <c>--format json</c>, parsed by <see cref="RubocopJsonOutputParser"/> —
/// no extra formatter package to pin), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> RuboCop offenses at severity <c>error</c> or <c>fatal</c>
/// map to <see cref="AuditSeverity.Error"/> and fail the audit; offenses at
/// <c>warning</c> map to <see cref="AuditSeverity.Warning"/> and are
/// advisory; <c>convention</c>, <c>refactor</c>, and <c>info</c> map to
/// <see cref="AuditSeverity.Info"/> and are advisory. Which severity a cop
/// reports is decided by the configuration in force — the audited
/// repository's <c>.rubocop.yml</c> by default, or an operator-pinned config
/// via <c>ConfigPath</c>/<c>--config</c>. To make style findings blocking,
/// set <c>MinimumSeverity</c> to <c>info</c> (the default — everything is
/// reported) and gate on <c>Passed</c> accordingly, or harden the ruleset;
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against the RuboCop source —
/// <c>STATUS_SUCCESS = 0</c>, <c>STATUS_OFFENSES = 1</c>,
/// <c>STATUS_ERROR = 2</c> in <c>lib/rubocop/cli.rb</c> — not assumed from
/// the common table).</b> <c>0</c> = scanned clean (empty JSON
/// <c>offenses</c>); <c>1</c> = scanned with offenses (JSON report on
/// stdout). Both are findings-producing verdicts. <c>2</c> = could not run:
/// bad flags, unreadable config, unknown cop names, or a crash — warnings
/// go to stderr and stdout carries no report, so the JSON parser fails
/// closed as infrastructure. <c>126</c>/<c>127</c> = cannot execute / not
/// found — infrastructure. Anything else (including the <c>SIGINT+128</c>
/// interrupted status) is an unknown convention and fails loudly as
/// infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A linter's cop implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>rubocop --version</c> before the
/// scan; a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding. The shared first-<c>major.minor.patch</c>
/// extraction fits this tool: the version number leads the probe output.</para>
///
/// <para><b>Repository-controlled suppression.</b> RuboCop honors inline
/// disable comments (<c># rubocop:disable …</c>) authored inside the audited
/// repository — and the audit subject writes that repository. An auditor its
/// subject can silence is not a gate, so by default the scan passes
/// <c>--ignore-disable-comments</c>, making every such comment inert;
/// findings then surface for code the comments would have suppressed.
/// Operators who deliberately trust repo-authored suppression set
/// <c>TrustRepositorySuppression</c> in scoped config. Note the separate,
/// larger surface: <c>.rubocop.yml</c> itself is repo-authored (it selects
/// cops and severities — the project's own lint contract is the meaningful
/// check); operators who need a fully operator-owned ruleset pin one
/// outside the repository via <c>ConfigPath</c> or <c>--config</c> +
/// <c>--force-default-config</c> in <c>ExtraArguments</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>rubocop .</c>: RuboCop's
/// own default excludes (verified: <c>node_modules/**/*</c>,
/// <c>tmp/**/*</c>, <c>vendor/**/*</c>, <c>.git/**/*</c>) plus the
/// repository configuration's <c>Exclude</c> list decide which files are
/// checked. <c>--cache false</c> keeps the scan from writing a cache entry
/// for the audited tree. On top of that, the finding-level
/// <c>ExcludePaths</c> backstop drops findings under vendored and generated
/// prefixes — RuboCop reports scan-relative paths, so the backstop matches
/// them directly. Operators narrow scope further with <c>--only</c> /
/// <c>--except</c> in <c>ExtraArguments</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: RuboCop Ruby Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "rubocop",
    InstallHint = "provision the pinned rubocop release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via gem (gem install rubocop -v "
        + DefaultExpectedVersion + ") or bundler — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class RubocopAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.rubocop";

    /// <summary>
    /// RuboCop release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.91.0";

    /// <summary>Scoped-config key for an explicit RuboCop configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// RuboCop's <c># rubocop:disable</c> comments. Default false: the
    /// audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = checked clean; 1 = offenses found. Both emit the JSON report
        // — both are verdicts. 2 (usage/config error, crash) and everything
        // else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/", "tmp/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:rubocop";

    /// <inheritdoc />
    protected override string ToolName => "rubocop";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new RubocopJsonOutputParser();

    /// <summary>
    /// Declared mapping from RuboCop's severity vocabulary
    /// (<c>lib/rubocop/cop/severity.rb</c>: <c>info</c>, <c>refactor</c>,
    /// <c>convention</c>, <c>warning</c>, <c>error</c>, <c>fatal</c>) to
    /// CodeyBox's <see cref="AuditSeverity"/>. Only <c>error</c> and
    /// <c>fatal</c> fail the audit; style-level severities stay advisory.
    /// Raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["convention"] = AuditSeverity.Info,
            ["refactor"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
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
            // Machine-readable JSON report on stdout for the parser. An
            // operator --format would replace (or, as a repeatable flag,
            // corrupt with a second formatter's output) the JSON the parser
            // expects and break the run into infrastructure failure; let that
            // surface loudly.
            args.Add("--format");
            args.Add("json");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--cache", "-C"))
        {
            // Never write a cache entry for the audited tree; the audit must
            // not mutate its subject (nor trust a stale cache about it).
            args.Add("--cache");
            args.Add("false");
        }

        // The audit subject authors rubocop:disable comments; keep them
        // inert unless the operator opts in to repo-controlled suppression.
        if (!_trustRepositorySuppression()
            && !ExtraArgumentsSupplyFlag(options, "--ignore-disable-comments"))
            args.Add("--ignore-disable-comments");

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
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "RubocopAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
