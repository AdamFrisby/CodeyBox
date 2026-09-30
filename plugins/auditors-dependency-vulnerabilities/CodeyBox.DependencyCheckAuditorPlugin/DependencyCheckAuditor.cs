using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DependencyCheckAuditorPlugin;

/// <summary>
/// Dependency vulnerability auditor wrapping OWASP <c>dependency-check</c>
/// (CVE matching against the audited repository's dependency tree — Java,
/// .NET, npm, Python, Go, Ruby, and more) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the tool's JSON report parser
/// (<see cref="DependencyCheckJsonReportParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// report-directory preparation via
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, the operator-owned
/// suppression plumbing via
/// <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync"/>, and
/// the dependency-check-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (blocking on Error).</b> The
/// tool's CVSS severities map as <c>CRITICAL</c>/<c>HIGH</c> to
/// <see cref="AuditSeverity.Error"/> — those findings fail the audit;
/// <c>MEDIUM</c> (and unscored <c>Unknown</c>) to
/// <see cref="AuditSeverity.Warning"/> is advisory and <c>LOW</c>/
/// <c>NONE</c> to <see cref="AuditSeverity.Info"/> is informational.
/// <c>MinimumSeverity</c> only drops findings, it never raises them. This
/// auditor is therefore blocking for high/critical dependency CVEs by
/// default — stated here, not smuggled in.</para>
///
/// <para><b>Report routing.</b> dependency-check cannot stream its report:
/// every format is written as a real file under <c>--out</c>, and console
/// output is progress logging, not machine output. The scan writes
/// <c>dependency-check-report.json</c> as a plain file inside the per-run
/// scratch directory (<see cref="ExternalToolAuditorBase.PerRunTempDirectoryPath"/>),
/// and <see cref="ResolveParserInputAsync(ISandbox, string, string, ExternalToolAuditorOptions, SandboxExecResult, string?, CancellationToken)"/>
/// reads it back through the separate bounded read the base's parser-input
/// seam exists for — the report reaches only the parser, never the persisted
/// <see cref="AuditResult.RawOutput"/>. A missing, oversized, or
/// unparseable report file fails closed as infrastructure — never a
/// pass.</para>
///
/// <para><b>Why JSON, not SARIF.</b> dependency-check's SARIF template
/// hardcodes <c>"level": "warning"</c> on every result (verified in the
/// 12.1.0 <c>sarifReport.vsl</c>), so SARIF levels carry no severity signal
/// and every finding would land at one mapped severity. The native JSON
/// report instead carries the per-vulnerability <c>severity</c> from the
/// CVSS base-severity enums — the tool's real severity vocabulary — which
/// the declared mapping below converts to CodeyBox severities.</para>
///
/// <para><b>Exit-code convention (verified against the dependency-check
/// 12.1.0 <c>App</c> source).</b> dependency-check does NOT follow the
/// common "1 = findings" convention. With the pinned
/// <c>--failOnCVSS 0</c>, a completed scan exits <c>0</c> when no
/// vulnerability exists and <c>15</c> (<c>determineReturnCode</c>) when at
/// least one does — in both cases the JSON report is written and is the
/// verdict. Every other exit means "could not run": <c>1</c>/<c>2</c> CLI
/// parse failures, <c>4</c> bad settings, <c>8</c>/<c>9</c> update
/// failures, <c>11</c> database errors, <c>12</c> report errors,
/// <c>13</c>/<c>14</c> fatal/non-fatal analysis errors (note: <c>14</c> is
/// raised after reports are written, but the scan is still incomplete, so
/// it stays infrastructure), and <c>126</c>/<c>127</c> cannot-execute. The
/// <c>0</c> default of <c>--failOnCVSS</c> is <c>11</c> (never fail), which
/// is why the flag is always passed explicitly rather than trusted to a
/// default.</para>
///
/// <para><b>Version pin.</b> dependency-check's analyzers, CPE matching, and
/// report shape change between releases, so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>dependency-check --version</c> (printing
/// <c>dependency-check version X.Y.Z</c>) before every scan; a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> dependency-check loads
/// suppression files, hints, and property files only from explicit CLI
/// flags (verified in the 12.1.0 <c>App.populateSettings</c> — no working
/// directory auto-discovery), so a <c>suppression.xml</c> committed in the
/// audited repository is inert unless an operator hands it to the tool.
/// The auditor therefore gates the only path that could: operator-configured
/// <c>SuppressionPaths</c> entries are canonicalized in the sandbox and
/// rejected when they resolve inside the audited worktree — the audit
/// subject must not author the file that silences its own findings.
/// (The centrally published hosted-suppressions feed IS applied
/// automatically over the network; that is upstream data, not
/// repository-controlled.)</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>--scan .</c> — the
/// whole worktree, every analyzer the tool ships. Findings inside vendored
/// or generated trees (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) describe upstream code and usually duplicate the
/// manifest-declared finding for the same package, so they are excluded by
/// default; operators re-include a path by overriding
/// <c>ExcludePaths</c>.</para>
///
/// <para><b>Network and database.</b> dependency-check downloads and
/// validates the NVD CVE feed, the hosted-suppressions feed, RetireJS data,
/// and per-scan OSS Index answers, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> and those hosts must be in the
/// deployment's <c>AuditToolAllowedHosts</c> egress list. From 12.x the NVD
/// API requires an API key for timely updates: provision
/// <c>NVD_API_KEY</c> in the baseline environment (or pass
/// <c>--nvdApiKey</c> via <c>ExtraArguments</c>); without a key, updates
/// throttle and cold runs can exceed the timeout — which fails closed as
/// infrastructure, never as a pass. Fully offline deployments pre-seed the
/// data directory into the baseline image and set <c>NoUpdate</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Dependency-Check Dependency Vulnerabilities",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "dependency-check",
    InstallHint = "provision the pinned dependency-check release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — unpack the versioned upstream "
        + "release archive (https://github.com/dependency-check/DependencyCheck/releases) with "
        + "its bin/dependency-check on PATH via CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions, pre-seed the NVD data directory "
        + "so cold sandbox runs do not pay a multi-hundred-MB download inside the default "
        + "timeout, and provide an NVD API key (NVD_API_KEY); no distro apt package carries a "
        + "pinned dependency-check")]
