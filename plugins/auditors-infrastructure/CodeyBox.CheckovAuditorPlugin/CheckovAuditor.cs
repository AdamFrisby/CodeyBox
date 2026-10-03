using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CheckovAuditorPlugin;

/// <summary>
/// Infrastructure-as-code security auditor wrapping <c>checkov</c> (Bridgecrew
/// Checkov) on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor configuration.
/// This class adds the checkov SARIF invocation shape (<c>checkov -d . -o sarif
/// --output-file-path &lt;per-run-dir&gt; --compact --quiet</c> with the shared
/// <see cref="SarifToolOutputParser"/>), the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and the checkov-specific
/// plumbing below.
///
/// <para><b>Gate behaviour: severity-driven (hybrid) — not blocking by default.</b>
/// Checkov severities go through the declared map, never raw: <c>CRITICAL</c>/
/// <c>HIGH</c> (SARIF <c>error</c>) → <see cref="AuditSeverity.Error"/> (fails the
/// audit), <c>MEDIUM</c> (SARIF <c>warning</c>) → <see cref="AuditSeverity.Warning"/>
/// (advisory), <c>LOW</c>/<c>NONE</c> (SARIF <c>note</c>/<c>none</c>) → <see
/// cref="AuditSeverity.Info"/> (informational); anything unrecognised → <see
/// cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> can only drop findings,
/// never raise them. The auditor is therefore a merge gate for high/critical IaC
/// misconfigurations, not a blocker on every low note.</para>
///
/// <para><b>Report routing.</b> Checkov cannot stream SARIF: <c>-o sarif</c> prints a
/// human-readable console summary to stdout and writes the machine report to a file —
/// <c>results.sarif</c> in the working directory by default, or
/// <c>&lt;output-file-path&gt;/results_sarif.sarif</c> when
/// <c>--output-file-path</c> is set (verified against the 3.3.x source:
/// <c>RunnerRegistry.print_reports</c> in
/// <c>checkov/common/runners/runner_registry.py</c> and <c>Sarif.write_sarif_output</c>
/// in <c>checkov/common/output/sarif.py</c>). The scan therefore writes its report as a
/// plain file inside the per-run scratch directory (<see
/// cref="ExternalToolAuditorBase.PerRunTempDirectoryPath"/>, outside the audited
/// worktree so the scan never pollutes the diff), and <see
/// cref="ResolveParserInputAsync(ISandbox, string, string, ExternalToolAuditorOptions, SandboxExecResult, IReadOnlyList{string}, string?, CancellationToken)"/>
/// reads it back through the separate bounded read of <see
/// cref="ExternalToolAuditorBase.ReadReportFileParseInputAsync"/> — the report reaches
/// only the parser, never the persisted raw output. <c>--compact</c> (no code blocks)
/// and <c>--quiet</c> (only failed checks, no progress bars) stay pinned so the
/// captured console summary stays small and free of echoed source lines. A missing,
/// oversized, or unparseable report file fails closed as infrastructure — never a
/// pass. The per-run directory is minted under the sandbox temp area and is not
/// explicitly removed: VM sandboxes discard the whole temp area with the instance.</para>
///
/// <para><b>Exit-code convention (verified against the checkov 3.3.x source — do not
/// assume the common scanner convention).</b> A completed scan exits <c>0</c> when no
/// check failed and <c>1</c> when checks failed (<c>RunnerRegistry.print_reports</c>
/// returns <c>1 if 1 in exit_codes else 0</c>, propagated by <c>Checkov.run</c> in
/// <c>checkov/main.py</c>); both are findings-producing. Everything else means "could
/// not run": <c>2</c> for crashes (<c>exit_run</c>: <c>exit(0) if no_fail_on_crash else
/// exit(2)</c>) and for argparse/usage errors, <c>126</c>/<c>127</c> for
/// cannot-execute/not-found, and any other exit. One edge breaks the "1 = findings"
/// reading: the SIGINT handler (<c>signal.signal(signal.SIGINT, lambda x, y:
/// sys.exit(''))</c>) exits <c>1</c> with no report. The discriminator is therefore the
/// SARIF file, not the exit code alone: an exit-<c>0</c>/<c>1</c> run without a
/// readable report fails closed as infrastructure through the report read, exactly as
/// an interrupted scan must.</para>
///
/// <para><b>Version pin.</b> Checkov's check corpus changes between releases, so findings
/// are only meaningful from the build the auditor was verified against. <c>checkov
/// --version</c> (printing the bare <c>X.Y.Z</c>) is probed before every run; a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool — never a pass,
/// never a finding.</para>
///
/// <para><b>Repository-controlled configuration.</b> Checkov auto-loads
/// <c>.checkov.yaml</c>/<c>.checkov.yml</c> from the scan directory, the process
/// working directory, and the home directory (<c>get_default_config_paths</c> in
/// <c>checkov/common/util/config_utils.py</c>) — i.e. a file the audited repository
/// can commit to inject <c>skip-check</c>, <c>soft-fail</c>, <c>framework</c>, or other
/// keys that silently empty the report. Worse, checkov merges config files per key
/// (configargparse: the explicit <c>--config-file</c> wins per key, but keys it does
/// not set still fall through to the repository file), so passing a config file alone
/// does not neutralize the surface. The auditor therefore does both: it always passes
/// <c>--config-file</c> (a generated empty file in the report directory by default, or
/// the operator's <c>ConfigFile</c>), <em>and</em> it probes for repository
/// <c>.checkov.yaml</c>/<c>.checkov.yml</c> files under the scan roots before every run
/// and fails closed as deterministic infrastructure when one is present — unless the
/// operator sets <c>TrustRepositoryConfig</c>. An operator <c>ConfigFile</c> must
/// resolve outside the audited worktree (canonicalized in the sandbox with
/// <c>realpath -m</c>; rejected otherwise): the audit subject must not steer the gate.
/// <c>CKV_*</c> environment variables can also set un-passed flags; they come from the
/// sandbox baseline (operator-controlled), not from the audited tree.</para>
///
/// <para><b>Repository-controlled suppression.</b> Checkov honors inline
/// <c>checkov:skip=&lt;check&gt;</c> comments. That is the tool's normal suppression
/// surface (like tflint's <c>tflint-ignore</c> comments), not an auditor bypass: checks
/// it suppresses are suppressed by the scanner itself, and the suppression is visible
/// in the scanned file. There is no tool flag to disable it, so the auditor does not
/// try — operators who want those lines gone enforce it by policy, not by scanner
/// flags.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is <c>checkov -d .</c> over the whole
/// work tree: checkov detects frameworks itself (Terraform, CloudFormation, Kubernetes,
/// Dockerfile, Helm, Bicep, ARM, Ansible, Serverless, OpenAPI, and more). Findings under
/// generated and vendored trees (<c>.terraform/</c>, <c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) are dropped by default —
/// <c>.terraform/</c> holds modules downloaded by <c>terraform init</c>, so problems
/// there describe upstream code, not the change under audit. Operators re-include a path
/// by overriding <c>ExcludePaths</c> in scoped config, and narrow the scan itself with
/// <c>Targets</c>, <c>Frameworks</c>, or <c>--skip-path</c> via
/// <c>ExtraArguments</c>.</para>
///
/// <para><b>Network.</b> The default scan uses only the checks bundled inside the
/// provisioned package and declares no network capability: with no API key checkov sets
/// <c>include_all_checkov_policies</c> locally, external-module download defaults off,
/// and no results are uploaded. Scans that need the network anyway (e.g.
/// <c>sca_image</c> framework checks, a <c>--download-external-modules</c> override via
/// <c>ExtraArguments</c>) degrade per the tool's own behaviour — a form the tool cannot
/// fetch is a scan the tool reports on, not a silent pass.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Checkov IaC Security",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "checkov",
    InstallHint = "provision the pinned checkov release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a Python package "
        + "(pip install checkov==" + DefaultExpectedVersion + " — no distro apt package "
        + "carries a version pin) through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class CheckovAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.checkov";

    /// <summary>
    /// Checkov release the invocation and its findings are verified against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "3.3.22";

    /// <summary>
    /// Scoped-config key for scan targets (comma-separated repo-relative paths;
    /// each entry becomes a repeatable <c>-d</c> argument). Unset → <c>.</c>
    /// (the whole work tree). Entries must stay inside the worktree.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key for framework restriction (comma-separated checkov
    /// framework ids — <c>terraform</c>, <c>cloudformation</c>,
    /// <c>kubernetes</c>, <c>dockerfile</c>, <c>helm</c>, <c>bicep</c>,
    /// <c>arm</c>, …; each becomes a repeatable <c>--framework</c> argument).
    /// Unset → all frameworks.
    /// </summary>
    public const string FrameworksKey = "Frameworks";

    /// <summary>
    /// Scoped-config key for checks to run (comma-separated checkov check ids
    /// such as <c>CKV_AWS_21</c>, or severities; emitted as one comma-joined
    /// <c>--check</c> argument — any other checks are skipped by the tool).
    /// </summary>
    public const string IncludedChecksKey = "IncludedChecks";

    /// <summary>
    /// Scoped-config key for checks to skip (comma-separated checkov check ids
    /// or severities; emitted as one comma-joined <c>--skip-check</c>
    /// argument).
    /// </summary>
    public const string SkippedChecksKey = "SkippedChecks";

    /// <summary>
    /// Scoped-config key for a checkov config file passed verbatim to
    /// <c>--config-file</c>. Unset → a generated empty file in the per-run
    /// directory, which also keeps checkov's auto-loaded repository config out
    /// of the merge for every key it would otherwise supply. Must resolve
    /// outside the audited worktree — a config inside the tree would hand the
    /// diff author flag control (check selection, soft-fail), so it is rejected
    /// deterministically before the scan runs.
    /// </summary>
    public const string ConfigFileKey = "ConfigFile";

    /// <summary>
    /// Scoped-config boolean opting in to repository-authored checkov config
    /// (<c>.checkov.yaml</c>/<c>.checkov.yml</c> under the scan roots).
    /// Default false: the audited repository must not be able to narrow the
    /// audit, so a present file fails the run closed as deterministic
    /// infrastructure unless the operator sets this to <c>true</c>.
    /// </summary>
    public const string TrustRepositoryConfigKey = "TrustRepositoryConfig";

    /// <summary>
    /// Repository config filenames checkov auto-loads (<c>get_default_config_paths</c>).
    /// Probed under every scan root before each run.
    /// </summary>
    private static readonly IReadOnlyList<string> RepositoryConfigFileNames =
        [".checkov.yaml", ".checkov.yml"];

    private const string ReportFileName = "results_sarif.sarif";
    private const string EmptyConfigFileName = "codeybox-empty-checkov.yaml";

    // Fixed script — no configuration-derived text: the per-run directory
    // arrives as $1, never spliced into the script. Creates the report dir
    // (mode 700, under the shared temp area) and the generated empty
    // --config-file that the scan argv names when the operator did not set
    // ConfigFile; the SARIF report itself stays a plain file the auditor
    // reads back after the scan.
    private const string ReportPreparationScript =
        "d=\"$1\""
        + " && mkdir -m 700 -p \"$d\""
        + " && printf '%s\\n' '{}' > \"$d/" + EmptyConfigFileName + "\"";

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // fight the pinned console flags, rescope the scan, reopen the
    // repository-controlled config surface, or change the declared exit-code
    // contract. Rejected deterministically with a pointer to the scoped key
    // covering the same need.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--output", "-o"),                // report format — pinned to sarif
        ("--output-file-path", ""),        // report sink — ours
        ("--compact", ""),                 // stdout purity — pinned on
        ("--quiet", ""),                   // stdout purity — pinned on
        ("--directory", "-d"),             // scan scope — use Targets
        ("--file", "-f"),                  // scan scope — use Targets
        ("--config-file", ""),             // repo-config surface — use ConfigFile
        ("--check", "-c"),                 // check selection — use IncludedChecks
        ("--skip-check", ""),              // check selection — use SkippedChecks
        ("--framework", ""),               // framework selection — use Frameworks
        ("--soft-fail", "-s"),             // exit contract — findings exit 1
        ("--soft-fail-on", ""),            // exit contract — findings exit 1
        ("--hard-fail-on", ""),            // exit contract — findings exit 1
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = completed scan, no failed checks; 1 = completed scan with
        // failed checks. The SARIF report file is the discriminator: an exit
        // 0/1 without a readable report (interrupted scan, crashed writer)
        // fails closed through the report read. Every other exit — 2 for
        // crashes and usage errors, 126/127, anything else — is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Checkov runs 20+ frameworks over the whole tree — minutes on large
        // repositories. Bounded well under the shared MaxTimeoutSeconds
        // ceiling; exceeding it is infrastructure, not a pass.
        Timeout = TimeSpan.FromMinutes(10),
        // Findings inside downloaded-module and vendored trees describe
        // upstream templates, not the change under audit — noise that trains
        // operators to ignore the auditor. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = [".terraform/", "vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<IReadOnlyList<string>> _frameworks = static () => [];
    private Func<IReadOnlyList<string>> _includedChecks = static () => [];
    private Func<IReadOnlyList<string>> _skippedChecks = static () => [];
    private Func<string?> _configFile = static () => null;
    private Func<bool> _trustRepositoryConfig = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:checkov";

    /// <summary>
    /// The default scan uses only the checks bundled inside the provisioned
    /// package (no API key, no uploads, no external-module download), so the
    /// auditor needs no network egress and runs in the most restrictive
    /// sandbox.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "checkov";

    /// <summary>
    /// The shared SARIF parser: checkov's <c>-o sarif</c> report carries the
    /// check id (<c>CKV_*</c>), the severity-derived level
    /// (<c>error</c>/<c>warning</c>/<c>note</c>/<c>none</c>), the message, and
    /// the file/line location per result, so no plugin-local parser exists to
    /// drift.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from checkov's SARIF severity vocabulary (checkov's
    /// <c>SEVERITY_TO_SARIF_LEVEL</c>: CRITICAL/HIGH → <c>error</c>, MEDIUM →
    /// <c>warning</c>, LOW → <c>note</c>, plus <c>none</c>) to <see
    /// cref="AuditSeverity"/>: <c>error</c> fails the audit, <c>warning</c> is
    /// advisory, <c>note</c>/<c>none</c> (and the spelled-out severities) are
    /// informational. Anything unrecognised maps to <see
    /// cref="AuditSeverity.Warning"/> — visible, never silently informational
    /// and never raw. Raw tool tokens never reach findings; the tool level is
    /// preserved in the finding description as proof the value flowed through
    /// the mapping.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
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
        // pollutes the diff.
        var reportDir = PerRunTempDirectoryPath;

        var args = new List<string>();

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        if (targets.Count == 0)
        {
            args.Add("-d");
            args.Add(".");
        }
        else
        {
            foreach (var target in targets)
            {
                args.Add("-d");
                args.Add(ValidatedRepoRelativeTarget(target, TargetsKey));
            }
        }

        // SARIF is the auditor's parsing contract; the file lands under the
        // per-run directory and is read back through the parser-input seam.
        // --compact/--quiet are stdout purity, not cosmetics: without them the
        // captured console summary echoes full code blocks of every finding.
        args.Add("-o");
        args.Add("sarif");
        args.Add("--output-file-path");
        args.Add(reportDir);
        args.Add("--compact");
        args.Add("--quiet");

        foreach (var framework in _frameworks())
        {
            if (string.IsNullOrWhiteSpace(framework))
                continue;
            args.Add("--framework");
            args.Add(ValidatedArgumentValue(framework.Trim(), FrameworksKey));
        }

        var included = _includedChecks()
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .ToList();
        if (included.Count > 0)
        {
            args.Add("--check");
            args.Add(string.Join(",", included.Select(c => ValidatedArgumentValue(c, IncludedChecksKey))));
        }

        var skipped = _skippedChecks()
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .ToList();
        if (skipped.Count > 0)
        {
            args.Add("--skip-check");
            args.Add(string.Join(",", skipped.Select(c => ValidatedArgumentValue(c, SkippedChecksKey))));
        }

        return args;
    }

    /// <summary>
    /// Emits the <c>--config-file</c> pair — the one argv element that needs a
    /// bounded sandbox probe to compute. Always set: checkov would otherwise
    /// merge a <c>.checkov.yaml</c>/<c>.checkov.yml</c> committed in the audited
    /// repository into the run. Unset <c>ConfigFile</c> → the generated empty
    /// file in the report directory, which binds nothing. An operator's
    /// <c>ConfigFile</c> is read once here, canonicalized in the sandbox,
    /// rejected when it resolves inside the worktree, and passed through
    /// verbatim — the value validated is exactly the value argv carries (a
    /// mid-run scoped-config reload cannot split the guard from the flag).
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
            return ["--config-file", Path.Combine(PerRunTempDirectoryPath, EmptyConfigFileName)];

        // From inside the tree the diff author could bind flags absent from
        // argv (skip-check, soft-fail, framework) and silently empty the
        // report — the shared canonicalize-then-contain guard rejects any
        // resolution into the worktree.
        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, configured, ConfigFileKey, options, ct).ConfigureAwait(false);
        return ["--config-file", canonical];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _frameworks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[FrameworksKey]);
        _includedChecks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[IncludedChecksKey]);
        _skippedChecks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[SkippedChecksKey]);
        _configFile = () => scoped[ConfigFileKey];
        _trustRepositoryConfig = () =>
            bool.TryParse(scoped[TrustRepositoryConfigKey], out var trust) && trust;
        context.Logger.LogInformation(
            "CheckovAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fails closed on repository-authored checkov config, then prepares the
    /// report directory the scan argv names. A preparation failure fails closed
    /// as infrastructure naming the tool: the scan cannot produce its report
    /// without the directory.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (!_trustRepositoryConfig())
        {
            var present = await ProbeRepositoryFilesPresentAsync(
                    sandbox, workingDirectory, tool, RepositoryConfigCandidates(), options, ct)
                .ConfigureAwait(false);
            if (present.Count > 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{tool}' found repository-controlled checkov "
                    + $"configuration ({string.Join(", ", present.Take(8))}) under the scan roots — "
                    + "checkov merges it into the run, so the diff author could narrow what the "
                    + "scan reports. Remove the file(s), or set "
                    + $"CodeyBox:Plugins:{PluginId}:{TrustRepositoryConfigKey} to true to accept "
                    + "repository config explicitly.")
                { IsDeterministic = true };
        }

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
    /// Reads the SARIF report checkov wrote under the per-run directory through
    /// a separate bounded sandbox read — checkov prints only a console summary
    /// to stdout and writes the machine report to
    /// <c>&lt;output-file-path&gt;/results_sarif.sarif</c>, so the parser-input
    /// seam is the only way to reach it. The read is the shared <see
    /// cref="ExternalToolAuditorBase.ReadReportFileParseInputAsync"/>: a missing
    /// or oversized report fails closed as infrastructure — which is also what
    /// distinguishes an interrupted scan (exit 1, no report) from a scan that
    /// ran and found problems (exit 1, report present).
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
            ct,
            oversizedScopeHint: "(Targets, Frameworks)");

    /// <summary>
    /// Resolves the absolute scan directory as the tool sees it, so the parser
    /// can relativize absolute artifact URIs the report may carry when scan
    /// targets are not repo-relative. Sandbox providers may translate the
    /// audit's working directory, so it is probed rather than assumed — through
    /// the shared <c>pwd</c> probe.
    /// </summary>
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => await ProbeSandboxWorkingDirectoryAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

    private IReadOnlyList<string> RepositoryConfigCandidates()
    {
        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim().Replace('\\', '/').TrimEnd('/'))
            .Where(static t => t.Length > 0 && t != ".")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var candidates = new List<string>(RepositoryConfigFileNames.Count * (targets.Count + 1));
        foreach (var name in RepositoryConfigFileNames)
            candidates.Add(name);
        foreach (var target in targets)
        {
            foreach (var name in RepositoryConfigFileNames)
                candidates.Add(target + "/" + name);
        }
        return candidates;
    }

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
            + "sink, fight the pinned console flags, rescope the scan, re-open the "
            + "repository-controlled config surface, or change the declared exit-code contract. Use "
            + $"the scoped keys under CodeyBox:Plugins:{PluginId} (Targets, Frameworks, "
            + "IncludedChecks, SkippedChecks, ConfigFile) or the shared knobs; ExtraArguments is "
            + "for everything else (e.g. --skip-framework, --skip-path, --external-checks-dir).")
        { IsDeterministic = true };
    }
}
