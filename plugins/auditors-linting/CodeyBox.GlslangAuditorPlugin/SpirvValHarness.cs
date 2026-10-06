using CodeyBox.Core;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// Static-only access to the shared <see cref="ExternalToolAuditorBase"/>
/// sandbox policy for the SPIR-V module auditor: <c>spirv-val</c> validates
/// exactly one binary per invocation (a second operand exits non-zero with
/// <c>More than one input file specified</c>), so the orchestrator
/// (<see cref="SpirvValAuditor"/>) validates each selected module with its
/// own invocation and aggregates the results. Rather than duplicating the
/// timeout, transport-loss, path-containment, and message-shaping policy —
/// or shadowing the base entry point, which would not change interface
/// dispatch — the orchestrator routes every sandbox and validation decision
/// through these wrappers, and the single-module engine
/// (<see cref="SpirvValSingleModuleScan"/>) keeps extending the base
/// directly. Abstract and never instantiated: the inherited abstract
/// auditor members stay unimplemented because no instance ever runs.
/// </summary>
internal abstract class SpirvValHarness : ExternalToolAuditorBase
{
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

    internal static Task<IReadOnlyList<string>> ProbeFilesPresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        IReadOnlyList<string> relativePaths,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => ProbeRepositoryFilesPresentAsync(sandbox, workingDirectory, tool, relativePaths, options, ct);

    internal static int ProbeOutputCap => ProbeMaxOutputBytes;

    /// <summary>
    /// Version gate for a tool whose releases are two-component
    /// (<c>vYYYY.N</c>, e.g. <c>SPIRV-Tools v2025.1 unknown hash, …</c> on
    /// stdout with exit 0 — verified against the pinned release's
    /// <c>--version</c> contract). The shared declarative pin extracts a
    /// three-component token on its expected side, which can never
    /// represent this scheme, so no declarative
    /// <see cref="ToolVersionPin"/> is set and the equivalent gate lives
    /// here: the reported token (extracted by
    /// <see cref="SpirvValidationSupport.ExtractSpirvToolsVersion"/>) must
    /// be present and exactly equal to the configured-or-default
    /// expectation. Runs once up front from the orchestrator and again per
    /// module from the scan engine, so a future caller that scans without
    /// the orchestrator keeps the pin.
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
        var expected = SpirvValidationSupport.ExtractSpirvToolsVersion(
            string.IsNullOrWhiteSpace(configured)
                ? SpirvValAuditor.DefaultExpectedVersion
                : configured.Trim());
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{SpirvValAuditor.AuditorName}' has an unparseable {ToolVersionPin.ExpectedVersionKey} "
                + $"('{TruncateForMessage(configured)}'); set CodeyBox:Plugins:{SpirvValAuditor.PluginId}:{ToolVersionPin.ExpectedVersionKey} "
                + $"to a {tool} release such as '{SpirvValAuditor.DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var probe = await ExecToolBoundedAsync(
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

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);

        var reported = SpirvValidationSupport.ExtractSpirvToolsVersion(probe.Stdout + "\n" + probe.Stderr);
        if (probe.ExitCode != 0
            || reported is null)
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
}