public sealed class DependencyCheckAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.dependency-check";

    /// <summary>
    /// dependency-check release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "12.1.0";

    /// <summary>
    /// Scoped-config key for operator-owned suppression XML files
    /// (comma-separated paths, each becoming a repeatable
    /// <c>--suppression</c> argument). Unset → no suppression. Every entry
    /// must resolve outside the audited worktree — a suppression file the
    /// audit subject authors could silence its own findings, so an in-tree
    /// resolution is a deterministic configuration failure.
    /// </summary>
    public const string SuppressionPathsKey = "SuppressionPaths";

    /// <summary>
    /// Scoped-config boolean for <c>--noupdate</c>: never fetch NVD,
    /// hosted-suppression, or RetireJS updates — requires a pre-seeded data
    /// directory in the baseline. Per-scan OSS Index queries may still need
    /// egress; the auditor keeps declaring
    /// <see cref="AuditCapabilities.Network"/>.
    /// </summary>
    public const string NoUpdateKey = "NoUpdate";

    /// <summary>Report file name dependency-check writes for <c>--format JSON</c> into a directory target.</summary>
    internal const string ReportFileName = "dependency-check-report.json";

    // Fixed script — no configuration-derived text: the per-run directory
    // arrives as $1, never spliced into the script. Creates the report dir
    // (mode 700 under the shared temp area) the scan argv names; the report
    // itself stays a plain file the auditor reads back after the scan.
    private const string ReportPreparationScript =
        "d=\"$1\""
        + " && mkdir -m 700 -p \"$d\"";

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // rescope the scan, change the declared exit-code contract, or skip the
    // scan the report must come from. Rejected deterministically (naming the
    // scoped key to use instead) rather than failing later as an opaque
    // parse failure or silently changing what the audit means.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--out", "-o"),            // report sink — ours
        ("--format", "-f"),         // report format — pinned to JSON
        ("--scan", "-s"),           // scan scope — the worktree root
        ("--failOnCVSS", ""),       // exit contract — pinned to 0
        ("--updateonly", ""),       // skips the scan — use NoUpdate
        // Suppression and property files steer what the gate measures: they
        // travel only through the guarded SuppressionPaths knob (canonicalized
        // outside the worktree), never verbatim from ExtraArguments, where an
        // in-tree path would let the audit subject silence its own findings.
        // A property file can additionally re-point suppression, hints, and
        // analyzer switches, so it has no knob — bake such tuning into the
        // baseline image instead.
        ("--suppression", ""),
        ("--propertyfile", "-P"),
        ("--hints", ""),
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against the dependency-check 12.1.0 App source: with the
        // pinned --failOnCVSS 0, 0 is "scan completed, no vulnerabilities"
        // and 15 is "scan completed, report written, at least one
        // vulnerability". Every other exit — 1/2 CLI parse, 4 bad settings,
        // 8/9 update failures, 11 database, 12 report, 13/14 analysis
        // (14 fires after reports are written but the scan is still
        // incomplete), 126/127 cannot-execute — means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 15 },
        // dependency-check runs its full analyzer set over the tree and
        // validates a multi-hundred-MB CVE feed on cold runs — minutes, not
        // seconds. Bounded well under the shared MaxTimeoutSeconds ceiling;
        // exceeding it is infrastructure, not a pass. Pre-seed the data
        // directory so steady-state runs need a fraction of this.
        Timeout = TimeSpan.FromMinutes(10),
        // The JSON report embeds per-dependency evidence (file, vendor,
        // product, version strings) for every analyzed library: megabytes
        // on large trees. The shared 1 MiB default would clip real reports
        // into fail-closed infrastructure noise.
        MaxOutputBytesPerStream = 8 * 1024 * 1024,
        // Findings inside vendored/dependency trees describe upstream code
        // and usually duplicate the manifest-declared finding for the same
        // package — noise that trains operators to ignore the auditor.
        // Operators re-include a path by overriding ExcludePaths in scoped
        // config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _suppressionPaths = static () => [];
    private Func<bool> _noUpdate = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:dependency-check";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "dependency-check";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new DependencyCheckJsonReportParser();

    /// <summary>
    /// Declared mapping from dependency-check's JSON severity vocabulary
    /// (the CVSS base-severity enums plus the unscored marker) to
    /// <see cref="AuditSeverity"/>: <c>CRITICAL</c>/<c>HIGH</c> → Error
    /// (blocking), <c>MEDIUM</c> → Warning, <c>LOW</c>/<c>NONE</c> → Info,
    /// unscored <c>Unknown</c> → Warning (advisory: worth triage, not a
    /// gate), unrecognised → Warning. Raw tool tokens never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["low"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["unknown"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        RejectReservedExtraArguments(options);

        // The base mints the per-run scratch directory before argv is built;
        // VerifyToolAsync prepares the report directory the argv names. The
        // directory lives outside the audited worktree so the scan never
        // pollutes the diff. The --out target names the exact report file
        // (a path ending in .json is used verbatim, not treated as a
        // directory to append the default name to).
        var reportPath = Path.Combine(PerRunTempDirectoryPath, ReportFileName);

        var args = new List<string>
        {
            // Whole-worktree scan: every analyzer the tool ships runs over
            // the tree; dependency-check resolves manifests and binaries
            // itself.
            "--scan", ".",
            "--format", "JSON",
            "--out", reportPath,
            // Pinned, not a knob: the exit-code contract above depends on
            // it. 0 is dependency-check's lowest threshold — any recorded
            // vulnerability trips exit 15, separating "ran with matches"
            // from "could not run". The JSON report — not the exit code —
            // is the verdict.
            "--failOnCVSS", "0",
        };

        if (_noUpdate())
            args.Add("--noupdate");

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _suppressionPaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[SuppressionPathsKey]);
        _noUpdate = () => bool.TryParse(scoped[NoUpdateKey], out var noUpdate) && noUpdate;
        context.Logger.LogInformation(
            "DependencyCheckAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Emits one <c>--suppression</c> pair per configured
    /// <see cref="SuppressionPathsKey"/> entry — the one argv element that
    /// needs bounded sandbox probes to compute. Each entry is
    /// canonicalized in the sandbox and rejected when it resolves inside
    /// the worktree: a suppression file silences findings, so the audit
    /// subject must not author it. The value validated is exactly the value
    /// argv carries (a mid-run scoped-config reload cannot split the guard
    /// from the flag).
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var extra = new List<string>();
        foreach (var entry in _suppressionPaths())
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var canonical = await CanonicalizeOutsideWorktreeAsync(
                sandbox, workingDirectory, entry.Trim(), SuppressionPathsKey, options, ct).ConfigureAwait(false);
            extra.Add("--suppression");
            extra.Add(canonical);
        }

        return extra;
    }

    /// <summary>
    /// Prepares the report directory the scan argv names: a fresh per-run
    /// output directory (outside the worktree). A preparation failure fails
    /// closed as infrastructure naming the tool: the scan cannot produce
    /// its report without the directory.
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
    /// Reads the JSON report file dependency-check wrote under the per-run
    /// directory through a separate bounded sandbox read — the report never
    /// touches the captured scan streams (stdout is progress logging, and
    /// captured output is persisted as <see cref="AuditResult.RawOutput"/>).
    /// The read shares the configured per-stream capture bound; a missing
    /// or oversized report fails closed as infrastructure. The read's own
    /// output is never copied into a failure message — only <c>cat</c>'s
    /// stderr (provider error text) is carried.
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
                + "parse. Raise MaxOutputBytesPerStream or narrow the scan with ExcludePaths.")
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
    /// parser can relativize the absolute <c>filePath</c> values the JSON
    /// report embeds. Sandbox providers may translate the audit's working
    /// directory, so it is probed rather than assumed — through the shared
    /// <c>pwd</c> probe.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => await ProbeSandboxWorkingDirectoryAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

    private void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
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
            $"could-not-verify: auditor '{Name}' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would redirect the report "
            + "sink, rescope the scan, change the declared exit-code contract, skip the scan the "
            + "report must come from, or hand the tool an unguarded suppression/property/hints "
            + "file. Use the scoped keys under "
            + $"CodeyBox:Plugins:{PluginId} (SuppressionPaths, NoUpdate) or the shared knobs; "
            + "ExtraArguments is for everything else (for example --exclude, --nvdApiKey, "
            + "or analyzer toggles).")
        { IsDeterministic = true };
    }
}
