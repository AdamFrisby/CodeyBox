using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure decision helper for NotDiffAttributable audit-test escalation.
/// Selects actionable attributions, normalizes test names, builds the
/// deterministic de-dup key, the child title/prompt, and matches existing
/// open fix-tasks. No I/O; the service owns persistence.
/// </summary>
public static class NonDeterministicTestEscalationPolicy
{
    /// <summary>ExternalIds namespace marking a flake-fix child item.</summary>
    public const string FixMarkerNamespace = "flake-fix";

    /// <summary>Maximum characters for a single test name carried into prompt/title.</summary>
    public const int MaxTestNameChars = 256;

    /// <summary>
    /// Returns the distinct, actionable flaky test names: attribution is
    /// NotDiffAttributable with SkipReason None (a genuine base-branch
    /// verdict, not a fail-closed attribution). Empty when nothing qualifies.
    /// </summary>
    public static IReadOnlyList<string> SelectActionableTests(
        IEnumerable<TestFailureAttributionResult>? attributions,
        int maxTestsPerChild)
    {
        if (attributions is null)
            return [];
        var cap = Math.Clamp(maxTestsPerChild, 1, 50);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var a in attributions)
        {
            if (a.Attribution != TestFailureAttribution.NotDiffAttributable)
                continue;
            if (a.SkipReason != TestFailureAttributionSkipReason.None)
                continue;
            var name = NormalizeTestName(a.TestName);
            if (name is null)
                continue;
            if (!seen.Add(name))
                continue;
            result.Add(name);
            if (result.Count >= cap)
                break;
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Trims and bounds a single test name. Returns null for empty input.
    /// Fully-qualified names are kept verbatim (no substring matching
    /// downstream — de-dup compares exact normalized names).
    /// </summary>
    public static string? NormalizeTestName(string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return null;
        var trimmed = testName.Trim();
        if (trimmed.Length == 0)
            return null;
        if (trimmed.Length > MaxTestNameChars)
            trimmed = trimmed[..MaxTestNameChars];
        return trimmed;
    }

    /// <summary>
    /// Deterministic de-dup key for a normalized, sorted test-name set.
    /// SHA256 hex over NUL-joined names; safe for ExternalIds (no whitespace,
    /// fixed 64 chars).
    /// </summary>
    public static string ComputeDedupKey(IReadOnlyList<string> normalizedSortedTests)
    {
        ArgumentNullException.ThrowIfNull(normalizedSortedTests);
        var joined = string.Join('\0', normalizedSortedTests);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Builds the child title for the given tests.</summary>
    public static string BuildChildTitle(IReadOnlyList<string> tests, int maxTitleTestNames)
    {
        ArgumentNullException.ThrowIfNull(tests);
        var cap = Math.Clamp(maxTitleTestNames, 1, 10);
        var shown = tests.Take(cap).ToArray();
        var title = $"[Flaky test] Stabilize {string.Join(", ", shown)}";
        if (tests.Count > shown.Length)
            title += $" (+{tests.Count - shown.Length} more)";
        const int maxTitleChars = 200;
        return title.Length <= maxTitleChars ? title : title[..maxTitleChars];
    }

    /// <summary>
    /// Builds the zero-tolerance child prompt: reproduce on base, find the
    /// non-determinism source, fix the test (or code under test). Explicitly
    /// forbids skips, Trait/quarantine attributes, and retry-attributes cover.
    /// </summary>
    public static string BuildChildPrompt(
        IReadOnlyList<string> tests,
        string baseBranch,
        string parentTitle,
        WorkItemId parentId)
    {
        ArgumentNullException.ThrowIfNull(tests);
        var branch = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("Stabilize the following non-deterministic test(s). They failed during audit but the same failure reproduces on the base branch, so the parent work item did not cause them:");
        foreach (var t in tests)
            sb.AppendLine($"- {t}");
        sb.AppendLine();
        sb.AppendLine($"Base branch: {branch}");
        sb.AppendLine($"Parent work item: {parentId} ({parentTitle})");
        sb.AppendLine();
        sb.AppendLine("Steps:");
        sb.AppendLine("1. Reproduce each listed test against the base branch in isolation and under repetition until the flake shows.");
        sb.AppendLine("2. Find the non-determinism source (ordering, timing, shared state, randomness seed, parallel interference, external dependency, time/date sensitivity).");
        sb.AppendLine("3. Fix the test — or the code under test when the test exposed a real race — so the test becomes genuinely deterministic.");
        sb.AppendLine();
        sb.AppendLine("Hard constraints (zero tolerance):");
        sb.AppendLine("- Do NOT skip, ignore, or conditionally disable the test.");
        sb.AppendLine("- Do NOT use [Trait], quarantine categories, or any skip/ignore attribute as a cover.");
        sb.AppendLine("- Do NOT add retry attributes or retry wrappers to mask the flake.");
        sb.AppendLine("- The test must pass reliably under repetition on both the base branch and your branch.");
        return sb.ToString();
    }

    /// <summary>
    /// Finds an existing open (non-terminal) fix-task in the same project
    /// carrying the same de-dup key. Returns null when no such item exists.
    /// Exact-match only; never substring.
    /// </summary>
    public static WorkItem? FindExistingFixTask(
        IReadOnlyList<WorkItem> allItems,
        ProjectId projectId,
        string dedupKey)
    {
        ArgumentNullException.ThrowIfNull(allItems);
        ArgumentNullException.ThrowIfNull(dedupKey);
        foreach (var item in allItems)
        {
            if (item.ProjectId != projectId)
                continue;
            if (WorkItemDependencies.TerminalStates.Contains(item.State))
                continue;
            if (item.ExternalIds.TryGetValue(FixMarkerNamespace, out var v)
                && string.Equals(v, dedupKey, StringComparison.Ordinal))
                return item;
        }
        return null;
    }
}
