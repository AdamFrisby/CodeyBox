using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SquawkAuditorPlugin;

/// <summary>
/// Schema auditor wrapping <c>squawk</c> (PostgreSQL migration safety) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the squawk JSON report parser
/// (<see cref="SquawkJsonOutputParser"/>), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// squawk-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: blocking — stated explicitly.</b> squawk's
/// severity vocabulary is degenerate: every rule violation reports
/// <c>"Warning"</c> and only unparseable SQL reports <c>"Error"</c> — there is
/// no weaker level for a violated rule. A squawk violation is a dangerous
/// migration pattern (blocking lock, table rewrite, non-reversible change),
/// so the declared mapping sends both levels — and any unrecognised level —
/// to <see cref="AuditSeverity.Error"/>: every reported violation fails the
/// audit. There is no advisory mode; narrow scope with <c>ExcludedRules</c>,
/// <c>ExcludePaths</c>, or <c>Patterns</c> instead.</para>
///
/// <para><b>Exit-code convention (verified against squawk v2.64.0
/// source).</b> squawk does NOT follow the common "1 = findings, 2 = could
/// not run" convention: <c>lint_and_report</c> returns
/// <c>ExitCode::FAILURE</c> (1) both when violations exist and whenever the
/// run fails — glob resolution errors, unreadable files, and config parse
/// errors all <c>process::exit(1)</c> or propagate as <c>Err</c> from
/// <c>main</c>, which Rust also reports as 1. The discriminator is therefore
/// the report itself, not the exit code: a completed scan always writes a
/// JSON violations array to stdout (empty array when clean), while every
/// run failure writes a plain-text error to stderr and no report. Exits
/// <c>0</c> and <c>1</c> are declared findings-producing and a
/// <see cref="SquawkJsonOutputParser"/> that demands a JSON array fails
/// closed — an exit 0/1 without a parseable array is infrastructure, never a
/// pass. Clap usage errors exit <c>2</c>, <c>126</c>/<c>127</c> mean
/// cannot-execute/not-found, and anything else is unknown — all
/// infrastructure.</para>
///
/// <para><b>The unmatched-pattern trap.</b> squawk exits 1 when its patterns
/// match no files (unless <c>--no-error-on-unmatched-pattern</c>), which
/// would turn every repository without SQL migrations into an
/// infrastructure failure. Passing the flag is not enough either: with no
/// matched paths and a non-terminal stdin, squawk falls back to linting
/// stdin, so a scan over an empty file set could block on a read. The
/// auditor appends a <c>/dev/null</c> sentinel to the pattern list: it always
/// resolves to a readable empty file, so squawk never enters its stdin
/// branch and a repository with no SQL produces a deterministic empty report.
/// </para>
///
/// <para><b>Repository-controlled suppression.</b> squawk discovers a
/// <c>.squawk.toml</c> by traversing up from the working directory, and a
/// repository under audit can use it to silence rules wholesale
/// (<c>excluded_rules</c>, <c>excluded_paths</c>) — an auditor its subject
/// can silence is not a gate. The scan therefore pins
/// <c>--config /dev/null</c> by default: an empty config that disables both
/// the repo-root file and any ancestor lookup in the sandbox filesystem.
/// Operators who deliberately trust repository configuration set
/// <c>ConfigPath</c> (an explicit <c>--config</c> path — repo or baseline)
/// or <c>TrustRepositoryConfig</c> (squawk's natural discovery). squawk has
/// no flag to disable the inline <c>squawk-ignore</c> /
/// <c>squawk-ignore-file</c> comments — those remain honored and are a
/// documented residual surface, not a finding the auditor can produce.</para>
///
/// <para><b>Version pin.</b> squawk's rule set, message text, and report
/// shape change between releases, so findings are only meaningful from the
/// build the auditor was verified against. The auditor probes
/// <c>squawk --version</c> before every scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is the glob
/// <c>**/*.sql</c> over the whole work tree — squawk's own
/// <c>find_paths</c> expands it and lints every matched file, so ordinary
/// non-SQL content is out of scope by construction. On top of that, findings
/// under vendored, dependency, and VCS trees (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>, <c>.git/</c>) are dropped by
/// default — problems there describe upstream packages or git internals, not
/// the change under audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Squawk PostgreSQL Migrations",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "squawk",
    InstallHint = "provision the pinned squawk release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "squawk — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class SquawkAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.squawk";

    /// <summary>
    /// squawk release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.64.0";

    /// <summary>
    /// Scoped-config key for scan patterns (comma-separated; each entry
    /// becomes a positional glob argument). Unset → <c>**/*.sql</c>.
    /// </summary>
    public const string PatternsKey = "Patterns";

    /// <summary>
    /// Scoped-config key for <c>--pg-version</c>: a squawk Postgres version
    /// such as <c>16.4</c>. Unset → squawk lints without a version
    /// assumption.
    /// </summary>
    public const string PgVersionKey = "PgVersion";

    /// <summary>
    /// Scoped-config key for squawk's transaction assumption: <c>true</c>
    /// emits <c>--assume-in-transaction</c>, <c>false</c> emits
    /// <c>--no-assume-in-transaction</c>, unset emits neither (squawk's own
    /// default applies).
    /// </summary>
    public const string AssumeInTransactionKey = "AssumeInTransaction";

    /// <summary>
    /// Scoped-config key for an explicit squawk configuration file (passed
    /// as <c>--config</c>). Operator-chosen — may point at a repository
    /// <c>.squawk.toml</c> or a baseline-provisioned path. Supersedes the
    /// default inert pin.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to squawk's natural <c>.squawk.toml</c>
    /// discovery (the repo-root file and ancestor traversal). Default false:
    /// the audited repo must not be able to silence the audit.
    /// </summary>
    public const string TrustRepositoryConfigKey = "TrustRepositoryConfig";

    // Default scan scope: every SQL file under the work tree. squawk treats
    // positional arguments as glob patterns, so a repo-relative glob keeps
    // finding paths repo-relative too.
    private const string DefaultPattern = "**/*.sql";

    // squawk falls back to reading stdin whenever no positional path resolves
    // (empty match set + non-terminal stdin): a repository without SQL files
    // would then either error (without --no-error-on-unmatched-pattern) or
    // block on a stdin read. A guaranteed-present empty file keeps
    // found_paths non-empty in every case, so "no SQL files" is a
    // deterministic clean report instead.
    private const string EmptyFileSentinel = "/dev/null";

    // Neutralizes squawk's config discovery by default: read_to_string of an
    // empty device file parses as an empty TOML document, so neither the
    // audited repository's .squawk.toml nor any ancestor's can drop rules or
    // paths from the scan.
    private const string InertConfigPath = "/dev/null";

    // squawk's clap grammar treats a bare positional matching a subcommand
    // name as that subcommand (server runs the LSP until the outer timeout),
    // and a leading-dash pattern is eaten as a flag — both misconfigurations,
    // so they fail closed deterministically before the scan runs.
    private static readonly HashSet<string> SubcommandNames = new(StringComparer.Ordinal)
    {
        "server",
        "upload-to-github",
        "help",
    };

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean scan; 1 = completed scan with violations — OR a run
        // failure (glob errors, unreadable files, config parse errors all
        // exit 1 too). The JSON array on stdout is the discriminator: run
        // failures emit none, so exit 1 without a report fails closed through
        // the parser. Every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and git internals describe
        // content that is not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", ".git/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _patterns = static () => [];
    private Func<string?> _pgVersion = static () => null;
    private Func<string?> _assumeInTransaction = static () => null;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositoryConfig = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:squawk";

    /// <inheritdoc />
    protected override string ToolName => "squawk";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SquawkJsonOutputParser();

    /// <summary>
    /// Declared mapping from squawk's level vocabulary to
    /// <see cref="AuditSeverity"/>. squawk emits <c>"Warning"</c> for every
    /// violated safety rule and <c>"Error"</c> only for SQL the parser
    /// rejected — both are blocking findings here, and any unrecognised level
    /// from a foreign build fails closed through the declared default. Raw
    /// tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["warning"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
        }, AuditSeverity.Error);

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
            "--reporter", "json",
        };

        // Pin the config file to an empty source so repository-controlled
        // .squawk.toml files (repo root or ancestor) cannot drop rules or
        // exclude paths from the audit. ConfigPath outranks the pin;
        // TrustRepositoryConfig restores squawk's natural discovery; an
        // operator-supplied -c/--config in ExtraArguments outranks all.
        var configPath = _configPath();
        if (!ExtraArgumentsSupplyFlag(options, "-c", "--config"))
        {
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                args.Add("--config");
                args.Add(configPath.Trim());
            }
            else if (!_trustRepositoryConfig())
            {
                args.Add("--config");
                args.Add(InertConfigPath);
            }
        }

        var pgVersion = _pgVersion();
        if (!string.IsNullOrWhiteSpace(pgVersion)
            && !ExtraArgumentsSupplyFlag(options, "--pg-version"))
        {
            args.Add("--pg-version");
            args.Add(pgVersion.Trim());
        }

        var assumeInTransaction = _assumeInTransaction();
        if (!string.IsNullOrWhiteSpace(assumeInTransaction)
            && !ExtraArgumentsSupplyFlag(options, "--assume-in-transaction", "--no-assume-in-transaction"))
        {
            if (!bool.TryParse(assumeInTransaction, out var assume))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has an invalid AssumeInTransaction "
                    + $"('{TruncateForMessage(assumeInTransaction)}'); set "
                    + $"CodeyBox:Plugins:{PluginId}:{AssumeInTransactionKey} to true or false.")
                { IsDeterministic = true };
            args.Add(assume ? "--assume-in-transaction" : "--no-assume-in-transaction");
        }

        var configured = _patterns();
        var patterns = configured.Count == 0 ? [DefaultPattern] : configured;
        var invalid = patterns
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0)
            .Where(static p => p.StartsWith('-')
                || SubcommandNames.Contains(p))
            .ToList();
        if (invalid.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with unusable pattern(s) "
                + $"('{TruncateForMessage(string.Join(' ', invalid))}') — leading-dash values are "
                + "eaten as flags and bare squawk subcommand names (server, upload-to-github, "
                + $"help) parse as commands. Fix CodeyBox:Plugins:{PluginId}:{PatternsKey} to "
                + "repo-relative glob patterns such as '**/*.sql'.")
            { IsDeterministic = true };

        args.AddRange(patterns
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0));

        // Always resolvable, always empty: keeps squawk off its stdin branch
        // when the real patterns match nothing, so "no SQL files" is a clean
        // report — not a block reading stdin or a failed-pattern error.
        args.Add(EmptyFileSentinel);

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _patterns = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[PatternsKey]);
        _pgVersion = () => scoped[PgVersionKey];
        _assumeInTransaction = () => scoped[AssumeInTransactionKey];
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositoryConfig = () =>
            bool.TryParse(scoped[TrustRepositoryConfigKey], out var trust) && trust;
        context.Logger.LogInformation(
            "SquawkAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
