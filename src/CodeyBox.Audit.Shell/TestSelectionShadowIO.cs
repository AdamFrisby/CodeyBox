using System.Globalization;
using System.Text.Json;
using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Baseline load outcome for the shadow hook: the parsed baseline (or null on
/// any failure) plus a human-readable detail fragment for the shadow record.
/// Every failure mode resolves to "no baseline" — the selectors then fall back
/// to the full suite (fail-safe).
/// </summary>
public sealed record ShadowBaselineResult(TestSelectionBaseline? Baseline, string Detail);

/// <summary>
/// Sandbox IO behind the test-selection shadow: changed-file enumeration via
/// <c>git diff</c> (same three-dot merge-base semantics as the coverage gate)
/// and baseline-artifact loading via <c>cat</c> with a byte cap. Pure
/// converters are split out so they are unit-testable without a sandbox.
/// </summary>
public static class TestSelectionShadowIO
{
    /// <summary>
    /// Enumerates the changed files (with line ranges) for the shadow request.
    /// A git-diff failure yields an empty list — the selectors treat that as
    /// "changeset unknown" and fall back to the full suite (fail-safe).
    /// </summary>
    public static async Task<IReadOnlyList<TestSelectionChangedFile>> GetChangedFilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string baseBranch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        if (string.IsNullOrWhiteSpace(baseBranch))
            return [];

        var diff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff", "--unified=0", "--no-color",
                    "--end-of-options", $"origin/{baseBranch}...HEAD"],
        }, ct).ConfigureAwait(false);

        if (!diff.Success)
        {
            diff = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "diff", "--unified=0", "--no-color",
                        "--end-of-options", $"{baseBranch}...HEAD"],
            }, ct).ConfigureAwait(false);
        }

        if (!diff.Success)
            return [];

        return ToChangedFiles(UnifiedDiffParser.ParseAddedLines(diff.Stdout));
    }

    /// <summary>
    /// Groups added diff lines by file, merging consecutive new-file lines
    /// into single <see cref="ChangedLineRange"/> runs. Lines with no file
    /// header are dropped (they cannot be attributed to a source file).
    /// </summary>
    public static IReadOnlyList<TestSelectionChangedFile> ToChangedFiles(
        IReadOnlyList<AddedDiffLine> addedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);

        var byFile = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var line in addedLines)
        {
            if (line.File is null || line.NewLine < 1)
                continue;
            if (!byFile.TryGetValue(line.File, out var set))
                byFile[line.File] = set = new SortedSet<int>();
            set.Add(line.NewLine);
        }

        return byFile
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new TestSelectionChangedFile(kvp.Key, ToRanges(kvp.Value)))
            .ToList();
    }

    private static IReadOnlyList<ChangedLineRange> ToRanges(SortedSet<int> lines)
    {
        var ranges = new List<ChangedLineRange>();
        int? runStart = null;
        var previous = 0;
        foreach (var line in lines)
        {
            if (runStart is null)
            {
                runStart = line;
            }
            else if (line != previous + 1)
            {
                ranges.Add(new ChangedLineRange(runStart.Value, previous - runStart.Value + 1));
                runStart = line;
            }
            previous = line;
        }
        if (runStart is not null)
            ranges.Add(new ChangedLineRange(runStart.Value, previous - runStart.Value + 1));
        return ranges;
    }

    /// <summary>
    /// Loads the baseline artifact from its sandbox path. Missing files, read
    /// failures, oversized output, and parse errors all yield a null baseline
    /// with a detail fragment — never a throw.
    /// </summary>
    public static async Task<ShadowBaselineResult> ReadBaselineAsync(
        ISandbox sandbox,
        string workingDirectory,
        Func<CoverageTestSelectionOptions> optionsAccessor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(optionsAccessor);

        CoverageTestSelectionOptions options;
        try
        {
            options = optionsAccessor();
        }
        catch (Exception ex)
        {
            return new ShadowBaselineResult(null, $"selection options unavailable ({ex.GetType().Name})");
        }

        if (!CoverageTestSelectionOptions.IsValid(options))
            return new ShadowBaselineResult(null, "selection options are invalid");
        if (string.IsNullOrWhiteSpace(options.BaselineSandboxPath))
            return new ShadowBaselineResult(null, "no baseline path is configured");

        var maxBytes = (int)Math.Min(options.MaxBaselineBytes, int.MaxValue);
        var read = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "--", options.BaselineSandboxPath],
            WorkingDirectory = workingDirectory,
            MaxStdoutBytes = maxBytes,
        }, ct).ConfigureAwait(false);

        if (!read.Success)
            return new ShadowBaselineResult(null, $"baseline not readable at '{options.BaselineSandboxPath}'");
        if (read.StdoutLimitExceeded)
        {
            return new ShadowBaselineResult(null, string.Create(
                CultureInfo.InvariantCulture,
                $"baseline exceeds the size cap ({maxBytes} bytes)"));
        }
        if (string.IsNullOrWhiteSpace(read.Stdout))
            return new ShadowBaselineResult(null, "baseline file is empty");

        try
        {
            var baseline = TestSelectionBaselineParser.Parse(
                read.Stdout, BaselineReadLimits.FromOptions(options));
            return new ShadowBaselineResult(baseline, string.Create(
                CultureInfo.InvariantCulture,
                $"commit '{baseline.Commit}' with {baseline.Tests.Count} recorded test(s)"));
        }
        catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is ArgumentOutOfRangeException)
        {
            return new ShadowBaselineResult(null, $"baseline unparseable ({ex.GetType().Name})");
        }
    }
}
