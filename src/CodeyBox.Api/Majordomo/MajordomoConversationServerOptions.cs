using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Config-file shape for the durable majordomo conversation bounds, nested
/// under <c>CodeyBox:Majordomo:Conversation</c>. Settable properties bind
/// from configuration; <see cref="ToHistoryOptions"/> builds the validated
/// <see cref="MajordomoHistoryOptions"/> the store and assembler consume.
/// Follows the same pattern as <see cref="MajordomoServerOptions"/> itself:
/// the binder sets plain values and validation rejects bad ones with a
/// per-rule message instead of silently weakening a bound on a typo.
/// </summary>
public sealed class MajordomoConversationServerOptions
{
    /// <summary>Per-row cap on stored entry text. 1024–262144.</summary>
    public int MaxEntryChars { get; set; } = MajordomoHistoryOptions.DefaultMaxEntryChars;

    /// <summary>Cap on stored conversation rows before compaction. 100–100000.</summary>
    public int MaxEntries { get; set; } = MajordomoHistoryOptions.DefaultMaxEntries;

    /// <summary>Total bound on one assembled context, in characters. 1000–200000.</summary>
    public int MaxContextChars { get; set; } = MajordomoHistoryOptions.DefaultMaxContextChars;

    /// <summary>Cap on recent entries replayed verbatim. 1–500.</summary>
    public int MaxRecentEntries { get; set; } = MajordomoHistoryOptions.DefaultMaxRecentEntries;

    /// <summary>Budget for the summary section; 0 omits it. 0–16000.</summary>
    public int MaxSummaryChars { get; set; } = MajordomoHistoryOptions.DefaultMaxSummaryChars;

    /// <summary>Budget for the live fleet section; 0 omits it. 0–16000.</summary>
    public int MaxFleetChars { get; set; } = MajordomoHistoryOptions.DefaultMaxFleetChars;

    /// <summary>Builds the validated history policy the store and assembler consume.</summary>
    public MajordomoHistoryOptions ToHistoryOptions() => new()
    {
        MaxEntryChars = MaxEntryChars,
        MaxEntries = MaxEntries,
        MaxContextChars = MaxContextChars,
        MaxRecentEntries = MaxRecentEntries,
        MaxSummaryChars = MaxSummaryChars,
        MaxFleetChars = MaxFleetChars,
    };

    /// <summary>Hot-reload validator: returns the failure message or null.</summary>
    public static string? Validate(MajordomoConversationServerOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var candidate = new MajordomoHistoryOptions();
        try
        {
            candidate = candidate with
            {
                MaxEntryChars = opts.MaxEntryChars,
                MaxEntries = opts.MaxEntries,
                MaxContextChars = opts.MaxContextChars,
                MaxRecentEntries = opts.MaxRecentEntries,
                MaxSummaryChars = opts.MaxSummaryChars,
                MaxFleetChars = opts.MaxFleetChars,
            };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ex.Message;
        }

        return MajordomoHistoryOptions.Validate(candidate);
    }
}
