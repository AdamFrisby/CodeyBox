using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.OsvScannerAuditorPlugin;

/// <summary>
/// Dependency vulnerability auditor wrapping <c>osv-scanner</c> (Google's
/// scanner matching project lockfiles against the OSV database) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the repository-config
/// gate via <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, the
/// operator-config containment via
/// <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync(CodeyBox.Core.ISandbox, string, CodeyBox.Core.AuditContext, ExternalToolAuditorOptions, CancellationToken)"/>,
/// and the osv-scanner-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven (blocking on error).</b> The
/// parser converts each rule's CVSS score to a qualitative token
/// (<c>critical</c>/<c>high</c> to <see cref="AuditSeverity.Error"/> — those
/// findings fail the audit; <c>medium</c> to
/// <see cref="AuditSeverity.Warning"/> is advisory and <c>low</c> to
/// <see cref="AuditSeverity.Info"/> is informational; unscored results take
/// the mapping default of <see cref="AuditSeverity.Warning"/>).
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against osv-scanner 2.6.0).</b>
/// osv-scanner follows the common "1 = findings" convention: <c>0</c> is
/// "ran" (clean, or nothing to scan — see <c>--allow-no-lockfiles</c>
/// below) and <c>1</c> is "ran and matched at least one vulnerability".
/// Both are verdicts whose findings come from parsing the SARIF report,
/// never from the exit code alone. Every other exit is infrastructure:
/// <c>127</c> for bad flags and unreadable config files, <c>128</c> when no
/// package sources exist (unreachable with the auditor's pinned flags, but
/// classified all the same), <c>129</c> when the vulnerability-database
/// query fails, <c>130</c> for an invalid (unparseable or misused)
/// configuration file, and <c>126</c>/<c>127</c> cannot-execute.</para>
///
/// <para><b>Stream note.</b> The SARIF report goes to <b>stdout</b>
/// (<c>--format sarif</c>); walk progress and error text go to stderr. The
/// <see cref="OsvScannerSarifParser"/> reads stdout.</para>
///
/// <para><b>Version pin.</b> osv-scanner's extractors, matchers, and severity
/// data change between releases, so findings are only meaningful from the
/// build the auditor was verified against. The auditor probes
/// <c>osv-scanner --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> osv-scanner loads an
/// <c>osv-scanner.toml</c> sitting beside each scanned lockfile, and its
/// <c>IgnoredVulns</c> entries and <c>PackageOverrides</c> ignore rules
/// suppress matches the subject authors — verified: a one-entry file
/// dropped its CVE from the report. Any such file anywhere in the worktree
/// (the config applies per lockfile directory, not just the root) fails
/// closed as infrastructure by default; operators who trust repo-authored
/// config set <c>TrustRepositorySuppression</c>, and operators who need a
/// fixed organizational policy pin an operator-owned file via
/// <c>ConfigPath</c> (which must resolve outside the worktree — a relative
/// <c>--config</c> resolves against the worktree, so an in-tree path would
/// hand the diff author flag control). Pinning <c>ConfigPath</c> does not
/// lift the gate: it only replaces the default policy source.</para>
///
/// <para><b>Scope and defaults.</b> The scan target is the whole worktree
/// (<c>scan source --recursive .</c>): every lockfile the built-in extractors
/// recognise, including nested directories. <c>--allow-no-lockfiles</c> is
/// always passed so dependency-free worktrees (and lockfiles yielding zero
/// packages) exit <c>0</c> with an empty report — a pass, consistent with
/// the sibling auditors' clean runs — instead of tripping the
/// no-package-sources exit. Findings inside vendored or generated trees
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) describe
/// upstream code and usually duplicate the manifest-declared finding for
/// the same package, so they are excluded by default; operators re-include
/// a path by overriding <c>ExcludePaths</c>. Container-image scanning
/// (<c>scan image</c>) is out of scope — it needs a container runtime the
/// worktree audit does not have.</para>
///
/// <para><b>Network and database.</b> osv-scanner resolves and matches
/// packages against the OSV database (via the default
/// <c>deps.dev</c> data source) on every run, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> and the database hosts must be
/// in the deployment's <c>AuditToolAllowedHosts</c> egress list. A run that
/// cannot reach the database exits non-zero and fails loudly as
/// infrastructure — it never passes on stale or absent data. Fully offline
/// deployments cannot use this auditor as configured; operators with
/// pre-seeded local databases may reach for the tool's offline flags via
/// <c>ExtraArguments</c>, accepting that the verdict then reflects the
/// staleness of their cache.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: OSV-Scanner Dependency Vulnerabilities",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "osv-scanner",
    InstallHint = "provision the pinned osv-scanner release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — download the versioned "
        + "upstream release binary (https://github.com/google/osv-scanner/releases) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions; "
        + "no distro apt package carries a pinned osv-scanner")]
