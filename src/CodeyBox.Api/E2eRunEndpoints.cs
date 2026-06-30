using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

internal static class E2eRunEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/e2eruns");
        group.MapPost("/", EnqueueAsync);
        group.MapPost("/bulk", EnqueueBulkAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPost("/{id}/cancel", CancelAsync);

        app.MapGet("/testcases/{testCaseId}/runs", ListByTestCaseAsync);
        app.MapGet("/e2eruns/batches/{batchId}", ListByBatchAsync);
        app.MapGet("/e2eruns/batches/{batchId}/runs", ListBatchRunsAsync);
    }

    private static async Task<IResult> EnqueueAsync(
        [FromBody] EnqueueE2eRunRequest req,
        ITestCaseStore testCases,
        IE2eRunStore runs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TestCaseId))
            return Results.BadRequest(new { error = "TestCaseId is required" });

        var testCase = await testCases.GetAsync(req.TestCaseId, ct);
        if (testCase is null)
            return Results.NotFound(new { error = $"TestCase '{req.TestCaseId}' not found" });
        if (testCase.AutomationKind != AutomationKind.E2eReplay)
            return Results.BadRequest(new { error = $"TestCase '{req.TestCaseId}' AutomationKind is {testCase.AutomationKind}; expected E2eReplay" });
        if (string.IsNullOrWhiteSpace(testCase.ExecutableArtifactJson))
            return Results.BadRequest(new { error = $"TestCase '{req.TestCaseId}' has no ExecutableArtifactJson" });

        var run = new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = testCase.Id,
            Status = E2eRunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            BatchId = string.IsNullOrWhiteSpace(req.BatchId) ? null : req.BatchId,
        };

        await runs.CreateAsync(run, ct);
        return Results.Created($"/e2eruns/{run.Id}", ToDto(run));
    }

    private static async Task<IResult> EnqueueBulkAsync(
        [FromBody] EnqueueBulkE2eRunsRequest req,
        ITestCaseStore testCases,
        IE2eRunStore runs,
        IOptions<CodeyBoxOptions> options,
        CancellationToken ct)
    {
        if (req?.TestCaseIds is null || req.TestCaseIds.Count == 0)
            return Results.BadRequest(new { error = "TestCaseIds is required" });

        var maxBulk = options.Value.MaxBulkItems;
        if (req.TestCaseIds.Count > maxBulk)
            return Results.BadRequest(new { error = $"Bulk enqueue exceeds maximum of {maxBulk} items" });

        var batchId = string.IsNullOrWhiteSpace(req.BatchId) ? Guid.NewGuid().ToString("N") : req.BatchId;
        var validated = new List<TestCase>(req.TestCaseIds.Count);
        foreach (var tcId in req.TestCaseIds)
        {
            if (string.IsNullOrWhiteSpace(tcId))
                return Results.BadRequest(new { error = "TestCaseIds entries must not be empty" });
            var testCase = await testCases.GetAsync(tcId, ct);
            if (testCase is null)
                return Results.NotFound(new { error = $"TestCase '{tcId}' not found" });
            if (testCase.AutomationKind != AutomationKind.E2eReplay)
                return Results.BadRequest(new { error = $"TestCase '{tcId}' AutomationKind is {testCase.AutomationKind}; expected E2eReplay" });
            if (string.IsNullOrWhiteSpace(testCase.ExecutableArtifactJson))
                return Results.BadRequest(new { error = $"TestCase '{tcId}' has no ExecutableArtifactJson" });
            validated.Add(testCase);
        }

        var created = new List<E2eRunDto>(validated.Count);
        foreach (var testCase in validated)
        {
            var run = new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = testCase.Id,
                Status = E2eRunStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                BatchId = batchId,
            };
            await runs.CreateAsync(run, ct);
            created.Add(ToDto(run));
        }

        return Results.Ok(new EnqueueBulkE2eRunsResponse(batchId, created));
    }

    private static async Task<IResult> ListAsync(
        IE2eRunStore runs,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var page = NormalizePage(offset, limit);
        var list = new List<E2eRunDto>();
        await foreach (var r in runs.ListAsync(page.Offset, page.Limit, ct)) list.Add(ToDto(r));
        return Results.Ok(new E2eRunPageDto(page.Offset, page.Limit, list));
    }

    private static async Task<IResult> GetAsync(string id, IE2eRunStore runs, CancellationToken ct)
    {
        var run = await runs.GetAsync(id, ct);
        return run is null ? Results.NotFound() : Results.Ok(ToDto(run));
    }

    private static async Task<IResult> CancelAsync(
        string id,
        IE2eRunStore runs,
        E2eRunCancellationRegistry cancellations,
        CancellationToken ct)
    {
        var run = await runs.GetAsync(id, ct);
        if (run is null) return Results.NotFound();
        var signaledRunningRun = run.Status == E2eRunStatus.Running;
        if (run.Status == E2eRunStatus.Running)
            cancellations.Cancel(id);
        var ok = await runs.CancelAsync(id, ct);
        if (!ok)
        {
            var current = await runs.GetAsync(id, ct);
            if (signaledRunningRun && current?.Status == E2eRunStatus.Canceled)
                return Results.Ok(ToDto(current));
            return Results.Conflict(new { error = $"run '{id}' is already terminal (status={current?.Status ?? run.Status})" });
        }
        var refreshed = await runs.GetAsync(id, ct);
        return refreshed is null ? Results.NoContent() : Results.Ok(ToDto(refreshed));
    }

    private static async Task<IResult> ListByTestCaseAsync(
        string testCaseId,
        ITestCaseStore testCases,
        IE2eRunStore runs,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var testCase = await testCases.GetAsync(testCaseId, ct);
        if (testCase is null)
            return Results.NotFound(new { error = $"TestCase '{testCaseId}' not found" });

        var page = NormalizePage(offset, limit);
        var list = new List<E2eRunDto>();
        await foreach (var r in runs.ListByTestCaseAsync(testCaseId, page.Offset, page.Limit, ct)) list.Add(ToDto(r));
        return Results.Ok(new E2eRunPageDto(page.Offset, page.Limit, list));
    }

    private static async Task<IResult> ListByBatchAsync(string batchId, IE2eRunStore runs, CancellationToken ct)
    {
        var counts = await runs.GetBatchCountsAsync(batchId, ct);
        return counts is null ? Results.NotFound() : Results.Ok(ToBatchSummaryDto(counts));
    }

    private static async Task<IResult> ListBatchRunsAsync(
        string batchId,
        IE2eRunStore runs,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var counts = await runs.GetBatchCountsAsync(batchId, ct);
        if (counts is null) return Results.NotFound();
        var page = NormalizePage(offset, limit);
        var list = new List<E2eRunDto>();
        await foreach (var r in runs.ListByBatchAsync(batchId, page.Offset, page.Limit, ct)) list.Add(ToDto(r));
        return Results.Ok(new E2eRunPageDto(page.Offset, page.Limit, list));
    }

    private static BatchSummaryDto ToBatchSummaryDto(E2eRunBatchCounts counts) => new(
        counts.BatchId,
        counts.Total,
        counts.Queued,
        counts.Running,
        counts.Passed,
        counts.Failed,
        counts.Error,
        counts.Canceled,
        counts.Complete);

    private static E2eRunDto ToDto(E2eRun run) => new(
        run.Id,
        run.TestCaseId,
        run.Status,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt,
        DeserializeResult(run.Result),
        run.SandboxId,
        run.BatchId);

    private static E2eRunResult? DeserializeResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return null;
        try
        {
            return JsonSerializer.Deserialize<E2eRunResult>(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int Offset, int Limit) NormalizePage(int? offset, int? limit)
    {
        var effectiveOffset = Math.Max(0, offset ?? 0);
        var effectiveLimit = limit ?? E2eExecutionOptions.DefaultListPageSize;
        if (effectiveLimit < 1)
            effectiveLimit = E2eExecutionOptions.DefaultListPageSize;
        if (effectiveLimit > E2eExecutionOptions.MaximumListPageSize)
            effectiveLimit = E2eExecutionOptions.MaximumListPageSize;
        return (effectiveOffset, effectiveLimit);
    }
}

public sealed record EnqueueE2eRunRequest(string TestCaseId, string? BatchId = null);

public sealed record EnqueueBulkE2eRunsRequest(IReadOnlyList<string> TestCaseIds, string? BatchId = null);

public sealed record EnqueueBulkE2eRunsResponse(string BatchId, IReadOnlyList<E2eRunDto> Runs);

public sealed record E2eRunPageDto(int Offset, int Limit, IReadOnlyList<E2eRunDto> Runs);

public sealed record E2eRunDto(
    string Id,
    string TestCaseId,
    E2eRunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    E2eRunResult? Result,
    string? SandboxId,
    string? BatchId);

public sealed record BatchSummaryDto(
    string BatchId,
    int Total,
    int Queued,
    int Running,
    int Passed,
    int Failed,
    int Error,
    int Canceled,
    bool Complete);
