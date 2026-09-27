using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SwiftlintAuditorPlugin;

/// <summary>
/// Linting auditor wrapping <c>swiftlint</c> (Swift analysis) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the report selection (SwiftLint's built-in
/// <c>--reporter json</c>, parsed by <see cref="SwiftlintJsonOutputParser"/>
/// — no extra formatter package to pin), the scan-root resolution the parser
/// needs to relativize the report's absolute <c>file</c> paths (via
/// <see cref="ResolveScanRootAsync"/>), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> SwiftLint violations at severity <c>Error</c> map to
/// <see cref="AuditSeverity.Error"/> and fail the audit; violations at
/// <c>Warning</c> map to <see cref="AuditSeverity.Warning"/> and are
/// advisory. Which severity a rule reports is decided by the configuration
/// in force — the audited repository's <c>.swiftlint.yml</c> by default, or
/// an operator-pinned config via <c>ConfigPath</c>/<c>--config</c>.
/// <c>MinimumSeverity</c> only drops findings, it never raises them; to make
/// warnings blocking, pass <c>--strict</c> in <c>ExtraArguments</c> (the tool
/// then upgrades warnings to errors in the report itself).</para>
///
/// <para><b>Exit-code convention (verified against SwiftLint 0.65.1 — notably
/// NOT the common "0 clean / 1 findings / 2 error" table).</b> <c>0</c> =
/// linted clean or warnings only (warnings do not fail the run by default);
/// <c>2</c> = linted with error-level violations. Both emit the JSON report
/// on stdout, so both are findings-producing verdicts. <c>1</c> = no
/// lintable files at the scanned paths (stdout carries no report) — the scan
/// did not analyze anything, so like golangci-lint's no-go-files exit this
/// is infrastructure, not a clean pass. <c>64</c> = illegal command-line
/// parameters (<c>EX_USAGE</c>). A <c>SIGABRT</c> crash (exit <c>134</c>)
/// covers an unknown <c>--reporter</c> and an unreadable <c>--config</c>
/// file — stdout carries no report, so the JSON parser fails closed as
/// infrastructure. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed. A findings exit
/// (<c>0</c>/<c>2</c>) with no parseable JSON on stdout (a crash before the
/// report is written, or an operator <c>--reporter</c>/<c>--output</c>
/// override that moved the report off stdout) likewise fails closed as
/// infrastructure through the parser.</para>
///
/// <para><b>Version pin.</b> A linter's rule implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>swiftlint --version</c> before
/// the scan; a missing binary, an unrecognised version string, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass, never a finding. The shared
/// first-<c>major.minor.patch</c> extraction fits this tool: the probe
/// prints the bare version (<c>0.65.1</c>).</para>
///
/// <para><b>Repository-controlled suppression — stated, not hidden.</b>
/// SwiftLint honors <c>// swiftlint:disable …</c> comments authored inside
/// the audited repository — and the audit subject writes that repository.
/// SwiftLint offers no CLI flag that makes those comments inert, so the base
/// cannot express that gate and this auditor does not hand-roll one:
/// suppression comments stay honored. The same is true one level up — the
/// repo's <c>.swiftlint.yml</c> selects rules, severities, and the
/// <c>excluded</c> list, and a repo could weaken its own contract. That
/// posture matches the sibling linters: the project's own lint contract is
/// the meaningful check, and changes to it are visible in the audited diff.
/// Operators who need a fully operator-owned ruleset pin one outside the
/// repository via <c>ConfigPath</c> — which still does not disable inline
/// <c>swiftlint:disable</c> comments — and treat a warnings-clean local run
/// that disagrees with the audit as a signal to inspect the diff's
/// suppression comments.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>swiftlint lint .</c>:
/// SwiftLint lints every Swift file under the worktree except the paths in
/// the configuration's <c>excluded</c> list — it ships with no default
/// vendored excludes (verified: a <c>vendor/</c> tree is linted unless the
/// config excludes it), so the finding-level <c>ExcludePaths</c> backstop
/// below is load-bearing, not redundant. <c>--no-cache</c> keeps the scan
/// from writing a cache entry for the audited tree; the audit must not
/// mutate its subject (nor trust a stale cache about it).</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: SwiftLint Swift Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "swiftlint",
    InstallHint = "provision the pinned swiftlint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — on Linux unpack the pinned "
        + "swiftlint_linux_<arch>.zip from the realm/SwiftLint GitHub release (e.g. "
        + "https://github.com/realm/SwiftLint/releases/download/0.65.1/swiftlint_linux_amd64.zip); "
        + "on macOS use the pinned SwiftLint.pkg or 'brew install swiftlint' — no distro apt package "
        + "carries a version pin — through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions")]
