using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GosecAuditorPlugin;

/// <summary>
/// Go security auditor wrapping the gosec CLI (<c>gosec</c>) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, SARIF
/// parsing, severity mapping, exit-code classification, and per-auditor
/// configuration. This class adds the gosec invocation shape
/// (<c>gosec -fmt json -out &lt;scratch&gt; -stdout -verbose sarif -nosec
/// ./...</c> — SARIF on stdout for the parser, JSON to a file as the
/// completeness oracle), the <see cref="GosecSarifOutputParser"/> severity
/// recovery, the declared severity map over gosec's HIGH/MEDIUM/LOW
/// vocabulary, the pinned tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and a precondition probe
/// for the <c>go</c> toolchain gosec shells out to.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> gosec severities go through a declared map, never raw:
/// <c>HIGH</c> → <see cref="AuditSeverity.Error"/> (fails the audit);
/// <c>MEDIUM</c> → <see cref="AuditSeverity.Warning"/> (advisory);
/// <c>LOW</c> → <see cref="AuditSeverity.Info"/> (informational); anything
/// unrecognised → <see cref="AuditSeverity.Warning"/>. The native token is
/// recovered from each SARIF rule's <c>properties.tags</c> by <see
/// cref="GosecSarifOutputParser"/> because gosec flattens MEDIUM and HIGH
/// both to SARIF <c>error</c>; when a rule carries no severity tag the
/// reported SARIF level is what flows into the map (<c>error</c> → Error —
/// degraded toward blocking, never the other way).
/// <c>MinimumSeverity</c> can only drop findings, it never raises them.
/// The auditor is therefore a merge gate for high-severity Go security
/// issues, not a blocker on every low hint.</para>
///
/// <para><b>Exit-code convention (verified against gosec 2.29.0
/// <c>cmd/gosec/main.go</c> — do not assume the common convention
/// holds).</b> gosec has two verdict exits plus a usage exit: <c>0</c> =
/// scan completed with no unsuppressed issues AND no processing errors;
/// <c>1</c> = at least one unsuppressed issue <em>or</em> processing
/// error, and also every startup failure the main flow can return
/// (unreadable <c>-conf</c>, no packages found, analyzer failure, report
/// write failure); <c>2</c> = flag-parse/usage error — unrecognized flags
/// never reach the main flow because Go's default flagset runs
/// <c>flag.ExitOnError</c>, which calls <c>os.Exit(2)</c>. Only
/// <c>{0, 1}</c> is declared findings-producing; <c>126</c>/<c>127</c>
/// cannot-execute, the usage exit, and everything else is infrastructure.
/// Exit 1 alone cannot distinguish
/// "ran and found problems" from "could not run", and the SARIF report
/// carries no record of processing errors (<c>report/sarif</c> iterates
/// <c>data.Issues</c> only), so the audit writes the same
/// <c>ReportInfo</c> as JSON to a per-run file and reads its
/// <c>"Golang errors"</c> section back through a bounded sandbox read: a
/// non-empty map means one or more packages failed to load or type-check
/// and the SARIF covers only part of the tree — a partial scan fails
/// closed as infrastructure even when it also produced findings. An
/// exit-1 run whose JSON oracle shows no errors is a clean findings
/// verdict; an exit-1 run with neither findings nor errors is an
/// impossible contract state and fails closed in the parser. Startup
/// exits-1 print no report at all (they return before the writers run),
/// so the oracle's unreadable/missing-file path classifies them as
/// infrastructure too.</para>
///
/// <para><b>Version pin.</b> A scanner's rules and output shape change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes
/// <c>gosec -version</c> before every run (the CLI prints
/// <c>Version: X.Y.Z</c> followed by Git tag and build date); a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Argument layout.</b> gosec uses Go's stdlib flag package,
/// which stops parsing at the first positional argument — every flag
/// precedes the package patterns, and the base-appended
/// <c>ExtraArguments</c> land after them where they act as additional
/// package patterns (a nonexistent pattern fails the run loudly as
/// infrastructure, so a flag-shaped entry can never be silently honored —
/// it is also rejected deterministically in <see
/// cref="VerifyToolAsync"/> with a pointer at the dedicated scoped keys).
/// Every positional entry — configured or extra — is also restricted to
/// <c>./</c>-prefixed file-tree patterns (or <c>.</c>), because gosec
/// resolves go/packages patterns, not paths: a bare <c>all</c> or
/// <c>net/http</c> would scan GOROOT/module-cache source outside the
/// audited worktree. Positional patterns also seed the SARIF path
/// relativization (<c>getRootPaths(flag.Args())</c>), so <c>./...</c> at
/// the worktree root keeps every reported URI repo-relative.</para>
///
/// <para><b>Repository-controlled suppression: inert by construction.</b>
/// gosec honors <c>#nosec</c> comments and <c>//gosec:disable</c>
/// annotations authored inside the audited repository — but gosec's SARIF
/// and JSON writers do not drop suppressed issues (<c>filterIssues</c>
/// keeps NoSec issues in the reported set; only the non-SARIF/JSON
/// renderers filter them), so a suppression comment cannot hide a finding
/// even if the operator asked it to. The scan still passes <c>-nosec</c>
/// so the posture is explicit and version-proof rather than relying on
/// that report detail. gosec reads no configuration file from the
/// repository — its only config surface is the operator-supplied
/// <c>-conf</c> flag — so there is no repository-file gate to fail closed
/// on; the <c>-ai-*</c> autofix feature's ambient activation path is
/// closed by unsetting <c>GOSEC_AI_PROVIDER</c>,
/// <c>GOSEC_AI_API_KEY</c>, and <c>GOSEC_AI_BASE_URL</c> for the tool
/// process (an enabled provider would ship audited source to an external
/// API inside the audit boundary).</para>
///
/// <para><b>Go toolchain requirement.</b> gosec loads and type-checks
/// packages through <c>go list</c> (go/packages — see
/// <c>analyzer.go</c>), so a Go toolchain must be on PATH and the module
/// graph resolvable (vendored dependencies or a pre-seeded module cache —
/// the auditor declares <see cref="AuditCapabilities.None"/>, no network).
/// The offline contract is enforced on the tool process, not assumed:
/// <c>GOPROXY=off</c> and <c>GOTOOLCHAIN=local</c> are pinned in
/// <see cref="BuildToolEnvironment"/> so a repo-controlled
/// <c>go.mod</c>/<c>toolchain</c> directive cannot drive a module or
/// toolchain download inside the audit boundary on an egress-capable
/// sandbox profile, and <c>GOFLAGS</c>/<c>GOPACKAGESDRIVER</c> are removed
/// because ambient values can inject flags or a package-loading driver.
/// A missing <c>go</c> is probed for explicitly; a module graph that
/// cannot resolve lands in the errors map and fails closed through the
/// oracle path above — never a pass.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: gosec Go Security",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "gosec",
    InstallHint = "provision the pinned gosec release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `go install "
        + "github.com/securego/gosec/v2/cmd/gosec@v" + DefaultExpectedVersion
        + "`, or the checksum-verified gosec_" + DefaultExpectedVersion
        + "_linux_<arch> release tarball — no distro apt package carries a pinned "
        + "gosec — through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions")]
