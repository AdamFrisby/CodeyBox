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
    /// Sanitizes a provider-derived window name for safe rendering into log
    /// lines and DTO text. Provider tokens are untrusted input: a hostile or
    /// buggy probe could carry CR/LF (log injection), C0/C1 controls, or ANSI
    /// escape sequences (terminal-escape injection). JSON encoding does not
    /// protect the structured-log sink, so the name is allowlisted here, once,
    /// and every render site routes through this method. Allowed characters
    /// are ASCII letters, digits, <c>_</c>, and <c>-</c>; anything else becomes
    /// <c>_</c>. The result is then truncated to
    /// <see cref="MaxWindowNameLength"/> characters. Names that sanitize to
    /// empty fall back to <c>window</c> so callers never render empty quotes.
    /// Pure.
    /// </summary>
    public static string SanitizeWindowName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "window";
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "window";
        var chars = new char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            var allowed = c == '_' || c == '-'
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9');
            chars[i] = allowed ? c : '_';
        }
        var sanitized = new string(chars);
        if (sanitized.Length > MaxWindowNameLength) sanitized = sanitized[..MaxWindowNameLength];
        return sanitized.Length == 0 ? "window" : sanitized;
    }

    /// <summary>
    /// Returns the name of the known window with the least remaining
    /// availability, or null when no window carries a usable reading.
    /// Windows with unknown readings (negative) never bind. Ties resolve to
    /// the first scarcest window in probe order so the answer is
    /// deterministic for a given snapshot. The returned name is sanitized via
    /// <see cref="SanitizeWindowName"/> so it is safe to render into log lines
    /// and DTO text. Pure.
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
        return binding is null ? null : SanitizeWindowName(binding);
    }

    /// <summary>
    /// Renders the known windows scarcest-first, marking the binding window,
    /// e.g. <c>monthly 6.0% (binding), weekly 98.0%, rolling 100.0%</c>.
    /// Returns null when no window carries a usable reading. Bounded to
    /// <see cref="MaxSummaryWindows"/> entries with window names sanitized via
    /// <see cref="SanitizeWindowName"/> (allowlisted, then truncated), so a
    /// hostile probe payload can inject neither log lines nor terminal escapes
    /// nor unbounded text into a log line or DTO field. Pure.
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
        var binding = ResolveBindingWindow(known);
        var parts = new List<string>(Math.Min(known.Count, MaxSummaryWindows));
        foreach (var window in known)
        {
            if (parts.Count >= MaxSummaryWindows) break;
            var name = SanitizeWindowName(window.Name);
            var marker = string.Equals(name, binding, StringComparison.Ordinal) ? " (binding)" : string.Empty;
            parts.Add($"{name} {window.AvailablePct:F1}%{marker}");
        }
        return string.Join(", ", parts);
    }
}