public sealed class OsvScannerAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.osv-scanner";

    /// <summary>
    /// osv-scanner release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.6.0";

    /// <summary>
    /// Scoped-config key for an explicit osv-scanner configuration file
    /// (<c>--config</c>). Set it to pin an operator-owned policy; unset,
    /// osv-scanner resolves the audited repository's own per-directory
    /// <c>osv-scanner.toml</c> files (gated by default — see
    /// <see cref="TrustRepositorySuppressionKey"/>). Must resolve outside
    /// the audited worktree. Pinning a file replaces the default policy
    /// source; it does not lift the repository-suppression gate.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored osv-scanner config
    /// (<c>osv-scanner.toml</c> beside any lockfile). Default false: the
    /// audited repo must not be able to silence the scan with
    /// <c>IgnoredVulns</c> entries or <c>PackageOverrides</c> ignore rules.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // osv-scanner resolves its config per scanned lockfile directory, so a
    // suppression file can sit at any depth — the root-only probe cannot see
    // a nested one. The '*' matches across separators, so this one glob
    // covers the worktree root and every subdirectory ('.git' storage is
    // pruned by the probe: transport metadata, not audited source).
    private static readonly string[] RepositoryConfigGlobs = ["*osv-scanner.toml"];

    // Flags whose presence in ExtraArguments would redirect the report sink,
    // change the declared exit-code contract, retarget or narrow the pinned
    // whole-tree scan, widen the verdict beyond vulnerabilities, hang the
    // bounded run serving output, or re-open the repository-controlled
    // config surface. Rejected deterministically with a pointer to the
    // scoped key covering the same need.
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--format", "-f"), // report sink — pinned to sarif on stdout
        ("--output", ""), // report sink — ours is stdout (deprecated alias)
        ("--output-file", ""), // report sink — ours is stdout
        ("--config", ""), // repo-config surface — use ConfigPath
        ("--recursive", "-r"), // scan scope — always on by design
        ("--allow-no-lockfiles", ""), // exit contract — always on by design
        ("--lockfile", "-L"), // scan target — fixed to the whole tree
        ("--sbom", "-S"), // scan target — fixed to the whole tree
        ("--licenses", ""), // verdict semantics — vulnerabilities only
        ("--serve", ""), // bounded run — serving would hang the scan
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against osv-scanner 2.6.0: 0 is "ran" (clean, or nothing
        // to scan under --allow-no-lockfiles — the SARIF decides), 1 is "ran
        // and matched at least one vulnerability". Every other exit (127 for
        // bad flags and unreadable configs, 128 for no package sources, 129
        // for database query failures, 130 for invalid configs, 126/127
        // cannot-execute) means "could not run".
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings inside vendored/dependency trees describe upstream code and
        // usually duplicate the manifest-declared match for the same package —
        // noise that trains operators to ignore the auditor. Operators
        // re-include a path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:osv-scanner";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "osv-scanner";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new OsvScannerSarifParser();

    /// <summary>
    /// Declared mapping from the parser's qualitative severity tokens
    /// (derived from each rule's CVSS <c>security-severity</c> score via
    /// <see cref="OsvScannerSarifParser.MapScoreToToken(double)"/>: standard
    /// CVSS bands, so this tool's "high" means what every other auditor's
    /// "high" means) to <see cref="AuditSeverity"/>. Raw tool values never
    /// reach findings; unscored results take the <c>unknown</c> default.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            [OsvScannerSarifParser.CriticalToken] = AuditSeverity.Error,
            [OsvScannerSarifParser.HighToken] = AuditSeverity.Error,
            [OsvScannerSarifParser.MediumToken] = AuditSeverity.Warning,
            [OsvScannerSarifParser.LowToken] = AuditSeverity.Info,
            [OsvScannerSarifParser.UnknownToken] = AuditSeverity.Warning,
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

        // Structured argv, never a shell string: the explicit 'scan source'
        // subcommand (scan's default today, pinned so a future default
        // change cannot retarget the run), SARIF on stdout for the parser,
        // the whole worktree including nested lockfiles, and the
        // no-lockfiles allowance so dependency-free trees report clean
        // instead of tripping the no-package-sources exit. Author-chosen
        // constants, never untrusted data.
        return
        [
            "scan",
            "source",
            "--format", "sarif",
            "--recursive",
            "--allow-no-lockfiles",
            ".",
        ];
    }

    /// <summary>
    /// Emits the <c>--config</c> pair — the one argv element that needs a
    /// bounded sandbox probe to compute. Unset <c>ConfigPath</c> → no flag:
    /// osv-scanner would then resolve the audited repository's own
    /// per-directory <c>osv-scanner.toml</c> files, which the
    /// <c>VerifyToolAsync</c> gate already fails closed on. An operator's
    /// <c>ConfigPath</c> is canonicalized in the sandbox and rejected when
    /// it resolves inside the worktree — osv-scanner resolves a relative
    /// <c>--config</c> against the worktree, so an in-tree path would hand
    /// the diff author flag control — and the validated value is exactly
    /// the value argv carries.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = ValidatedScopedValue(_configPath(), ConfigPathKey);
        if (configured is null)
            return [];

        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, configured, ConfigPathKey, options, ct).ConfigureAwait(false);
        return ["--config", canonical];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "OsvScannerAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// osv-scanner-specific precondition on the live path: osv-scanner loads
    /// an <c>osv-scanner.toml</c> sitting beside each scanned lockfile, and
    /// the <c>IgnoredVulns</c> entries and <c>PackageOverrides</c> ignore
    /// rules inside can suppress matches the audit subject authors. Unless
    /// the operator opted in via
    /// <see cref="TrustRepositorySuppressionKey"/>, any such file anywhere
    /// in the worktree fails closed as infrastructure before the scan runs.
    /// Pinning an operator-owned policy via <see cref="ConfigPathKey"/>
    /// does not lift the gate — it only replaces the default policy source.
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
                + "osv-scanner loads them beside each scanned lockfile and their ignored "
                + "vulnerability entries and package overrides suppress matches, so the audit "
                + "subject could hide a vulnerability. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-authored osv-scanner config.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Resolves the absolute directory the scan runs in via a bounded
    /// <c>pwd</c> probe: sandbox providers may translate the audit's working
    /// directory, and osv-scanner reports absolute <c>file://</c> URIs with
    /// no worktree embedded, so the parser needs the per-run root to
    /// relativize findings (and to keep <c>ExcludePaths</c> effective)
    /// instead of guessing.
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
            $"could-not-verify: auditor 'codeybox:osv-scanner' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would redirect the report "
            + "sink, change the declared exit-code contract, retarget or narrow the pinned whole-tree "
            + "scan, widen the verdict beyond vulnerabilities, hang the bounded run, or re-open the "
            + "repository-controlled config surface. Use the scoped keys under "
            + $"CodeyBox:Plugins:{PluginId} (ConfigPath) or the shared knobs "
            + "(MinimumSeverity, IncludedRules, ExcludedRules); ExtraArguments is for everything else "
            + "(e.g. --no-resolve, --verbosity, --offline-vulnerabilities with a pre-seeded database).")
        { IsDeterministic = true };
    }
}
