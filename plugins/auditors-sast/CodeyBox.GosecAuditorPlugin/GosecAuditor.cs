using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GosecAuditorPlugin;

/// <summary>
/// SAST auditor wrapping <c>gosec</c> (Go security analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, finding identity, and per-auditor configuration. This
/// class adds the SARIF parser (<see cref="GosecSarifOutputParser"/>, which
/// also discriminates gosec's ambiguous exit 1), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: severity-driven — only HIGH findings block by
/// default.</b> gosec's native severity vocabulary (LOW / MEDIUM / HIGH) is
/// recovered from each result's rule descriptor — gosec flattens it into the
/// SARIF <c>level</c> (MEDIUM and HIGH both become <c>error</c>), so reading
/// <c>level</c> alone would over-block. The declared mapping is HIGH →
/// <see cref="AuditSeverity.Error"/> (fails the audit), MEDIUM →
/// <see cref="AuditSeverity.Warning"/>, LOW → <see cref="AuditSeverity.Info"/>
/// — consistent with every other auditor. <c>MinimumSeverity</c> only drops
/// findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against gosec 2.28.0).</b> gosec
/// does NOT follow the "1 = findings, 2 = could not run" convention:
/// <c>computeExitCode</c> returns <c>1</c> whenever there are unsuppressed
/// issues <em>or</em> per-package analysis errors, and every operational
/// failure — bad flags, bad <c>-conf</c>, "No packages found", analyzer or
/// report-write failure — returns the same <c>1</c>. The discriminator is
/// the SARIF report on stdout: exit 1 with findings is a verdict; exit 1
/// with a valid-but-empty results array means the run recorded analysis
/// errors (files/packages it could not load — gosec's SARIF has no error
/// channel) and fails closed as infrastructure; exit 1 with no SARIF is an
/// operational failure, also infrastructure. <c>-no-fail</c> would hide the
/// error signal entirely (exit 0 on analysis errors), so it is never
/// passed. Flag-parse errors exit 2, 126/127 is cannot-execute — all
/// infrastructure.</para>
///
/// <para><b>Scan scope and the positional argument.</b> The scan target is
/// the positional <c>./...</c> — not <c>-r</c>: SARIF artifact URIs are
/// relativized against <c>getRootPaths(flag.Args())</c>, so with no
/// positional args every finding's location is emitted as an empty URI and
/// file/line would be lost. A consequence: Go's flag package stops flag
/// parsing at the first positional, so flag-shaped
/// <see cref="ExternalToolAuditorOptions.ExtraArguments"/> cannot reach
/// gosec's flag parser — gosec would silently treat them as package paths
/// ("Skipping: … Path doesn't exist"). They are rejected deterministically;
/// the dedicated keys (<c>ConfigPath</c>, <c>ScanTests</c>,
/// <c>BuildTags</c>) cover the flags operators actually need, and path-like
/// extras still work as additional scan patterns.</para>
///
/// <para><b>Version pin.</b> gosec's rule set and SARIF shape change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>gosec -version</c> before every
/// scan; a missing binary, an unrecognised version string (note:
/// <c>go install</c> builds report <c>Version: dev</c> — only
/// ldflags-stamped binaries such as the upstream release tarballs carry a
/// usable version), or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> gosec honors in-source
/// suppression comments (<c>#nosec</c>, <c>//gosec:disable</c>) authored
/// inside the audited repository — and the audit subject writes that
/// repository. An auditor its subject can silence is not a gate, so by
/// default the scan passes <c>-nosec</c>, making every suppression comment
/// inert; findings then surface for code the comments would have hidden.
/// Operators who deliberately trust repo-authored suppression set
/// <c>TrustRepositorySuppression</c>. gosec loads no repository-authored
/// config file on its own — <c>-conf</c> is explicit-only, so no repo file
/// can steer the ruleset.</para>
///
/// <para><b>Environment hygiene.</b> gosec's opt-in AI fix feature
/// (<c>GOSEC_AI_PROVIDER</c>/<c>GOSEC_AI_API_KEY</c>/<c>GOSEC_AI_BASE_URL</c>)
/// would ship source snippets to an external service when enabled from the
/// baseline environment; the auditor clears those variables for the tool
/// process so the scan is always the deterministic, offline analysis.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>./...</c> — every Go
/// package under the worktree root. gosec itself already excludes
/// <c>vendor/</c> and <c>.git/</c> at scan time and the Go package loader
/// never sees <c>testdata/</c>; on top of that the auditor passes
/// <c>-exclude-generated</c> so files carrying the <c>// Code generated …
/// DO NOT EDIT</c> marker are not analyzed, and drops findings under
/// vendored prefixes as a finding-level backstop — problems in vendored or
/// generated code belong to upstream tooling, not the change under audit.
/// Test files are not scanned by default (<c>ScanTests</c> opt-in). A
/// repository with no loadable Go packages fails loudly as infrastructure
/// ("No packages found"), never as a pass.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Gosec Go Security",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "gosec",
    InstallHint = "provision the pinned gosec release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — install the ldflags-stamped "
        + "upstream release binary (gosec_" + DefaultExpectedVersion
        + "_linux_<arch>.tar.gz) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions; `go install` builds report 'Version: dev' and fail the pin "
        + "check, and no distro apt package carries a version pin")]
