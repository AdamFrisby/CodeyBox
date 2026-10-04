using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.TrufflehogAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping <c>trufflehog</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, severity
/// mapping, exit-code classification, and per-auditor configuration. This
/// class declares the scan argv, the pinned tool version, the SARIF report
/// parser, and the verification-state severity mapping.
///
/// <para><b>Gate behaviour: blocking on live credentials only.</b>
/// TruffleHog verifies each candidate secret against its provider and reports
/// the outcome as the SARIF level — <c>error</c> for a verified (live)
/// credential, <c>warning</c> otherwise — so the declared mapping sends
/// verified findings to <see cref="AuditSeverity.Error"/> (they fail the
/// audit) and unverified/unknown findings to
/// <see cref="AuditSeverity.Warning"/> (advisory: they are reported but do
/// not fail the audit). That is the intended gate for a verifying scanner:
/// a live credential blocks, a pattern match that could not be confirmed
/// asks a human to triage. Narrow scope with <c>ExcludedRules</c> or
/// <c>ExcludePaths</c> rather than expecting a single severity.</para>
///
/// <para><b>Why SARIF, not <c>--json</c>.</b> TruffleHog's JSON-lines report
/// embeds every finding's <c>Raw</c> secret bytes on stdout, which the audit
/// pipeline would then persist into the result's raw output — logging
/// secrets. The SARIF report carries only the detector id, the file/line,
/// the verified/unverified wording, and a SHA-256 fingerprint; no secret
/// material reaches findings or raw output. SARIF is also the only report
/// the shared <see cref="SarifToolOutputParser"/> reads without a custom
/// parser, so this auditor reports through <c>--sarif</c> deliberately.</para>
///
/// <para><b>Exit-code convention (verified against trufflehog 3.97.9).</b>
/// Without <c>--fail</c> the tool exits <c>0</c> whether or not it found
/// anything, so the scan passes <c>--fail</c>: <c>0</c> is then a clean run,
/// <c>183</c> is "ran with findings" (the SARIF document on stdout is the
/// verdict), and every other exit — <c>1</c> for scan/config errors such as
/// a non-git working directory, <c>126</c>/<c>127</c> cannot-execute — is
/// infrastructure. Findings-producing exits are therefore exactly
/// <c>{0, 183}</c>; anything else fails loud, never a pass.</para>
///
/// <para><b>Live verification means network egress.</b> Unlike scanners that
/// only pattern-match, this auditor verifies: each candidate secret is
/// checked against its provider's API, so the scan performs outbound
/// requests carrying candidate credential material. Results whose
/// verification errored (for example with no network) are still reported —
/// as unverified warnings, never silently dropped.</para>
///
/// <para><b>Version pin.</b> A scanner's detectors change between releases,
/// so findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>trufflehog --version</c> before every
/// scan; a missing binary, an unrecognised version string, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> The only suppression
/// surface trufflehog honors from inside the audited repository is the
/// inline <c>trufflehog:ignore</c> comment — the audit subject could
/// annotate a leaked line and silence it. By default the scan passes
/// <c>--no-ignore-tag</c> so those comments are ignored; an operator that
/// trusts repository-authored suppression sets
/// <see cref="TrustRepositorySuppressionKey"/> in scoped config. An
/// operator-supplied <c>--config</c> (or <c>--include-paths</c> /
/// <c>--exclude-paths</c>) file is canonicalized and rejected when it
/// resolves inside the audited worktree — the tool resolves it against its
/// cwd, so an in-tree value would hand gate-shaping content to
/// repository-controlled bytes.</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>file://.</c> — the
/// <c>git</c> source clones the repository to a temporary directory and
/// scans committed history (a secret committed and later deleted is still
/// reported), following the tool's own local-clone mitigation for malicious
/// git configs. Uncommitted worktree changes are out of scope: the clone
/// contains only committed content. Findings under vendored/dependency
/// trees (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) are
/// excluded by default: vendored secrets belong to upstream code, not the
/// change under audit. Operators re-include a path by overriding
/// <c>ExcludePaths</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: TruffleHog Secrets",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "trufflehog",
    InstallHint = "provision the pinned trufflehog release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline; it is not apt-installable with "
        + "a version pin — install the pinned upstream binary (verify against the release "
        + "checksums) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions")]
