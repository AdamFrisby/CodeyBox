using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SpectralAuditorPlugin;

/// <summary>
/// Schema auditor wrapping <c>spectral</c> (Stoplight Spectral CLI) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed invocation
/// with a bounded timeout, per-stream output caps, SARIF parsing, severity mapping,
/// exit-code classification, and per-auditor configuration. This class adds Spectral-specific
/// argument assembly and pinned tool version validation through
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>.
///
/// <para><b>Gate behaviour: hybrid / severity-driven.</b> Spectral classifies rule
/// violations into error, warning, info, and hint. By default, schema violations and
/// syntax errors reported as <c>error</c> map to <see cref="AuditSeverity.Error"/> and
/// block the merge (failing the audit), whereas style checks, conventions, and hints
/// reported as <c>warning</c> or <c>info</c> map to advisory severities (<see cref="AuditSeverity.Warning"/>
/// and <see cref="AuditSeverity.Info"/>) which do not block the merge. Operators can
/// change rule severities in their repository ruleset or adjust <c>MinimumSeverity</c> in
/// scoped configuration.</para>
///
/// <para><b>Exit-code convention (verified against Spectral v6.x).</b>
/// Spectral exits 0 when no violations at or above fail-severity exist (or on clean runs).
/// It exits 1 when rule violations at or above fail-severity (default: error) are found.
/// Both 0 and 1 are findings-producing verdicts because Spectral outputs SARIF on stdout in both cases.
/// In contrast, technical failures — such as a missing ruleset file, invalid ruleset syntax,
/// or missing input file — exit 2 and are classified as infrastructure failures.
/// Execution failures like invalid CLI arguments exit 1 without SARIF output on stdout,
/// which the SARIF parser recognizes and fails closed as an infrastructure failure (<see cref="AuditUnavailableException"/>).
/// Unexecutable or missing binaries exit 126 or 127 and are classified as infrastructure.</para>
///
/// <para><b>Version pin.</b> A linter's rule set and schema definitions change between releases,
/// so findings are only consistent from the build the auditor was verified against.
/// Spectral is probed with <c>spectral --version</c> before the scan; a missing binary,
/// an unrecognised version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass and never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The auditor scans JSON and YAML documents matching
/// <c>**/*.{json,yml,yaml}</c> by default, while excluding vendored and dependency directories
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) so third-party specifications
/// do not produce audit noise. It also passes <c>--ignore-unknown-format</c> to prevent noisy
/// unmatched-format warnings for non-OpenAPI/AsyncAPI JSON and YAML files.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Spectral Schema Linter",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "spectral",
    InstallHint = "provision the pinned spectral release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g @stoplight/spectral-cli@"
        + DefaultExpectedVersion + ") or standalone binary via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class SpectralAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.spectral";

    /// <summary>
    /// Spectral release the invocation and its findings are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "6.16.3";

    /// <summary>Scoped-config key for explicit ruleset file path.</summary>
    public const string RulesetPathKey = "RulesetPath";

    /// <summary>Scoped-config key for target document patterns / globs.</summary>
    public const string TargetPatternsKey = "TargetPatterns";

    private const string DefaultGlob = "**/*.{json,yml,yaml}";
    private const int ProbeMaxOutputBytes = 16 * 1024;
    private const int MessageValueMaxChars = 64;
    private static readonly TimeSpan ProbeTimeoutCap = TimeSpan.FromSeconds(30);
    private static readonly Regex VersionPattern = new(
        @"\d+\.\d+\.\d+[\w.\-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = clean or warnings only; 1 = rule violations found (errors). Both are verdicts.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Exclude vendored and third-party code by default so upstream schemas do not train
        // operators to ignore audit results.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _rulesetPath = static () => null;
    private Func<IReadOnlyList<string>> _targetPatterns = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:spectral";

    /// <inheritdoc />
    protected override string ToolName => "spectral";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// Declared mapping from Spectral's severity vocabulary to CodeyBox's <see cref="AuditSeverity"/>.
    /// Raw tool severities are never passed through directly.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var args = new List<string>
        {
            "lint",
            "-f", "sarif",
            "-q",
            "--ignore-unknown-format",
        };

        var ruleset = _rulesetPath();
        if (!string.IsNullOrWhiteSpace(ruleset)
            && !options.ExtraArguments.Contains("--ruleset", StringComparer.Ordinal)
            && !options.ExtraArguments.Contains("-r", StringComparer.Ordinal))
        {
            args.Add("--ruleset");
            args.Add(ruleset.Trim());
        }

        var targets = _targetPatterns();
        if (targets.Count > 0)
        {
            args.AddRange(targets);
        }
        else
        {
            args.Add(DefaultGlob);
        }

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped["ExpectedVersion"];
        _rulesetPath = () => scoped[RulesetPathKey] ?? scoped["Ruleset"];
        _targetPatterns = () => SplitList(scoped[TargetPatternsKey] ?? scoped["Documents"]);
        context.Logger.LogInformation(
            "SpectralAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Spectral-specific preconditions on the live path: validates that the installed
    /// spectral binary matches the pinned release (<c>ExpectedVersion</c>).
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        await ThrowIfToolVersionMismatchAsync(sandbox, workingDirectory, tool, options, ct)
            .ConfigureAwait(false);
    }

    private async Task ThrowIfToolVersionMismatchAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = _expectedVersion();
        var expected = NormalizeVersion(configured);
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable ExpectedVersion "
                + $"('{TruncateForMessage(configured)}'); set CodeyBox:Plugins:{PluginId}:ExpectedVersion "
                + $"to a {tool} release such as '{DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "version check",
            new SandboxExec
            {
                Argv = [tool, "--version"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        var reported = ExtractVersion(result.Stdout);
        if (result.ExecutionUnavailable
            || result.ExitCode != 0
            || reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + $"a missing or foreign '{tool}' is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' is version {reported}, but this auditor is "
                + $"pinned to {expected}. A different scanner version changes the rule set and the "
                + "findings; provision the pinned release or set ExpectedVersion to the version you provisioned.")
            { IsDeterministic = true };
    }

    private static TimeSpan ProbeTimeout(ExternalToolAuditorOptions options)
    {
        var timeout = EffectiveTimeout(options);
        return timeout > ProbeTimeoutCap ? ProbeTimeoutCap : timeout;
    }

    private static string? ExtractVersion(string stdout)
    {
        var match = VersionPattern.Match(stdout);
        return match.Success ? match.Value : null;
    }

    private static string? NormalizeVersion(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured)
            ? DefaultExpectedVersion
            : configured.Trim();
        var match = VersionPattern.Match(value);
        return match.Success ? match.Value : null;
    }

    private static string TruncateForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = SingleLine(value);
        return single.Length > MessageValueMaxChars
            ? single[..MessageValueMaxChars] + "…"
            : single;
    }

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => item.Length > 0)
                .ToList();
}
