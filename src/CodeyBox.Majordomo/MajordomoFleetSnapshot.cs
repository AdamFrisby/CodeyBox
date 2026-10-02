using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CodeyBox.Majordomo;

/// <summary>
/// The compact live view of the fleet replayed into every assembled
/// context: queue counts, in-flight items, quota state, and recent failures.
/// Compact by construction — every list is capped and every line truncated
/// at the sink, so the assembler renders a bounded section rather than the
/// whole queue every time. Immutable: the caller's collections cannot
/// mutate the snapshot after it was validated.
/// </summary>
public sealed record MajordomoFleetSnapshot
{
    /// <summary>Empty fleet view: queue assumed running with nothing in flight.</summary>
    public static readonly MajordomoFleetSnapshot Empty = new("running");

    /// <summary>Most lines kept per fleet list.</summary>
    public const int MaxLinesPerList = 32;

    /// <summary>Most characters kept per fleet line.</summary>
    public const int MaxCharsPerLine = 256;

    public MajordomoFleetSnapshot(
        string queueState,
        IReadOnlyDictionary<string, int>? stateCounts = null,
        IReadOnlyList<string>? inFlightItems = null,
        IReadOnlyList<string>? quotaLines = null,
        IReadOnlyList<string>? recentFailures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueState);

        if (stateCounts is not null)
        {
            foreach (var (state, count) in stateCounts)
            {
                if (string.IsNullOrWhiteSpace(state))
                    throw new ArgumentException("state count keys must be non-empty", nameof(stateCounts));
                if (count < 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(stateCounts), count, "state counts must be >= 0");
            }
        }

        QueueState = queueState;
        StateCounts = stateCounts is null
            ? FrozenDictionary<string, int>.Empty
            : stateCounts.ToFrozenDictionary(StringComparer.Ordinal);
        InFlightItems = CopyLines(inFlightItems);
        QuotaLines = CopyLines(quotaLines);
        RecentFailures = CopyLines(recentFailures);
    }

    /// <summary>Whether the queue is picking up new work (for example "running" or "paused").</summary>
    public string QueueState { get; }

    /// <summary>Live work-item counts keyed by lifecycle state name.</summary>
    public FrozenDictionary<string, int> StateCounts { get; }

    /// <summary>Items currently bound to dispatch slots, most recent last.</summary>
    public ImmutableArray<string> InFlightItems { get; }

    /// <summary>Per-agent quota/concurrency one-liners.</summary>
    public ImmutableArray<string> QuotaLines { get; }

    /// <summary>Recent failure one-liners, most recent last.</summary>
    public ImmutableArray<string> RecentFailures { get; }

    private static ImmutableArray<string> CopyLines(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return ImmutableArray<string>.Empty;
        var builder = ImmutableArray.CreateBuilder<string>(Math.Min(lines.Count, MaxLinesPerList));
        foreach (var line in lines.Take(MaxLinesPerList))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(line);
            builder.Add(line.Length <= MaxCharsPerLine ? line : line[..MaxCharsPerLine]);
        }
        return builder.ToImmutable();
    }
}
