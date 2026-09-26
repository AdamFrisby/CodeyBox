using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CargoSemverChecksAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>cargo-semver-checks</c> (Rust public
/// API semver-violation detection) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the cargo-semver-checks report parser
/// (<see cref="CargoSemverChecksReportParser"/> — the tool has no
/// <c>--format json</c> mode; its verdict is a structured human report of
/// <c>--- failure/warning &lt;lint-id&gt;: &lt;name&gt; ---</c> sections with
/// per-result <c>Failed in:</c> lines), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, baseline resolution
/// for the audit's base branch, and the manifest/suppression preconditions
/// below.
///
/// <para><b>Gate behaviour: blocking on deny-level findings.</b> Every lint
/// cargo-semver-checks reports at its <c>failure</c> level (the lints' stock
/// <c>Deny</c> level — public API removals and breaking signature changes)
/// maps to <see cref="AuditSeverity.Error"/> and fails the audit; its
/// <c>warning</c> level (lints configured <c>Warn</c>) maps to
/// <see cref="AuditSeverity.Warning"/> and is advisory. Version-bump
/// accounting stays upstream's: a breaking change the manifest already
/// version-bumped for is not a finding at all — cargo-semver-checks skips
/// the satisfied lints, so this auditor reports only <em>unresolved</em>
/// API breakage.</para>
///
/// <para><b>Exit-code convention (verified against cargo-semver-checks
/// v0.50.0 source — not the common 0/1/2 convention).</b> <c>0</c> = the
/// check completed and no deny-level lint found unsatisfied breakage
/// (warn-level sections may still be present); <c>100</c>
/// (<c>LINT_FAILURE_EXIT_CODE</c>) = the check completed and deny-level
/// findings exist; <c>101</c> (<c>ERROR_EXIT_CODE</c>) = the check could not
/// complete (bad manifest, unresolved baseline, rustdoc-format mismatch,
/// required-witness error). <c>2</c> is clap's usage error and
/// <c>126</c>/<c>127</c> are cannot-execute / not-found — all
/// infrastructure. A <c>100</c> exit whose stdout carries no
/// <c>--- failure</c> section contradicts the output contract and also fails
/// closed as infrastructure through the parser.</para>
///
/// <para><b>Baseline resolution.</b> A semver check is meaningless without a
/// "previous API" to compare against. cargo-semver-checks's stock default
/// (the latest published registry version) is unusable for the unpublished
/// crates that dominate audited internal projects — and silently wrong for
/// auditing a <em>change</em> rather than a <em>release</em>. Unless the
/// operator pins a baseline explicitly (one of <c>BaselineRev</c>,
/// <c>BaselineVersion</c>, <c>BaselineRoot</c>, <c>BaselineRustdoc</c>), the
/// auditor resolves the merge-base of <c>HEAD</c> and the work item's
/// <see cref="AuditContext.BaseBranch"/> — the same
/// <c>origin/&lt;base&gt;...HEAD</c> semantics the pipeline's own diff
/// auditors use — and passes it as <c>--baseline-rev</c>. Resolution probes
/// <c>origin/&lt;base&gt;</c> first, then the bare branch name (the
/// <see cref="Validation.ValidateBranchName"/>-validated value reaches git
/// only as an argv entry, never through a shell). An empty/invalid base
/// branch, an unresolvable ref, or no common ancestor is a deterministic
/// infrastructure failure pointing at the baseline knobs — never a pass.
/// The resolution needs the <see cref="AuditContext"/> that
/// <see cref="ExternalToolAuditorBase"/> does not thread into
/// <c>BuildToolArguments</c>, so this class shadows <see cref="RunAsync"/> to
/// resolve it immediately before the base's invocation: the base still owns
/// all invocation, parsing, classification, and bounding.</para>
///
/// <para><b>Version pin.</b> The lint set and the report shape change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes
/// <c>cargo-semver-checks --version</c> before every scan; a missing binary,
/// an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> cargo-semver-checks reads
/// lint-level and required-update overrides from the audited repository
/// itself — <c>[package.metadata.cargo-semver-checks.lints]</c> and
/// <c>[workspace.metadata.cargo-semver-checks.lints]</c> tables in the
/// subject/workspace manifests let a change set <c>deny</c> lints to
/// <c>allow</c> or soften required bumps, and the audit subject writes those
/// files. An auditor its subject can silence is not a gate, so by default
/// the pre-scan fails closed as deterministic infrastructure when any
/// <c>Cargo.toml</c> in the worktree (outside <c>.git</c>/<c>target</c>)
/// carries a <c>metadata.cargo-semver-checks</c> table. Operators who
/// deliberately trust repo-authored lint config set
/// <c>TrustRepositorySuppression</c>. Note the table only controls lint
/// levels — it cannot add findings, so trusting it trades suppression for
/// legitimate per-repo lint tuning.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>cargo-semver-checks check-release</c> at the repository root: cargo's
/// own manifest discovery decides scope — a single crate, or every
/// publishable workspace member (<c>publish = false</c> members are skipped
/// unless explicitly <c>--package</c>-selected; they are internal
/// implementation detail, not published API). No <c>ExcludePaths</c>
/// default is needed: findings describe only the checked crates' API
/// surface (<c>src/</c> files and <c>Cargo.toml</c>), never vendored or
/// generated trees. A repository without a root <c>Cargo.toml</c> fails
/// closed with a deterministic infrastructure error pointing at
/// <c>ManifestPath</c> — enabling this auditor on a non-Rust project is a
/// misconfiguration, and a loud one, not a silent skip.</para>
///
/// <para><b>Runtime and network.</b> The check builds rustdoc JSON for the
/// baseline and current crates — a full dependency compile of repo-authored
/// code (build scripts, proc macros) twice over, so the default timeout is
/// 15 minutes (bounded; tune via <c>TimeoutSeconds</c>) and the auditor runs
/// in the network-egress sandbox (<see cref="AuditCapabilities.Network"/>):
/// the audit-tool egress allowlist must cover the crates.io index and
/// static.crates.io, or dependencies must be vendored/cached and a
/// non-registry baseline configured. Registry-default and
/// <c>--baseline-version</c> baselines additionally need the package to be
/// published.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: cargo-semver-checks Rust API Compatibility",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cargo-semver-checks",
    InstallHint = "provision the pinned cargo-semver-checks release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — cargo install --locked "
        + "cargo-semver-checks@" + DefaultExpectedVersion + " (or the matching cargo-binstall "
        + "prebuilt) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions; no distro apt package carries it")]
