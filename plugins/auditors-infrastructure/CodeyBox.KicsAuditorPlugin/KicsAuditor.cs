using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.KicsAuditorPlugin;

/// <summary>
/// Infrastructure-as-code security auditor wrapping <c>kics</c> (Checkmarx
/// "Keeping Infrastructure as Code Secure") on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the KICS JSON report parser
/// (<see cref="KicsJsonReportParser"/>), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// KICS-specific plumbing below.
///
/// <para><b>Gate behaviour: severity-driven — blocking on Critical/High only.</b>
/// KICS severities go through the declared map, never raw:
/// <c>CRITICAL</c>/<c>HIGH</c> → <see cref="AuditSeverity.Error"/> (fails the
/// audit), <c>MEDIUM</c> → <see cref="AuditSeverity.Warning"/> (advisory),
/// <c>LOW</c>/<c>INFO</c>/<c>TRACE</c> → <see cref="AuditSeverity.Info"/>
/// (informational); anything unrecognised →
/// <see cref="AuditSeverity.Warning"/>. The auditor is a merge gate for
/// critical/high IaC misconfigurations, not a blocker on every note; narrow
/// with <c>MinimumSeverity</c>, <c>ExcludedRules</c>, or
/// <c>ExcludePaths</c>.</para>
///
/// <para><b>Report routing.</b> KICS cannot stream its report: the report
/// writers always create a real file under <c>--output-path</c>, and console
/// output is a human-readable table, not machine output. The report is also
/// secret-bearing — KICS's "Passwords And Secrets" queries embed the matched
/// literal secret in per-file <c>search_key</c>/<c>expected_value</c>/
/// <c>actual_value</c> fields — so it deliberately never rides the captured
/// scan streams: those bytes persist as <see cref="AuditResult.RawOutput"/>,
/// tail into unavailability messages, and ship in webhook payloads. Instead
/// the scan writes <c>results.json</c> as a plain file inside the per-run
/// scratch directory (<see cref="ExternalToolAuditorBase.PerRunTempDirectoryPath"/>),
/// and <see cref="ResolveParserInputAsync(ISandbox, string, string, ExternalToolAuditorOptions, SandboxExecResult, string?, CancellationToken)"/>
/// reads it back through the separate bounded read the base's parser-input
/// seam exists for — the report reaches only the parser. <c>--silent</c>
/// stays pinned so KICS's console table (which echoes affected source
/// lines) does not leak the same content onto captured stdout. A missing,
/// oversized, or unparseable report file fails closed as infrastructure —
/// never a pass.</para>
///
/// <para><b>Exit-code convention (verified against KICS v2.x source).</b>
/// KICS does NOT follow the common "1 = findings" convention: completed scans
/// use semantic exit codes — <c>60</c>/<c>50</c>/<c>40</c>/<c>30</c>/<c>20</c>
/// for CRITICAL/HIGH/MEDIUM/LOW/INFO results matching <c>--fail-on</c>,
/// <c>0</c> for clean (or TRACE-only) results — while engine failures exit
/// <c>126</c> (<c>EngineErrorCode</c>) and interrupts <c>130</c>. This auditor
/// pins <c>--ignore-on-exit results</c>: the report file is written before
/// the exit code is computed, so a completed scan — findings or not — always
/// exits <c>0</c>, and only "could not run" yields a non-zero exit. The
/// semantic codes are still declared findings-producing, so the mapping stays
/// correct if that pin is ever overridden upstream of the flags. Everything
/// else — <c>126</c> engine errors (including flag-parse failures), <c>130</c>
/// interrupts, any other exit — is infrastructure.</para>
///
/// <para><b>Repository-controlled config surface.</b> KICS binds flags through
/// viper: with a single <c>-p</c> target and no <c>--config</c>, it loads a
/// <c>kics.config</c> file sitting next to the scan path — i.e. a file the
/// audited repository could commit to inject <c>exclude-queries</c>,
/// <c>exclude-severities</c>, or other flags that silently empty the report.
/// The auditor therefore always passes <c>--config</c>: a generated empty
/// JSON file in the report directory by default, or the operator's
/// <c>ConfigFile</c> when configured. Only flags absent from argv bind from
/// that file, so even an operator-chosen config cannot redirect the report
/// sink or undo the pinned flags — but it can select queries, so a
/// <c>ConfigFile</c> resolving inside the audited worktree is rejected as a
/// deterministic configuration failure: the audit subject must not
/// influence query selection. The check is resolved once per run inside
/// <see cref="ResolveContextArgumentsAsync(ISandbox, string, AuditContext, ExternalToolAuditorOptions, CancellationToken)"/>
/// — the same value that reaches argv — and both the configured path and the
/// scan cwd are canonicalized in the sandbox with <c>realpath -m</c> before
/// containment, so a relative path, a <c>..</c> segment, or a symlinked
/// component cannot smuggle an in-tree file past the guard.</para>
///
/// <para><b>Version pin.</b> KICS's query corpus changes between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. <c>kics version</c> (printing <c>Keeping Infrastructure as Code
/// Secure X.Y.Z</c>) is probed before every run; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Reserved flags.</b> <c>ExtraArguments</c> is appended to the scan
/// argv by the shared base, so entries that would redirect the report sink,
/// re-open the repo-config surface, fight <c>--silent</c>, or change the exit
/// contract are rejected deterministically (naming the scoped key to use
/// instead) rather than failing later as an opaque parse failure or silently
/// changing what the audit means.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is the whole work tree
/// (<c>-p .</c>); KICS detects IaC formats itself (Terraform, Kubernetes,
/// Docker, CloudFormation, Ansible, OpenAPI, …). Findings under vendored and
/// dependency trees (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) are dropped by default — problems there describe
/// upstream packages, not the change under audit.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: KICS Infrastructure-as-Code Security",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "kics",
    InstallHint = "provision the pinned KICS release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline: unpack the versioned upstream "
        + "release tarball (checkmarx/kics releases) — it carries the kics binary plus the assets/ "
        + "query and library directories the CLI resolves relative to its real executable path — "
        + "verify the checksum, and put the binary on PATH via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class KicsAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.kics";

    /// <summary>
    /// KICS release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.2.0";

    /// <summary>
    /// Scoped-config key for scan targets (comma-separated paths; each entry
    /// becomes a repeatable <c>--path</c> argument). Unset → <c>.</c> (the
    /// whole work tree).
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key for a KICS config file passed verbatim to
    /// <c>--config</c>. Unset → an empty generated config, which also keeps
    /// KICS from auto-loading a <c>kics.config</c> committed in the audited
    /// repository. Must be an absolute path outside the audited worktree —
    /// a config inside the tree would hand the diff author KICS flag
    /// control (query selection, exclusions), so it is rejected
    /// deterministically before the scan runs.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config key for platform restriction (comma-separated KICS
    /// platform ids — <c>terraform</c>, <c>k8s</c>, <c>dockerfile</c>, …;
    /// each becomes a <c>--type</c> entry). Unset → all platforms.
    /// </summary>
    public const string PlatformsKey = "Platforms";

    private const string ReportFileBaseName = "results";
    private const string ReportFileName = ReportFileBaseName + ".json";
    private const string EmptyConfigFileName = "codeybox-empty-kics.config.json";

    // Fixed script — no configuration-derived text: the per-run directory
    // arrives as $1, never spliced into the script. Creates the report dir
    // (mode 700: results.json embeds literal detected secrets, so the fresh
    // directory must not be world-readable under the shared temp area) and
    // the generated empty config that holds the --config flag; the report
    // itself stays a plain file the auditor reads back after the scan (see
    // the class docstring's Report routing paragraph).
    private const string ReportPreparationScript =
        "d=\"$1\""
        + " && mkdir -m 700 -p \"$d\""
        + " && printf '%s\\n' '{}' > \"$d/" + EmptyConfigFileName + "\"";

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // reopen the repository-controlled config surface, fight --silent, or
    // change the declared exit-code contract. Rejected deterministically with
    // a pointer to the scoped key covering the same need.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--output-path", "-o"),      // report sink — ours
        ("--output-name", ""),        // report name — ours
        ("--report-formats", ""),     // report format — pinned to json
        ("--config", ""),             // repo-config surface — use ConfigFile
        ("--ignore-on-exit", ""),     // exit contract — pinned to results
        ("--silent", "-s"),           // stdout purity — pinned on
        ("--ci", ""),                 // mutually exclusive with --silent
        ("--verbose", "-v"),          // mutually exclusive with --silent
        ("--path", "-p"),             // scan scope — use Targets
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // `--ignore-on-exit results` is pinned, so a completed scan exits 0
        // whether or not the report carries findings. The semantic codes
        // (60/50/40/30/20 = CRITICAL/HIGH/MEDIUM/LOW/INFO) are declared too —
        // they remain findings-producing if the pin is ever dropped. Every
        // other exit — 126 engine error (KICS's own EngineErrorCode, also for
        // flag-parse failures), 130 interrupt, anything else — is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 20, 30, 40, 50, 60 },
        // KICS runs its whole rego query corpus over every IaC file in the
        // tree — minutes on large repositories. Bounded well under the
        // shared MaxTimeoutSeconds ceiling; exceeding it is infrastructure,
        // not a pass.
        Timeout = TimeSpan.FromMinutes(10),
        // Findings inside vendored/dependency trees describe upstream
        // templates, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<string?> _configFile = static () => null;
    private Func<IReadOnlyList<string>> _platforms = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:kics";

    /// <inheritdoc />
    protected override string ToolName => "kics";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new KicsJsonReportParser();

    /// <summary>
    /// Declared mapping from KICS's severity vocabulary to
    /// <see cref="AuditSeverity"/>: <c>CRITICAL</c>/<c>HIGH</c> → Error
    /// (blocking), <c>MEDIUM</c> → Warning, <c>LOW</c>/<c>INFO</c>/<c>TRACE</c>
    /// → Info, unrecognised → Warning. Raw tool tokens never reach findings,
    /// so "high" means the same thing as in every other auditor.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        BuildSeverityMapping();

    // Derived from the shared default so vocabulary additions there
    // propagate; KICS adds TRACE (its lowest level) and never emits the
    // generic fail/failure tokens.
    private static ExternalToolSeverityMapping BuildSeverityMapping()
    {
        var levels = new Dictionary<string, AuditSeverity>(
            ExternalToolSeverityMapping.Default.Levels, StringComparer.OrdinalIgnoreCase);
        levels.Remove("fail");
        levels.Remove("failure");
        levels["trace"] = AuditSeverity.Info;
        return new ExternalToolSeverityMapping(levels, AuditSeverity.Warning);
    }

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        RejectReservedExtraArguments(options);

        // The base mints the per-run scratch directory before argv is built;
        // VerifyToolAsync prepares the report directory the argv names. The
        // directory lives outside the audited worktree so the scan never
        // pollutes the diff.
        var reportDir = PerRunTempDirectoryPath;

        var args = new List<string>
        {
            "scan",
            // KICS writes its report only to files under --output-path; the
            // prepared directory keeps <base>.json a plain file the auditor
            // reads back through the parser-input seam (see the class
            // docstring — the report is secret-bearing and never rides the
            // captured scan streams).
            "--output-path", reportDir,
            "--output-name", ReportFileBaseName,
            "--report-formats", "json",
            // Completed scans — findings or not — exit 0; only engine errors
            // exit non-zero (126). The report file is written before KICS
            // computes its semantic result code, so suppressing the result
            // exit loses nothing and collapses the convention to "0 = ran".
            "--ignore-on-exit", "results",
            // --silent is secret hygiene, not cosmetics: without it KICS
            // prints a results table echoing affected source lines — the same
            // content the file-fetch design keeps out of captured output.
            "--silent",
            "--no-progress",
            "--no-color",
        };

        foreach (var platform in _platforms())
        {
            if (string.IsNullOrWhiteSpace(platform))
                continue;
            args.Add("--type");
            args.Add(ValidatedArgumentValue(platform, PlatformsKey));
        }

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        if (targets.Count == 0)
        {
            args.Add("--path");
            args.Add(".");
        }
        else
        {
            foreach (var target in targets)
            {
                args.Add("--path");
                args.Add(ValidatedArgumentValue(target, TargetsKey));
            }
        }

        return args;
    }

    /// <summary>
    /// Emits the <c>--config</c> pair — the one argv element that needs a
    /// bounded sandbox probe to compute. Always set: with a single
    /// <c>-p</c> target KICS would otherwise auto-load a <c>kics.config</c>
    /// sitting in the audited repository, letting the diff author inject
    /// flag values (query exclusions, severity filters). Unset
    /// <c>ConfigFile</c> → the generated empty file in the report directory,
    /// which binds nothing. An operator's <c>ConfigFile</c> is read once
    /// here, canonicalized in the sandbox, rejected when it resolves inside
    /// the worktree, and passed through verbatim — the value validated is
    /// exactly the value argv carries (a mid-run scoped-config reload cannot
    /// split the guard from the flag).
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = ValidatedScopedValue(_configFile(), ConfigFileKey);
        if (configured is null)
            return ["--config", Path.Combine(PerRunTempDirectoryPath, EmptyConfigFileName)];

        // From inside the tree the diff author could bind flags absent from
        // argv (exclude-queries, exclude-severities) and silently empty the
        // report — the shared canonicalize-then-contain guard rejects any
        // resolution into the worktree.
        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, configured, ConfigFileKey, options, ct).ConfigureAwait(false);
        return ["--config", canonical];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _configFile = () => scoped[ConfigFileKey];
        _platforms = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[PlatformsKey]);
        context.Logger.LogInformation(
            "KicsAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Prepares the report directory the scan argv names: a fresh per-run
    /// output directory (outside the worktree) containing the generated
    /// empty <c>--config</c> file. A preparation failure fails closed as
    /// infrastructure naming the tool: the scan cannot produce its report
    /// without the directory.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var reportDir = PerRunTempDirectoryPath;
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "report directory preparation",
            new SandboxExec
            {
                Argv = ["sh", "-c", ReportPreparationScript, "sh", reportDir],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' report directory preparation could not run: "
                + "the sandbox exec transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not prepare its report directory "
                + $"(exit {result.ExitCode}) — without it the scan cannot produce a "
                + "report, so this is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);
    }

    /// <summary>
    /// Reads the report file KICS wrote under the per-run directory through
    /// a separate bounded sandbox read — the reason the report never touches
    /// the captured scan streams: its per-file fields embed matched literal
    /// secrets, and captured output is persisted as
    /// <see cref="AuditResult.RawOutput"/>, tailed into unavailability
    /// messages, and shipped in webhook payloads. The read shares the
    /// configured per-stream capture bound; a missing or oversized report
    /// fails closed as infrastructure. The read's own output is never copied
    /// into a failure message — only <c>cat</c>'s stderr (provider error
    /// text) is carried.
    /// </summary>
    protected override async Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        string? scanRoot,
        CancellationToken ct)
    {
        var reportPath = Path.Combine(PerRunTempDirectoryPath, ReportFileName);
        var read = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "report read",
            new SandboxExec
            {
                Argv = ["cat", reportPath],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = CapturedOutputLimit(options),
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (read.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' report read could not run: the sandbox exec "
                + "transport was unavailable.");
        if (read.StdoutLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' wrote a report exceeding the "
                + $"{CapturedOutputLimit(options)}-byte capture bound — the report is fetched "
                + "through a bounded read, so an oversized one is infrastructure, never a partial "
                + "parse. Raise MaxOutputBytesPerStream or narrow the scan (Targets, Platforms).")
            { IsDeterministic = true };
        if (read.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' completed its scan but produced no readable "
                + $"report file (exit {read.ExitCode}) — a completed scan must leave a report, so "
                + "this is infrastructure, not a verdict on the diff.",
                read.ExitCode,
                read.Stderr);

        return new ExternalToolParseInput(
            tool,
            read.Stdout,
            result.Stderr,
            result.ExitCode,
            ScanRoot: scanRoot,
            WorkingDirectory: workingDirectory);
    }

    /// <summary>
    /// Resolves the absolute scan directory as the tool sees it, so the
    /// parser can relativize the absolute <c>file_name</c> values KICS emits
    /// when scan targets are not repo-relative. Sandbox providers may
    /// translate the audit's working directory, so it is probed rather than
    /// assumed — through the shared <c>pwd</c> probe.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => await ProbeSandboxWorkingDirectoryAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

    private static void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
    {
        var offenders = new List<string>();
        foreach (var (longFlag, shortFlag) in ReservedFlags)
        {
            var flags = shortFlag.Length == 0 ? new[] { longFlag } : new[] { longFlag, shortFlag };
            if (ExtraArgumentsSupplyFlag(options, flags))
                offenders.Add(longFlag);
        }
        if (offenders.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor 'codeybox:kics' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would redirect the report "
            + "sink, re-open the repository-controlled config surface, fight the pinned --silent, or "
            + "change the declared exit-code contract. Use the scoped keys under "
            + $"CodeyBox:Plugins:{PluginId} (Targets, ConfigFile, Platforms) or the shared knobs; "
            + "ExtraArguments is for everything else.")
        { IsDeterministic = true };
    }
}
