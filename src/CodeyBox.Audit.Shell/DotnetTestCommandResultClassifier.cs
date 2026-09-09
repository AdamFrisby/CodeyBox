using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

public sealed class DotnetTestCommandResultClassifier : IAuditResultClassifier
{
    // The test runner refused to run: VSTest rejected a test-source argument
    // (a missing assembly is reported as "invalid", not "not found"), or
    // MSBuild rejected the invocation before any target ran (unknown switch,
    // no discoverable project, nonexistent project file). No test executed,
    // so there is nothing attributable to the code under review — the failure
    // is an infrastructure fault (missing build artifacts, wrong working
    // directory, broken invocation), never a code finding.
    private static readonly Regex RunnerInvocationErrorRegex = new(
        @"^\s*The argument .+ is invalid\.|^\s*The test source file .+ was not found\.\s*$|MSB1001\s*:|MSB1003\s*:|MSB1009\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline,
        TimeSpan.FromSeconds(5));

    // Genuine toolchain verdicts about the code: the compiler, SDK, or NuGet
    // restore rejected the sources, or the build itself failed. These mean the
    // runner DID execute against the code and the code lost, so they stay
    // blocking code findings even when an invocation-shaped line appears
    // nearby (e.g. a stale VSTest banner in the same transcript).
    private static readonly Regex CodeFailureSignalRegex = new(
        @"error\s+(?:CS|NETSDK|NU)\d+\b|^\s*Build FAILED\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline,
        TimeSpan.FromSeconds(5));

    public AuditResult? ClassifyFailedCommand(AuditResultClassificationContext context)
    {
        var parsed = DotnetTestOutputParser.Parse(context.AuditorName, context.CombinedOutput);
        if (parsed.ParsedFailureCount == 0)
        {
            ThrowIfRunnerInvocationError(context);
            return null;
        }

        if (!parsed.HasCommandFailureSignals)
            return new AuditResult(
                parsed.Findings.Count == 0,
                parsed.Findings,
                RawOutput: context.CombinedOutput)
            {
                BuildTestGateEvidenceVerified = parsed.Findings.Count > 0 ? null : false,
            };

        if (parsed.Findings.Count == 0)
            return null;

        return new AuditResult(
            false,
            [.. parsed.Findings, context.CommandFinding],
            RawOutput: context.CombinedOutput);
    }

    /// <summary>
    /// Raises the runner-refused-to-run case as infrastructure. Zero parsed
    /// test failures plus a runner-invocation refusal — and no genuine
    /// compile/build failure signal — means no test executed, so the outcome
    /// cannot be attributed to the code. Throwing
    /// <see cref="AuditUnavailableException"/> routes the item to the
    /// infrastructure failure path (operator attention, rework budget intact)
    /// instead of feeding an unfixable blocking finding to the rework loop.
    /// </summary>
    /// <exception cref="AuditUnavailableException">
    /// Thrown when the output shows the runner refused its invocation.
    /// </exception>
    private static void ThrowIfRunnerInvocationError(AuditResultClassificationContext context)
    {
        if (!RunnerInvocationErrorRegex.IsMatch(context.CombinedOutput))
            return;
        if (CodeFailureSignalRegex.IsMatch(context.CombinedOutput))
            return;

        var signal = FirstSignalLine(context.CombinedOutput);
        throw new AuditUnavailableException(
            $"could-not-verify: test runner invocation failed for '{context.AuditorName}' (exit {context.Result.ExitCode}): {signal} (command: {string.Join(' ', context.Argv)})",
            context.Result.ExitCode,
            context.CombinedOutput);
    }

    private static string FirstSignalLine(string output)
    {
        foreach (var match in RunnerInvocationErrorRegex.EnumerateMatches(output.AsSpan()))
        {
            var start = match.Index;
            while (start > 0 && output[start - 1] is not ('\r' or '\n'))
                start--;
            var end = match.Index + match.Length;
            while (end < output.Length && output[end] is not ('\r' or '\n'))
                end++;
            var first = output.Substring(start, end - start).Trim();
            if (first.Length > 0)
                return first.Length <= 240 ? first : first[..240] + "...";
        }

        return "(runner invocation error)";
    }
}
