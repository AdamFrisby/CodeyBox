using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.ShellcheckAuditorPlugin;

/// <summary>
/// Scripting auditor wrapping <c>shellcheck</c> (shell script analysis) on
/// the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the report selection
/// (shellcheck 0.9.0 emits no SARIF — <c>-f json1</c> parsed by <see
/// cref="ShellcheckJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, shell
/// script discovery, and the shellcheck-specific knobs below.
///
/// <para><b>Gate behaviour: blocking on error-severity findings only.</b>
/// shellcheck assigns each diagnostic a level (<c>error</c>,
/// <c>warning</c>, <c>info</c>, <c>style</c>). The declared map sends
/// <c>error</c> to <see cref="AuditSeverity.Error"/> (broken syntax such as
/// <c>SC1046</c>/<c>SC1073</c>, fragile constructs such as <c>SC2045</c>),
/// <c>warning</c> to <see cref="AuditSeverity.Warning"/>, and
/// <c>info</c>/<c>style</c> to <see cref="AuditSeverity.Info"/>. The shared
/// base passes the audit unless an error-severity finding is present, so
/// portability notes and style suggestions stay advisory. Operators who
/// want a stricter gate lower the tool's own reporting floor with
/// <c>--severity=warning</c> in <c>ExtraArguments</c> (which suppresses
/// info/style diagnostics at the source) or raise
/// <c>MinimumSeverity</c>; <c>MinimumSeverity</c> only drops findings, it
/// never raises them.</para>
///
/// <para><b>Exit-code convention (verified empirically against shellcheck
/// 0.9.0 — NOT assumed).</b> <c>0</c> = scanned clean (empty json1
/// <c>comments</c>); <c>1</c> = scanned with diagnostics (json1 report on
/// stdout). Both are findings-producing verdicts. <c>2</c> = file errors
/// (unreadable inputs — stdout still carries an empty report, so the exit
/// code, not the report, is the discriminator); <c>3</c> = usage errors
/// (bad flags, zero file arguments — empty stdout); <c>4</c> = an
/// unsupported <c>-f</c> format selection (empty stdout). Exits
/// <c>2</c>–<c>4</c> are "could not run" with one narrow exception: the
/// zero-file-arguments <c>No files specified.</c> diagnostic on stderr at
/// exit <c>3</c> means discovery found no shell scripts and no
/// <c>Targets</c> were configured — a clean pass handled by <see
/// cref="ShellcheckJsonOutputParser"/>, mirroring the hadolint auditor's
/// sentinel. Every other report-less run fails closed as infrastructure
/// through the parser. <c>126</c>/<c>127</c> = cannot execute / not found —
/// infrastructure. Anything else is an unknown convention and fails loudly
/// as infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> A scanner's checks change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>shellcheck --version</c> before the scan;
/// a missing binary, an unrecognised version string, or a version other
/// than <c>ExpectedVersion</c> is an infrastructure failure naming the tool
/// — never a pass, never a finding.</para>
///
/// <para><b>Repository-authored configuration.</b> shellcheck
/// auto-discovers a <c>.shellcheckrc</c> file when no <c>--norc</c> is
/// passed — that file can disable checks or re-severity them, so it is
/// executable gate configuration owned by the audit subject. The default
/// (<c>TrustRepositoryConfig=true</c>) honours it, because linting against
/// the project's own lint contract is the meaningful check, and the load is
/// announced at info level so the gate posture is auditable. shellcheck
/// 0.9.0 offers no <c>--config</c>/<c>--rcfile</c> flag for an explicit
/// operator-owned file, so <c>TrustRepositoryConfig=false</c> passes
/// <c>--norc</c>, which neutralises repository discovery and runs
/// shellcheck under its defaults. Inline <c># shellcheck
/// disable=SCxxxx</c> pragmas always apply — shellcheck 0.9.0 provides no
/// flag to ignore them — and are honoured and documented as a limitation:
/// the subject can silence individual lines, and that silence is visible in
/// the audited diff.</para>
///
/// <para><b>Scope and defaults.</b> shellcheck lints files, not
/// directories, so with no <c>Targets</c> configured the auditor discovers
/// candidates with a fixed <c>find</c> probe (argv entries, never a shell,
/// nothing interpolated from configuration): files named <c>*.sh</c>,
/// <c>*.bash</c>, <c>*.ksh</c> or <c>*.bsh</c> (case-insensitive), pruning
/// directories named <c>.git</c>, <c>vendor</c>, <c>third_party</c>,
/// <c>node_modules</c>, <c>dist</c>, <c>build</c>, <c>out</c> or
/// <c>coverage</c> at any depth. Findings under the default
/// <c>ExcludePaths</c> prefixes are additionally dropped post-scan, so an
/// operator-supplied <c>Targets</c> entry under a vendored tree is still
/// filtered unless <c>ExcludePaths</c> is overridden. Every target —
/// discovered or configured — reaches shellcheck as one argv entry after a
/// <c>--</c> separator with a <c>./</c> prefix, so a repository-controlled
/// dash-leading filename (e.g. <c>--severity=error</c>) is parsed as a
/// path, never as a tool flag; a configured <c>Targets</c> entry with a
/// leading dash fails closed instead. More than
/// <c>MaxDiscoveredTargets</c> candidates is a deterministic infrastructure
/// failure directing the operator to set <c>Targets</c> — the scan never
/// silently covers a subset. A tree with no shell scripts is a clean pass,
/// not an error. The scan never follows <c>source</c>d files outside the
/// target set (<c>-x</c>/<c>--external-sources</c> is not passed) and runs
/// shellcheck's default check set (optional checks stay off unless the
/// operator enables them with <c>-o</c> in <c>ExtraArguments</c>).</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ShellCheck Shell Script Analyser",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "shellcheck",
    AptPackage = "shellcheck",
    InstallHint = "install the pinned shellcheck release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") — the declared apt package installs it into the sandbox baseline "
        + "automatically when this plugin is enabled; on baselines whose distro shellcheck differs, set "
        + "ExpectedVersion to the provisioned release")]
