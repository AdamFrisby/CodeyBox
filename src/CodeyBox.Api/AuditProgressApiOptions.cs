namespace CodeyBox.Api;

/// <summary>
/// Options for the audit-progress read API (<c>/workitems/{id}/audit-progress</c>).
/// Bound from configuration section <c>CodeyBox:AuditProgressApi</c> and read per request via
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>, so changes hot-reload.
/// </summary>
public sealed class AuditProgressApiOptions
{
    /// <summary>
    /// Maximum characters of each finding's <c>Description</c> returned by the LIST endpoint.
    /// Longer descriptions are truncated to this length and flagged (<c>descriptionTruncated</c>);
    /// the full text is always available from the per-row detail endpoint. This bounds the list
    /// response so the large (~80&#160;KB) outliers don't bloat every fetch, while the common case
    /// returns fully in one call. Must be &gt; 0; values &#8804; 0 disable truncation.
    /// Default 20000 returns the vast majority of rows in full on observed workloads.
    /// </summary>
    public int ListFindingDescriptionMaxChars { get; set; } = 20000;
}
