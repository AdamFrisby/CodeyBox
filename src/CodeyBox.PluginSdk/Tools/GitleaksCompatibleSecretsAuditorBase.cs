using System.Globalization;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// The tool-specific deltas a gitleaks-compatible secrets auditor declares —
/// everything the shared <see cref="GitleaksCompatibleSecretsAuditorBase"/>
/// policy needs beyond the tool name.
/// </summary>
/// <param name="PluginId">
/// Plugin id used in <c>Plugins:Enabled</c>, the scoped-config section
/// (<c>CodeyBox:Plugins:&lt;id&gt;</c>), and operator-facing hints.
/// </param>
/// <param name="DefaultExpectedVersion">
/// Release the invocation and its report shape were verified against —
/// the fallback for the <c>ExpectedVersion</c> scoped-config pin.
/// </param>
/// <param name="ConfigTomlEnvVar">
/// Env var carrying inline TOML config the tool reads at precedence below
/// <c>--config</c>/<c>*_CONFIG</c> but above repository config files
/// (e.g. <c>GITLEAKS_CONFIG_TOML</c>, <c>BETTERLEAKS_CONFIG_TOML</c>) — the
/// channel the base pins the built-in ruleset through.
/// </param>
/// <param name="RepositorySuppressionFiles">
/// Repository-root files that shape or suppress the scan (ignore files,
/// config files), in every spelling the tool honors. Presence at the
/// worktree root fails closed unless the operator sets
/// <see cref="GitleaksCompatibleSecretsAuditorBase.TrustRepositorySuppressionKey"/>.
/// </param>
/// <param name="SuppressionGateRationale">
/// Why those files let the audit subject hide a leak, spliced into the
/// gate's failure message — e.g. which loads are unconditional or which
/// expressions discard findings.
/// </param>
/// <param name="ConfigPathEnvVars">
/// Env vars carrying a config FILE PATH the tool honors above the pinned
/// inline <paramref name="ConfigTomlEnvVar"/> (e.g.
/// <c>GITLEAKS_CONFIG</c>, which betterleaks also reads as a fallback
/// spelling — a baseline exporting it for one auditor would silently
/// re-point the other's ruleset). The scan exec unsets them: they bypass
/// the pinned ruleset AND the out-of-worktree canonicalization guard that
/// covers <c>--config</c>, so the sanctioned operator override is the
/// guarded <c>ExtraArguments --config</c>, never an unverified path in the
/// baseline environment.
/// </param>
public sealed record GitleaksCompatibleSecretsProfile(
    string PluginId,
    string DefaultExpectedVersion,
    string ConfigTomlEnvVar,
    IReadOnlyList<string> RepositorySuppressionFiles,
    string SuppressionGateRationale,
    IReadOnlyList<string> ConfigPathEnvVars)
{
    /// <summary>
    /// Repo-relative path globs matching the filename family the tool's own
    /// ruleset exempts from the scan wherever it appears — the stock
    /// <c>gitleaks\.toml</c> entry in the inherited prefilter/allowlist.
    /// Globs use <c>find -path</c>/default git-pathspec semantics:
    /// <c>*</c>/<c>?</c> cross directory separators (git's <c>glob</c>
    /// pathspec magic, whose wildcards do not, is deliberately not used).
    /// The tool drops a matching fragment on path alone, before any rule
    /// runs, so a secret committed inside such a path — nested, differently
    /// spelled (<c>x-gitleaks.toml.bak</c>), or deleted before the audit —
    /// is never reported while the audit still passes. Any match in the
    /// worktree or anywhere in git history therefore fails closed. Default
    /// empty.
    /// </summary>
    public IReadOnlyList<string> ScanExemptedPathGlobs { get; init; } = [];
}

