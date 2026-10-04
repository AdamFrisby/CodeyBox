using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SocketAuditorPlugin;

/// <summary>
/// Dependency supply-chain risk auditor wrapping <c>socket</c> (Socket.dev's
/// scanner for supply-chain risk signals — malware, typosquats, protestware,
/// telemetry, troll packages, deprecated or unmaintained dependencies, CVEs,
/// and related package alerts) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the repository-config
/// gate via <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, the
/// operator-config containment via
/// <see cref="ExternalToolAuditorBase.BuildToolArguments"/>, and the
/// socket-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (blocking on error).</b> The
/// organisation-policy actions map as <c>error</c> to
/// <see cref="AuditSeverity.Error"/> — those findings fail the audit;
/// <c>warn</c> to <see cref="AuditSeverity.Warning"/> (advisory) and
/// <c>monitor</c>/<c>ignore</c>/<c>defer</c> to
/// <see cref="AuditSeverity.Info"/> (informational).
/// <c>MinimumSeverity</c> only drops findings, it never raises them. This
/// auditor therefore blocks the audit only on policy-error findings.</para>
///
/// <para><b>Exit-code convention (verified against socket 1.4.1).</b> Socket
/// does NOT follow the common "1 = findings" convention in either direction:
/// <c>0</c> is "ran, report healthy" (the JSON may still carry warn/monitor
/// alerts — the report, not the exit, is the verdict), <c>1</c> is both "ran
/// and the report is unhealthy" AND every operational failure (missing API
/// token at call time, API error, network failure), and <c>2</c> is
/// incorrect usage or failed input validation (missing org, missing API
/// token, unknown arguments). <c>126</c>/<c>127</c> cannot-execute is
/// infrastructure via the base. Both <c>0</c> and <c>1</c> are verdicts
/// whose meaning comes from parsing the JSON report: <c>{ok:true,…}</c> is
/// the scan verdict, <c>{ok:false,…}</c> is "could not run" and fails closed
/// as infrastructure through the parser.</para>
///
/// <para><b>Stream note.</b> The JSON policy report goes to <b>stdout</b>
/// (<c>--json --report</c>); the banner, spinner, and progress logs go to
/// stderr and are silenced with <c>--no-banner --no-spinner</c> so the
/// report stream stays clean. The <see cref="SocketScanReportParser"/> reads
/// stdout.</para>
///
/// <para><b>Version pin.</b> Socket's alert taxonomy, policy evaluation, and
/// report shape change between releases, so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>socket --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> Socket reads
/// <c>socket.json</c> from the scan root and from nested directories (a
/// per-directory cascade) to take flag defaults for manifest generation:
/// disabled ecosystems are skipped entirely and build-tool selections
/// (<c>bin</c>, <c>gradleOpts</c>, <c>sbtOpts</c>, <c>excludeConfigs</c>)
/// point the CLI at executables and options from inside the audited tree.
/// The audit subject could use any of that to shrink or steer the scan, so
/// the presence of a <c>socket.json</c> file at any depth fails closed as
/// infrastructure by default; operators who trust repo-authored config set
/// <c>TrustRepositorySuppression</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>.</c> — the whole
/// worktree: every manifest Socket discovers (npm, PyPI, Maven, Go,
/// RubyGems, and more) is uploaded for server-side analysis. Findings inside
/// vendored or generated trees (<c>vendor/</c>, <c>third_party/</c>,
/// <c>node_modules/</c>) describe upstream code, so they are excluded by
/// default; operators re-include a path by overriding
/// <c>ExcludePaths</c>. Files ignored by the repository's own
/// <c>.gitignore</c> are additionally skipped by Socket's manifest
/// discovery itself — absence of findings for git-ignored files is a tool
/// limitation, not evidence of safety.</para>
///
/// <para><b>Credentials, network, and quota.</b> Every scan uploads the
/// discovered manifests to the Socket API and evaluates them against the
/// organisation's security policy, so the auditor declares
/// <see cref="AuditCapabilities.Network"/>, the operator must provision an
/// API token into the sandbox baseline (<c>SOCKET_CLI_API_TOKEN</c> or a
/// logged-in CLI config) and select the organisation via <c>Org</c> (or the
/// CLI's configured default), and each run consumes Socket quota (one scan
/// plus one report). The Socket hosts must be in the deployment's
/// <c>AuditToolAllowedHosts</c> egress list. A missing token or org fails
/// closed as infrastructure naming the tool — never a pass. Audit scans run
/// with <c>--tmp</c> so they stay temporary and never become the
/// organisation alerts page.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Socket Dependency Supply-Chain Risks",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "socket",
    InstallHint = "provision the pinned socket release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — install the versioned npm release "
        + "(`npm install -g socket@VERSION`) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions, then authenticate once (`socket login` or SOCKET_CLI_API_TOKEN) and "
        + "configure the default org; no distro apt package carries a pinned socket")]
