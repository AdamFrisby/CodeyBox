using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CodeqlAuditorPlugin;

/// <summary>
/// Deep dataflow security auditor wrapping the CodeQL CLI (<c>codeql</c>) on
/// the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps, SARIF
/// parsing, severity mapping, exit-code classification, and per-auditor
/// configuration. This class adds the CodeQL two-phase invocation (database
/// creation plus analysis), the CodeQL SARIF severity recovery
/// (<see cref="CodeqlSarifOutputParser"/>), and the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>.
///
/// <para><b>Two phases, one verdict.</b> CodeQL cannot analyze source
/// directly: <c>codeql database create</c> first extracts the source root
/// into a relational database, then <c>codeql database analyze</c> runs the
/// queries and emits SARIF. The base builds the scan argv before any probe
/// runs, so the database path is agreed through an <see
/// cref="AsyncLocal{T}"/> handoff: <see
/// cref="BuildToolArguments(ExternalToolAuditorOptions)"/> mints a fresh
/// per-run directory under the system temp area, and <see
/// cref="VerifyToolAsync(ISandbox, string, string, ExternalToolAuditorOptions, CancellationToken)"/>
/// creates the database there through the base's bounded exec helper (same
/// timeout bounding and failure classification as the scan — not a
/// hand-rolled invocation). The <c>AsyncLocal</c> (not a field) is load
/// bearing: auditor instances are DI singletons shared across concurrent
/// audits, so per-run state must flow with the invocation, not sit on the
/// instance. Databases live outside the audited worktree so the scan never
/// pollutes the diff or trips sibling auditors; they are per-run
/// GUID-suffixed directories the sandbox discards with its temp area.</para>
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> CodeQL query severities go through a declared map, never
/// raw: <c>error</c> → <see cref="AuditSeverity.Error"/> (fails the audit),
/// <c>warning</c> → <see cref="AuditSeverity.Warning"/> (advisory),
/// <c>note</c>, <c>none</c>, and <c>recommendation</c> → <see
/// cref="AuditSeverity.Info"/> (informational); anything unrecognised →
/// <see cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> only drops
/// findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against CodeQL 2.27.1).</b>
/// <c>database analyze</c> exits <c>0</c> whenever the analysis completes —
/// whether or not any alert was produced; the verdict is in the SARIF
/// document, not the exit code. There is deliberately no separate
/// "found something" exit: only <c>0</c> is findings-producing, and every
/// non-zero exit (verified: <c>2</c> for an unknown language, a missing
/// database, or an analysis failure; <c>126</c>/<c>127</c> for
/// cannot-execute/not-found) is infrastructure. If a future CodeQL release
/// ever exits non-zero alongside results, the run fails closed as
/// infrastructure — loud, never a silent pass.</para>
///
/// <para><b>Version pin.</b> A scanner's queries change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>codeql version</c> before every run (the
/// CLI prints <c>CodeQL command-line toolchain release X.Y.Z.</c>); a
/// missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Single language per configuration.</b> <c>database create</c>
/// rejects a multi-language database without <c>--db-cluster</c> (exit 2),
/// so the scan covers exactly one <c>Language</c> (default <c>csharp</c>).
/// The value is checked against CodeQL's documented language identifiers
/// (including its alternative identifiers) before anything executes; an
/// unknown language is a deterministic infrastructure failure and the scan
/// never runs. With no <c>QuerySuites</c> configured the analysis runs
/// CodeQL's default queries for that language.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: CodeQL Deep Dataflow SAST",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "codeql",
    InstallHint = "provision the pinned CodeQL bundle release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline: download codeql-bundle-linux64 "
        + "from the codeql-action releases (the bundle carries the CLI plus the query packs a "
        + "bare CLI download lacks), verify the checksum, and put its codeql/ directory on PATH "
        + "via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class CodeqlAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.codeql";

    /// <summary>
    /// CodeQL release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.27.1";

    /// <summary>Scoped-config key for the CodeQL language identifier to analyze.</summary>
    public const string LanguageKey = "Language";

    /// <summary>Scoped-config key for CodeQL query suites/packs overriding the language defaults.</summary>
    public const string QuerySuitesKey = "QuerySuites";

    /// <summary>Language analyzed when the operator does not configure one.</summary>
    public const string DefaultLanguage = "csharp";

    /// <summary>
    /// CodeQL's documented language identifiers, including the alternative
    /// identifiers it accepts in place of the canonical ones. Compared
    /// case-insensitively; the trimmed lowercase value is passed to the CLI.
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownLanguages = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "c-cpp", "c", "cpp",
        "csharp",
        "actions",
        "go",
        "java-kotlin", "java", "kotlin",
        "javascript-typescript", "javascript", "typescript",
        "python",
        "ruby",
        "rust",
        "swift",
    };

    private const string DatabaseDirectoryPrefix = "codeybox-codeql-";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // codeql database analyze exits 0 whenever the analysis completes —
        // with or without alerts. Every non-zero exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0 },
        // Deep dataflow over a full repository routinely takes minutes: the
        // default bounds each phase (database creation, analysis) separately,
        // so a full run may take up to twice this. Operators tune it with
        // TimeoutSeconds; the base still caps it at MaxTimeoutSeconds.
        Timeout = TimeSpan.FromMinutes(20),
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    // Per-run database root, minted in BuildToolArguments (which the base
    // invokes before VerifyToolAsync) and consumed in VerifyToolAsync and the
    // scan argv. AsyncLocal — not a field — because auditor instances are
    // shared singletons: concurrent audits must not see each other's paths.
    private readonly AsyncLocal<string?> _databaseRoot = new();

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _language = static () => DefaultLanguage;
    private Func<IReadOnlyList<string>> _querySuites = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:codeql";

    /// <inheritdoc />
    protected override string ToolName => "codeql";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new CodeqlSarifOutputParser();

    /// <summary>
    /// Declared mapping from CodeQL's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. CodeQL queries carry
    /// <c>problem.severity</c> of <c>error</c>, <c>warning</c>, or
    /// <c>recommendation</c>, surfaced in SARIF rule metadata as
    /// <c>defaultConfiguration.level</c> (<c>error</c>, <c>warning</c>,
    /// <c>note</c>, <c>none</c>); the parser recovers that token per result
    /// and it is translated here, never passed through.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["recommendation"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Minted here — not in VerifyToolAsync — because the base builds the
        // scan argv before running any probe. VerifyToolAsync consumes this
        // same path to create the database the scan analyzes; the argv below
        // already carries it. The leaf itself is the database: `database
        // create` requires the parent to exist, and the system temp directory
        // always does, so no setup step is needed.
        var root = Path.Combine(Path.GetTempPath(), DatabaseDirectoryPrefix + Guid.NewGuid().ToString("N"));
        _databaseRoot.Value = root;

        var args = new List<string>
        {
            "database", "analyze",
            root,
            "--format", "sarifv2.1.0",
            // "--output -" does NOT stream SARIF: analyze creates a literal
            // file named "-". /dev/stdout (Linux sandboxes) is the documented
            // stdout sink the parser reads.
            "--output", "/dev/stdout",
            // analyze prints diagnostics/metrics summaries to stdout by
            // default, which would corrupt the SARIF document the parser
            // reads; progress logs stay on stderr where they belong.
            "--no-print-diagnostics-summary",
            "--no-print-metrics-summary",
        };

        foreach (var suite in _querySuites())
        {
            if (!string.IsNullOrWhiteSpace(suite))
                args.Add(suite.Trim());
        }

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _language = () => string.IsNullOrWhiteSpace(scoped[LanguageKey]) ? DefaultLanguage : scoped[LanguageKey];
        _querySuites = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[QuerySuitesKey]);
        context.Logger.LogInformation(
            "CodeqlAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the CodeQL database the scan analyzes, using the base's
    /// bounded exec helper: a creation failure (unknown language rejected
    /// earlier excluded) is infrastructure naming the tool, never a pass.
    /// The database is built from the audited worktree at a per-run temp
    /// path outside it, with <c>--overwrite</c> so a stale directory from an
    /// uncleaned previous run cannot fail the creation.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var root = _databaseRoot.Value;
        if (string.IsNullOrWhiteSpace(root))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' database path was not initialized for this run.")
            { IsDeterministic = true };

        var language = ResolveLanguage(tool, _language());
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "database create",
            new SandboxExec
            {
                Argv = [
                    tool,
                    "database", "create",
                    root,
                    "--language=" + language,
                    "--source-root=.",
                    "--overwrite",
                ],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            EffectiveTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' database creation could not run: the sandbox exec "
                + "transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not create the {language} database for the "
                + $"audited repository (exit {result.ExitCode}) — without a database there is nothing to "
                + "analyze, so this is infrastructure, not a verdict on the diff. Compiled languages need "
                + "their build toolchain in the audit sandbox; see the plugin README.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);
    }

    private static string ResolveLanguage(string tool, string? configured)
    {
        var language = configured?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(language) || !KnownLanguages.Contains(language))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' was configured with unknown language "
                + $"'{Truncate(configured)}'. Set CodeyBox:Plugins:{PluginId}:{LanguageKey} to a CodeQL "
                + $"language identifier ({string.Join(", ", KnownLanguages.OrderBy(static l => l, StringComparer.Ordinal))}).")
            { IsDeterministic = true };
        return language;
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > 64 ? single[..64] + "…" : single;
    }
}
