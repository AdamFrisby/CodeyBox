using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class AgentStreamEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapGet("/{id}/agent-streams", ListAsync);
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
}