[CodeyBoxPluginRequiresTool(
    "go",
    InstallHint = "gosec loads packages through the Go toolchain — provision a `go` toolchain "
        + "new enough for the audited module's go.mod directive into the sandbox baseline, and "
        + "pre-cache module dependencies (`go mod download` at bake) or vendor them: the audit "
        + "sandbox has no network")]
public sealed class GosecAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.gosec";

    /// <summary>
    /// gosec release the invocation and its SARIF shape are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in
    /// the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.28.0";

    /// <summary>
    /// gosec's "found unsuppressed issues or recorded analysis errors" exit —
    /// also every operational failure. The SARIF report on stdout
    /// discriminates the two cases; see <see cref="GosecSarifOutputParser"/>.
    /// </summary>
    internal const int IssuesOrErrorsExitCode = 1;

    /// <summary>Scoped-config key for an explicit gosec configuration file (<c>-conf</c>).</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config boolean for <c>-tests</c>: include <c>_test.go</c> files
    /// in the scan. Default false.
    /// </summary>
    public const string ScanTestsKey = "ScanTests";

    /// <summary>
    /// Scoped-config value passed to <c>-tags</c>: comma-separated Go build
    /// tags so files behind build constraints resolve during analysis.
    /// </summary>
    public const string BuildTagsKey = "BuildTags";

    /// <summary>
    /// Scoped-config boolean: when true, omits <c>-exclude-generated</c> so
    /// generated files (<c>// Code generated … DO NOT EDIT</c>) are analyzed.
    /// Default false — generated code is not the change under audit.
    /// </summary>
    public const string IncludeGeneratedKey = "IncludeGenerated";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// gosec's <c>#nosec</c> and <c>//gosec:disable</c> comments. Default
    /// false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean run; 1 = ran with findings or analysis errors, OR could
        // not run — the SARIF on stdout discriminates (see parser). Anything
        // else (2 usage error, 126/127 cannot-execute, unknown) is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, IssuesOrErrorsExitCode },
        // gosec already skips vendor/ and .git/ at scan time via its built-in
        // -exclude-dir defaults; these entries are a finding-level backstop.
        // Findings there describe upstream code, not the change under audit.
        ExcludePaths = ["vendor/", "third_party/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _scanTests = static () => false;
    private Func<string?> _buildTags = static () => null;
    private Func<bool> _includeGenerated = static () => false;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:gosec";

    /// <inheritdoc />
    protected override string ToolName => "gosec";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new GosecSarifOutputParser();

    /// <summary>
    /// Declared mapping from gosec's severity vocabulary to
    /// <see cref="AuditSeverity"/>. The parser supplies the native
    /// <c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c> recovered from the rule
    /// descriptor, falling back to the SARIF <c>level</c> when the descriptor
    /// is unreadable — so both vocabularies are covered here. Raw tool levels
    /// never reach findings: HIGH and SARIF <c>error</c> block, MEDIUM and
    /// <c>warning</c> are advisory, LOW and <c>note</c>/<c>none</c> are
    /// informational, and anything unrecognised is advisory.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        NormalizeExtraArguments(options);

        var args = new List<string>
        {
            "-fmt", "sarif",
            // Explicit stdout contract: the report is the verdict channel.
            "-stdout",
        };

        // Generated files (// Code generated … DO NOT EDIT) are not the
        // change under audit.
        if (!_includeGenerated())
            args.Add("-exclude-generated");

        if (_scanTests())
            args.Add("-tests");

        var buildTags = _buildTags();
        if (!string.IsNullOrWhiteSpace(buildTags))
        {
            args.Add("-tags");
            args.Add(buildTags.Trim());
        }

        // The audit subject authors #nosec / //gosec:disable comments; keep
        // them inert unless the operator opts in to repo-controlled
        // suppression. In trust mode the comments work and suppressed issues
        // are dropped by gosec before the report is written.
        if (!_trustRepositorySuppression())
            args.Add("-nosec");

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            args.Add("-conf");
            args.Add(configPath.Trim());
        }

        // Positional scan target, emitted last: SARIF artifact URIs are
        // relativized against the positional roots, so this must be present
        // (and gosec's flag parser sees everything before it as flags).
        args.Add("./...");
        return args;
    }

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? BuildToolEnvironment(
        ExternalToolAuditorOptions options)
        // gosec's AI autofix is opt-in via environment; a baseline that
        // exports GOSEC_AI_* would ship audited source to an external
        // provider. An auditor run is deterministic analysis — clear the
        // knobs unconditionally.
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GOSEC_AI_PROVIDER"] = string.Empty,
            ["GOSEC_AI_API_KEY"] = string.Empty,
            ["GOSEC_AI_BASE_URL"] = string.Empty,
        };

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _scanTests = () => bool.TryParse(scoped[ScanTestsKey], out var scan) && scan;
        _buildTags = () => scoped[BuildTagsKey];
        _includeGenerated = () =>
            bool.TryParse(scoped[IncludeGeneratedKey], out var include) && include;
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "GosecAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rewrites <see cref="ExternalToolAuditorOptions.ExtraArguments"/> to
    /// its validated, normalized form. Entries land after the positional
    /// <c>./...</c> scan target, and Go's flag package stops flag parsing
    /// at the first positional — a flag-shaped extra would be silently
    /// swallowed as a (nonexistent) package path instead of reaching
    /// gosec's flag parser. Flag-shaped and tree-escaping entries are
    /// rejected deterministically rather than letting configured behavior
    /// evaporate, and the forwarded argv carries the trimmed
    /// <c>/</c>-separated pattern that was validated — not a raw entry
    /// whose padding gosec would read as a different, nonexistent path.
    /// Package-pattern entries (e.g. <c>./pkg/...</c>) pass through.
    /// </summary>
    private static void NormalizeExtraArguments(ExternalToolAuditorOptions options)
    {
        var normalized = new List<string>(options.ExtraArguments.Count);
        foreach (var arg in options.ExtraArguments)
        {
            var pattern = TryNormalizeWorktreeRelativePath(arg)
                ?? throw new AuditUnavailableException(
                    "could-not-verify: gosec auditor ExtraArguments entry "
                    + $"'{SingleLine(arg)}' is not a repository-relative package pattern; absolute "
                    + "paths and '..' segments would scan outside the audited worktree.")
                { IsDeterministic = true };
            if (pattern.StartsWith('-'))
                throw new AuditUnavailableException(
                    "could-not-verify: gosec auditor ExtraArguments entry "
                    + $"'{SingleLine(arg)}' looks like a flag, but tool arguments are appended after "
                    + "the positional './...' scan target and Go's flag package treats everything past "
                    + "it as package patterns — the flag would be silently ignored. Use the dedicated "
                    + $"scoped-config keys ({ConfigPathKey}, {ScanTestsKey}, {BuildTagsKey}) or the "
                    + "severity/rule/path options under "
                    + $"CodeyBox:Plugins:{PluginId} instead.")
                { IsDeterministic = true };
            normalized.Add(pattern);
        }
        options.ExtraArguments = normalized;
    }
}
