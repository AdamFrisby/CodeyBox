using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable options for the merge-to-main admission flake gate, bound
/// from the <c>Audit:Flake</c> configuration section (full path
/// <c>CodeyBox:Audit:Flake</c>) via <c>IOptionsMonitor</c> so edits take
/// effect without a process restart. Every operational value is a knob, not
/// a source literal.
/// </summary>
public sealed class FlakeAdmissionOptions
{
    /// <summary>Configuration section this binds to (relative to <c>CodeyBox:</c>).</summary>
    public const string SectionName = "Audit:Flake";

    /// <summary>Default number of admission runs for affected tests.</summary>
    public const int DefaultAdmissionReruns = 3;

    /// <summary>
    /// Upper bound for <see cref="AdmissionReruns"/>. Caps cost against an
    /// unbounded operator value; clamping is always logged explicitly by the
    /// consumer, never silent.
    /// </summary>
    public const int MaxAdmissionReruns = 10;

    /// <summary>
    /// How many times the pre-merge gate runs the affected tests. Default 3:
    /// a single green run cannot wave a flaky test through. Values below 1
    /// are clamped to 1 (the gate always runs at least once) and values above
    /// <see cref="MaxAdmissionReruns"/> are clamped to the max; both clamps
    /// are logged explicitly at the gate.
    /// </summary>
    public int AdmissionReruns { get; set; } = DefaultAdmissionReruns;

    /// <summary>
    /// Effective run count after clamping to <c>[1, <see cref="MaxAdmissionReruns"/>]</c>.
    /// </summary>
    public int EffectiveAdmissionReruns() => Math.Clamp(AdmissionReruns, 1, MaxAdmissionReruns);

    /// <summary>True when the configured value needs an explicit clamp log.</summary>
    public bool NeedsClampLog() => AdmissionReruns != EffectiveAdmissionReruns();

    /// <summary>Structural validity for fail-fast options validation.</summary>
    public static bool IsValid(FlakeAdmissionOptions? opts) =>
        opts is not null && opts.AdmissionReruns >= 1 && opts.AdmissionReruns <= MaxAdmissionReruns;
}

/// <summary>
/// One <c>dotnet test</c> failure header: the fully-qualified test name plus
/// the raw duration text and the header's span in the output (for slicing the
/// per-test body that follows it). Single source of truth for the
/// <c>Failed &lt;name&gt; [&lt;duration&gt;]</c> header shape, shared by the
/// audit-shell output parser and the pre-merge admission gate so the two
/// cannot drift apart.
/// </summary>
public sealed record DotnetTestFailureHeader(string Name, string DurationText, int Index, int Length);

/// <summary>Result of <see cref="DotnetTestFailureHeaders.Extract"/>.</summary>
public sealed record DotnetTestFailureHeaderParseResult(
    IReadOnlyList<DotnetTestFailureHeader> Headers,
    bool HitHeaderCap);

