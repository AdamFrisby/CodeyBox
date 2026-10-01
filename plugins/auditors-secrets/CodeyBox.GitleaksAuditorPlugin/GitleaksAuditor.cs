using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GitleaksAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping <c>gitleaks</c> on
/// <see cref="GitleaksCompatibleSecretsAuditorBase"/>: the shared base
/// supplies the scan argv (including the <c>--exit-code</c> reassignment and
/// the <c>--log-opts … --text</c> git-log pinning that stops a committed
/// <c>.gitattributes</c> from blanking the patch stream), the pinned-ruleset
/// environment, the total severity mapping, the scoped-config wiring, and
/// the suppression/operator-flag gates. This class declares only the
/// gitleaks deltas in <see cref="Profile"/>.
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
/// "could not run" would be indistinguishable. The shared base overrides
/// <c>--exit-code</c> to
/// <see cref="GitleaksCompatibleSecretsAuditorBase.LeaksFoundExitCode"/>: 0
/// is a clean run, 4 is "ran with findings", and anything else (1, 126 usage
/// error, 126/127 cannot-execute) is infrastructure. Upstream also exits 1
/// on error before checking findings, so a partial scan can never look like
/// a verdict.</para>
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
/// silence is not a gate, so by default the shared base pins gitleaks's
/// built-in ruleset via <c>GITLEAKS_CONFIG_TOML</c>, passes
/// <c>--ignore-gitleaks-allow</c>, points <c>--gitleaks-ignore-path</c> at an
/// inert path, and fails closed when a repo-root <c>.gitleaksignore</c>
/// exists in the worktree or a <c>.gitleaks.toml</c> exists anywhere in the
/// worktree or git history. An operator that deliberately trusts
/// repo-authored suppression — or relies on a repo <c>.gitleaks.toml</c> for
/// custom detectors — sets
/// <see cref="GitleaksCompatibleSecretsAuditorBase.TrustRepositorySuppressionKey"/>
/// in scoped config; an operator-supplied <c>--config</c> via
/// <c>ExtraArguments</c> still outranks the pinned env config but is
/// canonicalized outside the worktree by the shared base.</para>
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
public sealed class GitleaksAuditor : GitleaksCompatibleSecretsAuditorBase
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

    // Repository-controlled suppression surfaces gitleaks honors: the ignore
    // file carries fingerprint suppressions (loaded unconditionally), the
    // config file carries rule/allowlist edits.
    private static readonly GitleaksCompatibleSecretsProfile Profile = new(
        PluginId: PluginId,
        DefaultExpectedVersion: DefaultExpectedVersion,
        // Precedence 3 of 4 (above the repo's .gitleaks.toml, below --config
        // and GITLEAKS_CONFIG): pins the built-in ruleset so the audited repo
        // cannot extend rules or add allowlists. GITLEAKS_CONFIG stays
        // available to the operator via the sandbox baseline environment.
        ConfigTomlEnvVar: "GITLEAKS_CONFIG_TOML",
        RepositorySuppressionFiles: [".gitleaksignore", ".gitleaks.toml"],
        SuppressionGateRationale:
            "gitleaks loads the repo-root ignore file unconditionally and exempts its own "
            + "config path from the scan, so either file lets the audit subject hide a leak.")
    {
        // gitleaks sets Config.Path to <source>/.gitleaks.toml whenever
        // --config is unset — regardless of where the config actually came
        // from — and skips every fragment at that path. The file is
        // therefore never scanned in ANY commit: a secret committed inside
        // it (including one later deleted) evades the audit, so its presence
        // in git history is gated, not just in the worktree.
        HistoryGatedPaths = [".gitleaks.toml"],
    };

    /// <summary>Declares the shared gitleaks policy.</summary>
    public GitleaksAuditor()
        : base(Profile)
    {
    }

    /// <inheritdoc />
    public override string Name => "codeybox:gitleaks";

    /// <inheritdoc />
    protected override string ToolName => "gitleaks";
}
