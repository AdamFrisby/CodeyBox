using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.HadolintAuditorPlugin;

/// <summary>
/// Infrastructure auditor wrapping <c>hadolint</c> (Dockerfile lint) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the SARIF report parser (<see
/// cref="HadolintSarifOutputParser"/> — a thin adapter over the shared <see
/// cref="SarifToolOutputParser"/> plus the tool's own "no input files"
/// sentinel), the pinned tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, Dockerfile discovery, and
/// the hadolint-specific knobs below.
///
/// <para><b>Gate behaviour: blocking on error-severity findings only.</b>
/// hadolint assigns each rule a severity (<c>error</c>, <c>warning</c>,
/// <c>info</c>, <c>style</c>); its SARIF emitter folds <c>info</c> and
/// <c>style</c> into SARIF <c>note</c>. The declared map sends SARIF
/// <c>error</c> to <see cref="AuditSeverity.Error"/> (Dockerfile parse
/// errors, <c>DL1000</c>, and rules escalated to error), <c>warning</c> to
/// <see cref="AuditSeverity.Warning"/>, and <c>note</c> to <see
/// cref="AuditSeverity.Info"/>. The shared base passes the audit unless an
/// error-severity finding is present, so style and best-practice notes stay
/// advisory. Operators who want a stricter gate lower severities into
/// errors through the tool's own <c>override.error</c> config (via <c>ConfigPath</c>);
/// <c>MinimumSeverity</c> only drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified empirically against hadolint
/// 2.15.1 — NOT assumed).</b> hadolint exits <c>0</c> when no rule at or
/// above the failure threshold fired and <c>1</c> otherwise. Exit <c>1</c>
/// is therefore ambiguous: findings above the threshold (SARIF report on
/// stdout), zero file arguments (<c>Please provide a Dockerfile</c> on
/// stdout, no report — the parser maps this to a clean pass, see <see
/// cref="HadolintSarifOutputParser"/>), bad flags (usage error on stderr,
/// no report), and unreadable files (a Haskell exception with a backtrace
/// on stderr, no report). The discriminator is the report, not the exit
/// code: exits <c>0</c> and <c>1</c> with a parseable SARIF document are
/// findings-producing verdicts; an exit without one fails closed as
/// infrastructure through the parser. <c>126</c>/<c>127</c> (cannot execute
/// / not found) and any other exit are infrastructure.</para>
///
/// <para><b>Version pin.</b> A scanner's rules change between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>hadolint --version</c> before the scan; a
/// missing binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-authored configuration.</b> hadolint auto-discovers
/// <c>$PWD/.hadolint.yaml</c> (<c>.yml</c>) when no <c>--config</c> is
/// passed — that file can ignore rules or re-severity them, so it is
/// executable gate configuration owned by the audit subject. The default
/// (<c>TrustRepositoryConfig=true</c>) honours it, because linting against
/// the project's own lint contract is the meaningful check, and the load is
/// announced at info level so the gate posture is auditable. An explicit
/// <c>ConfigPath</c> replaces repository discovery entirely (hadolint
/// semantics: an explicit <c>--config</c> wins outright) and should live
/// outside the audited tree. <c>TrustRepositoryConfig=false</c> with no
/// <c>ConfigPath</c> passes <c>--config /dev/null</c>, which neutralises
/// repository discovery and runs hadolint under its defaults (hadolint
/// reports an unparseable config file on stderr and falls back to the
/// default configuration rather than aborting — verified against 2.15.1).
/// Inline <c># hadolint ignore=…</c> pragmas always apply unless the
/// operator disables them via config/flags; they are honoured and
/// documented as a limitation.</para>
///
/// <para><b>Scope and defaults.</b> hadolint lints files, not directories,
/// so with no <c>Targets</c> configured the auditor discovers candidates
/// with a fixed <c>find</c> probe (argv entries, never a shell, nothing
/// interpolated from configuration): files named <c>Dockerfile*</c>,
/// <c>*.dockerfile</c>, <c>Containerfile*</c> or <c>*.containerfile</c>
/// (case-insensitive), pruning directories named <c>.git</c>,
/// <c>vendor</c>, <c>third_party</c>, <c>node_modules</c>, <c>dist</c>,
/// <c>build</c>, <c>out</c> or <c>coverage</c> at any depth. Findings under
/// the default <c>ExcludePaths</c> prefixes are additionally dropped
/// post-scan, so an operator-supplied <c>Targets</c> entry under a vendored
/// tree is still filtered unless <c>ExcludePaths</c> is overridden. More
/// than <c>MaxDiscoveredTargets</c> candidates is a deterministic
/// infrastructure failure directing the operator to set <c>Targets</c> —
/// the scan never silently covers a subset. A tree with no Dockerfiles is a
/// clean pass, not an error.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Hadolint Dockerfile Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "hadolint",
    InstallHint = "provision the pinned hadolint release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "a version pin — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class HadolintAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.hadolint";

    /// <summary>
    /// hadolint release the invocation and its findings are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.15.1";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repository-relative Dockerfile paths, positional args). Unset →
    /// Dockerfile discovery (see the class documentation). Set → discovery
    /// is skipped and the entries are passed verbatim.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key for an operator-owned hadolint configuration file,
    /// passed as <c>--config</c>. It should live outside the audited
    /// repository: a repo-authored file lets the audit subject shape the
    /// gate (ignoring rules, re-severitying them). When set it is the only
    /// config passed — repository config discovery is skipped. Ignored when
    /// <c>ExtraArguments</c> already supplies <c>--config</c>.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key opting out of repository-authored tool
    /// configuration. Default true: the conventional repo config file
    /// (<c>.hadolint.yaml</c> / <c>.hadolint.yml</c> at the worktree root,
    /// which hadolint auto-discovers) applies, because linting against the
    /// project's own lint contract is the meaningful check. Set to
    /// <c>false</c> so the repo's config is not loaded at all (the scan
    /// runs under hadolint's defaults via <c>--config /dev/null</c>); pair
    /// with <see cref="ConfigPathKey"/> for an operator-owned gate.
    /// </summary>
    internal const string TrustRepositoryConfigKey = "TrustRepositoryConfig";

    /// <summary>Conventional repository config locations probed to announce the applied lint contract.</summary>
    private static readonly IReadOnlyList<string> RepositoryConfigCandidates =
    [
        ".hadolint.yaml",
        ".hadolint.yml",
    ];

    /// <summary>
    /// Upper bound on Dockerfiles the discovery probe contributes as scan
    /// targets. Discovery beyond this fails closed (deterministic
    /// infrastructure directing the operator to <c>Targets</c>) rather than
    /// scanning a silent subset or breaching the shared built-argument
    /// bound.
    /// </summary>
    internal const int MaxDiscoveredTargets = 200;

    private const int DiscoveryProbeMaxStdoutBytes = 64 * 1024;

    // Fixed discovery probe: argv entries only, never a shell, and nothing
    // interpolated from configuration — operator input reaches hadolint as
    // positional args, never as probe text.
    private static readonly IReadOnlyList<string> DockerfileDiscoveryArgv =
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
        "(", "-iname", "dockerfile*",
        "-o", "-iname", "*.dockerfile",
        "-o", "-iname", "containerfile*",
        "-o", "-iname", "*.containerfile",
        ")", "-print",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean (or findings below the failure threshold — the SARIF
        // report still carries them, so 0 is a verdict). 1 = findings above
        // the threshold — OR a run failure (bad flags, unreadable files,
        // zero file arguments), which writes no SARIF and fails closed
        // through the parser. Every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<string?> _configPath = static () => null;
    private Func<bool> _trustRepositoryConfig = static () => true;
    private ILogger _logger = NullLogger.Instance;

    /// <inheritdoc />
    public override string Name => "codeybox:hadolint";

    /// <inheritdoc />
    protected override string ToolName => "hadolint";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new HadolintSarifOutputParser();

    /// <summary>
    /// Declared mapping from hadolint's SARIF level vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. hadolint's SARIF emitter
    /// folds its <c>info</c> and <c>style</c> severities into SARIF
    /// <c>note</c> (verified against the 2.15.1 source), so only
    /// <c>error</c> blocks the audit; raw levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fatal"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["style"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
        => ["-f", "sarif"];

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var args = new List<string>();

        // An operator-supplied --config in ExtraArguments wins outright: the
        // auditor never stacks a second config selection on it.
        if (!ExtraArgumentsSupplyFlag(options, "--config", "-c"))
        {
            var configured = _configPath();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                args.Add("--config");
                args.Add(configured.Trim());
            }
            else if (!_trustRepositoryConfig())
            {
                // hadolint offers no --no-config flag: an explicit --config is
                // the only way to switch off repository discovery. /dev/null
                // fails hadolint's config parse, which hadolint reports on
                // stderr and falls back to the default configuration rather
                // than aborting — the scan still runs, under operator-owned
                // defaults instead of the repository's contract.
                args.Add("--config");
                args.Add("/dev/null");
            }
            else
            {
                var present = await ProbeRepositoryFilesPresentAsync(
                    sandbox, workingDirectory, ToolName, RepositoryConfigCandidates, options, ct)
                    .ConfigureAwait(false);
                if (present.Count > 0)
                {
                    // Repo-authored config is executable configuration: announce
                    // the load so the audit trail shows the gate ran under the
                    // repository's own lint contract rather than silently
                    // inheriting it. The path is one of the fixed candidates,
                    // never arbitrary input.
                    _logger.LogInformation(
                        "hadolint: repository-authored config {ConfigFile} applies; "
                        + "set {TrustKey}=false for a fully operator-owned run",
                        present[0], TrustRepositoryConfigKey);
                }
            }
        }

        var targets = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        if (targets.Count > 0)
            args.AddRange(targets);
        else
            args.AddRange(await DiscoverDockerfilesAsync(sandbox, workingDirectory, options, ct)
                .ConfigureAwait(false));

        return args;
    }

    private async Task<IReadOnlyList<string>> DiscoverDockerfilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var timeout = EffectiveTimeout(options);
        if (timeout > TimeSpan.FromSeconds(30))
            timeout = TimeSpan.FromSeconds(30);
        var probe = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "Dockerfile discovery",
            new SandboxExec
            {
                Argv = DockerfileDiscoveryArgv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = DiscoveryProbeMaxStdoutBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            timeout,
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' Dockerfile discovery could not run: "
                + "the sandbox exec transport was unavailable.");
        if (probe.ExitCode is 126 or 127)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' Dockerfile discovery could not run "
                + $"(exit {probe.ExitCode} — the 'find' helper is missing or not executable in the sandbox).");
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' Dockerfile discovery failed "
                + $"(exit {probe.ExitCode}). Set CodeyBox:Plugins:{PluginId}:{TargetsKey} to explicit "
                + "Dockerfile paths to skip discovery.");
        if (probe.OutputLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' Dockerfile discovery produced more than "
                + $"{DiscoveryProbeMaxStdoutBytes} bytes of output. Set CodeyBox:Plugins:{PluginId}:{TargetsKey} "
                + "to explicit Dockerfile paths to skip discovery.")
            { IsDeterministic = true };

        var discovered = probe.Stdout
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .Select(static line => line.StartsWith("./", StringComparison.Ordinal) ? line[2..] : line)
            .Where(static line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static line => line, StringComparer.Ordinal)
            .ToList();
        if (discovered.Count > MaxDiscoveredTargets)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' discovery found {discovered.Count} Dockerfiles, "
                + $"exceeding the bound of {MaxDiscoveredTargets}. Set CodeyBox:Plugins:{PluginId}:{TargetsKey} "
                + "to explicit Dockerfile paths to scope the scan.")
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
        _configPath = () => scoped[ConfigPathKey];
        _trustRepositoryConfig = () =>
            !bool.TryParse(scoped[TrustRepositoryConfigKey], out var trust) || trust;
        _logger = context.Logger;
        context.Logger.LogInformation(
            "HadolintAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }
}
