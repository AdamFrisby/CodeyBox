using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GovulncheckAuditorPlugin;

/// <summary>
/// Dependency vulnerability auditor wrapping <c>govulncheck</c> (the Go
/// team's scanner for vulnerabilities reachable in Go source) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the govulncheck-specific arguments and
/// knobs, the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, and a precondition
/// probe for the <c>go</c> toolchain govulncheck shells out to.
///
/// <para><b>Gate behaviour: severity-driven (blocking on error).</b> At the
/// default symbol scan level govulncheck marks a vulnerability
/// <c>error</c> when the audited code actually calls a vulnerable symbol,
/// <c>warning</c> when it imports a vulnerable package without reaching the
/// symbol, and <c>note</c> when it merely depends on a vulnerable module.
/// Those levels map to <see cref="AuditSeverity.Error"/>/
/// <see cref="AuditSeverity.Warning"/>/<see cref="AuditSeverity.Info"/> — a
/// reachable vulnerability fails the audit; the weaker matches are advisory.
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against govulncheck v1.8.0
/// source).</b> govulncheck does NOT follow the common "1 = findings"
/// convention: with <c>-format sarif</c> a completed run exits <c>0</c>
/// whether or not it found anything — the SARIF report is the verdict for
/// both outcomes. The non-zero codes are all non-verdicts: <c>3</c> is
/// "vulnerabilities found" but is only produced for text output (never under
/// <c>-format sarif</c>, so seeing it here means the format was overridden
/// or the report shape changed); <c>2</c> is usage error (bad flag, no
/// patterns, patterns matching no packages, a file passed in source mode);
/// <c>1</c> is every other run failure (no go.mod, package-load error,
/// unreachable vulnerability database); <c>126</c>/<c>127</c>
/// cannot-execute. Only <c>0</c> is declared findings-producing; every other
/// exit fails closed as infrastructure.</para>
///
/// <para><b>Stream note.</b> The SARIF report goes to <b>stdout</b>
/// (<c>-format sarif</c>); progress and error text goes to stderr. The
/// shared <see cref="SarifToolOutputParser"/> reads stdout.</para>
///
/// <para><b>Version pin — shared, with a banner-anchored extractor.</b>
/// govulncheck's <c>-version</c> banner prints
/// <c>Go: go&lt;toolchain-version&gt;</c> before <c>Scanner:
/// govulncheck@v&lt;version&gt;</c>, and the pin's default extraction takes
/// the first semver token — which would verify the Go toolchain's version,
/// not govulncheck's, and fail closed on every correctly provisioned
/// baseline. The declared <see cref="VersionPin"/> therefore supplies
/// <see cref="ExtractGovulncheckVersion"/>, which reads the
/// <c>govulncheck@v…</c> token specifically. A missing binary, an
/// unrecognised banner, or a version other than <c>ExpectedVersion</c> is
/// an infrastructure failure naming the tool — never a pass, never a
/// finding.</para>
///
/// <para><b>Repository-controlled suppression: none exists.</b> govulncheck
/// reads no configuration file from the audited repository — upstream
/// explicitly provides no way to silence findings ("There is no support for
/// silencing vulnerability findings", govulncheck docs), so there is no
/// repository-suppression gate to fail closed on. go.mod/go.sum/go.work are
/// the audit subject itself: they define the module graph under audit, and
/// weakening them is visible in the diff.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>govulncheck -format sarif ./...</c> at the worktree root: every
/// package in the module, symbol-level reachability. Test files are
/// excluded by default (test-only vulnerable calls are not shipped) —
/// <c>IncludeTests</c> re-enables them. Vendored trees produce no extra
/// findings: govulncheck analyses the resolved module graph, not the file
/// tree, so a vendored copy contributes its version to the graph rather
/// than a second report. All findings locate at <c>go.mod</c> —
/// govulncheck's SARIF attaches each result to the manifest with a stub
/// line 1; call-site positions live in the report's stacks/codeFlows which
/// the shared parser does not read. <c>ExcludePaths</c> can therefore only
/// ever match <c>go.mod</c> itself — an all-or-nothing suppression, not a
/// path filter — so a <c>go.mod</c> entry is rejected outright as a
/// deterministic configuration failure rather than silently zeroing the
/// audit. Binary mode (<c>-mode binary</c>) is out of scope: the audit
/// subject is the worktree's source.</para>
///
/// <para><b>Network.</b> govulncheck queries the Go vulnerability database
/// (default <c>https://vuln.go.dev</c>, override via <c>DbUrl</c>) and the
/// <c>go</c>-driven package load may reach the module proxy, so the auditor
/// declares <see cref="AuditCapabilities.Network"/> — the database and
/// proxy hosts must be in the deployment's <c>AuditToolAllowedHosts</c>
/// egress list. Fully offline deployments set <c>Offline</c>, which drops
/// the capability (the auditor then joins the no-egress sandbox group) and
/// pins <c>GOPROXY=off</c> so the package load uses only the pre-seeded
/// module cache; <c>DbUrl</c> must point at a provisioned local database —
/// the database decides the verdict, so prefer <c>https://</c> or a
/// local/loopback source over a plaintext <c>http://</c> feed.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: govulncheck Go Vulnerability Scan",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "govulncheck",
    InstallHint = "provision the pinned govulncheck release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `go install "
        + "golang.org/x/vuln/cmd/govulncheck@v" + DefaultExpectedVersion + "` via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions; no distro apt package carries a pinned govulncheck")]
