using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PyrightAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>pyright</c> (Python static type analysis) on
/// the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the pyright JSON output parser
/// (<see cref="PyrightJsonOutputParser"/> — pyright's built-in
/// <c>--outputjson</c> report, which emits absolute <c>file</c> paths and no
/// embedded cwd, so the parser relativizes against the scan root captured
/// per run through <see cref="ResolveContextArgumentsAsync"/>), the pinned
/// tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the repository-configuration posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Pyright diagnostics at severity <c>error</c> map to
/// <see cref="AuditSeverity.Error"/> and fail the audit; <c>warning</c> maps
/// to <see cref="AuditSeverity.Warning"/> (advisory) and <c>information</c>
/// to <see cref="AuditSeverity.Info"/>. Which rules produce errors is decided
/// by the type-checking contract in force — the audited repository's
/// <c>pyrightconfig.json</c> / <c>[tool.pyright]</c> section, or an
/// operator-pinned config via <c>ProjectPath</c>/<c>--project</c>.
/// <c>MinimumSeverity</c> only drops findings, it never raises them; to make
/// more diagnostics blocking, raise their severity in the ruleset.</para>
///
/// <para><b>Exit-code convention (verified against pyright v1.1.414 — the
/// values are publicly documented upstream, but this auditor does not assume
/// the common "1 = findings" convention holds for every tool).</b>
/// <c>0</c> = analysis completed, no errors reported (warnings and
/// information-level diagnostics may still be in the report);
/// <c>1</c> = analysis completed, one or more errors reported (or warnings
/// when <c>--warnings</c> is passed — this auditor does not pass it). Both
/// emit the JSON report on stdout, so both are findings-producing verdicts.
/// <c>2</c> = fatal error with no diagnostics (could not run);
/// <c>3</c> = configuration file could not be read or parsed;
/// <c>4</c> = illegal command-line parameters — all are "could not run" and
/// classified as infrastructure. <c>126</c>/<c>127</c> = cannot execute /
/// not found — infrastructure. Exit <c>0</c>/<c>1</c> with no parseable JSON
/// report on stdout fails closed as infrastructure through the parser.
/// Anything else is an unknown convention and fails loudly rather than being
/// guessed.</para>
///
/// <para><b>Version pin.</b> A type checker's rule implementations and its
/// diagnostic vocabulary change between releases, so findings are only
/// meaningful from the build the auditor was verified against. The auditor
/// probes <c>pyright --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression — stated, not hidden.</b>
/// Pyright honors <c># type: ignore</c> and <c># pyright: ignore</c>
/// suppression comments authored inside the audited repository — and the
/// audit subject writes that repository. Unlike ESLint's
/// <c>--no-inline-config</c>, pyright offers no CLI flag that makes those
/// comments inert (<c>enableTypeIgnoreComments</c> is config-only, and the
/// config is repo-authored), so the base cannot express that gate and this
/// auditor does not hand-roll one: suppression comments stay honored. The
/// same is true one level up — the repo's <c>pyrightconfig.json</c> or
/// <c>pyproject.toml</c> decides the type-checking mode and the include /
/// exclude sets, and a repo could weaken its own contract (e.g.
/// <c>"typeCheckingMode": "off"</c>). That posture matches the sibling
/// linters: the project's own analysis contract is the meaningful check —
/// and unlike ESLint's executable config, pyright's is declarative JSON, so
/// honoring it carries no code-execution risk; the audit still runs with
/// <see cref="AuditCapabilities.None"/>. Operators who need a
/// fully operator-owned ruleset pin one via <c>ProjectPath</c> (see the
/// README for pyright's project-root caveat) and treat a local run that
/// disagrees with the audit as a signal to inspect the diff's suppression
/// comments and config changes.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>pyright --outputjson</c>
/// with no positional arguments: pyright analyzes the project rooted at the
/// audit worktree, honoring the repository's include/exclude configuration
/// — the project's own declaration of type-checkable scope. On top of that,
/// findings under vendored (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>), interpreter-environment (<c>.venv/</c>,
/// <c>venv/</c>, <c>.tox/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>) prefixes are dropped by default: problems
/// there belong to installed packages or build output, not the change under
/// audit, and reporting them trains operators to ignore the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Pyright Python Type Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "pyright",
    InstallHint = "provision the pinned pyright release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g pyright@"
        + DefaultExpectedVersion + ") — the pip 'pyright' package is a wrapper that downloads the same "
        + "npm release; no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class PyrightAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.pyright";

    /// <summary>
    /// Pyright release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.1.414";

    /// <summary>
    /// Scoped-config key for an explicit pyright project configuration —
    /// passed to <c>--project</c>, which accepts a config file or a directory
    /// containing <c>pyrightconfig.json</c>. Note pyright derives the project
    /// root (and therefore the analysis scope) from that location, so an
    /// out-of-repo config must retarget the repository through its own
    /// include/execution-environments entries or the scan looks at the wrong
    /// tree.
    /// </summary>
    public const string ProjectPathKey = "ProjectPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = no errors reported; 1 = one or more errors reported. Both emit
        // the JSON report on stdout — both are verdicts. 2 (fatal, no
        // diagnostics), 3 (config unreadable/unparseable), 4 (illegal
        // parameters) and everything else is "could not run": infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Whole-tree static type analysis is slower than a lint pass; bound
        // it generously but finitely. Probes share the 30s cap.
        Timeout = TimeSpan.FromMinutes(10),
        // The report is one JSON document — a truncated stdout cannot be
        // partially parsed and fails closed as infrastructure. 4 MiB keeps
        // ordinary finding volumes parseable instead of turning a flooded
        // report into a transport failure.
        MaxOutputBytesPerStream = 4 * 1024 * 1024,
        // Findings in vendored/dependency trees, interpreter environments,
        // and generated build output describe code that is not the change
        // under audit — noise that trains operators to ignore the auditor.
        // Operators re-include a path by overriding ExcludePaths in scoped
        // config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", ".venv/", "venv/", ".tox/", "dist/", "build/", "out/", "coverage/"],
    };

    private readonly PyrightJsonOutputParser _parser = new();
    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _projectPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:pyright";

    /// <inheritdoc />
    protected override string ToolName => "pyright";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser => _parser;

    /// <summary>
    /// Declared mapping from pyright's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Pyright reports <c>error</c>,
    /// <c>warning</c>, and <c>information</c> (only those categories reach
    /// the JSON report); the neighbouring levels common to other scanners are
    /// mapped identically so a future diagnostic shape carrying them is not a
    /// unique dialect. Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
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
            // Machine-readable report on stdout; pyright redirects all console
            // chatter to stderr in this mode, so stdout stays pure JSON.
            "--outputjson",
        };

        var projectPath = _projectPath();
        if (!string.IsNullOrWhiteSpace(projectPath)
            && !ExtraArgumentsSupplyFlag(options, "--project", "-p"))
        {
            args.Add("--project");
            args.Add(projectPath.Trim());
        }

        // No positional file arguments: pyright analyzes the project rooted
        // at the worktree per the config's include/exclude, and positional
        // arguments are mutually exclusive with --project anyway.
        return args;
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Pyright's report carries absolute `file` paths and no embedded cwd,
        // so the parser relativizes against the directory the tool actually
        // ran in. That is not necessarily the `workingDirectory` string:
        // sandbox providers may translate it (the process provider maps
        // "/work" onto a host temp path), so it is resolved here with a
        // bounded `pwd` probe — the same cwd the scan will see. RunAsync
        // resolves context arguments before it executes the tool and parses
        // its output — that ordering is the base's contract — and tool
        // auditors run sequentially, so the captured root cannot be
        // overwritten by a concurrent run before the parse reads it.
        _parser.ScanRoot = await ResolveScanRootAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);
        return [];
    }

    private async Task<string> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // `pwd` is a shell builtin — the audited repository cannot shadow it
        // via PATH — and it prints the process's own logical cwd, which is
        // exactly the path prefix pyright embeds in its absolute `file` values.
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "scan-root probe",
            new SandboxExec
            {
                Argv = ["sh", "-c", "pwd", "sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' scan-root probe could not run: the sandbox exec "
                + "transport was unavailable.");

        var root = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (result.ExitCode != 0 || string.IsNullOrEmpty(root))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve the scan root (exit "
                + $"{result.ExitCode}) — the worktree root must be resolvable for findings to be "
                + "reported repository-relative.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        return PyrightJsonOutputParser.NormalizePath(root);
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _projectPath = () => scoped[ProjectPathKey];
        context.Logger.LogInformation(
            "PyrightAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