/// <summary>
/// Intermediate base for secrets auditors wrapping a gitleaks-compatible CLI
/// (gitleaks, betterleaks): the shared scan invocation, suppression policy,
/// and scoped-config wiring live here so each plugin declares only its tool
/// deltas in a <see cref="GitleaksCompatibleSecretsProfile"/> — a fix to the
/// gate semantics lands in both scanners at once instead of drifting across
/// forks.
///
/// <para><b>Shared scan policy.</b> <c>&lt;tool&gt; git .</c> scans committed
/// source plus full history with the SARIF report on stdout;
/// <c>--exit-code</c> is reassigned to <see cref="LeaksFoundExitCode"/> so
/// "ran and found secrets" is disjoint from the tools' error exits (every
/// other code is infrastructure, never a verdict). <c>--log-opts</c> pins
/// the <c>git log</c> rev arguments — the tools replace their defaults
/// wholesale when the flag is set — to the stock
/// <c>--full-history --all --diff-filter=tuxdb</c> plus <c>--text</c>: a
/// committed <c>.gitattributes</c> marking a secret-bearing path
/// <c>-diff</c>/<c>binary</c> otherwise makes <c>git log -p</c> emit
/// "Binary files differ" with no patch content, silently blanking the
/// patch stream the scanner reads. <c>--redact=100</c> keeps the detected
/// secret out of the report.</para>
///
/// <para><b>Shared suppression policy.</b> The profile's
/// <see cref="GitleaksCompatibleSecretsProfile.ConfigPathEnvVars"/> are
/// unset on every scan — they carry a config path at a precedence above
/// the pinned ruleset (and above repo files in trust mode) while bypassing
/// the out-of-worktree canonicalization guard that covers
/// <c>--config</c>. Ambient GIT_* variables
/// (<see cref="ExternalToolAuditorBase.GitEnvironmentRemovals"/>) are
/// unset on the scan and the gate's own <c>git log</c> probes alike — the
/// baseline environment could otherwise redirect or re-configure every git
/// the run spawns. Unless the operator sets
/// <see cref="TrustRepositorySuppressionKey"/>: the built-in ruleset is
/// pinned via the tool's <c>*_CONFIG_TOML</c> env var (inline content — an
/// operator <c>--config</c> still outranks it),
/// <c>--ignore-gitleaks-allow</c> disables <c>gitleaks:allow</c>-style
/// comments, <c>--gitleaks-ignore-path</c> points at an inert file, and
/// the profile's repository suppression files fail the run closed when
/// present at the worktree root. Separately,
/// <see cref="GitleaksCompatibleSecretsProfile.ScanExemptedPathGlobs"/>
/// name the path family the tool's own ruleset skips wherever it appears —
/// a match at any worktree depth or anywhere in git history fails closed,
/// since the scanner would drop the fragment before any rule ran.</para>
///
/// <para><b>Shared operator-flag guard.</b> <c>ExtraArguments</c> flags whose
/// value is a file the tool loads for gate-shaping data —
/// <c>--config</c>/<c>-c</c>, <c>--baseline-path</c>/<c>-b</c>,
/// <c>--gitleaks-ignore-path</c>/<c>-i</c> — are canonicalized and rejected
/// when they resolve inside the audited worktree: the tool resolves them
/// against its cwd, so a relative or in-tree value hands gate-shaping
/// content to repository-controlled bytes at a path outside the gated
/// filenames (a loaded config file additionally exempts its own path from
/// the scan). The guard runs in both trust modes — it protects the
/// operator knob, not repository-authored suppression.</para>
/// </summary>
public abstract class GitleaksCompatibleSecretsAuditorBase
    : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>
    /// Exit code assigned to "ran and found secrets" via <c>--exit-code</c>.
    /// Disjoint from every gitleaks-family error convention (0 clean,
    /// 1 error/fatal, 126 usage, 127 not-found) so only this value and 0
    /// are verdicts.
    /// </summary>
    public const int LeaksFoundExitCode = 4;

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression
    /// surfaces (repo-root config/ignore files and <c>*:allow</c> comments,
    /// in every spelling the tool honors). Default false: the audited repo
    /// must not be able to silence the audit.
    /// </summary>
    public const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // The tool reads the --gitleaks-ignore-path file (and the repo-root
    // ignore files) in addition to its unconditional repo-root load — the
    // flag only ADDS files. Point it at a guaranteed-empty file so its own
    // load sites can never pick up suppression fingerprints; the
    // unconditional repo-root load is gated separately by presence.
    private const string InertGitleaksIgnorePath = "/dev/null";

    // `[extend] useDefault = true`: pins the built-in ruleset so the audited
    // repository cannot add filters or rewrite rules. The env var sits below
    // --config and *_CONFIG in precedence — an operator --config via
    // ExtraArguments still wins, while the profile's ConfigPathEnvVars are
    // unset so a baseline *_CONFIG path cannot outrank the pin outside the
    // canonicalization guard. The inherited ruleset's own path exemptions
    // (the stock gitleaks\.toml prefilter/allowlist entry, binary and
    // lockfile extensions, vendored trees) are the profile's
    // ScanExemptedPathGlobs / documented scan scope.
    private const string PinnedDefaultConfigToml = "[extend]\nuseDefault = true\n";

    // The `git log` rev args these tools substitute when --log-opts is unset
    // — the flag replaces them wholesale, so they are re-stated — plus
    // --text: a committed .gitattributes marking a secret-bearing path
    // -diff/binary makes `git log -p` emit "Binary files differ" with no
    // patch content, silently blanking the scanner's input; --text forces
    // attributed files through the patch stream.
    private const string GitLogOptions = "--full-history --all --diff-filter=tuxdb --text";

    // ExtraArguments flags whose value is a file the tool loads for
    // gate-shaping data — config ruleset, baseline, ignore fingerprints —
    // each with its pflag short form. Gated outside the worktree in
    // VerifyToolAsync.
    private static readonly (string LongFlag, string? ShortFlag)[] PathValuedArgumentFlags =
    [
        ("--config", "-c"),
        ("--baseline-path", "-b"),
        ("--gitleaks-ignore-path", "-i"),
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        FindingsExitCodes = new HashSet<int> { 0, LeaksFoundExitCode },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private readonly GitleaksCompatibleSecretsProfile _profile;
    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    protected GitleaksCompatibleSecretsAuditorBase(GitleaksCompatibleSecretsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.PluginId)
            || string.IsNullOrWhiteSpace(profile.DefaultExpectedVersion)
            || string.IsNullOrWhiteSpace(profile.ConfigTomlEnvVar))
            throw new ArgumentException(
                "Profile must declare a plugin id, a default expected version, and the tool's "
                + "inline-config env var.", nameof(profile));
        _profile = profile with
        {
            RepositorySuppressionFiles = profile.RepositorySuppressionFiles ?? [],
            ConfigPathEnvVars = profile.ConfigPathEnvVars ?? [],
            ScanExemptedPathGlobs = profile.ScanExemptedPathGlobs ?? [],
        };
        _expectedVersion = () => _profile.DefaultExpectedVersion;
    }

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// These tools report no per-finding severity — their SARIF carries no
    /// level and the per-rule <c>confidence</c> vocabulary is a detection
    /// likelihood, not a severity — so the declared mapping is total: every
    /// level the parser can supply, including the "warning" it substitutes
    /// for the absent SARIF level, maps to <see cref="AuditSeverity.Error"/>.
    /// Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase),
            AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(_profile.PluginId, _expectedVersion, _profile.DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var timeoutSeconds = (int)Math.Clamp(
            Math.Ceiling(EffectiveTimeout(options).TotalSeconds),
            1,
            ExternalToolAuditorOptions.MaxTimeoutSeconds);
        var args = new List<string>
        {
            // `git` scans committed source plus full history rather than only
            // the filesystem worktree.
            "git", ".",
            "--report-format", "sarif",
            // "-" is the stdout report sink; SARIF is all of stdout. SARIF is
            // required (not json/csv): the other formats embed raw secrets
            // (Match/Secret), while SARIF carries only redacted snippets.
            "--report-path", "-",
            "--exit-code", LeaksFoundExitCode.ToString(CultureInfo.InvariantCulture),
            // The detected secret must never land in findings or raw output.
            "--redact=100",
            "--no-banner",
            "--no-color",
            // stderr carries diagnostics only; the report carries the verdict.
            "--log-level", "error",
            // Cooperative in-tool bound under the base's outer timeout.
            "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture),
            // Keep the flag's own ignore-file load sites out of the repo; the
            // unconditional repo-root load is gated separately.
            "--gitleaks-ignore-path", InertGitleaksIgnorePath,
            // Pin the git-log rev args (the flag replaces the tool's defaults
            // wholesale) including --text — see GitLogOptions.
            "--log-opts", GitLogOptions,
        };
        if (!_trustRepositorySuppression())
            args.Add("--ignore-gitleaks-allow");
        return args;
    }

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? BuildToolEnvironment(
        ExternalToolAuditorOptions options)
        => _trustRepositorySuppression()
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [_profile.ConfigTomlEnvVar] = PinnedDefaultConfigToml,
            };

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(
        ExternalToolAuditorOptions options)
        => [.. _profile.ConfigPathEnvVars, .. GitEnvironmentRemovals];

    /// <summary>
    /// Binds the shared scoped-config knobs (operator options,
    /// <c>ExpectedVersion</c>, <see cref="TrustRepositorySuppressionKey"/>)
    /// to hot-reloadable accessors.
    /// </summary>
    public virtual Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "{AuditorType} initialized: pluginId={PluginId}", GetType().Name, context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The shared pre-scan preconditions beyond the base's presence and
    /// pinned-version checks: the <c>git</c> binary the <c>git</c> scan mode
    /// shells out to must exist; operator-supplied file flags in
    /// <c>ExtraArguments</c> must resolve outside the audited worktree; and —
    /// unless the operator opted in — the profile's repository suppression
    /// files must be absent from the worktree root, and no path matching
    /// the ruleset-exempted globs may exist anywhere in the worktree or in
    /// git history. All fail closed as infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // `git` scan mode shells out to the git binary — probe it explicitly
        // so a missing git names 'git', not the scanner.
        await ThrowIfBinaryMissingAsync(
            sandbox,
            workingDirectory,
            "git",
            options,
            ct,
            purpose: $"'{tool} git' scan mode shells out to 'git log'").ConfigureAwait(false);

        await ThrowIfPathFlagResolvesInWorktreeAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);

        if (_trustRepositorySuppression())
            return;

        // These tools load the repo-root ignore files unconditionally — the
        // --gitleaks-ignore-path flag only ADDS files — so presence must be
        // gated rather than redirected. Their fingerprints are deterministic
        // and the audit subject can compute them, so honoring the files by
        // default would let the subject hide a leak.
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            _profile.RepositorySuppressionFiles,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled file(s) "
                + $"'{string.Join("', '", present)}' in the audited repository — "
                + _profile.SuppressionGateRationale
                + " Remove the file(s), or set "
                + $"CodeyBox:Plugins:{_profile.PluginId}:{TrustRepositorySuppressionKey} to true "
                + "to trust repository-controlled suppression surfaces.")
            { IsDeterministic = true };

        // The stock ruleset the pinned config extends exempts the declared
        // path family wherever it appears — a file merely NAMED after the
        // pattern (docs/gitleaks.toml, x-gitleaks.toml.bak) is dropped on
        // path alone before any rule runs, in the worktree and in every
        // historical commit. Neither the root-only presence gate nor the
        // scanner can see such a path, so any match fails closed.
        var exempted = await ProbeRepositoryPathGlobsPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            _profile.ScanExemptedPathGlobs,
            options,
            ct).ConfigureAwait(false);
        if (exempted.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository path(s) "
                + $"'{DescribeMatchedPaths(exempted)}' matching "
                + $"'{string.Join("', '", _profile.ScanExemptedPathGlobs)}' — the tool's ruleset "
                + "exempts paths matching that glob from the scan in every commit, so a secret "
                + "inside such a file would never be reported. Rename or remove the file(s), or set "
                + $"CodeyBox:Plugins:{_profile.PluginId}:{TrustRepositorySuppressionKey} to true "
                + "to trust repository-controlled suppression surfaces.")
            { IsDeterministic = true };

        foreach (var glob in _profile.ScanExemptedPathGlobs)
            await ThrowIfPathGlobPresentInGitHistoryAsync(
                sandbox, workingDirectory, tool, glob, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Canonicalizes the value of every <c>ExtraArguments</c> flag that names
    /// a gate-shaping file the tool loads and fails closed when it resolves
    /// inside the audited worktree. The flags resolve against the tool's cwd
    /// — the worktree — so a relative or in-tree value would hand the
    /// ruleset/baseline/ignore list to repository-controlled content at a
    /// path outside the gated filenames (a loaded config file additionally
    /// exempts its own path from the scan). Runs in both trust modes: it
    /// guards the operator knob, and the opt-in
    /// <see cref="TrustRepositorySuppressionKey"/> already loads the
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
            // The shared scanner covers the bare, attached "=", joined
            // "-fvalue", and clustered "-vc <value>" pflag spellings; the
            // last occurrence wins — only the effective value is gated
            // because the tool only opens that one.
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

    /// <summary>
    /// Fails closed when any repository path matching
    /// <paramref name="pathGlob"/> appears anywhere in the audited
    /// repository's git history. The profile gates a path family this way
    /// when the tool's ruleset exempts it from the scan in every commit: a
    /// subject could commit a secret inside a matching file and delete it —
    /// the leak stays in history while the worktree check sees a clean
    /// tree. Two probe details keep that honest:
    ///
    /// <para>- The glob rides a <c>:(top)</c> pathspec — anchored at the
    /// repository root and matched with git's DEFAULT fnmatch semantics,
    /// where <c>*</c>/<c>?</c> cross <c>/</c>, the same semantics
    /// <c>find -path</c> and the worktree probe's re-filter apply. Git's
    /// <c>glob</c> pathspec magic must NOT be used: it applies FNM_PATHNAME,
    /// so <c>*</c> never crosses <c>/</c> and <c>*gitleaks.toml*</c> would
    /// silently miss <c>docs/gitleaks.toml</c> — the nested path this gate
    /// exists to catch.</para>
    /// <para>- <c>--full-history</c> disables history simplification: a
    /// matching path committed on a side branch and merged TREESAME to the
    /// mainline (e.g. <c>git merge -s ours</c>) is otherwise pruned from
    /// the walk even under <c>--all</c> — invisible to the gate while the
    /// scanner's own <c>--full-history</c> log-opts still emits its
    /// (ruleset-exempted) fragment.</para>
    ///
    /// Ambient GIT_* variables are unset
    /// (<see cref="ExternalToolAuditorBase.GitEnvironmentRemovals"/>) so the
    /// baseline environment can neither redirect the repository nor change
    /// how the pathspec matches (<c>GIT_LITERAL_PATHSPECS</c> would read
    /// <c>*</c> literally; <c>GIT_GLOB_PATHSPECS</c> would re-impose
    /// FNM_PATHNAME). Any commit touching a matching path fails closed. A
    /// non-git working directory makes this probe fail, which is still
    /// infrastructure — the <c>git</c> scan would fail the same way.
    /// </summary>
    private async Task ThrowIfPathGlobPresentInGitHistoryAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        string pathGlob,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var normalizedGlob = NormalizeProbePathGlob(pathGlob);
        var history = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "suppression check",
            new SandboxExec
            {
                Argv = ["git", "log", "--all", "--full-history", "-1", "--format=%H", "--", ":(top)" + normalizedGlob],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
                EnvironmentVariablesToUnset = GitEnvironmentRemovals,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (history.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(history.ExitCode);
        if (history.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not confirm the audited repository's git "
                + $"history is free of paths matching '{normalizedGlob}' (exit {history.ExitCode}) — the "
                + "tool's ruleset exempts matching paths from the scan in every commit.",
                history.ExitCode,
                history.Stdout + "\n" + history.Stderr);
        var historyCommit = SingleLine(history.Stdout);
        if (historyCommit.Length > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found a path matching '{normalizedGlob}' in the "
                + $"audited repository's git history (commit {TruncateForMessage(historyCommit)}) — the "
                + "tool's ruleset exempts matching paths from the scan in every commit, so a secret "
                + "committed inside such a file would never be reported. Purge it from history, or set "
                + $"CodeyBox:Plugins:{_profile.PluginId}:{TrustRepositorySuppressionKey} to true "
                + "to trust repository-controlled suppression surfaces.",
                history.ExitCode,
                history.Stdout + "\n" + history.Stderr)
            { IsDeterministic = true };
    }

    // Bounds the path enumeration spliced into the exempted-paths gate
    // failure: names are repository-controlled bytes, so each is
    // single-lined/truncated and the list is capped.
    private static string DescribeMatchedPaths(IReadOnlyList<string> paths)
    {
        const int maxListed = 8;
        var listed = string.Join("', '", paths.Take(maxListed).Select(TruncateForMessage));
        return paths.Count <= maxListed
            ? listed
            : $"{listed} … and {paths.Count - maxListed} more";
    }
}
