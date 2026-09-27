using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CargoAuditAuditorPlugin;

/// <summary>
/// Dependency auditor wrapping <c>cargo-audit</c> (RustSec advisory matching
/// against the audited repository's <c>Cargo.lock</c>) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the repository-config
/// gate via <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, and the
/// cargo-audit-specific arguments and knobs below. cargo-audit emits SARIF,
/// so the shared <see cref="SarifToolOutputParser"/> parses the report — no
/// tool-specific parser is needed.
///
/// <para><b>Gate behaviour: severity-driven (blocking on vulnerabilities).</b>
/// cargo-audit's SARIF reports every matched vulnerability advisory at
/// <c>error</c> level → <see cref="AuditSeverity.Error"/>, which fails the
/// audit — the same gate cargo-audit itself applies when it exits non-zero.
/// Warnings (yanked packages and the informational-advisory kinds
/// <c>unmaintained</c>, <c>unsound</c>, <c>notice</c>) report at
/// <c>warning</c> level → <see cref="AuditSeverity.Warning"/>, advisory
/// only. <c>MinimumSeverity</c> only drops findings, it never raises
/// them.</para>
///
/// <para><b>Exit-code convention (verified against the cargo-audit 0.22.x
/// source).</b> cargo-audit does NOT follow the common "0 = clean, 1 =
/// findings, 2 = could not run" convention in a way the exit code alone can
/// express: <c>0</c> = the report was generated and no vulnerability (and no
/// denied warning) was found; <c>1</c> = EITHER the report was generated and
/// <c>should_exit_with_failure</c> held (vulnerabilities found — this
/// auditor passes no <c>-D</c> flags, so warnings never trip it) OR an
/// advisory-database fetch/load failure aborted the run
/// (<c>exit(1)</c> after a <c>status_err!</c>); <c>2</c> = could not run —
/// clap usage errors, a lockfile that could not be loaded, and every
/// <c>audit_lockfile</c> error. The discriminator between "ran and found"
/// and "could not run" at exit <c>1</c> is the SARIF document itself:
/// <c>presenter.print_report</c> writes it to stdout only when the audit
/// completes, so the shared parser fails closed as infrastructure when
/// stdout does not carry a well-formed SARIF report — a failed advisory-db
/// fetch emits only a stderr status line. <c>101</c> panics and
/// <c>126</c>/<c>127</c> cannot-execute are infrastructure as well.</para>
///
/// <para><b>Stream note.</b> The SARIF report goes to <b>stdout</b>;
/// <c>is_quiet()</c> is implied by the SARIF output format, so the
/// <c>status_ok!</c>/<c>status_warn!</c> progress and self-advisory prints
/// that would otherwise pollute the report stream are suppressed. stderr
/// carries only error diagnostics, retained in the raw output.</para>
///
/// <para><b>Version pin.</b> cargo-audit's matching behaviour and report
/// shape change between releases, so findings are only meaningful from the
/// build the auditor was verified against. The auditor probes
/// <c>cargo-audit --version</c> before every scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> cargo-audit loads
/// <c>./.cargo/audit.toml</c> relative to its working directory — the
/// audited worktree root — and that config can ignore advisory ids
/// (<c>[advisories] ignore</c>), point the advisory database at an
/// attacker-chosen URL (<c>[database] url</c>/<c>path</c>), or disable the
/// yanked check (<c>[yanked] enabled = false</c>): a suppression surface
/// the audit subject authors. Its presence fails closed as infrastructure
/// by default; operators who trust repo-authored config set
/// <c>TrustRepositorySuppression</c>. The home-directory
/// <c>$CARGO_HOME/audit.toml</c> load site is operator territory, not the
/// audit subject's.</para>
///
/// <para><b>Lockfile resolution.</b> The auditor always passes an explicit
/// <c>--file</c> (default <c>Cargo.lock</c>): cargo-audit's
/// <c>locate_or_generate</c> then returns the path directly, so the
/// implicit "generate a missing lockfile via
/// <c>cargo update --workspace</c>" path — which would mutate the audited
/// worktree, require the <c>cargo</c> binary, and honour a repository
/// <c>.cargo/config.toml</c> registry redirect — can never run. A missing
/// or unreadable lockfile is therefore a plain load failure: exit
/// <c>2</c>, infrastructure, never a pass.</para>
///
/// <para><b>Scope and defaults.</b> cargo-audit's subject is the resolved
/// version set in the lockfile, not a file tree — vendored or generated
/// source trees are irrelevant to its matching and produce no findings, so
/// there is no vendored-code noise to exclude by default. Findings carry
/// the lockfile path (<c>Cargo.lock</c>) and line <c>1</c> — the only
/// location cargo-audit reports; <c>ExcludePaths</c> matches only that
/// path. Non-Rust repositories fail loudly: the lockfile load error is
/// infrastructure, never a pass.</para>
///
/// <para><b>Network.</b> By default cargo-audit fetches the RustSec
/// advisory database (a git clone) and updates the cached crates.io index
/// for yanked-crate checks, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> and the egress hosts must be in
/// the deployment's <c>AuditToolAllowedHosts</c> list. Fully offline
/// deployments pre-seed the advisory database (and, for yanked checks, the
/// index) into the baseline and set <c>Offline</c> — and optionally pin
/// the database location via <c>DatabasePath</c>; a missing index degrades
/// to a skipped yanked check with a tool warning, never a hidden
/// pass.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: cargo-audit Rust Dependency Vulnerabilities",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cargo-audit",
    InstallHint = "provision the pinned cargo-audit release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `cargo install cargo-audit "
        + "--locked --version " + DefaultExpectedVersion + "` or the versioned upstream GitHub "
        + "release binary via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions, plus a pre-seeded advisory database when running Offline; "
        + "no distro apt package carries a pinned cargo-audit")]
