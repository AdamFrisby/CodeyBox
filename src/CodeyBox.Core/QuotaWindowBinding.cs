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

    /// <summary>Window name rendered when the provider's name sanitizes to nothing usable.</summary>
    public const string FallbackWindowName = "window";

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
    /// empty fall back to <see cref="FallbackWindowName"/> so callers never
    /// render empty quotes. Pure.
    /// </summary>
    public static string SanitizeWindowName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FallbackWindowName;
        var trimmed = name.Trim();
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
        return sanitized;
    }

    /// <summary>
    /// Returns the sanitized name of the known window with the least remaining
    /// availability, or null when no window carries a usable reading.
    /// Windows with unknown readings (negative) never bind. Ties resolve to
    /// the first scarcest window in probe order so the answer is
    /// deterministic for a given snapshot. Pure.
    /// </summary>
    public static string? ResolveBindingWindow(IReadOnlyList<WindowQuota>? windows) =>
        FindBindingWindow(windows) is { } binding
            ? SanitizeWindowName(binding.Name)
            : null;

    /// <summary>
    /// Returns the sanitized name of the binding window, but only when that
    /// window's reading IS <paramref name="producedPct"/>: the aggregate
    /// reading. Probes aggregate via the minimum, so equality pins which
    /// window produced the aggregate; when the aggregate came from elsewhere
    /// (a per-model fallback, a budget composite) the result is null rather
    /// than a name that did not gate it. Pure.
    /// </summary>
    public static string? ResolveBindingWindow(
        IReadOnlyList<WindowQuota>? windows, double producedPct)
    {
        var binding = FindBindingWindow(windows);
        return binding is not null && binding.AvailablePct == producedPct
            ? SanitizeWindowName(binding.Name)
            : null;
    }

    /// <summary>
    /// The known window with the least remaining availability, or null when no
    /// window carries a usable reading. Ties resolve to the first scarcest
    /// window in probe order. Pure.
    /// </summary>
    private static WindowQuota? FindBindingWindow(IReadOnlyList<WindowQuota>? windows)
    {
        if (windows is null) return null;
        WindowQuota? binding = null;
        var best = double.PositiveInfinity;
        foreach (var window in windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.Name)) continue;
            var available = window.AvailablePct;
            if (!double.IsFinite(available) || available < 0) continue;
            if (available < best)
            {
                best = available;
                binding = window;
            }
        }
        return binding;
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
        // Resolve the binding window BEFORE sorting: the probe-ordered list
        // keeps the deterministic first-scarcest tie-break, and marking by
        // reference avoids double-marking when two raw names sanitize to the
        // same string.
        var binding = FindBindingWindow(windows);
        foreach (var window in windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.Name)) continue;
            if (!double.IsFinite(window.AvailablePct) || window.AvailablePct < 0) continue;
            known.Add(window);
        }
        if (known.Count == 0) return null;
        known.Sort(static (a, b) => a.AvailablePct.CompareTo(b.AvailablePct));
        var parts = new List<string>(Math.Min(known.Count, MaxSummaryWindows));
        foreach (var window in known)
        {
            if (parts.Count >= MaxSummaryWindows) break;
            var name = SanitizeWindowName(window.Name);
            var marker = ReferenceEquals(window, binding) ? " (binding)" : string.Empty;
            parts.Add($"{name} {window.AvailablePct:F1}%{marker}");
        }
        return string.Join(", ", parts);
    }
}
