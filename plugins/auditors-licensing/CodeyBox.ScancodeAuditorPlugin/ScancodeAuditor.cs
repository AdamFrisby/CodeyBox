using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ScancodeAuditorPlugin;

/// <summary>
/// Licence and copyright auditor wrapping ScanCode Toolkit (<c>scancode</c>)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// report-file preparation via
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, the update-check
/// opt-out on the scan argv, and the scancode-specific arguments and knobs
/// below.
///
/// <para><b>Gate behaviour: severity-driven (blocking on Error).</b> The
/// tool's licence categories map as <c>Copyleft</c> (strong copyleft:
/// GPL/AGPL-family) to <see cref="AuditSeverity.Error"/> — those findings
/// fail the audit; <c>Copyleft Limited</c> (LGPL/MPL/EPL-family),
/// <c>Proprietary Free</c>, <c>Commercial</c>, <c>Free Restricted</c>,
/// <c>Source-available</c> and <c>Unstated License</c> to
/// <see cref="AuditSeverity.Warning"/> are advisory; <c>Permissive</c>,
/// <c>Public Domain</c> and <c>Patent License</c> detections plus copyright
/// notices to <see cref="AuditSeverity.Info"/> are informational.
/// <c>MinimumSeverity</c> only drops findings, it never raises them. This
/// auditor is therefore a merge gate for strong-copyleft licence
/// detections only — stated here, not smuggled in.</para>
///
/// <para><b>Report routing.</b> scancode cannot stream its machine-readable
/// report: <c>--json</c> writes a real file, and console output is progress
/// logging, not parseable output. The scan writes
/// <c>scancode-report.json</c> as a plain file inside the per-run scratch
/// directory (<see cref="ExternalToolAuditorBase.PerRunTempDirectoryPath"/>),
/// and <see cref="ResolveParserInputAsync(ISandbox, string, string, ExternalToolAuditorOptions, SandboxExecResult, IReadOnlyList{string}, string?, CancellationToken)"/>
/// reads it back through the separate bounded read the base's parser-input
/// seam exists for — the report reaches only the parser, never the persisted
/// <see cref="AuditResult.RawOutput"/>. A missing, oversized, or
/// unparseable report file fails closed as infrastructure — never a
/// pass.</para>
///
/// <para><b>Exit-code convention (verified against the scancode 32.5.0
/// <c>cli.py</c> source: <c>rc = 0 if success else 1</c>).</b> scancode does
/// NOT follow the common "1 = findings" convention: a completed scan exits
/// <c>0</c> whether or not it detected anything, so findings and clean runs
/// are indistinguishable from the bare exit — the JSON report is the
/// verdict, never the exit code. Exit <c>1</c> (usage errors, unreadable
/// inputs, interrupted or crashed scans) means "could not run", as do
/// <c>126</c>/<c>127</c> (cannot-execute) and anything else. Only
/// <c>{0}</c> is declared findings-producing; every other exit fails closed
/// as infrastructure — loud, never a silent pass.</para>
///
/// <para><b>Version pin.</b> scancode's licence rules and report shape change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>scancode
/// --version</c> (printing <c>ScanCode version: X.Y.Z</c>) before every scan;
/// a missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> scancode loads no scan
/// configuration from the audited worktree (verified in the 32.5.0 CLI:
/// every behaviour switch — <c>--ignore</c>, <c>--max-depth</c>,
/// <c>--license-score</c> — arrives on argv; there is no config-file option
/// and no working-directory auto-discovery), so a file committed in the
/// audited repository cannot rescope or silence the scan. The operator-owned
/// <c>--ignore</c> patterns some deployments need for scan performance
/// travel only through <c>ExtraArguments</c> (visible operator config, not
/// repository content).</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>.</c> — the whole
/// worktree, licence and copyright detection together. Findings inside
/// vendored, generated, or version-control trees (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>, <c>.git/</c>) describe
/// upstream or non-authored content — licence text fragments inside
/// packfiles are not licence grants — so they are excluded by default;
/// operators re-include a path by overriding <c>ExcludePaths</c>.</para>
///
/// <para><b>Network.</b> The scan itself is fully local (the licence rule
/// database ships inside the installed tool), and the auditor passes
/// <c>--no-check-version</c> so scancode never phones home to check for a
/// newer release — that is latency and egress, not signal. The auditor
/// therefore declares <see cref="AuditCapabilities.None"/>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ScanCode Licence and Copyright",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "scancode",
    InstallHint = "provision the pinned scancode release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — pip install "
        + "scancode-toolkit==" + DefaultExpectedVersion + " (or unpack the versioned upstream "
        + "release archive https://github.com/aboutcode-org/scancode-toolkit/releases) with "
        + "its scancode entry point on PATH via CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions; no distro apt package "
        + "carries a pinned scancode")]
