using System.Globalization;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BetterleaksAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping <c>betterleaks</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, SARIF parsing,
/// severity mapping, exit-code classification, and per-auditor configuration.
/// This class declares the pinned tool version through the base's
/// <see cref="ExternalToolAuditorBase.VersionPin"/> and adds the
/// repository-suppression gate below through
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>.
///
/// <para><b>Gate behaviour: blocking by default.</b> betterleaks reports no
/// per-finding severity — its native vocabulary is per-rule
/// <c>confidence</c> (<c>low</c>/<c>medium</c>/<c>high</c>), a detection
/// likelihood, not a severity — and its SARIF carries no level, so every
/// finding maps to <see cref="AuditSeverity.Error"/> and any surviving
/// finding fails the audit. Scope findings down with <c>ExcludedRules</c> or
/// <c>ExcludePaths</c> rather than expecting advisory severity.</para>
///
/// <para><b>Exit-code convention (verified against betterleaks v1.8.1).</b>
/// betterleaks's default is unusable as-is: findings exit
/// <c>--exit-code</c> (default 1) and every failure mode — bad config
/// (<c>FTL unable to load config</c>), non-git scan root, scan error — also
/// exits 1, so "found something" and "could not run" would be
/// indistinguishable. The auditor overrides <c>--exit-code</c> to
/// <see cref="LeaksFoundExitCode"/>: 0 is a clean run, 4 is "ran with
/// findings", and anything else (1 error/fatal, 126 usage error, 127
/// cannot-execute) is infrastructure. Upstream exits 1 on error before
/// checking findings, so a partial scan can never look like a verdict.</para>
///
/// <para><b>Version pin.</b> A scanner's rule set changes between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. betterleaks's SARIF driver stamps a constant "v8.0.0" inherited
/// from its gitleaks lineage rather than the real release, so the version is
/// probed with <c>betterleaks version</c> before the scan; a missing binary,
/// an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> betterleaks honors four
/// suppression surfaces authored inside the audited repository — a
/// <c>.betterleaks.toml</c> or <c>.gitleaks.toml</c> (global
/// <c>filter</c>/<c>prefilter</c> expressions and rule edits), a
/// <c>.betterleaksignore</c> or <c>.gitleaksignore</c> (fingerprint
/// suppression, loaded unconditionally; no flag disables it), and inline
/// <c>betterleaks:allow</c>/<c>gitleaks:allow</c> comments — and the audit
/// subject is the repository's author. An auditor its subject can silence is
/// not a gate, so by default the scan pins the built-in ruleset via
/// <c>BETTERLEAKS_CONFIG_TOML</c>, passes <c>--ignore-gitleaks-allow</c>,
/// points <c>--gitleaks-ignore-path</c> at an inert path, and fails closed
/// when any of the four files exists at the worktree root. An operator that
/// deliberately trusts repo-authored suppression — or relies on a repo
/// <c>.betterleaks.toml</c> for custom detectors — sets
/// <c>TrustRepositorySuppression</c> in scoped config; an operator-supplied
/// <c>--config</c> via <c>ExtraArguments</c> still outranks the pinned env
/// config. Unlike gitleaks, betterleaks does not exempt its own config path
/// from the scan (a secret committed inside <c>.betterleaks.toml</c> is
/// reported), so no git-history gate is needed: a deleted historical config
/// cannot suppress the current scan.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.betterleaks",
    displayName: "CodeyBox: Betterleaks Secrets",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "betterleaks",
    InstallHint = "provision the pinned betterleaks release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline; the tool is not apt-installable — "
        + "install the pinned upstream binary via CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class BetterleaksAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.betterleaks";

    /// <summary>
    /// betterleaks release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.8.1";

    /// <summary>
    /// Exit code assigned to "ran and found secrets" via <c>--exit-code</c>.
    /// Disjoint from every betterleaks error convention (0 clean, 1 error/fatal,
    /// 126 usage, 127 not-found) so only this value and 0 are verdicts.
    /// </summary>
    internal const int LeaksFoundExitCode = 4;

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression surfaces
    /// (<c>.betterleaks.toml</c>, <c>.gitleaks.toml</c>,
    /// <c>.betterleaksignore</c>, <c>.gitleaksignore</c>,
    /// <c>betterleaks:allow</c>/<c>gitleaks:allow</c>).
    /// Default false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // betterleaks reads the --gitleaks-ignore-path file (and the repo-root
    // ignore files) in addition to its unconditional repo-root load. Point it
    // at a guaranteed-empty file so the flag's own load sites can never pick
    // up suppression fingerprints; the unconditional repo-root load is gated
    // separately.
    private const string InertGitleaksIgnorePath = "/dev/null";

    // Repository-controlled suppression surfaces, in both the native and the
    // gitleaks-compatible spellings betterleaks honors: ignore files carry
    // fingerprint suppressions (loaded unconditionally), config files carry
    // filter/prefilter/rule edits. betterleaks scans its own config path
    // normally, so — unlike gitleaks's .gitleaks.toml — presence in git
    // history needs no gate: only the worktree files can shape this scan.
    private static readonly string[] RepositorySuppressionFiles =
    [
        ".betterleaksignore",
        ".gitleaksignore",
        ".betterleaks.toml",
        ".gitleaks.toml",
    ];

    // Precedence 3 of 4 (above the repo's .betterleaks.toml/.gitleaks.toml,
    // below --config and BETTERLEAKS_CONFIG): pins the built-in ruleset so the
    // audited repo cannot add filters or rewrite rules. BETTERLEAKS_CONFIG
    // stays available to the operator via the sandbox baseline environment.
    private const string ConfigEnvVar = "BETTERLEAKS_CONFIG_TOML";
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
    public override string Name => "codeybox:betterleaks";

    /// <inheritdoc />
    protected override string ToolName => "betterleaks";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// betterleaks reports no per-finding severity — SARIF results carry no
    /// level and the tool's <c>confidence</c> vocabulary (low/medium/high) is
    /// a detection likelihood, not a severity — so the declared mapping is
    /// total: every level the parser can supply — including the "warning" it
    /// substitutes for the absent SARIF level — maps to
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
            "BetterleaksAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// betterleaks-specific preconditions on the live path beyond the base's
    /// pinned version check: unless the operator opted in, absence of the
    /// repository-controlled files betterleaks would honor
    /// (<c>.betterleaks.toml</c>, <c>.gitleaks.toml</c>,
    /// <c>.betterleaksignore</c>, <c>.gitleaksignore</c>) is confirmed
    /// through the shared fail-closed presence probe. Fails closed as
    /// infrastructure before the scan runs.
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

        // betterleaks loads the repo-root ignore files unconditionally — the
        // --gitleaks-ignore-path flag only adds files — so presence must be
        // gated rather than redirected. Its fingerprints are deterministic and
        // the audit subject can compute them, so honoring the files by default
        // would let the subject hide a leak. A repo-root config file is gated
        // alongside them: its global filter/prefilter expressions can discard
        // arbitrary findings.
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            RepositorySuppressionFiles,
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled file(s) "
                + $"'{string.Join("', '", present)}' in the audited repository — betterleaks honors "
                + "repo-root ignore files unconditionally and applies repo-root config "
                + "filter/prefilter expressions, so any of these files lets the audit subject hide "
                + "a leak. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to trust "
                + "repository-controlled suppression surfaces.")
            { IsDeterministic = true };
    }
}
