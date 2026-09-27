using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ImportLinterAuditorPlugin;

/// <summary>
/// Architecture auditor wrapping <c>lint-imports</c> (Import Linter — Python
/// architecture/import-boundary contracts) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report parser
/// (<see cref="ImportLinterTextOutputParser"/> — lint-imports emits text
/// only; there is no structured-output flag), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// defaults below.
///
/// <para><b>Gate behaviour: blocking for broken contracts; advisory for
/// warnings.</b> lint-imports has no severity scale — a contract either holds
/// or is broken — so every entry under <c>Broken contracts</c> is reported at
/// tool level <c>"error"</c> → <see cref="AuditSeverity.Error"/> and fails the
/// audit, while entries under <c>Warnings</c> (e.g. unmatched
/// <c>ignore_imports</c> with <c>unmatched_ignore_imports_alerting = warn</c>)
/// are advisory <see cref="AuditSeverity.Warning"/> findings. The declared map
/// covers the wider level vocabulary anyway so nothing raw passes through.
/// <c>MinimumSeverity</c> can only lower this posture (dropping warnings or
/// errors), never raise it.</para>
///
/// <para><b>Exit-code convention (verified against import-linter 2.15 —
/// deliberately NOT the usual linter table).</b> lint-imports has exactly two
/// exits: <c>0</c> = all contracts kept, and <c>1</c> = <em>either</em> broken
/// contracts <em>or</em> could not run — every failure path (missing or
/// unreadable config, invalid contract options rendered as a could-not-run
/// report, unknown <c>--contract</c> id, caught exception) is swallowed to
/// exit 1 by <c>use_cases.lint_imports</c>. Click usage errors exit 2;
/// 126/127 = cannot execute / not found. Because exit 1 is ambiguous, the
/// parser — not the exit code — carries the classification: only a report
/// ending in <c>Contracts: N kept, M broken.</c> counts as a verdict, and a
/// non-zero exit whose report shows zero broken contracts fails closed.
/// Missing output on a verdict exit (crash, foreign stdout) is likewise
/// infrastructure, never a pass.</para>
///
/// <para><b>Version pin.</b> Contract types and the rendered report change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes
/// <c>lint-imports --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding. import-linter versions are PEP 440
/// (<c>2.15</c>), while the shared pin compares a three-component token —
/// <see cref="ExtractImportLinterVersion"/> zero-pads the reported release
/// (<c>2.15</c> → <c>2.15.0</c>, equal under PEP 440), so
/// <c>ExpectedVersion</c> is always the three-part form.</para>
///
/// <para><b>Repository-controlled suppression.</b> The contract set is
/// entirely repo-authored — import-linter reads <c>pyproject.toml</c>
/// <c>[tool.importlinter]</c>, <c>setup.cfg [importlinter]</c>, or
/// <c>.importlinter</c>, and contract <c>ignore_imports</c> suppress
/// individual violations. The project's own declared contracts are the
/// meaningful check (changes to them are visible in the audited diff), and
/// the auditor runs with <see cref="AuditCapabilities.None"/>. Operators who
/// need an operator-owned contract set pin an out-of-repo file via
/// <c>ConfigPath</c> — a repository without any import-linter configuration
/// is an infrastructure failure ("Could not read any configuration."), not a
/// pass.</para>
///
/// <para><b>Scope and defaults.</b> The scan is the whole repository:
/// <c>lint-imports</c> builds an import graph over the configured
/// <c>root_packages</c>. <c>--no-cache</c> keeps it from writing
/// <c>.import_linter_cache</c> into the audited tree and <c>--no-logo</c>
/// keeps the report parseable text. <c>TERM=dumb</c> in the tool environment
/// keeps a baseline <c>FORCE_COLOR</c> from injecting Rich ANSI escapes and
/// live-progress control codes into stdout (verified: rich renders plain,
/// unstyled text on a dumb terminal even when FORCE_COLOR is set).
/// Findings under vendored (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>) module paths are dropped by default:
/// violations there belong to upstream packages or build output, not the
/// change under audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Import Linter Architecture Contracts",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "lint-imports",
    InstallHint = "provision the pinned import-linter release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via pip or pipx (pip install "
        + "'import-linter==2.15.0' — pip resolves the two-part 2.15 release by PEP 440 zero-padding) "
        + "together with a Python 3 interpreter — no distro apt package "
        + "carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class ImportLinterAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.import-linter";

    /// <summary>
    /// import-linter release the invocation and its report are verified
    /// against, in the three-component form the shared pin expects —
    /// <c>lint-imports --version</c> prints <c>import-linter 2.15</c> and
    /// <c>2.15</c> pads to <c>2.15.0</c> under PEP 440. Operators running a
    /// different pinned build set <c>ExpectedVersion</c> in the plugin's
    /// scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.15.0";

    /// <summary>Scoped-config key for an explicit import-linter configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly System.Text.RegularExpressions.Regex ReportedVersionPattern = new(
        @"\d+\.\d+(?:\.\d+)*",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = contracts kept; 1 = broken contracts OR could not run — the
        // parser separates the two via the report summary line. 2 (click
        // usage error) and everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings under vendored/dependency and generated module paths
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Paths here are module
        // names rendered slash-separated ("vendor/foo"); operators
        // re-include a path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/", ".venv/", "venv/", ".tox/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:import-linter";

    /// <inheritdoc />
    protected override string ToolName => "lint-imports";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ImportLinterTextOutputParser();

    /// <summary>
    /// Declared mapping from the levels the parser emits (and the wider
    /// vocabulary a future report shape might carry) to CodeyBox's
    /// <see cref="AuditSeverity"/>: broken-contract entries are
    /// <c>"error"</c> → <see cref="AuditSeverity.Error"/> and block; report
    /// warnings are <c>"warning"</c> → <see cref="AuditSeverity.Warning"/> and
    /// stay advisory. Raw levels never reach findings.
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
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"], ExtractImportLinterVersion);

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? BuildToolEnvironment(
        ExternalToolAuditorOptions options)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // lint-imports renders through Rich: a baseline FORCE_COLOR (or a
            // provider-allocated pty) would inject ANSI escapes and
            // live-progress cursor codes into stdout and corrupt the report
            // the parser reads. Verified on 2.15: a dumb TERM renders plain,
            // unwrapped-at-80 text even with FORCE_COLOR set.
            ["TERM"] = "dumb",
        };

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>();

        // Skip the ASCII logo so the report stays plain text.
        if (!ExtraArgumentsSupplyFlag(options, "--no-logo"))
            args.Add("--no-logo");

        // Never write .import_linter_cache into the audited tree; the audit
        // must not mutate its subject. An operator --cache-dir or --no-cache
        // defers the setting.
        if (!ExtraArgumentsSupplyFlag(options, "--no-cache")
            && !ExtraArgumentsSupplyFlag(options, "--cache-dir"))
            args.Add("--no-cache");

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        return args;
    }

    /// <summary>
    /// Extracts the reported version from <c>lint-imports --version</c>
    /// output (<c>import-linter 2.15</c>) and zero-pads it to a
    /// three-component release token — import-linter releases are PEP 440
    /// (<c>2.15</c>, occasionally <c>2.5.1</c>), while the shared pin
    /// compares a <c>major.minor.patch</c> string. Returns null when the
    /// output carries no version token so the pin fails closed.
    /// </summary>
    internal static string? ExtractImportLinterVersion(string output)
    {
        var match = ReportedVersionPattern.Match(output ?? string.Empty);
        if (!match.Success)
            return null;
        var version = match.Value.TrimEnd('.');
        if (version.Length == 0)
            return null;
        var components = version.Split('.').Length;
        return components switch
        {
            2 => version + ".0",
            >= 3 => version,
            _ => null,
        };
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
            "ImportLinterAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
