using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GltfValidatorAuditorPlugin;

/// <summary>
/// Static-only access to the shared <see cref="ExternalToolAuditorBase"/>
/// sandbox policy for auditors that orchestrate several bounded tool
/// invocations per run. The base runs exactly one invocation per audit, but
/// the glTF CLI's <c>--stdout</c> report mode accepts exactly one asset, so
/// <see cref="GltfValidatorAuditor"/> validates each selected asset with its
/// own invocation and aggregates the results. Rather than duplicating the
/// timeout, transport-loss, path-containment, and message-shaping policy —
/// or shadowing the base entry point, which would not change interface
/// dispatch — the orchestrator (a plain <see cref="IAuditor"/>) routes every
/// sandbox and validation decision through these wrappers, and the
/// single-asset engine (<see cref="GltfValidatorSingleAssetScan"/>) keeps
/// extending the base directly. Abstract and never instantiated: the
/// inherited abstract auditor members stay unimplemented because no
/// instance ever runs.
/// </summary>
internal abstract class GltfAuditorHarness : ExternalToolAuditorBase
{
    /// <summary>Upper bound on the length of one selected asset path.</summary>
    internal const int MaxAssetPathLength = 1024;

    // Asset paths are untrusted repository content: a URI scheme here would
    // turn the scan target into a remote reference, so anything shaped like
    // "<scheme>:" is rejected before it can reach the tool argv.
    private static readonly Regex UriSchemePattern = new(
        @"^[A-Za-z][A-Za-z0-9+.\-]*:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static Task<SandboxExecResult> ExecBoundedAsync(
        ISandbox sandbox,
        string tool,
        string operation,
        SandboxExec exec,
        TimeSpan timeout,
        CancellationToken ct)
        => ExecToolBoundedAsync(sandbox, tool, operation, exec, timeout, ct);

    internal static TimeSpan ProbeTimeoutFor(ExternalToolAuditorOptions options)
        => ProbeTimeout(options);

    internal static int CombinedOutputCap(ExternalToolAuditorOptions options)
        => CapturedOutputLimit(options);

    internal static string SingleLineMessage(string message)
        => SingleLine(message);

    internal static string TruncateMessage(string? value)
        => TruncateForMessage(value);

    internal static Task RequireBinaryPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string binary,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ThrowIfBinaryMissingAsync(sandbox, workingDirectory, binary, options, ct);

    /// <summary>
    /// Version gate for a CLI that defines no <c>--version</c> flag (its
    /// argument parser only knows the validation flags), so the shared
    /// <c>&lt;tool&gt; --version</c> probe — exit 0 with a token on stdout —
    /// can never succeed. Invoking the bare binary prints
    /// <c>glTF 2.0 Validator, version &lt;release&gt;</c> with usage text
    /// (verified against <c>lib/cmd_line.dart</c> upstream), so the gate is
    /// the version token in the combined output — present and exactly equal
    /// to the pin — not the exit code. Runs once up front from the
    /// orchestrator and again per asset from the scan engine, so a future
    /// caller that scans without the orchestrator keeps the pin.
    /// </summary>
    internal static async Task EnsureToolVersionAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        Func<string?> expectedVersion,
        CancellationToken ct)
    {
        var configured = expectedVersion();
        var expected = ExtractToolVersion(
            string.IsNullOrWhiteSpace(configured)
                ? GltfValidatorAuditor.DefaultExpectedVersion
                : configured.Trim());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{GltfValidatorAuditor.AuditorName}' has an unparseable {ToolVersionPin.ExpectedVersionKey} "
                + $"('{TruncateForMessage(configured)}'); set CodeyBox:Plugins:{GltfValidatorAuditor.PluginId}:{ToolVersionPin.ExpectedVersionKey} "
                + $"to a {tool} release such as '{GltfValidatorAuditor.DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var probe = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "version check",
            new SandboxExec
            {
                Argv = [tool],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);

        var reported = ExtractToolVersion(probe.Stdout + "\n" + probe.Stderr);
        if (reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' version could not be determined "
                + $"(exit {probe.ExitCode}). The pinned release is required before the scan can run — "
                + $"a missing or foreign '{tool}' is infrastructure, not a verdict on the diff.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' is version {reported}, but this auditor is "
                + $"pinned to {expected}. A different release changes the tool's checks and its "
                + $"findings; provision the pinned release or set {ToolVersionPin.ExpectedVersionKey} "
                + "to the version you provisioned.")
            { IsDeterministic = true };
    }

    internal static int ProbeOutputCap => ProbeMaxOutputBytes;

    internal static string? ExtractVersion(string output)
        => ExtractToolVersion(output);

    /// <summary>
    /// Containment sink for every asset path that reaches the tool argv —
    /// discovered or operator-configured. Beyond the shared
    /// repo-relative/argv guard, an entry must name a <c>.gltf</c>/<c>.glb</c>
    /// file and must not be shaped like a URI: the validator resolves
    /// relative references against the asset, but the scan target itself is
    /// never a remote reference. Returns the canonical bare repo-relative
    /// form.
    /// </summary>
    internal static string ContainAssetPath(string value, string source)
    {
        var validated = ValidatedRepoRelativeTarget(value, source);
        var normalized = validated.Replace('\\', '/').Trim();
        var stripped = normalized.StartsWith("./", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
        if (stripped.Length == 0
            || stripped.Length > MaxAssetPathLength
            || stripped.StartsWith("/", StringComparison.Ordinal)
            || stripped.Any(char.IsControl)
            || stripped.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(value)}') "
                + "must be a repo-relative path inside the worktree.")
            { IsDeterministic = true };
        if (UriSchemePattern.IsMatch(stripped))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(value)}') "
                + "must be a repository file, not a URI — remote references are rejected: the scan "
                + "target is always a worktree asset whose external references resolve inside the "
                + "audit sandbox.")
            { IsDeterministic = true };
        if (!stripped.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
            && !stripped.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(value)}') "
                + "must name a .gltf or .glb asset.")
            { IsDeterministic = true };
        return stripped;
    }
}
