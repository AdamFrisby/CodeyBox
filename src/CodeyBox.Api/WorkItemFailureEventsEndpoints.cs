using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Read API over the durable failure/park event log. Mirrors
/// <see cref="WorkItemTimingsEndpoints"/>: a thin projection over the store's
/// query, used for failure-rate / failure-mode analysis after the fact.
/// </summary>
internal static class WorkItemFailureEventsEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapGet("/failure-events", GetFailureEventsAsync);
    }

    private static async Task<IResult> GetFailureEventsAsync(
        IFailureEventStore store,
        string? since,
        string? kind,
        int? limit,
        CancellationToken ct)
    {
        DateTimeOffset? sinceParsed = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(
                    since,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return Results.BadRequest(new { error = "invalid 'since' (expected ISO-8601)" });
            }

            sinceParsed = parsed;
        }

        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? null : kind;
        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var rows = await store.QueryAsync(sinceParsed, normalizedKind, effectiveLimit, ct);

        return Results.Ok(new
        {
            count = rows.Count,
            events = rows.Select(r => new
            {
                id = r.Id,
                workItemId = r.WorkItemId.ToString(),
                agent = r.Agent,
                phase = r.Phase,
                iteration = r.Iteration,
                failureKind = r.FailureKind,
                errorMessage = r.ErrorMessage,
                sandboxName = r.SandboxName,
                provider = r.Provider,
                occurredAt = r.OccurredAt,
            }).ToList(),
        });
    }
}
