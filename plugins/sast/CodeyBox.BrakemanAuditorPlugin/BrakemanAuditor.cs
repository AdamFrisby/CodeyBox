using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BrakemanAuditorPlugin;

/// <summary>
/// Ruby on Rails security auditor wrapping the Brakeman CLI
/// (<c>brakeman</c>) on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout,
/// per-stream output caps, SARIF parsing, severity mapping, exit-code
/// classification, and per-auditor configuration. This class adds the
/// Brakeman invocation shape (<c>brakeman</c> with SARIF on stdout), the
/// declared severity map over Brakeman's confidence vocabulary, the pinned
/// tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Brakeman confidences go through a declared map, never raw:
/// <c>High</c> (plus SARIF <c>error</c>) → <see cref="AuditSeverity.Error"/>
/// (fails the audit); <c>Medium</c> (plus SARIF <c>warning</c>) → <see
/// cref="AuditSeverity.Warning"/> (advisory); <c>Weak</c> (plus SARIF
/// <c>note</c>/<c>none</c>) → <see cref="AuditSeverity.Info"/>
/// (informational); anything unrecognised → <see
/// cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> can only drop
/// findings, it never raises them. The auditor is therefore a merge gate
/// for high-confidence Rails vulnerabilities, not a blocker on every weak
/// hint.</para>
///
/// <para><b>Exit-code convention (verified against Brakeman 8.0.6 source:
/// <c>lib/brakeman.rb</c> exit-code constants and
/// <c>lib/brakeman/commandline.rb</c>).</b> Brakeman exits <c>0</c> when the
/// scan completes with no warnings, and <c>3</c>
/// (<c>Warnings_Found_Exit_Code</c>) when warnings were found — both carry
/// the SARIF report, so both are findings-producing. Every other exit means
/// "could not run" and is infrastructure: <c>7</c>
/// (<c>Errors_Found_Exit_Code</c>, scan errors with no warnings to report),
/// <c>4</c> (<c>No_App_Found_Exit_Code</c>, no Rails application detected),
/// <c>6</c> (<c>Missing_Checks_Exit_Code</c>, unknown check names),
/// <c>5</c>/<c>8</c>/<c>9</c> (version/ignore-note bookkeeping),
/// <c>-1</c> (invalid options), and <c>126</c>/<c>127</c>
/// (cannot-execute/not-found). In particular a repository that is not a
/// Rails application exits <c>4</c>: scope this auditor to Rails projects
/// rather than reading that as a pass.</para>
///
/// <para><b>Version pin.</b> A scanner's checks change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>brakeman --version</c> before every run
/// (the CLI prints <c>brakeman X.Y.Z</c>); a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Brakeman honors two
/// surfaces authored inside the audited repository — the ignore file
/// (<c>config/brakeman.ignore</c> by default) and the YAML config file
/// (<c>config/brakeman.yml</c>, loaded from the application path when
/// present) — and the audit subject writes that repository. An auditor its
/// subject can silence is not a gate. The ignore file has no disabling
/// switch, so by default the scan passes <c>--show-ignored</c>, which keeps
/// ignored warnings in the report (marked with SARIF <c>suppressions</c>)
/// without letting them soften the exit code; findings then surface for
/// code the ignore file would have hidden. The YAML config instead fails
/// closed: when <c>config/brakeman.yml</c> is present in the audited tree
/// the run is infrastructure — its scanner-weakening options (skipped
/// checks, skipped paths) would otherwise yield a clean verdict over
/// vulnerable code, and the gate runs even when <c>ExtraArguments</c>
/// supplies <c>-c</c> because the tool only prefers the operator file when
/// it names an existing file, which is the operator's to mispoint, not the
/// gate's to assume. Operators who deliberately trust repo-authored
/// suppression set <c>TrustRepositorySuppression</c> in scoped config,
/// which lifts both the ignore-file visibility flag and the config-file
/// gate. An operator-owned file passed with <c>-c</c>/<c>--config-file</c>
/// or <c>-i</c>/<c>--ignore-config</c> via <c>ExtraArguments</c> is
/// canonicalized in the sandbox and rejected when it resolves inside the
/// audited worktree; <c>--no-exit-on-error</c> (scan errors masquerading as
/// a clean verdict) and <c>-f</c>/<c>--format</c> or
/// <c>-o</c>/<c>--output</c> (replacing or diverting the SARIF the parser
/// expects) are rejected outright.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Brakeman Rails SAST",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "brakeman",
    InstallHint = "provision the pinned Brakeman release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a Ruby gem "
        + "(gem install brakeman --version "
        + DefaultExpectedVersion + ") — no distro apt package carries a version pin — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class BrakemanAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.brakeman";

    /// <summary>
    /// Brakeman release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "8.0.6";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression
    /// surfaces (the <c>config/brakeman.ignore</c> entries and the
    /// <c>config/brakeman.yml</c> scanner config). Default false: the
    /// audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Brakeman exits 0 when the scan completes with no warnings and 3
        // (Warnings_Found_Exit_Code) when warnings were found — both carry
        // the SARIF report. Every other exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 3 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Brakeman already skips vendor/ at scan time unless
        // told otherwise; this filter drops findings under the remaining
        // vendored prefixes. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:brakeman";

    /// <inheritdoc />
    protected override string ToolName => "brakeman";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from Brakeman's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Brakeman assigns each warning a
    /// confidence (<c>High</c>, <c>Medium</c>, <c>Weak</c>) and normalizes it
    /// to a SARIF level (<c>error</c>, <c>warning</c>, <c>note</c>) in
    /// <c>lib/brakeman/report/report_sarif.rb</c>; both vocabularies are
    /// translated here so a severity means the same thing regardless of
    /// which scanner produced it — raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["weak"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
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
        // Structured argv, never a shell string: the base appends the
        // operator's ExtraArguments after these entries.
        var args = new List<string>
        {
            // Quiet plus no pager: everything except the report already goes
            // to stderr, and the pager must never hold a sandbox exec open.
            "--quiet",
            "--no-pager",
            // SARIF is one of several formats; pin it explicitly because the
            // parser reads stdout and any other format would fail closed in
            // the parser rather than silently produce no findings.
            "--format", "sarif",
            // /dev/stdout (Linux sandboxes) is the documented stdout sink
            // for Brakeman reports.
            "--output", "/dev/stdout",
            // The audited worktree: the base runs with WorkingDirectory set
            // to it, so the relative path keeps report URIs repo-relative.
            "--path", ".",
        };

        // The audit subject authors the ignore file; keep its entries
        // visible in the report unless the operator opts in to
        // repo-controlled suppression.
        if (!_trustRepositorySuppression())
            args.Add("--show-ignored");

        return args;
    }

    // Brakeman loads this from the application path (the audited worktree
    // root) when present — the audit subject's to abuse when the worktree
    // is the scan root. The ignore file needs no presence gate:
    // --show-ignored keeps its entries in the report by default.
    private static readonly string[] RepositoryConfigFiles = ["config/brakeman.yml"];

    // ExtraArguments flags whose presence would collapse the declared
    // exit-code contract into a clean verdict (--no-exit-on-error) or
    // replace/divert the SARIF report the parser expects (-f/--format,
    // -o/--output). Rejected deterministically in VerifyToolAsync.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--no-exit-on-error", ""),
        ("--format", "-f"),
        ("--output", "-o"),
    ];

    // ExtraArguments flags whose value is a file the tool loads for
    // gate-shaping content — the YAML config and the ignore file — each
    // with its short form (verified against Brakeman's options.rb:
    // -c/--config-file, -i/--ignore-config). Gated outside the worktree in
    // VerifyToolAsync.
    private static readonly (string LongFlag, string? ShortFlag)[] PathValuedArgumentFlags =
    [
        ("--config-file", "-c"),
        ("--ignore-config", "-i"),
    ];

    /// <summary>
    /// Pre-scan preconditions beyond the base's presence and pinned-version
    /// checks: operator-supplied gate-shaping flags in
    /// <c>ExtraArguments</c> are rejected or contained, and — unless the
    /// operator opted in — the repository-controlled Brakeman config file
    /// must be absent from the audited tree. All fail closed as
    /// infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        RejectReservedExtraArguments(options);
        await ThrowIfPathFlagResolvesInWorktreeAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

        if (_trustRepositorySuppression())
            return;

        // Brakeman loads config/brakeman.yml from the application path when
        // present (Brakeman.config_file checks the in-tree path alongside
        // the operator's -c and the machine-wide defaults). Its options can
        // skip checks and paths, so a repo-authored file yields a clean
        // verdict over vulnerable code: presence fails closed unless the
        // operator trusts repository-controlled suppression. The gate runs
        // even when ExtraArguments supplies -c: the tool only prefers the
        // operator file when it names an existing file, which is the
        // operator's to mispoint, not the gate's to assume.
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            RepositoryConfigFiles,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled file(s) "
                + $"'{string.Join("', '", present)}' in the audited repository — "
                + "brakeman loads that config from the application path and its options "
                + "can skip checks and paths, so the audit subject could hide a "
                + "vulnerability. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true "
                + "to trust repository-controlled suppression surfaces.")
            { IsDeterministic = true };
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
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would collapse the "
            + "declared exit-code contract into a clean verdict or replace/divert the SARIF "
            + "report the parser expects. Use the scoped keys under "
            + $"CodeyBox:Plugins:{PluginId} (MinimumSeverity, IncludedRules, ExcludedRules, "
            + "ExcludePaths); ExtraArguments is for everything else (e.g. -t/--test, -x/--except).")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Canonicalizes the value of every <c>ExtraArguments</c> flag that names
    /// a gate-shaping file the tool loads and fails closed when it resolves
    /// inside the audited worktree. The flags resolve against the tool's cwd
    /// — the worktree — so a relative or in-tree value would hand the
    /// scanner config or ignore list to repository-controlled content.
    /// Runs in both trust modes: it guards the operator knob, and the
    /// opt-in <c>TrustRepositorySuppression</c> already covers the
    /// sanctioned repo files without a flag.
    /// </summary>
    private async Task ThrowIfPathFlagResolvesInWorktreeAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        foreach (var (longFlag, shortFlag) in PathValuedArgumentFlags)
        {
            if (!TryGetExtraArgumentsFlagValue(options, longFlag, shortFlag, out var value))
                continue;
            if (value is null)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' ExtraArguments supplies '{longFlag}' "
                    + $"with no following value — pass it as '{longFlag} <path>' or "
                    + $"'{longFlag}=<path>'.")
                { IsDeterministic = true };
            await CanonicalizeOutsideWorktreeAsync(
                sandbox, workingDirectory, value,
                $"ExtraArguments '{longFlag}'", options, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "BrakemanAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
