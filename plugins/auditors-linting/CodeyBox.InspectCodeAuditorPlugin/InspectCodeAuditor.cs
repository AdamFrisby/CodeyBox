using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.InspectCodeAuditorPlugin;

/// <summary>
/// Static-analysis auditor wrapping ReSharper's <c>inspectcode</c> (C#/.NET
/// code inspections) on the shared <see cref="ExternalToolAuditorBase"/>: the
/// base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the InspectCode invocation
/// shape (solution discovery plus SARIF-on-stdout arguments), the pinned
/// tool-version declaration via <see
/// cref="ExternalToolAuditorBase.VersionPin"/>, and the
/// repository-suppression posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> InspectCode severities go through a declared map, never raw:
/// <c>error</c> → <see cref="AuditSeverity.Error"/> (fails the audit);
/// <c>warning</c> → <see cref="AuditSeverity.Warning"/> (advisory);
/// <c>note</c>, <c>none</c>, <c>suggestion</c>, <c>hint</c> and <c>info</c> →
/// <see cref="AuditSeverity.Info"/> (informational); anything unrecognised →
/// <see cref="AuditSeverity.Warning"/>. <c>MinimumSeverity</c> only drops
/// findings, it never raises them. This auditor is therefore a merge gate
/// for error-severity inspections, not a blocker on every suggestion.</para>
///
/// <para><b>Exit-code convention (verified against InspectCode 2026.2.2 — do
/// not assume the eslint convention holds here).</b> <c>inspectcode</c>
/// exits <c>0</c> whenever the analysis completes — whether or not any issue
/// was produced; the verdict is in the SARIF document, not the exit code.
/// There is deliberately no separate "found something" exit: only <c>0</c>
/// is findings-producing, and every non-zero exit (verified: <c>1</c> for a
/// missing solution file, <c>3</c> for a solution with no files to inspect;
/// <c>126</c>/<c>127</c> for cannot-execute/not-found) is infrastructure. If
/// a future release ever exits non-zero alongside results, the run fails
/// closed as infrastructure — loud, never a silent pass.</para>
///
/// <para><b>Version pin.</b> Inspection implementations change between
/// releases, so findings are only meaningful from the build the auditor was
/// verified against. The auditor probes <c>inspectcode --version</c> before
/// the scan (the CLI prints <c>JetBrains Inspect Code X.Y.Z …</c>); a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> InspectCode applies
/// severity overrides from the audited repository's settings layers
/// (<c>*.sln.DotSettings</c> team-shared settings can demote any inspection
/// to <c>DO_NOT_SHOW</c>) — and the audit subject writes that repository. An
/// auditor its subject can silence is not a gate, so by default the scan
/// passes <c>--disable-settings-layers:SolutionShared;SolutionPersonal</c>,
/// making solution- and project-level settings inert; findings then surface
/// for code the settings would have suppressed. Operators who deliberately
/// trust repo-authored suppression set <c>TrustRepositorySuppression</c> in
/// scoped config. Residual surface, stated not hidden: <c>.editorconfig</c>
/// <c>resharper_*=none</c> severities are still honored (see the plugin
/// README), so a fully operator-owned gate pins an out-of-repo
/// <c>SettingsPath</c>.</para>
///
/// <para><b>Scope and defaults.</b> InspectCode analyzes a Visual Studio
/// solution, so the scan needs one: <c>SolutionPath</c> names it explicitly,
/// otherwise the shallowest <c>*.sln</c>/<c>*.slnx</c> within three levels of
/// the repository root is discovered deterministically. The report streams
/// SARIF to stdout (<c>-o=/dev/stdout</c> on Linux sandboxes) with console
/// chatter suppressed (<c>--verbosity=OFF</c>); the shared SARIF parser reads
/// it, so any non-SARIF stdout (for example from an operator-supplied
/// <c>--verbosity</c> override) fails closed as infrastructure. The analysis
/// runs with <c>--no-build</c> by default — the audit sandbox has no network
/// for a NuGet restore — unless <c>BuildSolution</c> is set. On top of the
/// tool's own scope, findings under vendored (<c>vendor/</c>,
/// <c>third_party/</c>, <c>node_modules/</c>) and generated
/// (<c>obj/</c>, <c>bin/</c>, <c>artifacts/</c>, <c>dist/</c>,
/// <c>build/</c>, <c>out/</c>, <c>coverage/</c>) prefixes are dropped by
/// default: problems there belong to upstream packages or compiler output,
/// not the change under audit, and reporting them trains operators to ignore
/// the auditor.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: ReSharper InspectCode Static Analysis",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "inspectcode",
    InstallHint = "provision the pinned ReSharper Command Line Tools release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline so inspectcode is on PATH — unzip "
        + "https://download.jetbrains.com/resharper/dotUltimate.<version>/JetBrains.ReSharper.CommandLineTools.<version>.zip "
        + "or wrap jb from dotnet tool install -g JetBrains.ReSharper.GlobalTools — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class InspectCodeAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.inspectcode";

    /// <summary>
    /// InspectCode release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2026.2.2";

    /// <summary>Scoped-config key for an explicit repository-relative solution path.</summary>
    public const string SolutionPathKey = "SolutionPath";

    /// <summary>Scoped-config key for an operator-owned InspectCode settings file path.</summary>
    public const string SettingsPathKey = "SettingsPath";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// solution/project settings layers that can demote or hide inspections.
    /// Default false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Scoped-config key opting in to building the solution before analysis.
    /// Default false: the audit sandbox has no network for a NuGet restore.
    /// </summary>
    internal const string BuildSolutionKey = "BuildSolution";

    // Fixed discovery probe: solution file names come from the audited
    // repository, so the script takes no input and emits a bounded,
    // sorted, one-path-per-line listing the auditor validates before use.
    private const string SolutionDiscoveryScript =
        "find . -maxdepth 3 \\( -iname '*.sln' -o -iname '*.slnx' \\) -print 2>/dev/null | sort | head -n 32";

    private const int MaxDiscoveredSolutions = 32;

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // inspectcode exits 0 whenever the analysis completes — with or
        // without issues. Every non-zero exit means "could not run".
        FindingsExitCodes = new HashSet<int> { 0 },
        // Solution-wide analysis over a full repository routinely takes
        // minutes: bound each phase (discovery probes share a 30s cap).
        Timeout = TimeSpan.FromMinutes(10),
        // Findings in vendored/dependency trees and compiler or build output
        // describe code that is not the change under audit — noise that
        // trains operators to ignore the auditor. Operators re-include a
        // path by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/", "obj/", "bin/", "artifacts/", "dist/", "build/", "out/", "coverage/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _solutionPath = static () => null;
    private Func<string?> _settingsPath = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;
    private Func<bool> _buildSolution = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:inspectcode";

    /// <inheritdoc />
    protected override string ToolName => "inspectcode";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from InspectCode's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. Verified SARIF levels are <c>error</c>,
    /// <c>warning</c> and <c>note</c> (ReSharper's suggestion/hint surface as
    /// <c>note</c>); the native names are mapped identically so a future
    /// report shape carrying them is not a unique dialect. Raw tool levels
    /// never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["suggestion"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
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
        // solution positional (resolved per run) and the operator's
        // ExtraArguments after these entries.
        var args = new List<string>
        {
            // InspectCode writes its report to a file, not stdout:
            // /dev/stdout (Linux sandboxes) is the stdout sink the shared
            // SARIF parser reads.
            "-o=/dev/stdout",
            // Console chatter shares stdout with the SARIF document; OFF
            // keeps the report the whole of stdout so it stays parseable.
            "--verbosity=OFF",
        };

        if (!ExtraArgumentsSupplyFlag(options, "--build", "--no-build"))
            args.Add(_buildSolution() ? "--build" : "--no-build");

        // The tool-side severity floor moves with the finding-side
        // MinimumSeverity so the two never disagree about what is reported:
        // INFO keeps everything, WARNING drops note-level suggestions and
        // hints, ERROR keeps only errors.
        if (!ExtraArgumentsSupplyFlag(options, "--severity", "-e"))
        {
            args.Add("-e=" + MinimumSeverityFlag(options.MinimumSeverity));
        }

        // The audit subject authors solution settings; keep them inert
        // unless the operator opts in to repo-controlled suppression.
        if (!_trustRepositorySuppression())
            args.Add("--disable-settings-layers:SolutionShared;SolutionPersonal");

        var settingsPath = _settingsPath();
        if (!string.IsNullOrWhiteSpace(settingsPath)
            && !ExtraArgumentsSupplyFlag(options, "--settings"))
        {
            args.Add("--settings");
            args.Add(settingsPath.Trim());
        }

        return args;
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = _solutionPath();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var normalized = NormalizeSolutionPath(configured);
            var present = await ProbeRepositoryFilesPresentAsync(
                sandbox, workingDirectory, ToolName, [normalized], options, ct).ConfigureAwait(false);
            if (present.Count == 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolName}' found no solution file '{normalized}' in the "
                    + $"audited repository. Set CodeyBox:Plugins:{PluginId}:{SolutionPathKey} to a "
                    + "repository-relative .sln or .slnx path.")
                { IsDeterministic = true };
            return [ToSafePositional(normalized)];
        }

        return [await DiscoverSolutionAsync(sandbox, workingDirectory, options, ct).ConfigureAwait(false)];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _solutionPath = () => scoped[SolutionPathKey];
        _settingsPath = () => scoped[SettingsPathKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        _buildSolution = () =>
            bool.TryParse(scoped[BuildSolutionKey], out var build) && build;
        context.Logger.LogInformation(
            "InspectCodeAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string MinimumSeverityFlag(AuditSeverity minimum) => minimum switch
    {
        AuditSeverity.Error => "ERROR",
        AuditSeverity.Warning => "WARNING",
        _ => "INFO",
    };

    private async Task<string> DiscoverSolutionAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "solution discovery",
            new SandboxExec
            {
                Argv = ["sh", "-c", SolutionDiscoveryScript, "sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' solution discovery could not run: the sandbox exec "
                + "transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' solution discovery failed (exit {result.ExitCode}) — "
                + "without a solution there is nothing to analyze, so this is infrastructure, not a verdict "
                + "on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        var solution = SelectDiscoveredSolution(result.Stdout);
        if (solution is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' discovered no .sln or .slnx file within three "
                + $"levels of the repository root. Set CodeyBox:Plugins:{PluginId}:{SolutionPathKey} to the "
                + "repository-relative solution path.")
            { IsDeterministic = true };
        return solution;
    }

    private static string NormalizeSolutionPath(string? configured)
    {
        var normalized = (configured ?? string.Empty).Replace('\\', '/').Trim();
        var stripped = normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
        if (stripped.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains('\n', StringComparison.Ordinal)
            || normalized.Contains('\r', StringComparison.Ordinal)
            || stripped.Split('/').Contains("..", StringComparer.Ordinal)
            || (!stripped.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !stripped.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{PluginId}' was configured with an invalid {SolutionPathKey} "
                + $"('{TruncateSingleLine(configured)}'): expected a repository-relative .sln or .slnx path "
                + "without '..' segments.")
            { IsDeterministic = true };
        return stripped;
    }

    private static string? SelectDiscoveredSolution(string stdout)
    {
        var candidates = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith("./", StringComparison.Ordinal))
                entry = entry[2..];
            if (entry.Length == 0
                || entry.StartsWith("/", StringComparison.Ordinal)
                || entry.Contains('\r', StringComparison.Ordinal)
                || entry.Split('/').Contains("..", StringComparer.Ordinal))
                continue;
            if (!entry.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !entry.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                continue;
            candidates.Add(entry);
            if (candidates.Count >= MaxDiscoveredSolutions)
                break;
        }

        if (candidates.Count == 0)
            return null;

        // Shallowest solution first — the repository's main solution usually
        // sits at the root — then by name, so multi-solution trees resolve
        // deterministically and operators pin the rest via SolutionPath.
        candidates.Sort(static (left, right) =>
        {
            var depth = left.Count(static c => c == '/').CompareTo(right.Count(static c => c == '/'));
            return depth != 0 ? depth : string.Compare(left, right, StringComparison.Ordinal);
        });
        return ToSafePositional(candidates[0]);
    }

    // A discovered or configured name comes from operator config or from a
    // repository listing — either way it is passed as one argv entry (never
    // through a shell), and the "./" prefix keeps a leading-dash name from
    // being read as a tool flag.
    private static string ToSafePositional(string relativePath)
        => "./" + relativePath;

    private static string TruncateSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > 64 ? single[..64] + "…" : single;
    }
}
