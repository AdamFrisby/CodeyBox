using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SqlfluffAuditorPlugin;

/// <summary>
/// Schema auditor wrapping <c>sqlfluff</c> (SQL linting) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the sqlfluff JSON report parser
/// (<see cref="SqlfluffJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// sqlfluff-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — stated
/// explicitly.</b> sqlfluff's severity vocabulary is a single boolean per
/// violation: <c>warning: false</c> is a rule violation that fails the run,
/// <c>warning: true</c> is a rule the repository's configuration downgraded
/// via its <c>warnings</c> list. The declared mapping sends the former to
/// <see cref="AuditSeverity.Error"/> (blocking) and the latter to
/// <see cref="AuditSeverity.Warning"/> (advisory). Unparseable SQL
/// (<c>PRS</c>) is a non-warning violation and blocks. Operators narrow
/// scope with <c>ExcludedRules</c>, <c>ExcludePaths</c>, <c>Paths</c>, or
/// <c>MinimumSeverity</c> instead of an advisory-only switch, which this
/// auditor deliberately does not offer.</para>
///
/// <para><b>Exit-code convention (verified against sqlfluff 3.4.2 — not
/// assumed from the common table).</b> <c>0</c> = ran clean (JSON array with
/// violation-free file entries, or <c>[]</c> when nothing matched);
/// <c>0</c> with <c>warning: true</c> violations also occurs when the
/// repository downgraded every violated rule via <c>warnings</c> — still a
/// verdict, with advisory findings. <c>1</c> = ran with violations (JSON
/// report on stdout). Both are findings-producing verdicts because the
/// parser demands the array. In contrast, <c>2</c> = could not run: bad
/// flags, unknown dialect, missing dialect with no repository default,
/// unreadable <c>--config</c> file, or nonexistent paths — stdout carries no
/// report, so the run fails closed as infrastructure. Unexecutable or
/// missing binaries exit <c>126</c>/<c>127</c> and are classified as
/// infrastructure.</para>
///
/// <para><b>Dialect is required and operator-owned by default.</b> sqlfluff
/// has no built-in default dialect: without <c>--dialect</c> and without a
/// repository <c>.sqlfluff</c> dialect it exits 2. The auditor therefore
/// passes <c>--dialect</c> from the <c>Dialect</c> knob (default
/// <c>ansi</c>), which overrides any repository-configured dialect — the
/// same binary must parse the same way regardless of what the audited tree
/// claims. Operators linting a single-dialect warehouse set <c>Dialect</c>
/// to it (e.g. <c>postgres</c>); clearing <c>Dialect</c> defers to the
/// repository file and fails closed (exit 2, infrastructure) when the
/// repository configures none.</para>
///
/// <para><b>Repository-controlled suppression.</b> sqlfluff honors inline
/// <c>-- noqa</c> comments authored inside the audited repository — and the
/// audit subject writes that repository. An auditor its subject can silence
/// is not a gate, so by default the scan passes <c>--disable-noqa</c>,
/// making every inline suppression inert; findings then surface for SQL the
/// comments would have suppressed. Operators who deliberately trust
/// repo-authored suppression set <c>TrustRepositorySuppression</c> in scoped
/// config. The repository's ruleset configuration (<c>.sqlfluff</c>,
/// <c>setup.cfg</c>, <c>tox.ini</c>, <c>pyproject.toml</c>) and its
/// <c>.sqlfluffignore</c> file remain honored — rule selection and excludes
/// there decide what is checked, as with the repository <c>ruff.toml</c> of
/// the Ruff auditor — and are documented as residual surfaces in the plugin
/// README. An explicit operator <c>ConfigPath</c> layers an additional
/// config file over them.</para>
///
/// <para><b>Version pin.</b> sqlfluff's rule implementations, message text,
/// and report shape change between releases, so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>sqlfluff --version</c> before every scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is <c>sqlfluff lint .
/// </c> over the whole work tree: sqlfluff itself selects <c>*.sql</c> files
/// (plus any dialect-appropriate extensions it recognises), so non-SQL
/// content is out of scope by construction. A directory with no SQL files
/// reports <c>[]</c> and passes — there is no stdin fallback to guard.
/// Findings under vendored, dependency, and VCS trees (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>, <c>.git/</c>) are dropped by
/// default — problems there describe upstream packages or git internals, not
/// the change under audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Sqlfluff SQL Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "sqlfluff",
    InstallHint = "provision the pinned sqlfluff release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via pip (pip install sqlfluff=="
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class SqlfluffAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.sqlfluff";

    /// <summary>
    /// sqlfluff release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "3.4.2";

    /// <summary>
    /// Scoped-config key for the SQL dialect passed as <c>--dialect</c>
    /// (e.g. <c>ansi</c>, <c>postgres</c>, <c>bigquery</c>). Defaults to
    /// <c>ansi</c>. Blank defers to the repository <c>.sqlfluff</c>
    /// configuration — which fails closed when the repository configures no
    /// dialect either.
    /// </summary>
    public const string DialectKey = "Dialect";

    /// <summary>Default <see cref="DialectKey"/> value.</summary>
    public const string DefaultDialect = "ansi";

    /// <summary>
    /// Scoped-config key for scan targets (comma-separated; each entry
    /// becomes a positional path argument). Unset → the work-tree root
    /// (<c>.</c>). Entries starting with <c>-</c>, and the bare stdin
    /// sentinel <c>-</c>, are rejected deterministically.
    /// </summary>
    public const string PathsKey = "Paths";

    /// <summary>
    /// Scoped-config key for an additional sqlfluff configuration file
    /// (passed as <c>--config</c>, layered over the repository files).
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// inline <c>noqa</c> comments. Default false: the audited repo must not
    /// be able to silence the audit.
    /// </summary>
    public const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // Default scan scope: the whole work tree. sqlfluff selects the SQL
    // files itself, so ordinary non-SQL content is out of scope by
    // construction, and a tree with no SQL reports [] and passes.
    private const string DefaultPath = ".";

    // sqlfluff reads audit input from stdin for a lone "-" path, and a
    // leading-dash path is eaten as a flag — both misconfigurations, so
    // they fail closed deterministically before the scan runs.

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran clean (or only repository-downgraded warnings, which the
        // report still carries as advisory findings); 1 = ran with
        // violations. Both emit the JSON array — both are verdicts. 2
        // (usage/config/dialect/path errors) carries no report, so the
        // parser fails closed; every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and git internals describe
        // content that is not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", ".git/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _dialect = static () => DefaultDialect;
    private Func<IReadOnlyList<string>> _paths = static () => [];
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:sqlfluff";

    /// <inheritdoc />
    protected override string ToolName => "sqlfluff";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SqlfluffJsonOutputParser();

    /// <summary>
    /// Declared mapping from the parser's level vocabulary to
    /// <see cref="AuditSeverity"/>. The parser emits <c>"error"</c> for
    /// rule violations that fail the run (including unparseable-SQL
    /// <c>PRS</c> findings) and <c>"warning"</c> for rules the repository
    /// configuration downgraded via its <c>warnings</c> list; the wider map
    /// keeps the mapping total so no spelling passes through raw. Raw tool
    /// values never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["notice"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string> { "lint" };

        // Machine-readable per-file report for the JSON parser. An operator
        // --format would replace the JSON the parser expects and break the
        // run into infrastructure failure; let that surface loudly.
        if (!ExtraArgumentsSupplyFlag(options, "--format", "-f"))
        {
            args.Add("--format");
            args.Add("json");
        }

        // sqlfluff has no built-in default dialect; without --dialect and
        // without a repository .sqlfluff dialect it exits 2. The knob
        // overrides repository configuration by design (see class summary);
        // a blank value defers to it. Defer to an operator --dialect too.
        var dialect = _dialect();
        if (!string.IsNullOrWhiteSpace(dialect)
            && !ExtraArgumentsSupplyFlag(options, "--dialect", "-d"))
        {
            args.Add("--dialect");
            args.Add(dialect.Trim());
        }

        // Deterministic machine output: no progress rendering on stderr, so
        // captured streams carry only the report (stdout) and real errors.
        if (!ExtraArgumentsSupplyFlag(options, "--disable-progress-bar"))
            args.Add("--disable-progress-bar");

        // The audit subject authors noqa comments; keep them inert unless
        // the operator opts in to repo-controlled suppression.
        if (!_trustRepositorySuppression()
            && !ExtraArgumentsSupplyFlag(options, "--disable-noqa"))
            args.Add("--disable-noqa");

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        var configured = _paths();
        var paths = configured.Count == 0 ? [DefaultPath] : configured;
        var invalid = paths
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0)
            .Where(static p => p.StartsWith('-'))
            .ToList();
        if (invalid.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with unusable path(s) "
                + $"('{TruncateForMessage(string.Join(' ', invalid))}') — leading-dash values are "
                + "eaten as flags and the bare '-' reads audit input from stdin. Fix "
                + $"CodeyBox:Plugins:{PluginId}:{PathsKey} to repository-relative file or "
                + "directory paths such as '.' or 'migrations'.")
            { IsDeterministic = true };

        args.AddRange(paths
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0));

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _dialect = () => scoped[DialectKey] ?? DefaultDialect;
        _paths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[PathsKey]);
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "SqlfluffAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