public sealed class SwiftlintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.swiftlint";

    /// <summary>
    /// SwiftLint release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.65.1";

    /// <summary>Scoped-config key for an explicit SwiftLint configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = linted clean or warnings only; 2 = linted with error-level
        // violations. Both emit the JSON report — both are verdicts.
        // 1 (no lintable files), 64 (illegal parameters), crashes and
        // everything else is "could not run": infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // SwiftLint ships with no default vendored excludes — a vendor/ or
        // Pods/ tree is linted unless the repo config excludes it — so this
        // finding-level backstop is what keeps dependency checkouts and
        // build output out of the audit. Operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "Pods/", "Carthage/", "DerivedData/", ".build/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:swiftlint";

    /// <inheritdoc />
    protected override string ToolName => "swiftlint";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SwiftlintJsonOutputParser();

    /// <summary>
    /// Declared mapping from SwiftLint's severity vocabulary
    /// (<c>Warning</c>, <c>Error</c> — <c>--strict</c> upgrades warnings to
    /// errors in the report itself, so the tool never emits a third level)
    /// to CodeyBox's <see cref="AuditSeverity"/>. Only <c>error</c> fails
    /// the audit; warnings stay advisory. The neighbouring levels common to
    /// other scanners are mapped identically so a future report shape
    /// carrying them is not a unique dialect. Raw levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Structured argv, never a shell string: the base appends the
        // operator's ExtraArguments after these entries.
        var args = new List<string> { "lint" };

        if (!ExtraArgumentsSupplyFlag(options, "--reporter"))
        {
            // Machine-readable JSON report on stdout (status logs go to
            // stderr, so stdout stays pure JSON). An operator --reporter
            // would replace the JSON the parser expects and break the run
            // into infrastructure failure; let that surface loudly.
            args.Add("--reporter");
            args.Add("json");
        }

        if (!ExtraArgumentsSupplyFlag(options, "--no-cache"))
        {
            // Never write a cache entry for the audited tree; the audit must
            // not mutate its subject (nor trust a stale cache about it).
            args.Add("--no-cache");
        }

        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !ExtraArgumentsSupplyFlag(options, "--config"))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }

        args.Add(".");
        return args;
    }

    /// <inheritdoc />
    protected override async Task<string?> ResolveScanRootAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // SwiftLint's report carries absolute `file` paths and no embedded
        // cwd, so the parser relativizes against the directory the tool
        // actually ran in. That is not necessarily the `workingDirectory`
        // string: sandbox providers may translate it (the process provider
        // maps "/work" onto a host temp path), so it is resolved with a
        // bounded `pwd` probe — the same cwd the scan will see. `pwd` is a
        // shell builtin — the audited repository cannot shadow it via PATH —
        // and it prints the process's own logical cwd, which is exactly the
        // path prefix SwiftLint embeds in its absolute `file` values.
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "scan-root probe",
            new SandboxExec
            {
                Argv = ["sh", "-c", "pwd", "sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' scan-root probe could not run: the sandbox exec "
                + "transport was unavailable.");

        var root = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (result.ExitCode != 0 || string.IsNullOrEmpty(root))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not resolve the scan root (exit "
                + $"{result.ExitCode}) — the worktree root must be resolvable for findings to be "
                + "reported repository-relative.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        return ExternalToolJsonHelpers.NormalizePath(root);
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        context.Logger.LogInformation(
            "SwiftlintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