public sealed class SocketAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.socket";

    /// <summary>
    /// Socket release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.4.1";

    /// <summary>
    /// Scoped-config key for the Socket organisation slug (<c>--org</c>).
    /// Unset, the CLI uses its configured default org or auto-discovery;
    /// without any org the scan fails closed as infrastructure.
    /// </summary>
    public const string OrgKey = "Org";

    /// <summary>
    /// Scoped-config key opting in to repository-authored Socket config
    /// (<c>socket.json</c> at any depth under the worktree). Default false:
    /// the audited repo must not be able to shrink or steer the scan with
    /// disabled ecosystems or build-tool overrides.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // Socket reads socket.json from the scan root and from nested
    // directories (a per-directory manifest cascade): "socket.json" covers
    // the root, "*/socket.json" every nested one. Both globs match the file
    // name exactly — a prefix like "mysocket.json" is never CLI config and
    // must not trip the gate.
    private static readonly string[] RepositoryConfigGlobs =
    [
        "socket.json",
        "*/socket.json",
    ];

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // remove the verdict the parser reads, silently narrow the reported
    // signal, bail before scanning, prompt in the sandbox, redirect the
    // scan outside the audited worktree, or change the dashboard
    // side-effect contract. Rejected deterministically with a pointer to the
    // scoped key covering the same need.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--json", ""), // report sink — pinned to JSON on stdout
        ("--markdown", ""), // report sink — pinned to JSON on stdout
        ("--report", ""), // required: without it stdout is scan metadata, not a verdict
        ("--report-level", ""), // pinned to monitor; use MinimumSeverity
        ("--short", ""), // drops alert detail — an unhealthy scan would report zero findings
        ("--org", ""), // org selection — use the Org scoped key
        ("--dry-run", ""), // bails before scanning — exit 0 with no verdict
        ("--read-only", ""), // stops before creating a report — no verdict
        ("--interactive", ""), // may prompt — hangs the sandbox; auditor pins --no-interactive
        ("--cwd", ""), // would redirect the scan outside the audited worktree
        ("--tmp", ""), // pinned temporary scan — keeps audit scans off the dashboard
        ("--no-tmp", ""), // pinned temporary scan — keeps audit scans off the dashboard
        ("--set-as-alerts-page", ""), // dashboard side effects — disabled via --tmp
        ("--no-set-as-alerts-page", ""), // dashboard side effects — disabled via --tmp
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against socket 1.4.1: 0 is "ran, report healthy" (the
        // JSON may still carry warn/monitor alerts — the report decides), 1
        // is both "ran, report unhealthy" and operational failure (the JSON
        // envelope decides: {ok:true} is the verdict, {ok:false} is
        // infrastructure). Every other exit (2 for incorrect usage and
        // failed input validation such as a missing token or org; 126/127
        // cannot-execute) means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings inside vendored/dependency trees describe upstream code —
        // noise that trains operators to ignore the auditor. Operators
        // re-include a path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _org = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:socket";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "socket";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SocketScanReportParser();

    /// <summary>
    /// Declared mapping from Socket's policy-action vocabulary to
    /// <see cref="AuditSeverity"/>: <c>error</c> (policy violation — the
    /// scan itself reports unhealthy) fails the audit, <c>warn</c> is
    /// advisory, and <c>monitor</c>/<c>ignore</c>/<c>defer</c> are
    /// informational. The alert's own severity field is irrelevant to the
    /// report path — policy actions are the verdict — so raw tool levels
    /// never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Warning,
            ["monitor"] = AuditSeverity.Info,
            ["ignore"] = AuditSeverity.Info,
            ["defer"] = AuditSeverity.Info,
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

        var args = new List<string>
        {
            // Repository scan producing the policy report the parser reads.
            "scan", "create",
            // JSON report on stdout; the parser reads the report stream.
            "--json",
            // Wait for scan completion and print the policy evaluation:
            // without it stdout is scan metadata, not a verdict.
            "--report",
            // Pin the inclusion floor to monitor (error, warn, and monitor
            // alerts are all emitted): MinimumSeverity filters after mapping,
            // so the report must carry everything mappable. Severities stay
            // pinned — --report-level is reserved.
            "--report-level", "monitor",
            // Temporary scan: audit runs must not become the organisation
            // alerts page or spam permanent dashboard history.
            "--tmp",
            // Never prompt (org auto-discovery, target confirmation): a
            // prompt hangs the sandbox until the timeout.
            "--no-interactive",
            // Keep logs off the report stream.
            "--no-banner",
            "--no-spinner",
        };

        AddValueFlag(args, "--org", _org());

        // Whole-worktree scan target. Author-chosen constant, never
        // untrusted data; --cwd is reserved so it cannot be redirected.
        args.Add(".");

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _org = () => ValidatedScopedValue(scoped[OrgKey], OrgKey);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "SocketAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Socket-specific precondition on the live path: Socket reads
    /// <c>socket.json</c> from the scan root and from nested directories to
    /// take flag defaults for manifest generation, and the disabled
    /// ecosystems and build-tool selections (<c>bin</c>, <c>gradleOpts</c>,
    /// <c>sbtOpts</c>, <c>excludeConfigs</c>) inside can shrink or steer the
    /// scan the audit subject authors. Unless the operator opted in via
    /// <see cref="TrustRepositorySuppressionKey"/>, any such file under the
    /// worktree fails closed as infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (_trustRepositorySuppression())
            return;

        var present = await ProbeRepositoryPathGlobsPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            RepositoryConfigGlobs,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled config "
                + $"file(s) '{string.Join("', '", present)}' in the audited repository — "
                + "socket reads them for manifest-generation defaults and their disabled "
                + "ecosystems and build-tool selections can shrink or steer the scan, so "
                + "the audit subject could hide a risky dependency. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-authored socket config.")
            { IsDeterministic = true };
    }

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
            $"could-not-verify: auditor 'codeybox:socket' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would redirect the report "
            + "sink, remove the verdict the parser reads, silently narrow the reported signal, bail "
            + "before scanning, prompt in the sandbox, redirect the scan outside the audited worktree, "
            + "or change the dashboard side-effect contract. Use the scoped keys under "
            + $"CodeyBox:Plugins:{PluginId} (Org) or the shared knobs "
            + "(MinimumSeverity, IncludedRules, ExcludedRules); ExtraArguments is for everything else "
            + "(e.g. --repo, --branch, --exclude-paths, --reach).")
        { IsDeterministic = true };
    }
}