/// <summary>
/// Extracts <c>dotnet test</c> failure headers
/// (<c>Failed &lt;name&gt; [&lt;duration&gt;]</c>) from combined command
/// output. Header-only: classification (unrunnable vs. real failure) stays
/// with the audit-shell parser; the admission gate needs just the names to
/// compare outcomes across reruns.
/// </summary>
public static class DotnetTestFailureHeaders
{
    private static readonly Regex HeaderRegex = new(
        @"^\s*Failed\s+(?<name>.+?)\s+\[(?<duration>[^\]\r\n]+)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromSeconds(5));

    /// <summary>Maximum headers extracted; bounds work on pathological output.</summary>
    public const int MaxHeaders = 1_024;

    /// <summary>
    /// Extracts up to <see cref="MaxHeaders"/> failure headers in output
    /// order, plus whether more headers remained beyond the cap. Never throws
    /// on malformed input: a regex timeout yields the headers found so far.
    /// </summary>
    public static DotnetTestFailureHeaderParseResult Extract(string output)
    {
        var headers = new List<DotnetTestFailureHeader>();
        if (string.IsNullOrEmpty(output))
            return new DotnetTestFailureHeaderParseResult(headers, HitHeaderCap: false);

        Match match;
        try
        {
            match = HeaderRegex.Match(output);
        }
        catch (RegexMatchTimeoutException)
        {
            return new DotnetTestFailureHeaderParseResult(headers, HitHeaderCap: false);
        }

        while (match.Success && headers.Count < MaxHeaders)
        {
            headers.Add(new DotnetTestFailureHeader(
                match.Groups["name"].Value.Trim(),
                match.Groups["duration"].Value.Trim(),
                match.Index,
                match.Length));
            try
            {
                match = match.NextMatch();
            }
            catch (RegexMatchTimeoutException)
            {
                return new DotnetTestFailureHeaderParseResult(headers, HitHeaderCap: false);
            }
        }

        return new DotnetTestFailureHeaderParseResult(headers, HitHeaderCap: match.Success);
    }

    /// <summary>
    /// Extracts up to <see cref="MaxHeaders"/> failure headers in output
    /// order. Convenience wrapper over <see cref="Extract"/> for callers
    /// that only need the names.
    /// </summary>
    public static IReadOnlyList<DotnetTestFailureHeader> ExtractNames(string output) =>
        Extract(output).Headers;
}

/// <summary>One admission run's outcome for flake evaluation.</summary>
public sealed record FlakeAdmissionRunResult(
    bool Succeeded,
    IReadOnlyList<string> FailedTestNames);

/// <summary>Verdict of the admission rerun comparison.</summary>
public enum FlakeAdmissionVerdict
{
    /// <summary>Every run passed — the gate passes.</summary>
    Pass = 0,
    /// <summary>Every run failed (same outcome) — blocks as today.</summary>
    FailDeterministic = 1,
    /// <summary>Outcome differed across runs — flaky, blocks as a new finding.</summary>
    Flaky = 2,
}

/// <summary>
/// Pure evaluation of admission reruns. A test (or the command itself) that
/// passes on some runs and fails on others is non-deterministic and must
/// block the merge: a single green run must never wave it through.
/// </summary>
public static class FlakeAdmissionEvaluator
{
    /// <summary>
    /// Evaluates per-run outcomes. Empty input is treated as deterministic
    /// failure (no evidence of green); callers must always pass the runs
    /// they actually executed.
    /// </summary>
    public static (FlakeAdmissionVerdict Verdict, IReadOnlyList<string> FlakyTestNames) Evaluate(
        IReadOnlyList<FlakeAdmissionRunResult> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            return (FlakeAdmissionVerdict.FailDeterministic, []);

        if (runs.All(static r => r.Succeeded))
            return (FlakeAdmissionVerdict.Pass, []);

        if (runs.All(static r => !r.Succeeded))
        {
            var first = new HashSet<string>(runs[0].FailedTestNames, StringComparer.Ordinal);
            for (var i = 1; i < runs.Count; i++)
            {
                if (!first.SetEquals(runs[i].FailedTestNames))
                    return (FlakeAdmissionVerdict.Flaky, OrderedUnion(runs));
            }

            return (FlakeAdmissionVerdict.FailDeterministic, []);
        }

        return (FlakeAdmissionVerdict.Flaky, OrderedUnion(runs));
    }

    private static IReadOnlyList<string> OrderedUnion(IReadOnlyList<FlakeAdmissionRunResult> runs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var run in runs)
        {
            foreach (var name in run.FailedTestNames)
            {
                if (seen.Add(name))
                    ordered.Add(name);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Builds the operator-visible reason for a flaky verdict. Names every
    /// non-deterministic test (bounded) so the finding is actionable.
    /// </summary>
    public static string BuildFlakyReason(
        int runCount,
        int passCount,
        IReadOnlyList<string> flakyTestNames,
        int maxNames = 20)
    {
        ArgumentNullException.ThrowIfNull(flakyTestNames);
        var failCount = runCount - passCount;
        if (flakyTestNames.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"flaky admission: test command passed {passCount} of {runCount} runs and failed {failCount} " +
                $"with no parseable test names — non-deterministic outcome blocks the merge");
        }

        // Agent-controlled test names flow into LastError/logs and the operator
        // terminal, so strip terminal control characters (and secret tokens)
        // at this sink before joining, matching SummariseOutput redaction.
        var shown = flakyTestNames
            .Select(static name => RawOutputRedactor.Redact(name ?? string.Empty))
            .Take(maxNames);
        var suffix = flakyTestNames.Count > maxNames
            ? string.Create(CultureInfo.InvariantCulture, $" (and {flakyTestNames.Count - maxNames} more)")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"flaky test(s) detected across {runCount} admission runs " +
            $"(passed {passCount}, failed {failCount}): {string.Join(", ", shown)}{suffix}");
    }
}