public sealed class ScancodeAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.scancode";

    /// <summary>
    /// scancode release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "32.5.0";

    /// <summary>Report file name scancode writes for <c>--json</c> into the per-run directory.</summary>
    internal const string ReportFileName = "scancode-report.json";

    // Fixed script — no configuration-derived text: the per-run directory
    // arrives as $1, never spliced into the script. Creates the report dir
    // (mode 700 under the shared temp area) the scan argv names; the report
    // itself stays a plain file the auditor reads back after the scan.
    private const string ReportPreparationScript =
        "d=\"$1\""
        + " && mkdir -m 700 -p \"$d\"";

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // rescope or descope the scan, re-point the input at a prebuilt report,
    // fight the pinned console/egress flags, or bloat the report past the
    // capture bound. Rejected deterministically (naming the scoped knobs to
    // use instead) rather than failing later as an opaque parse failure or
    // silently changing what the audit means.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--json", ""),                 // report sink — ours
        ("--json-pp", ""),              // report sink/format — ours (pretty JSON)
        ("--strip-root", ""),           // path contract — pinned on (repo-relative paths)
        ("--full-root", ""),            // path contract — would emit absolute paths
        ("--from-json", ""),            // input source — would skip the live scan
        ("--max-depth", ""),            // scan scope — silently descopes the audit
        ("--quiet", "-q"),              // stream contract — pinned on
        ("--verbose", "-v"),            // stream contract — floods the capture bound
        ("--check-version", "--no-check-version"), // egress pin — always off
        ("--license-text", ""),         // report bloat — embeds full licence texts
        ("--license-text-diagnostics", ""), // report bloat — embeds diagnostics
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against the scancode 32.5.0 cli.py source
        // (rc = 0 if success else 1): 0 is "scan completed" — with or
        // without detections, the JSON report is the verdict. Every other
        // exit (1 for usage errors, unreadable inputs, interrupted/crashed
        // scans; 126/127 cannot-execute) means "could not run".
        FindingsExitCodes = new HashSet<int> { 0 },
        // scancode matches every file against its full licence-rule database —
        // minutes on large trees, not seconds. Bounded well under the shared
        // MaxTimeoutSeconds ceiling; exceeding it is infrastructure, not a
        // pass.
        Timeout = TimeSpan.FromMinutes(10),
        // The JSON report embeds per-file licence, copyright, and holder
        // evidence across the whole tree: megabytes on large trees. The
        // shared 1 MiB default would clip real reports into fail-closed
        // infrastructure noise.
        MaxOutputBytesPerStream = 8 * 1024 * 1024,
        // Findings inside vendored/dependency trees describe upstream code,
        // and matches inside version-control internals are packfile-fragment
        // noise, not licence grants — both train operators to ignore the
        // auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", ".git/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;

    /// <inheritdoc />
    public override string Name => "codeybox:scancode";

    /// <inheritdoc />
    protected override string ToolName => "scancode";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ScancodeJsonReportParser();

    /// <summary>
    /// Declared mapping from scancode's licence-category vocabulary (plus the
    /// synthetic <c>Copyright</c> token the parser emits for copyright
    /// statements, which carry no tool severity) to
    /// <see cref="AuditSeverity"/>: strong copyleft → Error (blocking),
    /// limited copyleft / proprietary-ish / unknown → Warning (advisory),
    /// permissive / public-domain / patent grants and copyright notices →
    /// Info (informational), unrecognised → Warning. Raw tool tokens never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["copyleft"] = AuditSeverity.Error,
            ["copyleft limited"] = AuditSeverity.Warning,
            ["proprietary free"] = AuditSeverity.Warning,
            ["proprietary"] = AuditSeverity.Warning,
            ["commercial"] = AuditSeverity.Warning,
            ["free restricted"] = AuditSeverity.Warning,
            ["source-available"] = AuditSeverity.Warning,
            ["unstated license"] = AuditSeverity.Warning,
            ["unknown"] = AuditSeverity.Warning,
            ["permissive"] = AuditSeverity.Info,
            ["public domain"] = AuditSeverity.Info,
            ["patent license"] = AuditSeverity.Info,
            ["copyright"] = AuditSeverity.Info,
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
        // pollutes the diff. Every entry below is an author-chosen constant
        // or a path the auditor computed — never untrusted text, never
        // through a shell.
        var reportPath = Path.Combine(PerRunTempDirectoryPath, ReportFileName);

        return
        [
            // Whole-worktree scan: licence and copyright detection together.
            "--license",
            "--copyright",
            // Repo-relative paths in the report, so findings and
            // ExcludePaths behave like every other auditor's.
            "--strip-root",
            // Quiet keeps progress bars and the new-version notice off the
            // captured streams; the JSON report is the only verdict source.
            "--quiet",
            // Never phone home to check for a newer release: latency and
            // egress, not signal — and the version under audit is the
            // pinned baseline build, not whatever is newest.
            "--no-check-version",
            "--json", reportPath,
            ".",
        ];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        context.Logger.LogInformation(
            "ScancodeAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
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
    /// Reads the JSON report file scancode wrote under the per-run
    /// directory through a separate bounded sandbox read — the report never
    /// touches the captured scan streams (stdout is progress logging, and
    /// captured output is persisted as <see cref="AuditResult.RawOutput"/>).
    /// The read is the shared
    /// <see cref="ExternalToolAuditorBase.ReadReportFileParseInputAsync"/>:
    /// it shares the configured per-stream capture bound and a missing or
    /// oversized report fails closed as infrastructure.
    /// </summary>
    protected override Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        IReadOnlyList<string> argv,
        string? scanRoot,
        CancellationToken ct)
        => ReadReportFileParseInputAsync(
            sandbox,
            workingDirectory,
            tool,
            options,
            result,
            Path.Combine(PerRunTempDirectoryPath, ReportFileName),
            scanRoot,
            ct);

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
            + "sink, rescope or descope the scan, re-point the input at a prebuilt report, fight "
            + "the pinned console/egress flags, or bloat the report past the capture bound. Use "
            + "the shared knobs under "
            + $"CodeyBox:Plugins:{PluginId} (MinimumSeverity, IncludedRules, ExcludedRules, "
            + "ExcludePaths, TimeoutSeconds, MaxOutputBytesPerStream, MaxFindings); "
            + "ExtraArguments is for everything else (for example --ignore, --timeout, "
            + "-n/--processes, or --license-score).")
        { IsDeterministic = true };
    }
}
