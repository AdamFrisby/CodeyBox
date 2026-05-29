namespace CodeyBox.Cli.Services;

/// <summary>
/// Outcome of an SSE watch attempt on <c>GET /workitems/{id}/events</c>.
/// </summary>
internal enum SseWatchResult
{
    /// <summary>Terminal state observed; watch completed successfully.</summary>
    Completed,

    /// <summary>SSE could not be used; caller should fall back to polling.</summary>
    ShouldFallback,

    /// <summary>Work item does not exist (HTTP 404).</summary>
    NotFound,
}
