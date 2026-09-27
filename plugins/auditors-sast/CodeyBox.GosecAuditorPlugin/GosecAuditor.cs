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
/// <para><b>Exit-code convention (verified against the gosec 2.28.0 source;
/// live-checked against a 2.22.x binary).</b> gosec
/// does NOT follow the "1 = findings, 2 = could not run" convention:
/// <c>computeExitCode</c> returns <c>1</c> whenever there are unsuppressed
/// issues <em>or</em> per-package analysis errors, and every post-parse
/// operational failure — a bad <c>-conf</c>, "No packages found", analyzer
/// or report-write failure — returns the same <c>1</c>. Flag-parse errors
/// exit 2; 126/127 is cannot-execute — all infrastructure. <c>-no-fail</c>
/// would hide the error signal entirely (exit 0 on analysis errors), so it
/// is never passed.</para>
///
/// <para><b>The two-channel report.</b> gosec's SARIF carries no error
/// detail — the per-file/per-package analysis errors behind the same exit
/// 1 exist only in the JSON report's <c>"Golang errors"</c> map. The scan
/// therefore emits that JSON report to stderr (<c>-fmt json</c> selects the
/// <c>-out</c> format, <c>-out /dev/stderr</c> lands it on the captured
/// stream) while <c>-stdout -verbose sarif</c> keeps the stdout report
/// SARIF. <c>-log /dev/null</c> parks the progress logger so the JSON is
/// the only stderr writer. <see cref="GosecSarifOutputParser"/>
/// discriminates: exit 1 with findings is a verdict only when the error
/// channel confirms an empty map — findings alongside recorded analysis
/// errors, or an unreadable channel, fail closed as infrastructure,
/// because an unscanned package (a broken <c>go.mod</c>, an uncached
/// dependency) must not ride a passing verdict. Exit 1 with a
/// valid-but-empty results array means the run recorded errors and no
/// issues; exit 1 with no SARIF is an operational failure — both
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
/// ("Skipping: … Path doesn't exist"). They are rejected deterministically
/// when the operator options are bound (see <see cref="InitializeAsync"/>);
/// the dedicated keys (<c>ConfigPath</c>, <c>ScanTests</c>,
/// <c>BuildTags</c>) cover the flags operators actually need, and path-like
/// extras still work as additional scan patterns — normalized to a
/// <c>./</c>-relative form first, because gosec forwards patterns to
/// <c>packages.Load</c>, where a bare pattern (<c>std</c>, <c>all</c>,
/// <c>golang.org/x/...</c>) resolves against GOROOT or the module build
/// list — code outside the audited worktree.</para>
///
/// <para><b>Nested module roots evade <c>./...</c>.</b>
/// <c>go list ./...</c> — the loader behind gosec — does not descend into a
/// directory carrying its own <c>go.mod</c>: each is a separate module
/// root, skipped with no error recorded, so subject code under
/// <c>tools/</c>, <c>services/</c> or any nested module would ride a
/// passing verdict. A bounded pre-scan therefore enumerates <c>go.mod</c>
/// files below the worktree root (vendor/, testdata/, dot- and
/// underscore-prefixed directories pruned — the go tool never reaches them
/// either) and fails closed listing any roots beyond the root module's.
/// Operators who accept the residual scope — or who cover the nested
/// modules with a <c>go.work</c> workspace the loader resolves — set
/// <c>AllowNestedModules</c>, which downgrades the gate to a warning log
/// naming the roots. Files behind build constraints the sandbox does not
/// satisfy (other GOOS/GOARCH, custom <c>//go:build</c> tags not in
/// <c>BuildTags</c>) are likewise never analyzed.</para>
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
/// can steer the ruleset. The same posture applies to
/// <c>-exclude-generated</c>: the <c>// Code generated … DO NOT EDIT</c>
/// marker is authored in the repository too, so one comment line would
/// erase a file from analysis — generated files are scanned by default and
/// the marker only takes effect when an operator sets
/// <c>ExcludeGenerated</c>.</para>
///
/// <para><b>Environment hygiene.</b> gosec's opt-in AI fix feature
/// (<c>GOSEC_AI_PROVIDER</c>/<c>GOSEC_AI_API_KEY</c>/<c>GOSEC_AI_BASE_URL</c>)
/// would ship source snippets to an external service when enabled from the
/// baseline environment; the auditor clears those variables for the tool
/// process so the scan is always the deterministic, offline analysis.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>./...</c> — every Go
/// package under the worktree root, gated by the nested-module probe
/// above. gosec itself already excludes <c>vendor/</c> and <c>.git/</c> at
/// scan time and the Go package loader never sees <c>testdata/</c>; a
/// finding-level <c>vendor/</c> backstop is the only default
/// <c>ExcludePaths</c> entry — vendor semantics are tool-enforced, while a
/// subject-named directory (<c>third_party/</c>, …) is ordinary shippable
/// code and excluding it would be a subject-controlled suppression
/// channel. Generated files ARE analyzed: their marker is repo-authored
/// and dropping it by default would be the same channel (<c>ExcludeGenerated</c>
/// opts out). Test files are not scanned by default (<c>ScanTests</c>
/// opt-in). A repository with no loadable Go packages fails loudly as
/// infrastructure ("No packages found"), never as a pass.</para>
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
    /// also every post-parse operational failure. The SARIF report on stdout
    /// and the JSON <c>"Golang errors"</c> map on stderr discriminate the
    /// cases; see <see cref="GosecSarifOutputParser"/>.
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
    /// Scoped-config boolean: when true, passes <c>-exclude-generated</c> so
    /// files carrying the <c>// Code generated … DO NOT EDIT</c> marker are
    /// skipped. Default false — the marker is authored inside the audited
    /// repository, so honoring it by default would let the audit subject
    /// erase a file from analysis with one comment line, the same
    /// suppression class <c>-nosec</c> blocks. Generated code is scanned
    /// unless the operator opts out here.
    /// </summary>
    public const string ExcludeGeneratedKey = "ExcludeGenerated";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// gosec's <c>#nosec</c> and <c>//gosec:disable</c> comments. Default
    /// false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Scoped-config boolean acknowledging nested Go module roots. Default
    /// false: a <c>go.mod</c> below the worktree root fails the run
    /// deterministically because <c>go list ./...</c> never descends into a
    /// nested module root — code there is silently unscanned. When true the
    /// run proceeds and the uncovered roots are logged as a warning; the
    /// operator accepts the residual scope.
    /// </summary>
    internal const string AllowNestedModulesKey = "AllowNestedModules";

    // Enumerates candidate nested Go module roots: every go.mod below the
    // worktree root. Directories the go tool itself never reaches — vendor,
    // testdata, dot- and underscore-prefixed — are pruned so a go.mod in a
    // tree the scan cannot see anyway is not a false positive; the root
    // module's own ./go.mod is filtered out by the caller. find does not
    // follow symlinks, matching `go list`.
    private const string NestedModuleProbeScript =
        "find . -mindepth 1 -type d \\( -name vendor -o -name testdata -o -name '.*' -o -name '_*' \\) -prune -o -type f -name go.mod -print";

    private const int MaxModuleRootsInMessage = 8;
    private const int ModuleRootMaxChars = 120;

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean run; 1 = ran with findings or analysis errors, OR could
        // not run — the SARIF on stdout discriminates (see parser). Anything
        // else (2 usage error, 126/127 cannot-execute, unknown) is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, IssuesOrErrorsExitCode },
        // vendor/ is the only default exclusion: Go tooling enforces its
        // meaning (packages beneath it are never part of the build and
        // gosec already skips them at scan time), so this is a finding-level
        // backstop, not a suppression surface. No name-based exclusions
        // beyond it — a directory like third_party/ is ordinary shippable
        // code whose name the audit subject chooses, and dropping its
        // findings would be a subject-controlled suppression channel.
        // Operators add their own paths via ExcludePaths in scoped config.
        ExcludePaths = ["vendor/"],
    };

    // Pre-initialization accessor: the shared defaults carry no extras, so
    // normalization is identity here — InitializeAsync swaps in a bound
    // copy whose extras are validated/normalized per run.
    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<bool> _scanTests = static () => false;
    private Func<string?> _buildTags = static () => null;
    private Func<bool> _excludeGenerated = static () => false;
    private Func<bool> _trustRepositorySuppression = static () => false;
    private Func<bool> _allowNestedModules = static () => false;
    private ILogger? _logger;

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
        var args = new List<string>
        {
            // stdout carries the findings channel: -verbose overrides the
            // -fmt selection for the printed report, keeping it SARIF.
            "-stdout",
            "-verbose", "sarif",
            // gosec's SARIF has no error channel — the per-package analysis
            // errors that also drive exit 1 exist only in the JSON
            // ReportInfo ("Golang errors"). -fmt selects the -out format:
            // writing that report to /dev/stderr puts the error evidence
            // on the captured stderr stream in the same bounded invocation,
            // so findings can never mask an unscanned package.
            "-fmt", "json",
            "-out", "/dev/stderr",
            // The JSON must be the only stderr writer: gosec's progress
            // logger defaults to stderr and would corrupt the channel.
            "-log", "/dev/null",
        };

        // -exclude-generated is operator opt-in only: the "Code generated …
        // DO NOT EDIT" marker is authored inside the audited repository, so
        // honoring it by default would let the subject remove a file from
        // analysis with one comment line.
        if (_excludeGenerated())
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

    /// <summary>
    /// Pre-scan scope gate: <c>go list ./...</c> — the loader behind gosec —
    /// does not descend into a directory carrying its own <c>go.mod</c>
    /// (each is a separate module root, skipped with no error recorded), so
    /// subject code under a nested module would be silently unscanned while
    /// the run still passes. A bounded <c>find</c> enumerates every go.mod
    /// below the worktree root; any root beyond the root module's fails the
    /// audit deterministically, unless the operator set
    /// <see cref="AllowNestedModulesKey"/>, in which case the uncovered
    /// roots are logged and the run proceeds. Both outcomes happen before
    /// the scan: an unscanned subtree must never ride a passing verdict.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var nestedRoots = await ProbeNestedModuleRootsAsync(
            sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
        if (nestedRoots.Count == 0)
            return;

        var named = string.Join(
            ", ",
            nestedRoots.Take(MaxModuleRootsInMessage)
                .Select(root => "'" + ToolOutputText.SingleLine(root, ModuleRootMaxChars) + "'"));
        var remainder = nestedRoots.Count > MaxModuleRootsInMessage
            ? $", … +{nestedRoots.Count - MaxModuleRootsInMessage} more"
            : string.Empty;

        if (_allowNestedModules())
        {
            _logger?.LogWarning(
                "GosecAuditor: {NestedModuleCount} nested Go module root(s) are outside the './...' "
                + "scan scope (AllowNestedModules): {NestedModuleRoots}{More}",
                nestedRoots.Count, named, remainder);
            return;
        }

        throw new AuditUnavailableException(
            $"could-not-verify: audit tool '{tool}' cannot prove its scan scope: the worktree holds "
            + $"{nestedRoots.Count} nested Go module root(s) ({named}{remainder}), and 'go list ./...' "
            + "never descends into a directory carrying its own go.mod — code there is silently "
            + "unscanned, so the audit cannot pass on a partial tree. Merge the modules into the "
            + "root module, or acknowledge the residual scope via "
            + $"CodeyBox:Plugins:{PluginId}:{AllowNestedModulesKey}.")
        { IsDeterministic = true };
    }

    /// <summary>
    /// Bounded probe listing the directories under the worktree root that
    /// carry their own <c>go.mod</c>. The <c>find</c> prunes directories the
    /// go tool itself never descends into (vendor, testdata, dot- and
    /// underscore-prefixed), so only roots that look like part of the
    /// scanned tree — but are silently skipped — come back. The root
    /// module's own <c>./go.mod</c> carries no intermediate directory and
    /// is filtered out. Fails closed: a transport failure or a non-zero
    /// exit (including the kill fired when output exceeds the bound) is
    /// infrastructure, never "no nested modules". Output lines are
    /// untrusted — they are only ever sanitized into a message or log,
    /// never used as paths.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ProbeNestedModuleRootsAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "module-scope probe",
            new SandboxExec
            {
                Argv = ["sh", "-c", NestedModuleProbeScript],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' module-scope probe could not run: the sandbox "
                + "exec transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not enumerate nested Go module roots "
                + $"(exit {result.ExitCode}) — an unverifiable scan scope must not ride a passing "
                + "verdict.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        var roots = new List<string>();
        foreach (var line in result.Stdout.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // find prints './<dir>/go.mod'; the root module's own go.mod is
            // the scan's anchor, not a coverage gap.
            if (!line.StartsWith("./", StringComparison.Ordinal))
                continue;
            var relative = line[2..];
            if (!relative.EndsWith("/go.mod", StringComparison.Ordinal))
                continue;
            var dir = relative[..^"/go.mod".Length];
            if (dir.Length > 0)
                roots.Add(dir);
        }
        return roots;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        // ExtraArguments are validated and normalized here — at bind time,
        // inside the per-run accessor — so BuildToolArguments stays a pure
        // query over whatever options the base hands it.
        _optionsAccessor = () =>
        {
            var bound = ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
            bound.ExtraArguments = NormalizePackagePatterns(bound.ExtraArguments);
            return bound;
        };
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _scanTests = () => bool.TryParse(scoped[ScanTestsKey], out var scan) && scan;
        _buildTags = () => scoped[BuildTagsKey];
        _excludeGenerated = () =>
            bool.TryParse(scoped[ExcludeGeneratedKey], out var exclude) && exclude;
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        _allowNestedModules = () =>
            bool.TryParse(scoped[AllowNestedModulesKey], out var allow) && allow;
        _logger = context.Logger;
        context.Logger.LogInformation(
            "GosecAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates <see cref="ExternalToolAuditorOptions.ExtraArguments"/> and
    /// returns the normalized list to forward — a pure function; the shared
    /// defaults and the caller's list are never mutated. Entries land after
    /// the positional <c>./...</c> scan target, and Go's flag package stops
    /// flag parsing at the first positional — a flag-shaped extra would be
    /// silently swallowed as a (nonexistent) package path instead of
    /// reaching gosec's flag parser. Flag-shaped and tree-escaping entries
    /// are rejected deterministically rather than letting configured
    /// behavior evaporate. Surviving entries are confined to the worktree:
    /// gosec forwards them to <c>packages.Load</c>, where only a
    /// <c>./</c>-prefixed pattern is directory-relative — a bare pattern
    /// (<c>std</c>, <c>all</c>, <c>golang.org/x/...</c>) would resolve
    /// against GOROOT or the module build list, outside the audited tree —
    /// so each forwarded pattern is normalized to its <c>./</c>-relative
    /// form, and the argv carries the validated pattern rather than a raw
    /// entry whose padding or slashes gosec would read as a different,
    /// nonexistent path.
    /// </summary>
    private static IReadOnlyList<string> NormalizePackagePatterns(IReadOnlyList<string> extraArguments)
    {
        var normalized = new List<string>(extraArguments.Count);
        foreach (var arg in extraArguments)
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
            if (!pattern.StartsWith("./", StringComparison.Ordinal))
                pattern = "./" + pattern;
            normalized.Add(pattern);
        }
        return normalized;
    }
}
