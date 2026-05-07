using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class AgentStreamEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapGet("/agent-streams/aggregate", GetFleetAggregateAsync);
        group.MapGet("/{id}/agent-streams", ListAsync);
        group.MapGet("/{id}/agent-streams/aggregate", GetAggregateAsync);
        group.MapGet("/{id}/agent-streams/{fileName}/analysis", AnalyzeFileAsync);
        group.MapGet("/{id}/agent-streams/{fileName}", GetFileAsync);
    }

    private static async Task<IResult> ListAsync(
        string id,
        int? limit,
        bool includeLineCount,
        IWorkItemStore store,
        IAgentStreamStore streams,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var effectiveLimit = Math.Clamp(limit ?? AgentStreamStore.DefaultListLimit, 1, AgentStreamStore.MaxListLimit);
        var files = await streams.ListAsync(item!.Id, effectiveLimit, includeLineCount, ct);
        return Results.Ok(files.Select(f => new
        {
            fileName = f.FileName,
            phase = f.Phase,
            iteration = f.Iteration,
            sizeBytes = f.SizeBytes,
            lineCount = f.LineCount,
            capturedAt = f.CapturedAt,
        }));
    }

    private static async Task<IResult> GetFileAsync(
        string id,
        string fileName,
        IWorkItemStore store,
        IAgentStreamStore streams,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var stream = await streams.OpenReadAsync(item!.Id, fileName, ct);
        if (stream is null) return Results.NotFound();

        return Results.File(stream, "application/x-ndjson", fileDownloadName: fileName);
    }

    private static async Task<IResult> AnalyzeFileAsync(
        string id,
        string fileName,
        IWorkItemStore store,
        IAgentStreamStore streams,
        IEnumerable<IAgentStreamParser> parsers,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        await using var stream = await streams.OpenReadAsync(item!.Id, fileName, ct);
        if (stream is null) return Results.NotFound();

        var kind = item.Agent ?? new AgentKind("unknown");
        var parser = parsers.FirstOrDefault(p => p.Kind == kind)
            ?? parsers.FirstOrDefault(p => p.Kind.Value == "unknown")
            ?? new UnknownAgentStreamParser();
        var summary = await parser.ParseAsync(stream, ct);
        return Results.Ok(ToSummaryDto(summary, fileName, null, null, parser.Kind));
    }

    private static async Task<IResult> GetAggregateAsync(
        string id,
        IWorkItemStore store,
        IAgentStreamSummaryStore summaries,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var rows = await summaries.GetByWorkItemAsync(item!.Id, ct);
        return Results.Ok(ToAggregateDto(AgentStreamAnalytics.Aggregate(item.Id.ToString(), rows), rows));
    }

    private static async Task<IResult> GetFleetAggregateAsync(
        int? n,
        IAgentStreamSummaryStore summaries,
        CancellationToken ct)
    {
        var rows = new List<AgentStreamSummaryRow>();
        await foreach (var row in summaries.StreamRecentCompletedAsync(Math.Clamp(n ?? 50, 1, 500), ct))
            rows.Add(row);
        return Results.Ok(ToAggregateDto(AgentStreamAnalytics.Aggregate(null, rows), rows));
    }

    private static async Task<(WorkItem? item, IResult? error)> ResolveWorkItemAsync(
        string idSegment,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (idSegment.Contains(':'))
        {
            var colonIdx = idSegment.IndexOf(':', StringComparison.Ordinal);
            var projectPart = idSegment[..colonIdx];
            var externalPart = idSegment[(colonIdx + 1)..];
            if (string.IsNullOrEmpty(projectPart) || string.IsNullOrEmpty(externalPart))
                return (null, Results.BadRequest(new { error = "composite id format requires non-empty projectId and externalId: '<projectId>:<externalId>'" }));
            ProjectId pid;
            try { pid = new ProjectId(projectPart); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            try { Validation.ValidateExternalId(externalPart, "externalId"); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            var byExtId = await store.GetByExternalIdAsync(pid, externalPart, ct);
            return byExtId is null ? (null, Results.NotFound()) : (byExtId, null);
        }

        if (!Guid.TryParse(idSegment, out var g))
            return (null, Results.BadRequest(new { error = "invalid id" }));
        var byId = await store.GetAsync(new WorkItemId(g), ct);
        return byId is null ? (null, Results.NotFound()) : (byId, null);
    }

    private static object ToAggregateDto(AgentStreamAggregate aggregate, IReadOnlyList<AgentStreamSummaryRow> rows) => new
    {
        workItemId = aggregate.WorkItemId,
        totalAgentDurationMs = aggregate.TotalAgentDurationMs,
        totalToolCalls = aggregate.TotalToolCalls,
        byTool = aggregate.ByTool.Select(t => new
        {
            tool = t.Tool,
            count = t.Count,
            totalDurationMs = t.TotalDurationMs,
            medianMs = t.MedianMs,
        }),
        thinkingMs = aggregate.ThinkingMs,
        executingMs = aggregate.ExecutingMs,
        stallCount = aggregate.StallCount,
        longestStallMs = aggregate.LongestStallMs,
        estimatedUsdTotal = aggregate.EstimatedUsdTotal,
        invocations = rows.Select(r => ToSummaryDto(r.Summary, r.FileName, r.Phase, r.Iteration, r.AgentKind)),
    };

    private static object ToSummaryDto(
        AgentStreamSummary summary,
        string fileName,
        string? phase,
        int? iteration,
        AgentKind kind) => new
        {
            fileName,
            phase,
            iteration,
            agentKind = kind.Value,
            totalDurationMs = ToMs(summary.TotalDuration),
            timeToFirstTokenMs = summary.TimeToFirstToken.HasValue ? ToMs(summary.TimeToFirstToken.Value) : (long?)null,
            inputTokens = summary.InputTokens,
            outputTokens = summary.OutputTokens,
            cachedInputTokens = summary.CachedInputTokens,
            estimatedUsd = summary.EstimatedUsd,
            toolCalls = summary.ToolCalls.Select(t => new
            {
                toolUseId = t.ToolUseId,
                toolName = t.ToolName,
                inputSummary = t.InputSummary,
                startedAt = t.StartedAt,
                endedAt = t.EndedAt,
                durationMs = t.Duration.HasValue ? ToMs(t.Duration.Value) : (long?)null,
                succeeded = t.Succeeded,
                outputBytes = t.OutputBytes,
            }),
            stalls = summary.Stalls.Select(s => new
            {
                detectedAt = s.DetectedAt,
                gapDurationMs = ToMs(s.GapDuration),
                previousEventType = s.PreviousEventType,
                nextEventType = s.NextEventType,
                classification = s.Classification,
            }),
            finalAssistantMessage = summary.FinalAssistantMessage,
        };

    private static long ToMs(TimeSpan value) => Math.Max(0, (long)Math.Round(value.TotalMilliseconds));
}
