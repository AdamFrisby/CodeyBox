using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.RuffAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>ruff</c> (Python analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (ruff's built-in SARIF
/// emitter, parsed by the shared <see cref="SarifToolOutputParser"/> — no
/// custom parser), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the default
/// include-set / exclusion posture below.
///
/// <para><b>Gate behaviour: blocking — every finding fails the audit.</b>
/// Ruff's SARIF emitter reports each diagnostic at level <c>error</c>
/// (verified against 0.14.7 across rule violations and syntax errors), which
/// maps to <see cref="AuditSeverity.Error"/>. The declared map still
/// translates the full level vocabulary so a future ruff emitting
/// <c>warning</c>/<c>note</c> levels degrades to advisory instead of becoming
/// a unique dialect. <c>MinimumSeverity</c> is honored by the shared
/// mechanism but has no effect while ruff reports a single level — there is
/// no advisory-only mode for this auditor by design: a ruff diagnostic is a
/// violated rule, and the gate treats it as blocking.</para>
///
/// <para><b>Exit-code convention (verified against ruff 0.14.7 — not assumed
/// from the common table).</b> <c>0</c> = checked clean (empty SARIF
/// <c>results</c>); <c>1</c> = checked with violations (SARIF report on
/// stdout). Both are findings-producing verdicts. <c>2</c> = could not run:
/// bad flags, unreadable config, or config errors — stdout is empty, so the
/// SARIF parser fails closed as infrastructure. <c>126</c>/<c>127</c> =
/// cannot execute / not found — infrastructure. Anything else is an unknown
/// convention and fails loudly as infrastructure rather than being
/// guessed.</para>
///
/// <para><b>Version pin.</b> A linter's rule implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>ruff --version</c> before the
/// scan; a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Ruff honors <c># noqa</c>
/// suppression comments authored inside the audited repository — and the audit
/// subject writes that repository. An auditor its subject can silence is not
/// a gate, so by default the scan passes <c>--ignore-noqa</c>, making every
/// <c># noqa</c> comment inert; findings then surface for code the comments
/// would have suppressed. Operators who deliberately trust repo-authored
/// suppression set <c>TrustRepositorySuppression</c> in scoped config. Note
/// the separate, larger surface: the repo's <c>ruff.toml</c> /
/// <c>pyproject.toml [tool.ruff]</c> ruleset is honored (its rule selection
/// and <c>exclude</c> list decide what is checked), and the auditor runs with
/// <see cref="AuditCapabilities.None"/>. Operators who need a fully
/// operator-owned ruleset pass <c>--isolated</c> plus <c>--select</c> in
/// <c>ExtraArguments</c>, or pin an out-of-repo file via
/// <c>ConfigPath</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>ruff check .</c>: ruff's
/// own default excludes (verified: <c>.venv/</c>, <c>venv/</c>,
/// <c>dist/</c>, <c>build/</c>, <c>node_modules/</c>, <c>__pycache__/</c> and
/// friends) plus the repository configuration's <c>exclude</c> list decide
/// which files are checked. <c>--no-cache</c> keeps the scan from writing a
/// <c>.ruff_cache</c> directory into the audited tree. On top of that, the
/// finding-level <c>ExcludePaths</c> backstop drops findings under vendored
/// and generated prefixes — with one tool-imposed limit, documented in the
/// plugin README: ruff reports absolute <c>file://</c> artifact URIs, so the
/// shared SARIF parser preserves sandbox-absolute paths and a repo-relative
/// prefix entry cannot match them. The defaults stay (they filter any
/// relative paths and document intent), ruff's own excludes do the primary
/// vendored filtering, and operators narrow scope further with
/// <c>--exclude</c> in <c>ExtraArguments</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Ruff Python Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "ruff",
    InstallHint = "provision the pinned ruff release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via pip (pip install ruff=="
        + DefaultExpectedVersion + ") or the standalone binary from the astral-sh/ruff releases — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class RuffAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.ruff";

    /// <summary>
    /// Ruff release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.14.7";

    /// <summary>Scoped-config key for an explicit ruff configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// ruff's <c># noqa</c> comments. Default false: the audited repo must
    /// not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = checked clean; 1 = violations found. Both emit the SARIF
        // report — both are verdicts. 2 (usage/config error) and everything
        // else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config. Note ruff's own
        // default excludes already skip .venv/, dist/, build/ and friends at
        // scan time (see the class summary for the absolute-URI limit on
        // this finding-level backstop).
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/", ".venv/", "venv/", "__pycache__/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:ruff";

    /// <inheritdoc />
    protected override string ToolName => "ruff";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from ruff's SARIF level vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Ruff 0.14.7 reports every diagnostic at
    /// <c>error</c> — rule violations and syntax errors alike — so every
    /// finding fails the audit; the wider map keeps a future ruff emitting
    /// <c>warning</c>/<c>note</c> levels from becoming an unmapped dialect.
    /// Raw levels never reach findings.
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
        var args = new List<string> { "check" };

        if (!ExtraArgumentsSupplyFlag(options, "--output-format"))
        {
            // Machine-readable SARIF report for the shared parser. An
            // operator --output-format would replace the SARIF the parser
            // expects and break the run into infrastructure failure; let that
            // surface loudly.
            args.Add("--output-format");
            args.Add("sarif");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--no-cache", "-n"))
        {
            // Never write a .ruff_cache directory into the audited tree; the
            // audit must not mutate its subject. Ruff rejects a repeated
            // flag (exit 2), so defer to the operator's own setting.
            args.Add("--no-cache");
        }

        // The audit subject authors # noqa comments; keep them inert unless
        // the operator opts in to repo-controlled suppression. Ruff rejects
        // a repeated flag (exit 2), so defer to the operator's own setting.
        if (!_trustRepositorySuppression()
            && !ExtraArgumentsSupplyFlag(options, "--ignore-noqa"))
            args.Add("--ignore-noqa");

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config"))
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
            "RuffAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