[CodeyBoxPluginRequiresTool(
    "go",
    InstallHint = "govulncheck shells out to `go env`/`go list` for module and package "
        + "loading — provision a Go toolchain in the sandbox baseline (the go.dev "
        + "release tarball, or a distro golang package recent enough for the audited "
        + "modules) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions")]
public sealed class GovulncheckAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.govulncheck";

    /// <summary>
    /// govulncheck release the invocation and its report are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.8.0";

    /// <summary>
    /// Scoped-config key for the vulnerability database URL (<c>-db</c>).
    /// Unset uses govulncheck's built-in default, the public database at
    /// <c>https://vuln.go.dev</c>. Set it to point at an operator-owned
    /// mirror or a provisioned local database for offline deployments. The
    /// database contents decide the audit verdict, so prefer an
    /// <c>https://</c> or local/loopback source — a plaintext
    /// <c>http://</c> feed offers no integrity for the evidence this
    /// auditor reports.
    /// </summary>
    public const string DbUrlKey = "DbUrl";

    /// <summary>
    /// Scoped-config key for govulncheck's scanning level (<c>-scan</c>):
    /// <c>symbol</c> (default — findings reachable through call analysis),
    /// <c>package</c> (imports of vulnerable packages), or <c>module</c>
    /// (vulnerable modules in the dependency graph). Invalid values are a
    /// deterministic configuration failure.
    /// </summary>
    public const string ScanLevelKey = "ScanLevel";

    /// <summary>
    /// Scoped-config key for the directory govulncheck changes to before
    /// scanning (<c>-C</c>), relative to the worktree root or absolute. Set
    /// it when the audited Go module lives in a repository subdirectory;
    /// patterns are then resolved from that directory.
    /// </summary>
    public const string ModuleDirectoryKey = "ModuleDirectory";

    /// <summary>
    /// Scoped-config key for a comma-separated list of Go build tags
    /// (<c>-tags</c>) controlling which files participate in the analysis.
    /// </summary>
    public const string BuildTagsKey = "BuildTags";

    /// <summary>
    /// Scoped-config boolean controlling whether test files are analyzed
    /// (<c>-test</c>). Default false — a vulnerability reachable only from
    /// test code is not shipped.
    /// </summary>
    public const string IncludeTestsKey = "IncludeTests";

    /// <summary>
    /// Scoped-config key for the package patterns govulncheck scans,
    /// comma-separated (default <c>./...</c> — the whole module). Patterns
    /// are positional arguments emitted after every flag, so entries must
    /// not start with <c>-</c> (they would be parsed as govulncheck flags).
    /// Incompatible with <see cref="ScanLevelKey"/> <c>module</c>, which
    /// accepts no patterns.
    /// </summary>
    public const string PatternsKey = "Patterns";

    /// <summary>
    /// Scoped-config boolean declaring that the deployment needs no
    /// network: drops the <see cref="AuditCapabilities.Network"/>
    /// requirement so the auditor joins the no-egress sandbox group, and
    /// pins <c>GOPROXY=off</c> for the tool process so the <c>go</c>-driven
    /// package load uses only the pre-seeded module cache. Point
    /// <see cref="DbUrlKey"/> at a provisioned local database first — a
    /// remote <c>-db</c> with <c>Offline</c> set fails loudly in the
    /// no-egress sandbox.
    /// </summary>
    public const string OfflineKey = "Offline";

    /// <summary>govulncheck scan levels the auditor accepts for <see cref="ScanLevelKey"/>.</summary>
    private static readonly IReadOnlySet<string> AllowedScanLevels =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "symbol", "package", "module",
        };

    private const string DefaultScanPattern = "./...";
    private const int MaxPatterns = 64;

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against govulncheck v1.8.0 (internal/scan): with
        // -format sarif a completed run ALWAYS exits 0 — clean or with
        // findings — and the SARIF report is the verdict. 3 is "vulns found"
        // for text output only; 2 is usage error; 1 is every run failure;
        // 126/127 cannot-execute. Only 0 is findings-producing.
        FindingsExitCodes = new HashSet<int> { 0 },
        // govulncheck's SARIF embeds per-finding call stacks and code flows,
        // so one result is far larger than a typical single-line finding.
        // The shared 1 MiB stream cap can clip a large report mid-document —
        // a clipped report fails closed as unparseable infrastructure — so
        // this auditor defaults to a larger (still bounded) capture.
        MaxOutputBytesPerStream = 8 * 1024 * 1024,
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _dbUrl = static () => null;
    private Func<string?> _scanLevel = static () => null;
    private Func<string?> _moduleDirectory = static () => null;
    private Func<IReadOnlyList<string>> _buildTags = static () => [];
    private Func<bool> _includeTests = static () => false;
    private Func<IReadOnlyList<string>> _patterns = static () => [];
    private Func<bool> _offline = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:govulncheck";

    /// <inheritdoc />
    public override AuditCapabilities Required => _offline() ? AuditCapabilities.None : AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "govulncheck";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from govulncheck's SARIF level vocabulary to
    /// <see cref="AuditSeverity"/>: <c>error</c> (a vulnerable symbol is
    /// called) fails the audit, <c>warning</c> (vulnerable package imported,
    /// symbol not reached) is advisory, <c>note</c> (vulnerable module in
    /// the graph only) is informational. Raw tool levels never reach
    /// findings.
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
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-version"], ExtractGovulncheckVersion);

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? BuildToolEnvironment(ExternalToolAuditorOptions options)
        => _offline()
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["GOPROXY"] = "off" }
            : null;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        ThrowIfExcludePathsNeutersAudit(options);

        var scanLevel = ResolveScanLevel();
        var patterns = ResolvePatterns(scanLevel);

        // Flags must precede patterns: Go's flag parser stops at the first
        // positional argument, so anything after the first pattern is itself
        // a pattern — including operator ExtraArguments, which therefore act
        // as additional package patterns, never as flags.
        var args = new List<string>
        {
            "-format", "sarif",
            "-scan", scanLevel,
        };

        AddValueFlag(args, "-db", _dbUrl());
        AddValueFlag(args, "-C", _moduleDirectory());

        var tags = _buildTags()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        if (tags.Count > 0)
        {
            args.Add("-tags");
            args.Add(string.Join(",", tags));
        }

        if (_includeTests())
            args.Add("-test");

        args.AddRange(patterns);
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _dbUrl = () => scoped[DbUrlKey];
        _scanLevel = () => scoped[ScanLevelKey];
        _moduleDirectory = () => scoped[ModuleDirectoryKey];
        _buildTags = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[BuildTagsKey]);
        _includeTests = () => bool.TryParse(scoped[IncludeTestsKey], out var tests) && tests;
        _patterns = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[PatternsKey]);
        _offline = () => bool.TryParse(scoped[OfflineKey], out var offline) && offline;
        context.Logger.LogInformation(
            "GovulncheckAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// govulncheck-specific precondition on the live path: the <c>go</c>
    /// toolchain the scanner shells out to, probed explicitly — its absence
    /// otherwise surfaces as a misleading "no go.mod" run failure. The
    /// pinned tool version is declared via <see cref="VersionPin"/> with a
    /// banner-anchored extractor, because <c>govulncheck -version</c> prints
    /// the Go toolchain's version before the scanner's. Both failures are
    /// infrastructure — never a pass, never a finding.
    /// </summary>
    protected override Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ThrowIfBinaryMissingAsync(
            sandbox,
            workingDirectory,
            "go",
            options,
            ct,
            purpose: "audit tool 'govulncheck' requires the 'go' toolchain — govulncheck shells out "
                + "to `go env`/`go list` for module and package loading");

    /// <summary>
    /// Extracts the scanner's version from the <c>govulncheck -version</c>
    /// banner — the <c>govulncheck@v…</c> token on the <c>Scanner:</c> line.
    /// The banner's first semver token is the Go toolchain's
    /// (<c>Go: go1.x.y</c>), so <see cref="ExtractToolVersion"/> on the whole
    /// output would return the wrong component; the
    /// <c>govulncheck@</c>-anchored substring is extracted instead. Returns
    /// null when the banner carries no such token.
    /// </summary>
    internal static string? ExtractGovulncheckVersion(string output)
    {
        const string marker = "govulncheck@";
        ArgumentNullException.ThrowIfNull(output);
        var index = output.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : ExtractToolVersion(output[(index + marker.Length)..]);
    }

    private string ResolveScanLevel()
    {
        var configured = _scanLevel();
        var level = string.IsNullOrWhiteSpace(configured)
            ? "symbol"
            : configured.Trim().ToLowerInvariant();
        if (!AllowedScanLevels.Contains(level))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an invalid {ScanLevelKey} "
                + $"('{TruncateForMessage(configured)}'); expected one of: "
                + $"{string.Join(", ", AllowedScanLevels.Order())}. Set "
                + $"CodeyBox:Plugins:{PluginId}:{ScanLevelKey} to a supported scan level.")
            { IsDeterministic = true };
        return level;
    }

    // govulncheck attaches every SARIF result to go.mod:1 (see the class
    // doc), so an ExcludePaths entry that normalizes to 'go.mod' is not a
    // path filter — it drops the entire report and the audit passes on
    // nothing. Reject it as a deterministic configuration failure rather
    // than silently zeroing the audit.
    private void ThrowIfExcludePathsNeutersAudit(ExternalToolAuditorOptions options)
    {
        foreach (var entry in options.ExcludePaths)
        {
            if (string.Equals(NormalizeExcludePathEntry(entry), "go.mod", StringComparison.Ordinal))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' was configured with ExcludePaths 'go.mod', "
                    + "but every govulncheck finding locates at go.mod — the entry would drop the "
                    + "whole report and pass silently. To stand the auditor down, remove it from "
                    + "CodeyBox:Plugins:Enabled or the project's Audit.Custom list.")
                { IsDeterministic = true };
        }
    }

    private IReadOnlyList<string> ResolvePatterns(string scanLevel)
    {
        var configured = _patterns()
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p.Trim())
            .ToList();

        // Module-level scanning takes no patterns — govulncheck rejects them
        // as a usage error. Fail earlier and clearer as a deterministic
        // configuration failure.
        if (string.Equals(scanLevel, "module", StringComparison.Ordinal))
        {
            if (configured.Count > 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has {PatternsKey} configured alongside "
                    + $"{ScanLevelKey} 'module', but govulncheck's module-level scan accepts no "
                    + "package patterns. Drop one of the two scoped settings.")
                { IsDeterministic = true };
            return [];
        }

        if (configured.Count == 0)
            return [DefaultScanPattern];

        if (configured.Count > MaxPatterns)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with {configured.Count} "
                + $"{PatternsKey} entries, exceeding the bound of {MaxPatterns}.")
            { IsDeterministic = true };

        foreach (var pattern in configured)
        {
            if (pattern.StartsWith('-'))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' has a {PatternsKey} entry "
                    + $"('{TruncateForMessage(pattern)}') starting with '-' — patterns are positional "
                    + "arguments emitted after every flag, so a leading-dash entry would be parsed "
                    + "as a govulncheck flag. Use the dedicated scoped keys for flags.")
                { IsDeterministic = true };
        }

        return configured;
    }
}
