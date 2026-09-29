namespace CodeyBox.Core;

/// <summary>
/// Pure helpers that name the binding quota window: the known window with the
/// least remaining availability. Probes aggregate multi-window readings by
/// taking the minimum (see <see cref="AgentQuotaSnapshot.AvailablePct"/>), so
/// the binding window is the one that actually gates dispatch. Surfacing its
/// name keeps a plan with 98% of its weekly budget untouched from reading as
/// simply "out of quota" when the monthly window is the constraint.
/// </summary>
public static class QuotaWindowBinding
{
    /// <summary>Maximum windows rendered into a human-readable summary.</summary>
    public const int MaxSummaryWindows = 8;

    /// <summary>Maximum characters of a window name rendered into a summary.</summary>
    public const int MaxWindowNameLength = 64;

    /// <summary>
    /// Returns the name of the known window with the least remaining
    /// availability, or null when no window carries a usable reading.
    /// Windows with unknown readings (negative) never bind. Ties resolve to
    /// the first scarcest window in probe order so the answer is
    /// deterministic for a given snapshot. Pure.
    /// </summary>
    public static string? ResolveBindingWindow(IReadOnlyList<WindowQuota>? windows)
    {
        if (windows is null || windows.Count == 0) return null;
        string? binding = null;
        var best = double.PositiveInfinity;
        foreach (var window in windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.Name)) continue;
            var available = window.AvailablePct;
            if (!double.IsFinite(available) || available < 0) continue;
            if (available < best)
            {
                best = available;
                binding = window.Name;
            }
        }
        return binding;
    }

    /// <summary>
    /// Renders the known windows scarcest-first, marking the binding window,
    /// e.g. <c>monthly 6.0% (binding), weekly 98.0%, rolling 100.0%</c>.
    /// Returns null when no window carries a usable reading. Bounded to
    /// <see cref="MaxSummaryWindows"/> entries with window names truncated to
    /// <see cref="MaxWindowNameLength"/> characters, so a hostile probe
    /// payload cannot grow a log line or DTO field without bound. Pure.
    /// </summary>
    public static string? FormatWindowSummary(IReadOnlyList<WindowQuota>? windows)
    {
        if (windows is null || windows.Count == 0) return null;
        var known = new List<WindowQuota>(windows.Count);
        foreach (var window in windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.Name)) continue;
            if (!double.IsFinite(window.AvailablePct) || window.AvailablePct < 0) continue;
            known.Add(window);
        }
        if (known.Count == 0) return null;
        known.Sort(static (a, b) => a.AvailablePct.CompareTo(b.AvailablePct));
        var binding = known[0].Name;
        var parts = new List<string>(Math.Min(known.Count, MaxSummaryWindows));
        foreach (var window in known)
        {
            if (parts.Count >= MaxSummaryWindows) break;
            var name = window.Name.Trim();
            if (name.Length > MaxWindowNameLength) name = name[..MaxWindowNameLength];
            var marker = string.Equals(window.Name, binding, StringComparison.Ordinal) ? " (binding)" : string.Empty;
            parts.Add($"{name} {window.AvailablePct:F1}%{marker}");
        }
        return string.Join(", ", parts);
    }
}