public sealed class CargoAuditAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.cargo-audit";

    /// <summary>
    /// cargo-audit release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.22.2";

    /// <summary>
    /// Scoped-config key for the lockfile under audit (<c>--file</c>).
    /// Unset → <c>Cargo.lock</c> at the worktree root. An explicit
    /// <c>--file</c> is always passed so cargo-audit's implicit
    /// "generate a missing lockfile" path never runs. The value
    /// <c>-</c> (read the lockfile from stdin) is rejected: the auditor
    /// does not feed the tool's stdin.
    /// </summary>
    public const string LockfilePathKey = "LockfilePath";

    /// <summary>
    /// Scoped-config key for the advisory database git repository path
    /// (<c>--db</c>). Set it to pin a baseline-provisioned database;
    /// unset, cargo-audit uses its default
    /// <c>~/.cargo/advisory-db</c>.
    /// </summary>
    public const string DatabasePathKey = "DatabasePath";

    /// <summary>
    /// Scoped-config key for the advisory database git URL
    /// (<c>--url</c>) — e.g. an organization mirror of the RustSec
    /// advisory-db. The value is an outbound-request target the operator
    /// pins; the audited repository cannot override it because the
    /// repository <c>.cargo/audit.toml</c> is gated (see
    /// <see cref="TrustRepositorySuppressionKey"/>).
    /// </summary>
    public const string DatabaseUrlKey = "DatabaseUrl";

    /// <summary>
    /// Scoped-config boolean for <c>--no-fetch</c>: never touch the
    /// network — requires a pre-seeded advisory database
    /// (<see cref="DatabasePathKey"/> or the default location) and, for
    /// yanked checks, a cached crates.io index.
    /// </summary>
    public const string OfflineKey = "Offline";

    /// <summary>
    /// Scoped-config boolean for <c>--stale</c>: accept an advisory
    /// database that has not been updated recently instead of failing the
    /// fetch. Only meaningful while fetching is enabled.
    /// </summary>
    public const string StaleKey = "Stale";

    /// <summary>
    /// Scoped-config boolean for <c>--no-yanked</c>: skip the yanked-crate
    /// check entirely — and with it the crates.io-index access it needs.
    /// </summary>
    public const string NoYankedKey = "NoYanked";

    /// <summary>
    /// Scoped-config key for CPU-architecture filters (comma-separated
    /// values, repeatable <c>--target-arch</c>): advisories scoped to
    /// other architectures stop matching. cargo-audit validates the
    /// values itself; a bad value is a loud run failure, not a pass.
    /// </summary>
    public const string TargetArchKey = "TargetArch";

    /// <summary>
    /// Scoped-config key for operating-system filters (comma-separated
    /// values, repeatable <c>--target-os</c>): advisories scoped to other
    /// platforms stop matching. cargo-audit validates the values itself.
    /// </summary>
    public const string TargetOsKey = "TargetOs";

    /// <summary>
    /// Scoped-config key opting in to repository-authored cargo-audit
    /// config (<c>.cargo/audit.toml</c>). Default false: the audited repo
    /// must not be able to ignore advisories, redirect the advisory
    /// database, or disable checks.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private const string DefaultLockfilePath = "Cargo.lock";

    // Abscissa's config_path() checks ./.cargo/audit.toml relative to the
    // working directory — the worktree root — before $CARGO_HOME. Only the
    // in-repo load site is the audit subject's to abuse.
    private static readonly string[] RepositoryConfigFiles = [".cargo/audit.toml"];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against cargo-audit 0.22.x: 0 = report generated, no
        // vulnerability (and no denied warning) found; 1 = report
        // generated and vulnerabilities found — but ALSO an
        // advisory-database fetch/load failure, which emits only a stderr
        // status line. The SARIF document on stdout is the discriminator,
        // enforced by the shared parser. 2 = could not run (clap usage
        // errors, lockfile load failures, audit errors), 101 panic,
        // 126/127 cannot-execute — all infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _lockfilePath = static () => null;
    private Func<string?> _databasePath = static () => null;
    private Func<string?> _databaseUrl = static () => null;
    private Func<bool> _offline = static () => false;
    private Func<bool> _stale = static () => false;
    private Func<bool> _noYanked = static () => false;
    private Func<IReadOnlyList<string>> _targetArch = static () => [];
    private Func<IReadOnlyList<string>> _targetOs = static () => [];
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:cargo-audit";

    /// <inheritdoc />
    public override AuditCapabilities Required => _offline() ? AuditCapabilities.None : AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "cargo-audit";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from cargo-audit's SARIF severity vocabulary to
    /// <see cref="AuditSeverity"/>: <c>error</c> is a matched vulnerability
    /// advisory (the same verdict cargo-audit exits non-zero for) and maps
    /// to <see cref="AuditSeverity.Error"/>; <c>warning</c> is a yanked or
    /// informational-advisory warning and stays advisory;
    /// <c>note</c> maps to <see cref="AuditSeverity.Info"/>. Unknown levels
    /// default to <see cref="AuditSeverity.Warning"/>. Raw tool tokens never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var lockfilePath = _lockfilePath()?.Trim();
        if (string.IsNullOrWhiteSpace(lockfilePath))
            lockfilePath = DefaultLockfilePath;
        if (lockfilePath == "-")
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with {LockfilePathKey} '-', "
                + "cargo-audit's read-the-lockfile-from-stdin convention — the auditor does not "
                + $"feed tool stdin. Set CodeyBox:Plugins:{PluginId}:{LockfilePathKey} to a real "
                + "lockfile path (default: Cargo.lock at the worktree root).")
            { IsDeterministic = true };

        // `--file` is passed unconditionally: locate_or_generate returns an
        // explicit path without checking existence, which disables the
        // implicit `cargo update --workspace` lockfile-generation path — no
        // worktree mutation, no `cargo` binary needed, no repository
        // .cargo/config.toml in play. A missing lockfile then fails the load
        // loudly as exit 2.
        var args = new List<string>
        {
            "audit",
            "--format", "sarif",
            "--file", lockfilePath,
        };

        AddValueFlag(args, "--db", _databasePath());
        AddValueFlag(args, "--url", _databaseUrl());

        if (_offline())
            args.Add("--no-fetch");
        if (_stale())
            args.Add("--stale");
        if (_noYanked())
            args.Add("--no-yanked");

        foreach (var arch in _targetArch())
        {
            if (string.IsNullOrWhiteSpace(arch))
                continue;
            args.Add("--target-arch");
            args.Add(arch.Trim());
        }
        foreach (var os in _targetOs())
        {
            if (string.IsNullOrWhiteSpace(os))
                continue;
            args.Add("--target-os");
            args.Add(os.Trim());
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
        _lockfilePath = () => scoped[LockfilePathKey];
        _databasePath = () => scoped[DatabasePathKey];
        _databaseUrl = () => scoped[DatabaseUrlKey];
        _offline = () => bool.TryParse(scoped[OfflineKey], out var offline) && offline;
        _stale = () => bool.TryParse(scoped[StaleKey], out var stale) && stale;
        _noYanked = () => bool.TryParse(scoped[NoYankedKey], out var noYanked) && noYanked;
        _targetArch = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetArchKey]);
        _targetOs = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetOsKey]);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "CargoAuditAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// cargo-audit-specific precondition on the live path: abscissa's
    /// <c>config_path()</c> loads <c>./.cargo/audit.toml</c> relative to the
    /// working directory — the audited worktree root — before falling back
    /// to <c>$CARGO_HOME</c>. That file can list advisories to ignore,
    /// repoint or replace the advisory database
    /// (<c>[database] url</c>/<c>path</c>), and disable the yanked check —
    /// every one a way for the audit subject to hide findings or hollow
    /// out the scan. Unless the operator opted in via
    /// <see cref="TrustRepositorySuppressionKey"/>, its presence fails
    /// closed as infrastructure before the scan runs.
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

        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            RepositoryConfigFiles,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled config "
                + $"file(s) '{string.Join("', '", present)}' in the audited repository — "
                + "cargo-audit loads it from the working directory and its advisory ignores, "
                + "advisory-database override, and check switches suppress findings, so the "
                + "audit subject could hide a vulnerability or hollow out the scan. Remove the "
                + "file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-authored cargo-audit config.")
            { IsDeterministic = true };
    }
}