[CodeyBoxPluginRequiresTool(
    "cargo",
    InstallHint = "provision a Rust toolchain (cargo + rustdoc) whose rustdoc JSON format the "
        + "pinned cargo-semver-checks supports — each release supports the then-current stable "
        + "and beta toolchains; rustup installs are unpinned, so check the release notes before "
        + "baking")]
// IAuditor is re-declared on this class deliberately: a `new` member only
// becomes the interface implementation when the derived class re-lists the
// interface, and the RunAsync wrapper below must be the member the pipeline
// dispatches to.
public sealed class CargoSemverChecksAuditor : ExternalToolAuditorBase, IAuditor, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.cargo-semver-checks";

    /// <summary>
    /// cargo-semver-checks release the invocation, its exit convention, and
    /// its report shape were verified against. Operators running a different
    /// pinned build set <c>ExpectedVersion</c> in the plugin's scoped config
    /// to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.50.0";

    /// <summary>
    /// Exit code cargo-semver-checks uses for "check completed, deny-level
    /// lint findings exist" (<c>LINT_FAILURE_EXIT_CODE</c> upstream). Disjoint
    /// from <c>0</c> (clean) and <c>101</c> (could not run), so it and 0 are
    /// the only findings-producing exits.
    /// </summary>
    internal const int LintFindingsExitCode = 100;

    /// <summary>Scoped-config key for a baseline git revision (<c>--baseline-rev</c>).</summary>
    internal const string BaselineRevKey = "BaselineRev";

    /// <summary>Scoped-config key for a baseline registry version (<c>--baseline-version</c>).</summary>
    internal const string BaselineVersionKey = "BaselineVersion";

    /// <summary>Scoped-config key for a baseline source directory (<c>--baseline-root</c>).</summary>
    internal const string BaselineRootKey = "BaselineRoot";

    /// <summary>Scoped-config key for a baseline rustdoc JSON file (<c>--baseline-rustdoc</c>).</summary>
    internal const string BaselineRustdocKey = "BaselineRustdoc";

    /// <summary>
    /// Scoped-config key for the subject manifest path (<c>--manifest-path</c>)
    /// when the crate under audit does not live at the repository root.
    /// </summary>
    internal const string ManifestPathKey = "ManifestPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored
    /// <c>[package/workspace.metadata.cargo-semver-checks]</c> lint tables.
    /// Default false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // The grep must match the literal TOML key path — `[package.metadata.
    // cargo-semver-checks.lints]` and `[workspace.metadata.cargo-semver-
    // checks]` alike — without matching a `cargo-semver-checks` dependency
    // declaration, which is inert.
    private const string LintConfigMarker = "metadata.cargo-semver-checks";

    // grep -r content probe for repository-authored lint tables. .git and
    // target are excluded: .git never holds manifest config and target/
    // holds build output, never an authoritative source manifest. grep's own
    // exit code is the verdict — 0 = suppression files echoed on stdout, 1 =
    // confirmed clean, anything else = the probe could not confirm (unlike a
    // find -exec wrapper, which collapses grep's error code into "no match"
    // and would fail open).
    private const string SuppressionProbeScript =
        "grep -rlF --include=Cargo.toml --exclude-dir=.git --exclude-dir=target "
        + "\"" + LintConfigMarker + "\" .";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = check completed, no deny-level findings; 100 = check completed,
        // deny-level findings exist. Both emit the report — both are
        // verdicts. 101 (could not run), 2 (usage), and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, LintFindingsExitCode },
        // The check compiles the baseline and current crates' rustdoc JSON —
        // two dependency builds — so the stock 5-minute tool default is too
        // tight for a cold cargo cache. Still bounded; tune via
        // TimeoutSeconds.
        Timeout = TimeSpan.FromMinutes(15),
    };

    // Resolved merge-base rev for the default baseline, set by the RunAsync
    // wrapper immediately before the base invocation and read by
    // BuildToolArguments — the base exposes no channel from AuditContext to
    // argument building. AsyncLocal keeps concurrent invocations on the same
    // auditor instance from sharing the value.
    private readonly AsyncLocal<string?> _resolvedBaselineRev = new();

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _manifestPath = static () => null;
    private Func<string?> _baselineRev = static () => null;
    private Func<string?> _baselineVersion = static () => null;
    private Func<string?> _baselineRoot = static () => null;
    private Func<string?> _baselineRustdoc = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:cargo-semver-checks";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "cargo-semver-checks";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } =
        new CargoSemverChecksReportParser();

    /// <summary>
    /// cargo-semver-checks's report vocabulary is the lint level each finding
    /// section was printed at: <c>failure</c> (deny-level — unresolved
    /// breaking change) maps to <see cref="AuditSeverity.Error"/> and
    /// <c>warning</c> (warn-level advisory lints) to
    /// <see cref="AuditSeverity.Warning"/>. Unknown levels default to
    /// Warning; raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            [CargoSemverChecksReportParser.FailureLevel] = AuditSeverity.Error,
            [CargoSemverChecksReportParser.WarningLevel] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <summary>
    /// Resolves the git baseline before the base builds argv: the merge-base
    /// of HEAD and <see cref="AuditContext.BaseBranch"/> becomes the default
    /// <c>--baseline-rev</c> unless the operator pinned a baseline in scoped
    /// config. <see cref="ExternalToolAuditorBase.RunAsync"/> builds
    /// arguments before any sandbox access, so the context-derived baseline
    /// cannot be computed inside it — this wrapper is the narrowest channel
    /// that keeps every other behaviour (invocation, parsing, classification,
    /// bounding) in the shared base.
    /// </summary>
    public new async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(context);
        _resolvedBaselineRev.Value = await ResolveDefaultBaselineRevAsync(
            sandbox, workingDirectory, context, ct).ConfigureAwait(false);
        return await base.RunAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string> { "check-release", "--color", "never" };
        var manifestPath = ValidatedScopedPath(_manifestPath(), ManifestPathKey);
        if (manifestPath is not null)
            args.AddRange(["--manifest-path", manifestPath]);
        args.AddRange(ResolveBaselineArguments());
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _manifestPath = () => scoped[ManifestPathKey];
        _baselineRev = () => scoped[BaselineRevKey];
        _baselineVersion = () => scoped[BaselineVersionKey];
        _baselineRoot = () => scoped[BaselineRootKey];
        _baselineRustdoc = () => scoped[BaselineRustdocKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "CargoSemverChecksAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// cargo-semver-checks-specific preconditions on the live path: a root
    /// <c>Cargo.toml</c> must exist (unless <c>ManifestPath</c> points the
    /// tool elsewhere), and — unless the operator opted in — no
    /// <c>Cargo.toml</c> in the worktree may carry a
    /// <c>metadata.cargo-semver-checks</c> lint table the audit subject could
    /// use to downgrade or silence lints. Both fail closed as deterministic
    /// infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_manifestPath()))
        {
            var present = await ProbeRepositoryFilesPresentAsync(
                sandbox,
                workingDirectory,
                tool,
                ["Cargo.toml"],
                options,
                ct).ConfigureAwait(false);
            if (present.Count == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{tool}' found no Cargo.toml at the repository "
                    + "root — cargo-semver-checks needs a package manifest to check. Enable this "
                    + "auditor only for Rust projects, or set "
                    + $"CodeyBox:Plugins:{PluginId}:{ManifestPathKey} when the crate under audit "
                    + "lives in a subdirectory.")
                { IsDeterministic = true };
        }

        if (!_trustRepositorySuppression())
            await ThrowIfRepoLintConfigPresentAsync(sandbox, workingDirectory, tool, options, ct)
                .ConfigureAwait(false);
    }

    private async Task<string?> ResolveDefaultBaselineRevAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct)
    {
        if (HasConfiguredBaseline())
            return null;

        var options = OptionsAccessor() ?? new ExternalToolAuditorOptions();
        var baseBranch = context.BaseBranch?.Trim();
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw NoBaselineConfigured();

        try
        {
            Validation.ValidateBranchName(baseBranch!, nameof(context.BaseBranch));
        }
        catch (ArgumentException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' cannot resolve a baseline: {SingleLine(ex.Message)}. "
                + BaselineConfigHint, ex)
            { IsDeterministic = true };
        }

        // The work item's base branch is the previous public API state.
        // origin/<base> is the sandbox clone's canonical ref (the pipeline's
        // own diff auditors use origin/<base>...HEAD); the bare name covers
        // layouts that only carry a local branch.
        string? baseSha = null;
        foreach (var candidate in new[] { $"origin/{baseBranch}", baseBranch! })
        {
            var probe = await GitProbeAsync(
                sandbox,
                workingDirectory,
                options,
                ["rev-parse", "--verify", $"{candidate}^{{commit}}"],
                ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
            {
                baseSha = ReadCommitSha(probe.Stdout);
                if (baseSha is not null)
                    break;
            }
        }

        if (baseSha is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve base branch "
                + $"'{SingleLine(baseBranch!)}' (tried 'origin/{SingleLine(baseBranch!)}' and "
                + $"'{SingleLine(baseBranch!)}') in the audited repository — git must be available "
                + "and the base ref present in the sandbox clone. " + BaselineConfigHint)
            { IsDeterministic = true };

        // Merge-base semantics match the pipeline's three-dot work diff:
        // the API state the change actually diverged from, so API added to
        // the base after the branch point is not misread as removed.
        var mergeBase = await GitProbeAsync(
            sandbox,
            workingDirectory,
            options,
            ["merge-base", "HEAD", baseSha],
            ct).ConfigureAwait(false);
        var mergeBaseSha = mergeBase.ExitCode == 0 ? ReadCommitSha(mergeBase.Stdout) : null;
        if (mergeBase.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' baseline resolution could not run: "
                + "the sandbox exec transport was unavailable.");
        if (mergeBaseSha is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' found no merge base between HEAD and "
                + $"base branch '{SingleLine(baseBranch!)}' — the audited history must share an "
                + "ancestor with the base ref. " + BaselineConfigHint)
            { IsDeterministic = true };

        return mergeBaseSha;
    }

    private async Task<SandboxExecResult> GitProbeAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var argv = new List<string>(args.Count + 1) { "git" };
        argv.AddRange(args);
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "baseline resolution",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' baseline resolution could not run: "
                + "the sandbox exec transport was unavailable.");
        return result;
    }

    private async Task ThrowIfRepoLintConfigPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "suppression check",
            new SandboxExec
            {
                Argv = ["sh", "-c", SuppressionProbeScript],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression check could not run: the sandbox exec "
                + "transport was unavailable.");
        if (result.ExitCode == 0)
        {
            var files = result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => line.Length > 0)
                .Take(8)
                .ToList();
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled lint configuration "
                + $"'{LintConfigMarker}' in '{string.Join("', '", files)}' — the audited repository can "
                + "downgrade or silence cargo-semver-checks lints through that table. Remove the "
                + "table(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to trust "
                + "repository-controlled lint configuration.")
            { IsDeterministic = true };
        }
        if (result.ExitCode != 1)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not confirm the audited repository is "
                + $"free of '{LintConfigMarker}' lint configuration (exit {result.ExitCode}) — a failed "
                + "probe is infrastructure, not evidence that the config is absent.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);
    }

    private IReadOnlyList<string> ResolveBaselineArguments()
    {
        var configured = new List<(string Flag, string? Value)>
        {
            ("--baseline-rev", _baselineRev()),
            ("--baseline-version", _baselineVersion()),
            ("--baseline-root", _baselineRoot()),
            ("--baseline-rustdoc", _baselineRustdoc()),
        };
        var set = configured.Where(e => !string.IsNullOrWhiteSpace(e.Value)).ToList();
        if (set.Count > 1)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has more than one baseline configured "
                + $"({string.Join(", ", configured.Where(e => !string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Flag))}) — "
                + $"set exactly one of CodeyBox:Plugins:{PluginId}:{BaselineRevKey} / "
                + $"{BaselineVersionKey} / {BaselineRootKey} / {BaselineRustdocKey}.")
            { IsDeterministic = true };
        if (set.Count == 1)
            return [set[0].Flag, ValidatedScopedValue(set[0].Value!, set[0].Flag)];

        var resolved = _resolvedBaselineRev.Value;
        if (string.IsNullOrWhiteSpace(resolved))
            throw NoBaselineConfigured();
        return ["--baseline-rev", resolved];
    }

    private bool HasConfiguredBaseline()
        => !string.IsNullOrWhiteSpace(_baselineRev())
            || !string.IsNullOrWhiteSpace(_baselineVersion())
            || !string.IsNullOrWhiteSpace(_baselineRoot())
            || !string.IsNullOrWhiteSpace(_baselineRustdoc());

    private string BaselineConfigHint
        => $"Set one of CodeyBox:Plugins:{PluginId}:{BaselineRevKey} / {BaselineVersionKey} / "
            + $"{BaselineRootKey} / {BaselineRustdocKey} to pin the baseline explicitly.";

    private AuditUnavailableException NoBaselineConfigured()
        => new(
            $"could-not-verify: auditor '{Name}' has no baseline to compare against: "
            + "no baseline scoped key is configured and the merge-base resolution did not "
            + "produce a revision. " + BaselineConfigHint)
        { IsDeterministic = true };

    private static string? ValidatedScopedPath(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ValidatedScopedValue(value, key);
    }

    /// <summary>
    /// Validates a scoped-config value that travels to the tool as an argv
    /// entry: bounded length, no leading dash (it would be read as another
    /// flag), no control characters. Values are never concatenated into a
    /// shell string — this only guards the argv contract.
    /// </summary>
    private static string ValidatedScopedValue(string value, string key)
    {
        var trimmed = value.Trim();
        const int maxChars = 1024;
        if (trimmed.Length == 0 || trimmed.Length > maxChars
            || trimmed[0] == '-'
            || trimmed.Any(char.IsControl))
            throw new AuditUnavailableException(
                $"could-not-verify: scoped config '{key}' is not a usable argument value "
                + "(empty, overlong, leading '-', or contains control characters).")
            { IsDeterministic = true };
        return trimmed;
    }

    private static string? ReadCommitSha(string stdout)
    {
        var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (firstLine is null)
            return null;
        try
        {
            Validation.ValidateCommitSha(firstLine, "git output");
        }
        catch (ArgumentException)
        {
            return null;
        }
        return firstLine;
    }
}