public sealed class ShellcheckAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.shellcheck";

    /// <summary>
    /// shellcheck release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.9.0";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repository-relative shell script paths, positional args). Unset →
    /// shell script discovery (see the class documentation). Set →
    /// discovery is skipped and the entries are validated
    /// (repository-relative, no <c>..</c> segments, never leading-dash — a
    /// leading-dash positional would be option-parsed as a shellcheck flag)
    /// and passed after a <c>--</c> separator with a <c>./</c> prefix that
    /// keeps them positional.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key opting out of repository-authored tool
    /// configuration. Default true: the conventional repo config file
    /// (<c>.shellcheckrc</c> at the worktree root, which shellcheck
    /// auto-discovers) applies, because linting against the project's own
    /// lint contract is the meaningful check. Set to <c>false</c> so the
    /// repo's config is not loaded at all (the scan passes <c>--norc</c>
    /// and runs under shellcheck's defaults).
    /// </summary>
    internal const string TrustRepositoryConfigKey = "TrustRepositoryConfig";

    /// <summary>Conventional repository config locations probed to announce the applied lint contract.</summary>
    private static readonly IReadOnlyList<string> RepositoryConfigCandidates =
    [
        ".shellcheckrc",
    ];

    /// <summary>
    /// Exit code shellcheck returns for usage errors, including the
    /// zero-file-arguments case the <see cref="ShellcheckJsonOutputParser"/>
    /// maps to a clean pass via its <c>No files specified.</c> sentinel.
    /// </summary>
    internal const int NoFilesExitCode = 3;

    /// <summary>
    /// Upper bound on shell scripts the discovery probe contributes as scan
    /// targets. Discovery beyond this fails closed (deterministic
    /// infrastructure directing the operator to <c>Targets</c>) rather than
    /// scanning a silent subset or breaching the shared built-argument
    /// bound.
    /// </summary>
    internal const int MaxDiscoveredTargets = 200;

    private const int DiscoveryProbeMaxStdoutBytes = 64 * 1024;

    // Discovery is a liveness probe, not the scan: it shares the configured
    // scan timeout below this cap so file enumeration on a huge tree cannot
    // stall the audit. One named value feeds both the comparison and the
    // clamp so the two cannot drift apart.
    private static readonly TimeSpan DiscoveryProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on the length of one discovered script path. Discovery
    /// output lists repository file content (untrusted input), so a single
    /// unbounded entry is rejected rather than carried into the scan argv.
    /// </summary>
    private const int MaxDiscoveredPathLength = 1024;

    // Fixed discovery probe: argv entries only, never a shell, and nothing
    // interpolated from configuration — operator input reaches shellcheck as
    // positional args, never as probe text.
    private static readonly IReadOnlyList<string> ShellDiscoveryArgv =
    [
        "find", ".",
        "-type", "d",
        "(", "-name", ".git",
        "-o", "-name", "vendor",
        "-o", "-name", "third_party",
        "-o", "-name", "node_modules",
        "-o", "-name", "dist",
        "-o", "-name", "build",
        "-o", "-name", "out",
        "-o", "-name", "coverage",
        ")", "-prune",
        "-o", "-type", "f",
        "(", "-iname", "*.sh",
        "-o", "-iname", "*.bash",
        "-o", "-iname", "*.ksh",
        "-o", "-iname", "*.bsh",
        ")", "-print",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = scanned clean; 1 = diagnostics found (json1 report on
        // stdout). Both are findings-producing verdicts. 2 = file errors, 4
        // = unsupported format selection, 126/127 = not executable / not
        // found — infrastructure. 3 = usage errors, which write no report
        // and fail closed through the parser, except the zero-file-arguments
        // sentinel the parser maps to a clean pass.
        FindingsExitCodes = new HashSet<int> { 0, 1, NoFilesExitCode },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config. shellcheck
        // echoes target paths as passed (repository-relative), so these
        // prefixes match.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _trustRepositoryConfig = static () => true;
    private ILogger _logger = NullLogger.Instance;

    /// <inheritdoc />
    public override string Name => "codeybox:shellcheck";

    /// <inheritdoc />
    protected override string ToolName => "shellcheck";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new ShellcheckJsonOutputParser();

    /// <summary>
    /// Declared mapping from shellcheck's level vocabulary (<c>error</c>,
    /// <c>warning</c>, <c>info</c>, <c>style</c> — the closed set its
    /// <c>--severity</c> flag accepts, verified against 0.9.0) to
    /// CodeyBox's <see cref="AuditSeverity"/>. Only <c>error</c> blocks the
    /// audit; raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["style"] = AuditSeverity.Info,
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
        // Machine-readable json1 report for the JSON parser. An operator
        // --format/-f would replace the report the parser expects and break
        // the run into infrastructure failure; let that surface loudly.
        if (ExtraArgumentsSupplyFlag(options, "--format", "-f"))
            return [];
        return ["-f", "json1"];
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var args = new List<string>();

        // shellcheck 0.9.0 offers no --config/--rcfile flag, so repository
        // config is either honoured by discovery (default) or neutralised
        // with --norc. An operator-supplied --norc in ExtraArguments wins
        // outright: the auditor never stacks a second one on it.
        if (!ExtraArgumentsSupplyFlag(options, "--norc"))
        {
            if (!_trustRepositoryConfig())
            {
                args.Add("--norc");
            }
            else
            {
                var present = await ProbeRepositoryFilesPresentAsync(
                    sandbox, workingDirectory, ToolName, RepositoryConfigCandidates, options, ct)
                    .ConfigureAwait(false);
                if (present.Count > 0)
                {
                    // Repo-authored config is executable configuration:
                    // announce the load so the audit trail shows the gate ran
                    // under the repository's own lint contract rather than
                    // silently inheriting it. The path is one of the fixed
                    // candidates, never arbitrary input.
                    _logger.LogInformation(
                        "shellcheck: repository-authored config {ConfigFile} applies; "
                        + "set {TrustKey}=false for a fully operator-owned run",
                        present[0], TrustRepositoryConfigKey);
                }
            }
        }

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        IReadOnlyList<string> positionals = targets.Count > 0
            ? targets.Select(NormalizeTargetEntry).ToList()
            : await DiscoverShellScriptsAsync(sandbox, workingDirectory, options, ct)
                .ConfigureAwait(false);

        // The "--" separator ends option parsing (verified: shellcheck
        // honours it), and the "./" prefix additionally keeps a
        // dash-leading name from being read as a tool flag even by parsers
        // that ignore "--": the option parser reads "--severity=…" as a flag
        // but "./--severity=…" as a path.
        args.Add("--");
        args.AddRange(positionals);
        return args;
    }

    // A target — discovered or operator-configured — reaches shellcheck as
    // one argv entry (never through a shell), and the "./" prefix keeps a
    // leading-dash name from being read as a tool flag: the option parser
    // reads "--severity=…" as a flag but "./--severity=…" as a path.
    private static string ToSafePositional(string relativePath)
        => "./" + relativePath;

    // Operator-configured Targets entries are positional arguments emitted
    // after every flag, so a leading-dash entry would be parsed as a
    // shellcheck flag (e.g. "--severity=…" silently replacing the auditor's
    // own floor, or "--norc" re-enabling repository-controlled gate
    // configuration in reverse). Fail closed instead of scanning under
    // foreign flags.
    private static string NormalizeTargetEntry(string entry)
    {
        var normalized = entry.Replace('\\', '/').Trim();
        var stripped = normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
        if (stripped.Length == 0
            || stripped.StartsWith("/", StringComparison.Ordinal)
            || stripped.StartsWith("-", StringComparison.Ordinal)
            || stripped.Contains('\n', StringComparison.Ordinal)
            || stripped.Contains('\r', StringComparison.Ordinal)
            || stripped.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' was configured with an invalid {TargetsKey} entry "
                + $"('{TruncateForMessage(entry)}'): expected a repository-relative shell script path without "
                + "'..' segments; entries starting with '-' are rejected because a leading-dash positional "
                + "would be parsed as a shellcheck flag.")
            { IsDeterministic = true };
        return ToSafePositional(stripped);
    }

    // Discovery output lists repository file content — untrusted. Every entry
    // must be a repository-relative path, or the scan scope cannot be trusted
    // and the run fails closed instead of scanning a narrowed set. The
    // returned value keeps no "./" prefix; callers add it with
    // <see cref="ToSafePositional"/> (never stripped: stripping it is what
    // would expose a dash-leading name such as "--severity=x.sh" to the
    // tool's option parser), so a dash-leading name stays an inert
    // positional path.
    private string ValidateDiscoveredEntry(string entry)
    {
        var stripped = entry.StartsWith("./", StringComparison.Ordinal) ? entry[2..] : entry;
        if (stripped.Length == 0
            || stripped.Length > MaxDiscoveredPathLength
            || stripped.StartsWith("/", StringComparison.Ordinal)
            || stripped.Contains('\n', StringComparison.Ordinal)
            || stripped.Contains('\r', StringComparison.Ordinal)
            || stripped.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' shell script discovery returned an entry outside "
                + $"the expected repository-relative shape ('{TruncateForMessage(entry)}'). The scan scope "
                + "cannot be trusted, so this is infrastructure, not a verdict on the diff.")
            { IsDeterministic = true };
        return stripped;
    }

    private async Task<IReadOnlyList<string>> DiscoverShellScriptsAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var timeout = EffectiveTimeout(options);
        if (timeout > DiscoveryProbeTimeout)
            timeout = DiscoveryProbeTimeout;
        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "Shell script discovery",
            new SandboxExec
            {
                Argv = ShellDiscoveryArgv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = DiscoveryProbeMaxStdoutBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            timeout,
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' shell script discovery could not run: "
                + "the sandbox exec transport was unavailable.");
        if (probe.ExitCode is 126 or 127)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' shell script discovery could not run "
                + $"(exit {probe.ExitCode} — the 'find' helper is missing or not executable in the sandbox).");
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' shell script discovery failed "
                + $"(exit {probe.ExitCode}). Set CodeyBox:Plugins:{PluginId}:{TargetsKey} to explicit "
                + "shell script paths to skip discovery.");
        if (probe.OutputLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' shell script discovery produced more than "
                + $"{DiscoveryProbeMaxStdoutBytes} bytes of output. Set CodeyBox:Plugins:{PluginId}:{TargetsKey} "
                + "to explicit shell script paths to skip discovery.")
            { IsDeterministic = true };

        var discovered = probe.Stdout
            .Split('\n')
            .Select(static line => line.Replace('\\', '/').Trim())
            .Where(static line => line.Length > 0)
            .Select(ValidateDiscoveredEntry)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static line => line, StringComparer.Ordinal)
            .Select(ToSafePositional)
            .ToList();
        if (discovered.Count > MaxDiscoveredTargets)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' discovery found {discovered.Count} shell scripts, "
                + $"exceeding the bound of {MaxDiscoveredTargets}. Set CodeyBox:Plugins:{PluginId}:{TargetsKey} "
                + "to explicit shell script paths to scope the scan.")
            { IsDeterministic = true };
        return discovered;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _trustRepositoryConfig = () =>
            !bool.TryParse(scoped[TrustRepositoryConfigKey], out var trust) || trust;
        _logger = context.Logger;
        context.Logger.LogInformation(
            "ShellcheckAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