public sealed class TrufflehogAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.trufflehog";

    /// <summary>
    /// trufflehog release the invocation and its report shape are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "3.97.9";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression
    /// (inline <c>trufflehog:ignore</c> comments). Default false: the
    /// audited repo must not be able to silence the audit.
    /// </summary>
    public const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    // ExtraArguments flags that would break the report contract or silently
    // downgrade the gate, rejected deterministically before any sandbox
    // exec: --json/--json-legacy/--github-actions replace the SARIF report
    // the parser reads (--json additionally embeds each finding's Raw
    // secret bytes, which would persist secrets into the audit raw output),
    // while --no-verification turns every finding unverified so a live
    // credential can never block. The operator tunes noise with
    // MinimumSeverity, IncludedRules/ExcludedRules, and ExcludePaths
    // instead; --results stays operator-settable (it only narrows which
    // verification classes are emitted, still as SARIF).
    private static readonly string[] ReservedFlags =
    [
        "--json",
        "--json-legacy",
        "--github-actions",
        "--no-verification",
    ];

    // ExtraArguments flags whose value is a file the tool loads for
    // gate-shaping data — detector config, include/exclude path regexes —
    // each with its kong short form. Canonicalized and rejected when they
    // resolve inside the audited worktree: the tool resolves them against
    // its cwd, so a relative or in-tree value hands gate-shaping content to
    // repository-controlled bytes.
    private static readonly (string LongFlag, string? ShortFlag)[] PathValuedArgumentFlags =
    [
        ("--config", null),
        ("--include-paths", "-i"),
        ("--exclude-paths", "-x"),
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against trufflehog 3.97.9: without --fail the tool exits
        // 0 with or without findings, so the scan passes --fail to disjoint
        // the verdicts — 0 clean, 183 "ran with findings" (the SARIF
        // document on stdout is the verdict). 1 is a scan/config error
        // (bad flags, unreadable repo, non-git directory), 126/127 are
        // cannot-execute. {0, 183} are the only findings-producing exits.
        FindingsExitCodes = new HashSet<int> { 0, 183 },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to
        // ignore the auditor. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:trufflehog";

    /// <inheritdoc />
    protected override string ToolName => "trufflehog";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// TruffleHog's verification vocabulary, carried as the SARIF level: the
    /// tool reports <c>error</c> for a verified (live) credential and
    /// <c>warning</c> for anything it could not confirm (unverified or
    /// verification-errored). The declared mapping sends live credentials
    /// to <see cref="AuditSeverity.Error"/> and unconfirmed detections to
    /// <see cref="AuditSeverity.Warning"/>; anything unrecognized (the tool
    /// emits no other level today) maps to the declared
    /// <see cref="AuditSeverity.Warning"/> default. Raw tool levels never
    /// reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
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
            // `git` scans committed history through a temporary clone
            // rather than only the filesystem worktree.
            "git",
            // Relative to the tool's cwd — the audited worktree root.
            "file://.",
            // SARIF, not --json: the JSON-lines report embeds each
            // finding's Raw secret bytes, while SARIF carries only rule,
            // file/line, verified-ness, and a SHA-256 fingerprint.
            "--sarif",
            // Disjoint the verdicts: 183 when results are found, 0 when
            // clean — without it both share exit 0.
            "--fail",
            // Deterministic and offline except for verification itself: no
            // self-update check at scan start.
            "--no-update",
            // Pin the emitted verification classes explicitly so a future
            // tool-default change cannot silently narrow the report; the
            // level on each result still drives the severity mapping.
            "--results=verified,unverified,unknown",
            "--no-color",
        };
        if (!_trustRepositorySuppression())
            args.Add("--no-ignore-tag");
        return args;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(
        ExternalToolAuditorOptions options)
        => GitEnvironmentRemovals;

    /// <summary>
    /// Binds the shared scoped-config knobs (operator options,
    /// <c>ExpectedVersion</c>, <see cref="TrustRepositorySuppressionKey"/>)
    /// to hot-reloadable accessors.
    /// </summary>
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
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
    /// Pre-scan precondition beyond the base's presence and pinned-version
    /// checks: operator-supplied file flags in <c>ExtraArguments</c> must
    /// resolve outside the audited worktree. Runs in both trust modes: it
    /// guards the operator knob, not repository-authored suppression.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
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

    private void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
    {
        var offenders = ReservedFlags
            .Where(flag => ExtraArgumentsSupplyFlag(options, flag))
            .ToList();
        if (offenders.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would replace the "
            + "SARIF report the parser reads (--json additionally embeds each finding's Raw "
            + "secret bytes, which would persist secrets into the audit raw output) or silently "
            + "downgrade the gate (--no-verification can never report a live credential). Tune "
            + "noise with MinimumSeverity, IncludedRules/ExcludedRules, and ExcludePaths instead.")
        { IsDeterministic = true };
    }
}
