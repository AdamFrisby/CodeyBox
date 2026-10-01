using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.BetterleaksAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping <c>betterleaks</c> on
/// <see cref="GitleaksCompatibleSecretsAuditorBase"/>: the shared base
/// supplies the scan argv (including the <c>--exit-code</c> reassignment and
/// the <c>--log-opts … --text</c> git-log pinning that stops a committed
/// <c>.gitattributes</c> from blanking the patch stream), the pinned-ruleset
/// environment, the total severity mapping, the scoped-config wiring, and
/// the suppression/operator-flag gates. This class declares only the
/// betterleaks deltas in <see cref="Profile"/>.
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
/// indistinguishable. The shared base overrides <c>--exit-code</c> to
/// <see cref="GitleaksCompatibleSecretsAuditorBase.LeaksFoundExitCode"/>: 0
/// is a clean run, 4 is "ran with findings", and anything else (1
/// error/fatal, 126 usage error, 127 cannot-execute) is infrastructure.
/// Upstream exits 1 on error before checking findings, so a partial scan
/// can never look like a verdict.</para>
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
/// not a gate, so by default the shared base pins the built-in ruleset via
/// <c>BETTERLEAKS_CONFIG_TOML</c>, passes <c>--ignore-gitleaks-allow</c>,
/// points <c>--gitleaks-ignore-path</c> at an inert path, and fails closed
/// when any of the four files exists at the worktree root. An operator that
/// deliberately trusts repo-authored suppression — or relies on a repo
/// <c>.betterleaks.toml</c> for custom detectors — sets
/// <see cref="GitleaksCompatibleSecretsAuditorBase.TrustRepositorySuppressionKey"/>
/// in scoped config; an operator-supplied <c>--config</c> via
/// <c>ExtraArguments</c> still outranks the pinned env config but is
/// canonicalized outside the worktree by the shared base.</para>
///
/// <para><b>Config-path self-exemption.</b> Whether betterleaks exempts its
/// own config path from the scan is keyed on the loaded <c>Config.Path</c>:
/// it stays empty while the config comes from the pinned inline
/// <c>BETTERLEAKS_CONFIG_TOML</c>, so nothing is exempted by default and no
/// git-history gate is needed — a deleted historical config cannot shape
/// the scan. Under <c>TrustRepositorySuppression</c> a loaded repo
/// <c>.betterleaks.toml</c>/<c>.gitleaks.toml</c> DOES set
/// <c>Config.Path</c> and is exempted in every commit — a secret committed
/// inside that file is then never reported. That is moot in trust mode,
/// which already accepts repo-authored suppression; it is stated here so
/// the trade-off is explicit.</para>
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
public sealed class BetterleaksAuditor : GitleaksCompatibleSecretsAuditorBase
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.betterleaks";

    /// <summary>
    /// betterleaks release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.8.1";

    // Repository-controlled suppression surfaces, in both the native and the
    // gitleaks-compatible spellings betterleaks honors: ignore files carry
    // fingerprint suppressions (loaded unconditionally), config files carry
    // filter/prefilter/rule edits.
    private static readonly GitleaksCompatibleSecretsProfile Profile = new(
        PluginId: PluginId,
        DefaultExpectedVersion: DefaultExpectedVersion,
        // Precedence 3 of 4 (above the repo's .betterleaks.toml/.gitleaks.toml,
        // below --config and BETTERLEAKS_CONFIG): pins the built-in ruleset so
        // the audited repo cannot add filters or rewrite rules.
        // BETTERLEAKS_CONFIG stays available to the operator via the sandbox
        // baseline environment.
        ConfigTomlEnvVar: "BETTERLEAKS_CONFIG_TOML",
        RepositorySuppressionFiles:
        [
            ".betterleaksignore",
            ".gitleaksignore",
            ".betterleaks.toml",
            ".gitleaks.toml",
        ],
        SuppressionGateRationale:
            "betterleaks loads repo-root ignore files unconditionally and applies repo-root "
            + "config filter/prefilter expressions, so any of these files lets the audit "
            + "subject hide a leak.");
    // No HistoryGatedPaths: under the pinned inline BETTERLEAKS_CONFIG_TOML
    // the loaded Config.Path stays empty, so no path is exempted from the
    // scan and a deleted historical config cannot shape it. (In trust mode a
    // loaded repo config IS self-exempted — see the class docstring.)

    /// <summary>Declares the shared betterleaks policy.</summary>
    public BetterleaksAuditor()
        : base(Profile)
    {
    }

    /// <inheritdoc />
    public override string Name => "codeybox:betterleaks";

    /// <inheritdoc />
    protected override string ToolName => "betterleaks";
}
