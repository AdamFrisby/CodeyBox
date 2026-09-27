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
/// knobs, plus a pinned tool-version precondition via
/// <see cref="VerifyToolAsync"/>.
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
/// <para><b>Version pin — custom, not the shared pin.</b> govulncheck's
/// <c>-version</c> banner prints <c>Go: go&lt;toolchain-version&gt;</c>
/// before <c>Scanner: govulncheck@v&lt;version&gt;</c>, and the shared
/// <see cref="ToolVersionPin"/> extraction takes the first semver token —
/// so it would verify the Go toolchain's version, not govulncheck's, and
/// fail closed on every correctly provisioned baseline. The pin is instead
/// enforced in <see cref="VerifyToolAsync"/>, which extracts the
/// <c>govulncheck@v…</c> token specifically (see
/// <see cref="ExtractGovulncheckVersion"/>). A missing binary, an
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
/// the shared parser does not read — so <c>ExcludePaths</c> has nothing to
/// match and is inert. Binary mode (<c>-mode binary</c>) is out of scope:
/// the audit subject is the worktree's source.</para>
///
/// <para><b>Network.</b> govulncheck queries the Go vulnerability database
/// (default <c>https://vuln.go.dev</c>, override via <c>DbUrl</c>) and the
/// <c>go</c>-driven package load may reach the module proxy, so the auditor
/// declares <see cref="AuditCapabilities.Network"/> — the database and
/// proxy hosts must be in the deployment's <c>AuditToolAllowedHosts</c>
/// egress list. The declared capability permits egress, it does not force
/// it: offline deployments point <c>DbUrl</c> at a provisioned local
/// database and pre-seed the module cache.</para>
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
    /// mirror or a provisioned local database for offline deployments.
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

    /// <inheritdoc />
    public override string Name => "codeybox:govulncheck";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

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
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
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
        context.Logger.LogInformation(
            "GovulncheckAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// govulncheck-specific preconditions on the live path: the <c>go</c>
    /// toolchain the scanner shells out to (probed explicitly — its absence
    /// otherwise surfaces as a misleading "no go.mod" run failure), and the
    /// pinned tool version, which the shared <see cref="ToolVersionPin"/>
    /// cannot express because <c>govulncheck -version</c> prints the Go
    /// toolchain's version before the scanner's and the shared extractor
    /// takes the first semver token. Both failures are infrastructure —
    /// never a pass, never a finding.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        await ThrowIfGoMissingAsync(sandbox, workingDirectory, options, ct).ConfigureAwait(false);
        await VerifyPinnedVersionAsync(sandbox, workingDirectory, tool, options, ct).ConfigureAwait(false);
    }

    private async Task ThrowIfGoMissingAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            "govulncheck",
            "go toolchain check",
            new SandboxExec
            {
                Argv = ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "sh", "go"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                "could-not-verify: audit tool 'govulncheck' could not check for the 'go' toolchain: "
                + "the sandbox exec transport was unavailable.");
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                "could-not-verify: audit tool 'govulncheck' requires the 'go' toolchain in the audit "
                + "sandbox (govulncheck shells out to `go env`/`go list` for module and package "
                + "loading). Install it in the sandbox baseline; the check did not run, so this is "
                + "infrastructure, not a verdict on the diff.");
    }

    private async Task VerifyPinnedVersionAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = _expectedVersion();
        var expected = ExtractToolVersion(
            string.IsNullOrWhiteSpace(configured) ? DefaultExpectedVersion : configured.Trim());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable {ToolVersionPin.ExpectedVersionKey} "
                + $"('{SingleLine(configured ?? string.Empty)}'); set CodeyBox:Plugins:{PluginId}:"
                + $"{ToolVersionPin.ExpectedVersionKey} to a govulncheck release such as "
                + $"'{DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "version check",
            new SandboxExec
            {
                Argv = [tool, "-version"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        var reported = result.Stdout is null ? null : ExtractGovulncheckVersion(result.Stdout);
        if (result.ExecutionUnavailable || result.ExitCode != 0 || reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + $"a missing or foreign '{tool}' is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' is version {reported}, but this auditor is "
                + $"pinned to {expected}. A different release changes the tool's checks and its "
                + $"findings; provision the pinned release or set {ToolVersionPin.ExpectedVersionKey} "
                + "to the version you provisioned.")
            { IsDeterministic = true };
    }

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
                + $"('{SingleLine(configured ?? string.Empty)}'); expected one of: "
                + $"{string.Join(", ", AllowedScanLevels.Order())}. Set "
                + $"CodeyBox:Plugins:{PluginId}:{ScanLevelKey} to a supported scan level.")
            { IsDeterministic = true };
        return level;
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
                    + $"('{SingleLine(pattern)}') starting with '-' — patterns are positional "
                    + "arguments emitted after every flag, so a leading-dash entry would be parsed "
                    + "as a govulncheck flag. Use the dedicated scoped keys for flags.")
                { IsDeterministic = true };
        }

        return configured;
    }

    private static void AddValueFlag(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(flag);
        args.Add(value.Trim());
    }
}
