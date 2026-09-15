using System.Globalization;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// The single number/time formatting helper for the admin UI. Durations are
/// the most-read values here, so every page delegates to these pure functions
/// instead of formatting ad hoc. All output uses the invariant culture so
/// server locale can never shift a dashboard value.
/// </summary>
public static class AdminFormat
{
    /// <summary>Formats a millisecond duration: "850ms", "4.2s", "3m 4s", "2h 5m".</summary>
    public static string FormatDurationMs(long ms)
    {
        if (ms < 0)
        {
            return "—";
        }

        if (ms < 1000)
        {
            return $"{ms}ms";
        }

        if (ms < 60_000)
        {
            return $"{ms / 1000.0:F1}s";
        }

        var totalSeconds = ms / 1000;
        if (totalSeconds < 3600)
        {
            return $"{totalSeconds / 60}m {totalSeconds % 60}s";
        }

        return $"{totalSeconds / 3600}h {(totalSeconds % 3600) / 60}m";
    }

    /// <summary>Formats a <see cref="TimeSpan"/> duration via <see cref="FormatDurationMs"/>.</summary>
    public static string FormatDuration(TimeSpan span) =>
        span < TimeSpan.Zero ? "—" : FormatDurationMs((long)span.TotalMilliseconds);

    /// <summary>Compact age without a suffix: "12s", "4m", "3h", "2d". Matches the queue "Age" column.</summary>
    public static string FormatShortAge(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return "0s";
        }

        if (elapsed.TotalSeconds < 60)
        {
            return $"{(int)elapsed.TotalSeconds}s";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes}m";
        }

        if (elapsed.TotalHours < 24)
        {
            return $"{(int)elapsed.TotalHours}h";
        }

        return $"{(int)elapsed.TotalDays}d";
    }

    /// <summary>Relative time with a suffix: "just now", "12s ago", "4m ago". Pass both stamps explicitly for testability.</summary>
    public static string FormatRelative(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var elapsed = now - timestamp;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 10)
        {
            return "just now";
        }

        return $"{FormatShortAge(elapsed)} ago";
    }

    /// <summary>Compact count: "999", "12.3K", "3.4M".</summary>
    public static string FormatCount(long count) => count switch
    {
        < 0 => "—",
        < 1000 => count.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => $"{count / 1000.0:F1}K",
        _ => $"{count / 1_000_000.0:F1}M",
    };

    /// <summary>USD money: "$18.42". Zero renders as "$0.00"; sub-cent values keep 4 decimals so tiny costs stay visible.</summary>
    public static string FormatUsd(double usd)
    {
        if (usd == 0)
        {
            return "$0.00";
        }

        if (usd != 0 && usd < 0.01)
        {
            return $"${usd.ToString("F4", CultureInfo.InvariantCulture)}";
        }

        return $"${usd.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    /// <summary>USD money (decimal overload for budget DTOs).</summary>
    public static string FormatUsd(decimal usd) => FormatUsd((double)usd);

    /// <summary>Countdown to a future stamp: "now", "5m", "2h 5m", "3d 4h". Never throws; null renders as an em dash.</summary>
    public static string FormatCountdown(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null)
        {
            return "—";
        }

        var delta = at.Value - now;
        if (delta <= TimeSpan.Zero)
        {
            return "now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes}m";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours}h {delta.Minutes}m";
        }

        return $"{(int)delta.TotalDays}d {delta.Hours}h";
    }

    /// <summary>Local wall-clock for table cells: "2026-09-15 20:31". Never throws; null renders as an em dash.</summary>
    public static string FormatDateTime(DateTimeOffset? at) =>
        at is null ? "—" : at.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Round-trip timestamp for detail rows. Never throws; null renders as an em dash.</summary>
    public static string FormatDateTimeFull(DateTimeOffset? at) =>
        at is null ? "—" : at.Value.ToString("O", CultureInfo.InvariantCulture);
}