[CodeyBoxPluginRequiresTool(
    "go",
    InstallHint = "gosec shells out to `go list` for module and package loading — "
        + "provision a Go toolchain in the sandbox baseline (the go.dev release "
        + "tarball, or a distro golang package recent enough for the audited "
        + "modules) via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions")]
public sealed class GosecAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.gosec";

    /// <summary>
    /// gosec release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.29.0";

    /// <summary>
    /// Scoped-config key for the comma-separated gosec package patterns to
    /// scan (default <c>./...</c> — the whole tree). Entries are positional
    /// arguments emitted after every flag and must be <c>./</c>-prefixed
    /// file-tree patterns (or <c>.</c>): gosec positional arguments are
    /// go/packages patterns, not filesystem paths, so a bare import-path
    /// spelling such as <c>all</c>, <c>std</c>, or <c>net/http</c> would
    /// resolve Go distribution or module-cache source outside the audited
    /// worktree and is rejected along with leading-dash, absolute, and
    /// <c>..</c>-carrying entries. Report paths are relativized to the
    /// longest matching pattern root, so narrowing below the worktree root
    /// makes findings carry target-root-relative locations.
    /// </summary>
    internal const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key for an operator-owned gosec <c>-conf</c> JSON file
    /// (rule settings and globals such as rule selection). The value is
    /// canonicalized once per run in the sandbox and the canonical path is
    /// what reaches argv — so the file handed to gosec is exactly the file
    /// the outside-the-worktree gate checked. The run fails when the path
    /// resolves inside the audited worktree; a relative path therefore
    /// always fails, because relative resolution lands inside it.
    /// </summary>
    internal const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config boolean controlling whether <c>*_test.go</c> files are
    /// analyzed (<c>-tests</c>). Default false — gosec's own default — since
    /// issues reachable only from test code are not shipped; the harness
    /// still scans their non-test siblings.
    /// </summary>
    internal const string IncludeTestsKey = "IncludeTests";

    /// <summary>
    /// Scoped-config boolean controlling whether generated files
    /// (<c>// Code generated ... DO NOT EDIT</c>) are analyzed. Default
    /// false: the scan passes <c>-exclude-generated</c> because findings in
    /// generated code belong to the generator, not the diff under audit.
    /// </summary>
    internal const string IncludeGeneratedCodeKey = "IncludeGeneratedCode";

    /// <summary>
    /// Scoped-config key for a comma-separated list of Go build tags
    /// (<c>-tags</c>) controlling which files participate in the analysis.
    /// </summary>
    internal const string BuildTagsKey = "BuildTags";

    /// <summary>
    /// gosec env vars that would enable the AI-autofix feature from the
    /// ambient environment: an enabled provider ships audited source to an
    /// external API inside the audit boundary, so the names are removed
    /// from the tool process environment (flag-passed providers are
    /// unreachable too — operator ExtraArguments land after the positional
    /// patterns where flags cannot parse). <c>GOFLAGS</c> and
    /// <c>GOPACKAGESDRIVER</c> ride along: they steer the
    /// <c>go list</c>/go/packages machinery gosec shells out to — an
    /// ambient <c>GOFLAGS</c> can inject flags (e.g. <c>-overlay</c>,
    /// <c>-toolexec</c>) and an ambient <c>GOPACKAGESDRIVER</c> swaps
    /// package loading for an arbitrary binary — and neither has a
    /// legitimate role inside an audit sandbox.
    /// </summary>
    private static readonly IReadOnlyList<string> ToolEnvironmentRemovals =
    [
        "GOSEC_AI_PROVIDER",
        "GOSEC_AI_API_KEY",
        "GOSEC_AI_BASE_URL",
        "GOFLAGS",
        "GOPACKAGESDRIVER",
    ];

    /// <summary>
    /// Pinned environment for the declared-offline scan: the auditor
    /// declares <see cref="AuditCapabilities.None"/>, so the values that
    /// would let a repo-controlled <c>go.mod</c> require/toolchain
    /// directive drive a fetch inside the audit boundary are closed at the
    /// tool process rather than delegated to the sandbox's egress profile.
    /// <c>GOPROXY=off</c> resolves modules only from the cache or
    /// <c>vendor/</c> — an unresolvable graph fails closed through the
    /// <c>"Golang errors"</c> oracle either way — and
    /// <c>GOTOOLCHAIN=local</c> stops a <c>go.mod toolchain</c> line from
    /// downloading and executing a repo-selected toolchain.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ToolEnvironmentPins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GOPROXY"] = "off",
            ["GOTOOLCHAIN"] = "local",
        };

    /// <summary>Leaf name of the per-run JSON completeness report inside the scratch directory.</summary>
    private const string ErrorsReportFileName = "gosec-report.json";

    /// <summary>Upper bound on operator-configured <see cref="TargetsKey"/> entries.</summary>
    private const int MaxTargets = 64;

    private const string DefaultScanTarget = "./...";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against gosec 2.29.0 cmd/gosec/main.go: 0 = completed
        // with no unsuppressed issues and no processing errors;
        // FindingsOrErrorsExitCode = issues or processing errors or a
        // startup failure the main flow can return — disambiguated per run
        // by the "Golang errors" oracle, not assumed. Every other exit —
        // including the flag.ExitOnError usage exit — means "could not
        // run".
        FindingsExitCodes = new HashSet<int> { 0, GosecSarifOutputParser.FindingsOrErrorsExitCode },
        // Findings inside vendored/dependency trees describe upstream code,
        // not the change under audit — noise that trains operators to ignore
        // the auditor. gosec's own default -exclude-dir already skips
        // vendor/ and .git at scan time; this filter covers the findings
        // surface regardless. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<string?> _configPath = static () => null;
    private Func<bool> _includeTests = static () => false;
    private Func<bool> _includeGeneratedCode = static () => false;
    private Func<IReadOnlyList<string>> _buildTags = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:gosec";

    /// <inheritdoc />
    protected override string ToolName => "gosec";

    /// <inheritdoc />
    /// <summary>
    /// gosec's SARIF writer flattens MEDIUM and HIGH both to
    /// <c>error</c>; <see cref="GosecSarifOutputParser"/> recovers the
    /// native severity from each rule's <c>properties.tags</c> before the
    /// shared parser reads the shape.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new GosecSarifOutputParser();

    /// <summary>
    /// Declared mapping from gosec's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. gosec ranks each issue <c>HIGH</c>,
    /// <c>MEDIUM</c>, or <c>LOW</c>; the native tokens are recovered from
    /// rule metadata and translated here (plus the SARIF levels and common
    /// scanner tokens, for reports a rule tag could not describe) so a
    /// severity means the same thing regardless of which scanner produced
    /// it — raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["-version"]);

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string>? BuildToolEnvironment(
        ExternalToolAuditorOptions options)
        => ToolEnvironmentPins;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(ExternalToolAuditorOptions options)
        => ToolEnvironmentRemovals;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Structured argv, never a shell string. Flag order matters: Go's
        // stdlib flag parser stops at the first positional argument, so
        // this method emits flags only — ResolveContextArgumentsAsync
        // appends the operator -conf and the package patterns (its return
        // lands between these flags and the operator ExtraArguments, which
        // therefore act as additional patterns, never as flags).
        var args = new List<string>
        {
            // The JSON render of the same ReportInfo is the completeness
            // oracle ("Golang errors" — the section the SARIF writer
            // drops): written to the per-run scratch directory the base
            // mints, and read back through ResolveParserInputAsync.
            "-fmt", "json",
            "-out", Path.Combine(PerRunTempDirectoryPath, ErrorsReportFileName),
            // ... while SARIF — the report of record the parser reads —
            // goes to stdout: -stdout prints as well as saves, and
            // -verbose overrides the printed format only.
            "-stdout",
            "-verbose", "sarif",
            // The audit subject authors #nosec / //gosec:disable comments.
            // gosec's SARIF/JSON reports keep suppressed issues anyway, so
            // suppression can never hide a finding — the flag makes the
            // posture explicit instead of relying on that report detail.
            "-nosec",
        };

        if (!_includeGeneratedCode())
        {
            // Files carrying the "// Code generated ... DO NOT EDIT"
            // marker are machine-written: findings there belong to the
            // generator, not the change under audit.
            args.Add("-exclude-generated");
        }

        if (_includeTests())
            args.Add("-tests");

        var tags = _buildTags()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        if (tags.Count > 0)
        {
            args.Add("-tags");
            args.Add(ValidatedArgumentValue(string.Join(",", tags), BuildTagsKey));
        }

        return args;
    }

    /// <inheritdoc />
    /// <summary>
    /// Emits the argv tail that needs a per-run sandbox probe — the
    /// operator <c>-conf</c> and the package patterns, in that order so
    /// the flag still precedes the first positional. The configured
    /// ConfigPath is canonicalized exactly once here and the CANONICAL
    /// path is emitted: the value the outside-the-worktree gate checked
    /// is the same string instance gosec receives — a scoped-config reload
    /// between argv build and a later verification hook cannot leave an
    /// ungated path in the frozen argv (the seam exists for precisely this
    /// kind of argv entry; a VerifyToolAsync re-read of the hot-reloadable
    /// key would gate a different read than the one consumed).
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Target validation is pure — run it before the canonicalization
        // probe so a bad Targets config fails without a wasted exec.
        var targets = ResolveTargets();
        var args = new List<string>();
        var configPath = ValidatedScopedValue(_configPath(), ConfigPathKey);
        if (configPath is not null)
        {
            var canonical = await CanonicalizeOutsideWorktreeAsync(
                sandbox, workingDirectory, configPath, ConfigPathKey, options, ct)
                .ConfigureAwait(false);
            args.Add("-conf");
            args.Add(canonical);
        }

        args.AddRange(targets);
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _configPath = () => scoped[ConfigPathKey];
        _includeTests = () => bool.TryParse(scoped[IncludeTestsKey], out var tests) && tests;
        _includeGeneratedCode = () =>
            bool.TryParse(scoped[IncludeGeneratedCodeKey], out var include) && include;
        _buildTags = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[BuildTagsKey]);
        context.Logger.LogInformation(
            "GosecAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// gosec-specific preconditions beyond the base's presence and
    /// pinned-version checks, all failing closed as infrastructure before
    /// the scan runs: the <c>go</c> toolchain gosec shells out to must
    /// exist (its absence otherwise surfaces only as a per-package load
    /// error), <c>ExtraArguments</c> are validated as additional package
    /// patterns (flag-shaped entries can never parse after the positional
    /// patterns, and bare import-path spellings would scan code outside
    /// the audited worktree), and the per-run scratch directory the
    /// <c>-out</c> report writes into is created. The operator
    /// <c>-conf</c> gate is not re-checked here: it ran when the argv was
    /// built (<see cref="ResolveContextArgumentsAsync"/>) so the value
    /// frozen into argv is the value the gate saw.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        await ThrowIfBinaryMissingAsync(
            sandbox,
            workingDirectory,
            "go",
            options,
            ct,
            purpose: "audit tool 'gosec' requires the 'go' toolchain — gosec loads and "
                + "type-checks packages through `go list`")
            .ConfigureAwait(false);

        RejectFlagShapedExtraArguments(options);
        RejectOutOfTreeExtraArgumentPatterns(options);

        // The -out report lands in the per-run scratch directory; the base
        // mints the path but does not create it, and gosec's report writer
        // cannot create missing parents.
        var mkdir = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "report directory setup",
            new SandboxExec
            {
                Argv = ["mkdir", "-p", "--", PerRunTempDirectoryPath],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (mkdir.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(mkdir.ExitCode);
        if (mkdir.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not create its report directory "
                + $"(exit {mkdir.ExitCode}) — the scan would have nowhere to write the "
                + "completeness report, so this is infrastructure, not a verdict on the diff.",
                mkdir.ExitCode,
                mkdir.Stderr);
    }

    /// <inheritdoc />
    /// <summary>
    /// Disambiguates gosec's merged exit-1 convention before the SARIF is
    /// parsed. The SARIF report carries no record of processing errors
    /// (the formatter iterates <c>data.Issues</c> only), so the JSON
    /// render of the same <c>ReportInfo</c> — written to the per-run
    /// scratch file by <c>-out</c> — is read back through the base's
    /// bounded report read. A non-empty <c>"Golang errors"</c> map means
    /// one or more packages failed to load or type-check and the SARIF
    /// covers only part of the tree: a partial scan fails closed as
    /// infrastructure even when it also produced findings, because its
    /// verdict cannot certify the diff.
    /// </summary>
    protected override async Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        IReadOnlyList<string> argv,
        string? scanRoot,
        CancellationToken ct)
    {
        var reportPath = Path.Combine(PerRunTempDirectoryPath, ErrorsReportFileName);
        var oracle = await ReadReportFileParseInputAsync(
            sandbox,
            workingDirectory,
            tool,
            options,
            result,
            reportPath,
            scanRoot,
            ct,
            oversizedScopeHint: "with narrower Targets")
            .ConfigureAwait(false);

        var errorCount = CountProcessingErrors(oracle.Stdout, tool, result.ExitCode);
        if (errorCount > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' recorded processing errors in "
                + $"{errorCount} package(s) alongside its report (exit {result.ExitCode}) — "
                + "gosec skips packages it cannot load or type-check, so the findings cover only "
                + "part of the audited tree. Fix the failing packages or narrow Targets; a "
                + "partial scan is infrastructure, not a verdict on the diff.");

        return new ExternalToolParseInput(
            tool,
            result.Stdout,
            result.Stderr,
            result.ExitCode,
            ScanRoot: scanRoot,
            WorkingDirectory: workingDirectory);
    }

    /// <summary>
    /// Counts the entries in the JSON side-report's
    /// <c>"Golang errors"</c> map (one key per file or package gosec could
    /// not process). A report that is not parseable JSON, or lacks the
    /// section entirely, is a contract violation — the tool was invoked to
    /// produce exactly this document, so a malformed one fails closed
    /// rather than reading as "no errors".
    /// </summary>
    private static int CountProcessingErrors(string json, string tool, int exitCode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' wrote a completeness report that is not "
                + $"valid JSON (exit {exitCode}): {SingleLine(ex.Message)}",
                ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Golang errors"u8, out var errors))
            {
                if (errors.ValueKind == JsonValueKind.Null)
                    return 0;
                if (errors.ValueKind == JsonValueKind.Object)
                {
                    var count = 0;
                    foreach (var _ in errors.EnumerateObject())
                        count++;
                    return count;
                }
            }

            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' wrote a completeness report without the "
                + $"'Golang errors' section (exit {exitCode}) — scan completeness cannot be "
                + "verified, so this is infrastructure, not a verdict on the diff.");
        }
    }

    // Go's flag parser stops at the first positional argument — the scan
    // targets come last in argv, so an ExtraArguments entry starting with
    // a dash is never a gosec flag; it is swallowed into flag.Args() and
    // would die at scan time as a nonexistent package path. Reject it
    // deterministically with the real explanation instead.
    private void RejectFlagShapedExtraArguments(ExternalToolAuditorOptions options)
    {
        var offenders = options.ExtraArguments
            .Where(static arg => arg.Length > 0 && IsParameterDash(arg[0]))
            .ToList();
        if (offenders.Count == 0)
            return;

        throw new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' was configured with flag-shaped ExtraArguments "
            + $"('{string.Join("', '", offenders.Select(TruncateForMessage))}') — gosec's flag "
            + "parser takes no flags after the positional package patterns, and ExtraArguments "
            + "always append after them, so the entries would be misread as scan targets. Use the "
            + $"dedicated scoped keys under CodeyBox:Plugins:{PluginId} ({TargetsKey}, "
            + $"{ConfigPathKey}, {IncludeTestsKey}, {IncludeGeneratedCodeKey}, {BuildTagsKey}, "
            + "MinimumSeverity, IncludedRules, ExcludedRules, ExcludePaths); flag-free "
            + "'./'-prefixed entries still work as additional package patterns.")
        { IsDeterministic = true };
    }

    // Flag-free ExtraArguments are appended after the package patterns and
    // gosec reads them as more go/packages patterns — where a bare
    // import-path spelling ('all', 'std', 'net/http', 'example.com/x/...')
    // resolves GOROOT or module-cache source outside the audited worktree
    // and the findings it produces carry no repo root to relativize to.
    // They get the same containment guard as configured Targets.
    private static void RejectOutOfTreeExtraArgumentPatterns(ExternalToolAuditorOptions options)
    {
        foreach (var arg in options.ExtraArguments)
        {
            if (arg.Length > 0 && IsParameterDash(arg[0]))
                continue; // reported by RejectFlagShapedExtraArguments
            ValidatedGoPackagePattern(arg, "ExtraArguments");
        }
    }

    private IReadOnlyList<string> ResolveTargets()
    {
        var configured = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        if (configured.Count == 0)
            return [DefaultScanTarget];
        if (configured.Count > MaxTargets)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with {configured.Count} "
                + $"{TargetsKey} entries, exceeding the bound of {MaxTargets}.")
            { IsDeterministic = true };

        // Positional patterns must stay inside the audited worktree.
        var resolved = new List<string>(configured.Count);
        foreach (var target in configured)
            resolved.Add(ValidatedGoPackagePattern(target, TargetsKey));
        return resolved;
    }

    // gosec positional arguments are go/packages patterns, not filesystem
    // paths, so the repo-relative guard alone cannot express containment:
    // a bare 'all', 'std', 'net/http', or 'example.com/x/...' passes it
    // (not rooted, no '..', no leading dash) yet resolves into GOROOT or
    // module-cache source outside the worktree — findings on out-of-tree
    // code. Only './'-prefixed file-tree patterns (and '.') keep the scan
    // — and the SARIF path relativization rooted at flag.Args() — inside
    // the tree under audit.
    private static string ValidatedGoPackagePattern(string value, string source)
    {
        var validated = ValidatedRepoRelativeTarget(value, source);
        if (!string.Equals(validated, ".", StringComparison.Ordinal)
            && !validated.StartsWith("./", StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(validated)}') "
                + "must be a './'-prefixed file-tree package pattern ('./...', './pkg/...', or "
                + "'.') — gosec resolves bare import-path spellings into GOROOT or module-cache "
                + "source outside the audited worktree.")
            { IsDeterministic = true };
        return validated;
    }
}
