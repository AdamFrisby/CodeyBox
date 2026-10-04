using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BanditAuditorPlugin;

/// <summary>
/// Python security-issue auditor wrapping the Bandit CLI
/// (<c>bandit</c>) on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout,
/// per-stream output caps, SARIF parsing, severity mapping, exit-code
/// classification, and per-auditor configuration. This class adds the
/// Bandit invocation shape (<c>bandit -r . -f sarif</c> with the report on
/// stdout), the declared severity map over Bandit's severity vocabulary,
/// the pinned tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Bandit severities go through a declared map, never raw:
/// <c>High</c> (plus SARIF <c>error</c>) → <see cref="AuditSeverity.Error"/>
/// (fails the audit); <c>Medium</c> (plus SARIF <c>warning</c>) → <see
/// cref="AuditSeverity.Warning"/> (advisory); <c>Low</c> (plus SARIF
/// <c>note</c>/<c>none</c>) → <see cref="AuditSeverity.Info"/>
/// (informational); anything unrecognised → <see
/// cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> can only drop
/// findings, it never raises them. The auditor is therefore a merge gate
/// for high-severity Python security issues, not a blocker on every low
/// hint.</para>
///
/// <para><b>Exit-code convention (verified against Bandit's own
/// <c>bandit/cli/main.py</c> — do not assume the common convention
/// holds).</b> Bandit exits <c>0</c> when the scan completes with no
/// issues above the severity/confidence filters, and <c>1</c> when issues
/// were found (unless <c>--exit-zero</c> is passed, which collapses that
/// distinction) — both carry the SARIF report, so both are
/// findings-producing. Exit <c>2</c> means "could not run": an invalid
/// config file, no scan targets, an empty test profile, an unknown
/// profile, an unreadable baseline, a baseline paired with a formatter
/// that does not accept one, multiple <c>.bandit</c> files, or an
/// <c>argparse</c> rejection. <c>126</c>/<c>127</c> mean
/// cannot-execute/not-found. Every exit outside <c>{0, 1}</c> is
/// infrastructure: loud, never a silent pass. Operators must not pass
/// <c>--exit-zero</c> or override the report shape via
/// <c>ExtraArguments</c>: the former is rejected outright, and any exit
/// outside the declared set fails closed anyway.</para>
///
/// <para><b>Version pin.</b> A scanner's checks change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>bandit --version</c> before every run
/// (the CLI prints <c>bandit X.Y.Z</c> followed by a second line naming
/// the interpreter); a missing binary, an unrecognised version string, or
/// a version other than <c>ExpectedVersion</c> is an infrastructure
/// failure naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Bandit honors two
/// surfaces authored inside the audited repository — inline <c># nosec</c>
/// comments (with optional test-id scoping) and the project INI file
/// (<c>.bandit</c>, auto-discovered by walking the scan targets whenever
/// <c>-r</c> is used: a single file is silently honored, several abort the
/// scan) — and the audit subject writes that repository. An auditor its
/// subject can silence is not a gate. Comment suppressions are inert by
/// default: the scan passes <c>--ignore-nosec</c>, so findings surface for
/// code a <c># nosec</c> comment would have hidden. The INI file has no
/// disabling switch, so its presence anywhere in the audited tree fails
/// closed as infrastructure instead: its options can exclude paths and
/// drop tests, yielding a clean verdict over vulnerable code. Operators
/// who deliberately trust repo-authored suppression set
/// <c>TrustRepositorySuppression</c> in scoped config, which omits
/// <c>--ignore-nosec</c> and lifts the <c>.bandit</c> gate. An
/// operator-owned file passed with <c>-c</c>/<c>--configfile</c>,
/// <c>--ini</c>, or <c>-b</c>/<c>--baseline</c> via <c>ExtraArguments</c>
/// is canonicalized in the sandbox and rejected when it resolves inside
/// the audited worktree; <c>--exit-zero</c> (findings masquerading as a
/// clean verdict) and <c>-f</c>/<c>--format</c> or
/// <c>-o</c>/<c>--output</c> (replacing or diverting the SARIF the parser
/// expects) are rejected outright. A repository <c>bandit.yaml</c>,
/// <c>pyproject.toml</c> <c>[tool.bandit]</c> section, or legacy
/// <c>/etc/bandit/bandit.yaml</c> is inert unless the operator names it
/// with <c>-c</c> — Bandit loads a YAML/TOML config only from the explicit
/// flag — so those need no presence gate.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Bandit Python SAST",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "bandit",
    InstallHint = "provision the pinned Bandit release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline as a Python package "
        + "(pipx install bandit==" + DefaultExpectedVersion
        + ", or pip install bandit==" + DefaultExpectedVersion + " — no distro apt package "
        + "carries a version pin) through CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class BanditAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.bandit";

    /// <summary>
    /// Bandit release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.9.4";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression
    /// surfaces (inline <c># nosec</c> comments and the project
    /// <c>.bandit</c> INI file). Default false: the audited repo must not
    /// be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Bandit exits 0 when the scan completes with no issues and 1 when
        // issues were found — both carry the SARIF report. Exit 2 (and
        // anything else) means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding ExcludePaths
        // in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:bandit";

    /// <inheritdoc />
    protected override string ToolName => "bandit";

    /// <inheritdoc />
    /// <summary>
    /// Bandit's SARIF formatter writes one result per issue with its own
    /// <c>level</c> (<c>error</c> for <c>HIGH</c>, <c>warning</c> for
    /// <c>MEDIUM</c>, <c>note</c> for <c>LOW</c>), so the shared parser
    /// reads the shape directly — no rule-metadata recovery step is needed.
    /// The native <c>issue_severity</c>/<c>issue_confidence</c> properties
    /// ride along in each result for operator context; the verdict follows
    /// <c>level</c> through the declared map below.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from Bandit's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Bandit ranks each issue <c>HIGH</c>,
    /// <c>MEDIUM</c>, or <c>LOW</c> and normalizes it to a SARIF level
    /// (<c>error</c>, <c>warning</c>, <c>note</c>); both vocabularies are
    /// translated here (plus the common scanner tokens) so a severity
    /// means the same thing regardless of which scanner produced it — raw
    /// tool levels never reach findings.
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
            // Recursive over the audited worktree: the base runs with
            // WorkingDirectory set to it, so the relative target keeps
            // report URIs repo-relative.
            "-r", ".",
            // SARIF is one of several formats; pin it explicitly because
            // the parser reads stdout (Bandit's default report sink) and
            // any other format would fail closed in the parser rather than
            // silently produce no findings.
            "-f", "sarif",
        };

        // The audit subject authors # nosec comments; keep them inert
        // unless the operator opts in to repo-controlled suppression.
        if (!_trustRepositorySuppression())
            args.Add("--ignore-nosec");

        return args;
    }

    // Bandit's project INI file is auto-discovered by walking the scan
    // targets whenever -r is used — a single .bandit anywhere in the tree
    // is silently honored (its exclude/tests/skips shape the gate), while
    // several abort the scan. The glob matches the name at any depth; the
    // tool's own -c/--ini/--baseline flags are operator-owned and gated
    // separately below.
    private static readonly string[] RepositorySuppressionGlobs = ["*.bandit"];

    // ExtraArguments flags whose presence would collapse the declared
    // exit-code contract (--exit-zero) or replace/divert the SARIF report
    // the parser expects (-f/--format, -o/--output). Rejected
    // deterministically in VerifyToolAsync.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--exit-zero", ""),
        ("--format", "-f"),
        ("--output", "-o"),
    ];

    // ExtraArguments flags whose value is a file the tool loads for
    // gate-shaping content — the YAML/TOML scanner config, the project INI
    // supplying command-line arguments, and the JSON baseline the report is
    // diffed against — each with its short form (verified against
    // bandit/cli/main.py: -c/--configfile, --ini, -b/--baseline). Gated
    // outside the worktree in VerifyToolAsync.
    private static readonly (string LongFlag, string? ShortFlag)[] PathValuedArgumentFlags =
    [
        ("--configfile", "-c"),
        ("--ini", null),
        ("--baseline", "-b"),
    ];

    /// <summary>
    /// Pre-scan preconditions beyond the base's presence and pinned-version
    /// checks: operator-supplied gate-shaping flags in
    /// <c>ExtraArguments</c> are rejected or contained, and — unless the
    /// operator opted in — no repository-controlled <c>.bandit</c> project
    /// file may be present anywhere in the audited tree. All fail closed as
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

        // Bandit walks the -r scan targets for a project .bandit INI file
        // and silently honors a single match: its exclude/tests/skips
        // entries can drop paths and checks, so a repo-authored file yields
        // a clean verdict over vulnerable code. Presence fails closed
        // unless the operator trusts repository-controlled suppression.
        var present = await ProbeRepositoryPathGlobsPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            RepositorySuppressionGlobs,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled file(s) "
                + $"'{string.Join("', '", present)}' in the audited repository — "
                + "bandit auto-discovers that project file when scanning recursively and its "
                + "options can exclude paths and skip tests, so the audit subject could hide a "
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
            + "ExcludePaths); ExtraArguments is for everything else (e.g. -t/--tests, -s/--skip, "
            + "-p/--profile, -x/--exclude).")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Canonicalizes the value of every <c>ExtraArguments</c> flag that names
    /// a gate-shaping file the tool loads and fails closed when it resolves
    /// inside the audited worktree. The flags resolve against the tool's cwd
    /// — the worktree — so a relative or in-tree value would hand the
    /// scanner config, argument INI, or finding baseline to
    /// repository-controlled content. Runs in both trust modes: it guards
    /// the operator knob, and the opt-in <c>TrustRepositorySuppression</c>
    /// already covers the sanctioned repo file without a flag.
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
            "BanditAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
