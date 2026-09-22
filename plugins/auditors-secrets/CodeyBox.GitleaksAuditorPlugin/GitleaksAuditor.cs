using System.Globalization;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GitleaksAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping <c>gitleaks</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, SARIF parsing,
/// severity mapping, exit-code classification, and per-auditor configuration.
///
/// <para><b>Gate behaviour: blocking by default.</b> gitleaks has no severity
/// vocabulary — every SARIF result is a detected credential — so every finding
/// maps to <see cref="AuditSeverity.Error"/> and any surviving finding fails
/// the audit. Scope findings down with <c>ExcludedRules</c>,
/// <c>ExcludePaths</c>, or the repository's own <c>.gitleaks.toml</c> /
/// <c>.gitleaksignore</c> rather than expecting advisory severity.</para>
///
/// <para><b>Exit-code convention (verified against gitleaks v8.x).</b>
/// gitleaks's default is unusable as-is: findings exit <c>--exit-code</c>
/// (default 1) and every failure mode — config load, scan error, report-write
/// failure, zerolog <c>Fatal</c> — also exits 1, so "found something" and
/// "could not run" would be indistinguishable. The auditor overrides
/// <c>--exit-code</c> to <see cref="LeaksFoundExitCode"/>: 0 is a clean run,
/// 4 is "ran with findings", and anything else (1, 126 usage error, 126/127
/// cannot-execute) is infrastructure. Upstream also exits 1 on error before
/// checking findings, so a partial scan can never look like a verdict.</para>
///
/// <para><b>Version pin.</b> A scanner's rule set changes between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. gitleaks's SARIF driver stamps a constant "v8.0.0" rather than the
/// real release, so the version is probed with <c>gitleaks version</c> before
/// the scan; a missing binary, an unrecognised version string, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass, never a finding.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.gitleaks",
    displayName: "CodeyBox: Gitleaks Secrets",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "gitleaks",
    InstallHint = "provision the pinned gitleaks release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline; the distro apt package is "
        + "unpinned and too old on Ubuntu LTS — install the pinned upstream binary via "
        + "CodeyBox:MultipassExtraRuncmd / Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class GitleaksAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.gitleaks";

    /// <summary>
    /// gitleaks release the invocation and its findings are verified against.
    /// Anything older than 8.24.0 lacks <c>--report-path -</c> (SARIF on
    /// stdout) and the <c>git</c> subcommand, so it fails closed. Operators
    /// running a different pinned build set <c>ExpectedVersion</c> in the
    /// plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "8.30.1";

    /// <summary>
    /// Exit code assigned to "ran and found secrets" via <c>--exit-code</c>.
    /// Disjoint from every gitleaks error convention (0 clean, 1 error/fatal,
    /// 126 usage, 127 not-found) so only this value and 0 are verdicts.
    /// </summary>
    internal const int LeaksFoundExitCode = 4;

    // Mirrors the TimeoutSeconds ceiling in ExternalToolAuditorOptions.Bind so
    // the tool's own --timeout never exceeds what the outer bound allows.
    private const int MaxToolTimeoutSeconds = 3600;
    private const int VersionProbeMaxOutputBytes = 16 * 1024;
    // The version probe is a liveness check, not the scan: it never needs more
    // than this and shares the operator-configured timeout below it.
    private static readonly TimeSpan VersionProbeTimeoutCap = TimeSpan.FromSeconds(30);
    private static readonly Regex VersionPattern = new(
        @"\d+\.\d+\.\d+[\w.\-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        FindingsExitCodes = new HashSet<int> { 0, LeaksFoundExitCode },
        // Findings inside vendored/dependency trees describe upstream code, not
        // the change under audit — noise that trains operators to ignore the
        // auditor. Repo-level allowlisting belongs to gitleaks's own
        // .gitleaks.toml / .gitleaksignore; operators re-include a path by
        // overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;

    /// <inheritdoc />
    public override string Name => "codeybox:gitleaks";

    /// <inheritdoc />
    protected override string ToolName => "gitleaks";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    /// <summary>
    /// gitleaks reports no per-finding severity, so the declared mapping is
    /// total: every level the parser can supply — including the "warning" it
    /// substitutes for gitleaks's absent SARIF level — maps to
    /// <see cref="AuditSeverity.Error"/>. Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase),
            AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var timeoutSeconds = options.Timeout <= TimeSpan.Zero
            ? ExternalToolAuditorOptions.DefaultTimeoutSeconds
            : (int)Math.Clamp(Math.Ceiling(options.Timeout.TotalSeconds), 1, MaxToolTimeoutSeconds);
        return
        [
            // `git` scans committed source plus full history — the "source and
            // git history" scope — rather than only the filesystem worktree.
            "git", ".",
            "--report-format", "sarif",
            // "-" is gitleaks's stdout report sink; SARIF is all of stdout.
            "--report-path", "-",
            "--exit-code", LeaksFoundExitCode.ToString(CultureInfo.InvariantCulture),
            // The detected secret must never land in findings or raw output.
            "--redact=100",
            "--no-banner",
            "--no-color",
            // stderr carries diagnostics only; the report carries the verdict.
            "--log-level", "error",
            // Cooperative in-tool bound under the base's outer timeout.
            "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped["ExpectedVersion"];
        context.Logger.LogInformation(
            "GitleaksAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds the version precondition the shared base has no seam for, then
    /// delegates to it for the scan. Dispatch through <see cref="IAuditor"/>
    /// (the only call path) resolves here; the base still owns invocation,
    /// parsing, severity mapping, and failure classification.
    /// </summary>
    public new async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(context);
        var options = _optionsAccessor() ?? new ExternalToolAuditorOptions();
        await ThrowIfToolVersionMismatchAsync(sandbox, workingDirectory, options, ct).ConfigureAwait(false);
        return await base.RunAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
    }

    private async Task ThrowIfToolVersionMismatchAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var expected = NormalizeVersion(_expectedVersion());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable ExpectedVersion "
                + $"('{TruncateForMessage(_expectedVersion())}'); set CodeyBox:Plugins:{PluginId}:ExpectedVersion "
                + $"to a gitleaks release such as '{DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var probeTimeout = options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(ExternalToolAuditorOptions.DefaultTimeoutSeconds)
            : options.Timeout;
        if (probeTimeout > VersionProbeTimeoutCap)
            probeTimeout = VersionProbeTimeoutCap;

        SandboxExecResult result;
        using var timeoutCts = new CancellationTokenSource(probeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["gitleaks", "version"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = VersionProbeMaxOutputBytes,
                MaxStderrBytes = VersionProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            }, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool 'gitleaks' version check timed out after {probeTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool 'gitleaks' version check could not run: {SingleLine(ex.Message)}",
                ex);
        }

        var reported = ExtractVersion(result.Stdout);
        if (result.ExecutionUnavailable
            || result.ExitCode != 0
            || reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool 'gitleaks' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + "a missing or foreign 'gitleaks' is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool 'gitleaks' is version {reported}, but this auditor is "
                + $"pinned to {expected}. A different scanner version changes the rule set and the "
                + "findings; provision the pinned release or set ExpectedVersion to the version you provisioned.")
            { IsDeterministic = true };
    }

    private static string? ExtractVersion(string stdout)
    {
        var match = VersionPattern.Match(stdout ?? string.Empty);
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
        return single.Length > 64 ? single[..64] + "…" : single;
    }

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
