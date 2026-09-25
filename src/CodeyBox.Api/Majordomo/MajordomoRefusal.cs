using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// A refused majordomo call, shaped so the calling model can correct itself
/// and retry: the machine-readable reason, the human-facing detail, and —
/// where known — the offending item and field.
/// </summary>
internal sealed record MajordomoRefusal(
    string Reason,
    string Detail,
    string? Item = null,
    string? Field = null);

/// <summary>
/// The proposal a Proposed-mode mutation produces: the tool call itself plus
/// the change set the dry-run machinery computed for it, so the operator's
/// review sees exactly what approval would commit.
/// </summary>
internal sealed record MajordomoProposalView(
    string Tool,
    System.Text.Json.Nodes.JsonNode? Arguments,
    int AffectedItems,
    object ChangeSet);

/// <summary>
/// What a mutation backend produced: either the change set (validated plan
/// or committed result) or a refusal. <see cref="WritesApplied"/> is true
/// once any commit ran — used for honest turn-budget accounting when a
/// multi-part commit fails partway.
/// </summary>
internal sealed record MajordomoMutationResult(
    MajordomoChangeSet? ChangeSet,
    MajordomoRefusal? Refusal,
    bool WritesApplied)
{
    public static MajordomoMutationResult Planned(MajordomoChangeSet changeSet) =>
        new(changeSet, null, WritesApplied: false);

    public static MajordomoMutationResult Committed(MajordomoChangeSet changeSet) =>
        new(changeSet, null, WritesApplied: true);

    public static MajordomoMutationResult Refused(MajordomoRefusal refusal, bool writesApplied = false) =>
        new(null, refusal, writesApplied);
}
