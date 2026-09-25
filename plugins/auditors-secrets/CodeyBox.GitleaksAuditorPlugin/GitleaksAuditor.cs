using System.Globalization;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GitleaksAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping <c>gitleaks</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, SARIF parsing,
/// severity mapping, exit-code classification, and per-auditor configuration.
/// This class declares the pinned tool version through the base's
/// <see cref="ExternalToolAuditorBase.VersionPin"/> and adds the
/// repository-suppression gate below through
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>.
///
/// <para><b>Gate behaviour: blocking by default.</b> gitleaks has no severity
/// vocabulary — every SARIF result is a detected credential — so every finding
/// maps to <see cref="AuditSeverity.Error"/> and any surviving finding fails
/// the audit. Scope findings down with <c>ExcludedRules</c> or
/// <c>ExcludePaths</c> rather than expecting advisory severity.</para>
///
/// <para><b>Exit-code convention (verified against gitleaks v8.x).</b>
/// gitleaks's default is unusable as-is: findings exit <c>--exit-code</c>
/// (default 1) and every failure mode — config load, scan error, report-write
/// failure, zerolog <c>Fatal</c> — also exits 1, so "found something" and
/// "could not run" would be indistinguishable. The auditor overrides
/// <c>--exit-code</c> to <see cref="LeaksFoundExitCode"/>: 0 is a clean run,
/// 4 is "ran with findings", and anything else (1, 126 usage error, 126/127
/// cannot-execute) is infrastructure. Upstream also exits 1 on error before
/// checking findings, so a partial scan can never look like a verdict.</para>
///
/// <para><b>Version pin.</b> A scanner's rule set changes between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. gitleaks's SARIF driver stamps a constant "v8.0.0" rather than the
/// real release, so the version is probed with <c>gitleaks version</c> before
/// the scan; a missing binary, an unrecognised version string, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> gitleaks honors three
/// suppression surfaces authored inside the audited repository — a
/// <c>.gitleaks.toml</c> (rule/allowlist edits), a <c>.gitleaksignore</c>
/// (fingerprint suppression, loaded unconditionally; no flag disables it),
/// and inline <c>gitleaks:allow</c> comments — and the audit subject is the
/// repository's author. <c>.gitleaks.toml</c> is worse than a config
/// override: gitleaks unconditionally exempts its own config path from the
/// scan in every commit, so a secret committed inside that file — even one
/// deleted before the audit — is never reported. An auditor its subject can
/// silence is not a gate, so by default the scan pins gitleaks's built-in
/// ruleset via <c>GITLEAKS_CONFIG_TOML</c>, passes
/// <c>--ignore-gitleaks-allow</c>, points <c>--gitleaks-ignore-path</c> at an
/// inert path, and fails closed when a repo-root <c>.gitleaksignore</c>
/// exists in the worktree or a <c>.gitleaks.toml</c> exists anywhere in the
/// worktree or git history. An operator that deliberately trusts
/// repo-authored suppression — or relies on a repo <c>.gitleaks.toml</c> for
/// custom detectors — sets <c>TrustRepositorySuppression</c> in scoped
/// config; an operator-supplied <c>--config</c> via <c>ExtraArguments</c>
/// still outranks the pinned env config.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.gitleaks",
    displayName: "CodeyBox: Gitleaks Secrets",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "gitleaks",
    InstallHint = "provision the pinned gitleaks release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline; the distro apt package is "
        + "unpinned and too old on Ubuntu LTS — install the pinned upstream binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class GitleaksAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.gitleaks";

    /// <summary>
    /// gitleaks release the invocation and its findings are verified against.
    /// Anything older than 8.24.0 lacks <c>--report-path -</c> (SARIF on
    /// stdout) and the <c>git</c> subcommand, so it fails closed. Operators
    /// running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "8.30.1";

    /// <summary>
    /// Exit code assigned to "ran and found secrets" via <c>--exit-code</c>.
    /// Disjoint from every gitleaks error convention (0 clean, 1 error/fatal,
    /// 126 usage, 127 not-found) so only this value and 0 are verdicts.
    /// </summary>
    internal const int LeaksFoundExitCode = 4;

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression surfaces
    /// (<c>.gitleaks.toml</c>, <c>.gitleaksignore</c>, <c>gitleaks:allow</c>).
    /// Default false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // gitleaks reads the --gitleaks-ignore-path file (and <path>/.gitleaksignore)
    // in addition to <repo>/.gitleaksignore. Point it at a guaranteed-empty
    // file so the flag's own load sites can never pick up suppression
    // fingerprints; the unconditional repo-root load is gated separately.
    private const string InertGitleaksIgnorePath = "/dev/null";
    private const string RepositoryIgnoreFile = ".gitleaksignore";

    // gitleaks sets Config.Path to <source>/.gitleaks.toml whenever --config
    // is unset — regardless of where the config actually came from — and skips
    // every fragment at that path. The file is therefore never scanned: a
    // secret committed inside it (including one later deleted) evades the
    // audit, so its presence in the worktree or history is gated, not just
    // outranked by the pinned ruleset.
    private const string RepositoryConfigFile = ".gitleaks.toml";

    // Precedence 3 of 4 (above the repo's .gitleaks.toml, below --config and
    // GITLEAKS_CONFIG): pins the built-in ruleset so the audited repo cannot
    // extend rules or add allowlists. GITLEAKS_CONFIG stays available to the
    // operator via the sandbox baseline environment.
    private const string ConfigEnvVar = "GITLEAKS_CONFIG_TOML";
    private const string PinnedDefaultConfigToml = "[extend]\nuseDefault = true\n";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        FindingsExitCodes = new HashSet<int> { 0, LeaksFoundExitCode },
        // Findings inside vendored/dependency trees describe upstream code, not
        // the change under audit — noise that trains operators to ignore the
        // auditor. Operators re-include a path by overriding ExcludePaths in
        // scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:gitleaks";

    /// <inheritdoc />
    protected override string ToolName => "gitleaks";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// gitleaks reports no per-finding severity, so the declared mapping is
    /// total: every level the parser can supply — including the "warning" it
    /// substitutes for gitleaks's absent SARIF level — maps to
    /// <see cref="AuditSeverity.Error"/>. Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase),
            AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var timeoutSeconds = (int)Math.Clamp(
            Math.Ceiling(EffectiveTimeout(options).TotalSeconds),
            1,
            ExternalToolAuditorOptions.MaxTimeoutSeconds);
        var args = new List<string>
        {
            // `git` scans committed source plus full history — the "source and
            // git history" scope — rather than only the filesystem worktree.
            "git", ".",
            "--report-format", "sarif",
            // "-" is gitleaks's stdout report sink; SARIF is all of stdout.
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
            // unconditional <repo>/.gitleaksignore load is gated separately.
            "--gitleaks-ignore-path", InertGitleaksIgnorePath,
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
                [ConfigEnvVar] = PinnedDefaultConfigToml,
            };

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
            "GitleaksAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// gitleaks-specific preconditions on the live path beyond the base's
    /// pinned version check: unless the operator opted in, absence of the
    /// repository-controlled files gitleaks would honor or exempt from the
    /// scan (<c>.gitleaksignore</c>, <c>.gitleaks.toml</c>) is confirmed
    /// through the shared fail-closed presence probe — and
    /// <c>.gitleaks.toml</c> is checked in git history too, since the
    /// config-path exemption covers every commit. Both fail closed as
    /// infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (!_trustRepositorySuppression())
            await ThrowIfRepoSuppressionFilePresentAsync(sandbox, workingDirectory, tool, options, ct)
                .ConfigureAwait(false);
    }

    private async Task ThrowIfRepoSuppressionFilePresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // gitleaks loads <repo>/.gitleaksignore unconditionally — the
        // --gitleaks-ignore-path flag only adds files — so presence must be
        // gated rather than redirected. Its fingerprints are deterministic and
        // the audit subject can compute them, so honoring the file by default
        // would let the subject hide a leak. A repo-root .gitleaks.toml is
        // gated alongside it: gitleaks exempts its own config path from the
        // scan, so the file's contents are never checked for secrets at all.
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            [RepositoryIgnoreFile, RepositoryConfigFile],
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled file(s) "
                + $"'{string.Join("', '", present)}' in the audited repository — gitleaks honors "
                + $"'{RepositoryIgnoreFile}' unconditionally and never scans its own "
                + $"'{RepositoryConfigFile}' config path, so either file lets the audit subject hide "
                + "a leak. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to trust "
                + "repository-controlled suppression surfaces.")
            { IsDeterministic = true };

        // The config-path exemption covers every commit, not just the
        // worktree: a subject could commit a secret inside .gitleaks.toml and
        // delete the file — the leak stays in history while the presence
        // check above sees a clean tree. Any commit touching the path fails
        // closed too. A non-git working directory makes this probe fail,
        // which is still infrastructure — the gitleaks git scan would fail
        // the same way.
        var history = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "suppression check",
            new SandboxExec
            {
                Argv = ["git", "log", "--all", "-1", "--format=%H", "--", RepositoryConfigFile],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (history.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression check could not run: the sandbox exec "
                + "transport was unavailable.");
        if (history.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not confirm the audited repository's git "
                + $"history is free of '{RepositoryConfigFile}' (exit {history.ExitCode}) — gitleaks "
                + "exempts that path from scanning in every commit.",
                history.ExitCode,
                history.Stdout + "\n" + history.Stderr);
        var historyCommit = SingleLine(history.Stdout);
        if (historyCommit.Length > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found '{RepositoryConfigFile}' in the audited "
                + $"repository's git history (commit {historyCommit}) — gitleaks exempts its own config "
                + "path from the scan in every commit, so a secret committed inside that file would "
                + "never be reported. Purge it from history, or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to trust "
                + "repository-controlled suppression surfaces.",
                history.ExitCode,
                history.Stdout + "\n" + history.Stderr)
            { IsDeterministic = true };
    }
}
