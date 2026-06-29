using System;
using System.Collections.Generic;
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

    private static async Task<IResult> ListAsync(IE2eRunStore runs, CancellationToken ct)
    {
        var list = new List<E2eRunDto>();
        await foreach (var r in runs.ListAsync(ct)) list.Add(ToDto(r));
        return Results.Ok(list);
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
        if (run.Status == E2eRunStatus.Running)
            cancellations.Cancel(id);
        var ok = await runs.CancelAsync(id, ct);
        if (!ok) return Results.Conflict(new { error = $"run '{id}' is already terminal (status={run.Status})" });
        var refreshed = await runs.GetAsync(id, ct);
        return refreshed is null ? Results.NoContent() : Results.Ok(ToDto(refreshed));
    }

    private static async Task<IResult> ListByTestCaseAsync(
        string testCaseId,
        ITestCaseStore testCases,
        IE2eRunStore runs,
        CancellationToken ct)
    {
        var testCase = await testCases.GetAsync(testCaseId, ct);
        if (testCase is null)
            return Results.NotFound(new { error = $"TestCase '{testCaseId}' not found" });

        var list = new List<E2eRunDto>();
        await foreach (var r in runs.ListByTestCaseAsync(testCaseId, ct)) list.Add(ToDto(r));
        return Results.Ok(list);
    }

    private static async Task<IResult> ListByBatchAsync(string batchId, IE2eRunStore runs, CancellationToken ct)
    {
        var list = new List<E2eRunDto>();
        await foreach (var r in runs.ListByBatchAsync(batchId, ct)) list.Add(ToDto(r));
        if (list.Count == 0) return Results.NotFound();

        var summary = SummariseBatch(batchId, list);
        return Results.Ok(summary);
    }

    private static BatchSummaryDto SummariseBatch(string batchId, IReadOnlyList<E2eRunDto> runs)
    {
        int queued = 0, running = 0, passed = 0, failed = 0, error = 0, canceled = 0;
        foreach (var r in runs)
        {
            switch (r.Status)
            {
                case E2eRunStatus.Queued: queued++; break;
                case E2eRunStatus.Running: running++; break;
                case E2eRunStatus.Passed: passed++; break;
                case E2eRunStatus.Failed: failed++; break;
                case E2eRunStatus.Error: error++; break;
                case E2eRunStatus.Canceled: canceled++; break;
            }
        }
        return new BatchSummaryDto(
            batchId,
            runs.Count,
            queued,
            running,
            passed,
            failed,
            error,
            canceled,
            queued == 0 && running == 0,
            runs);
    }

    private static E2eRunDto ToDto(E2eRun run) => new(
        run.Id,
        run.TestCaseId,
        run.Status,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt,
        run.Result,
        run.SandboxId,
        run.BatchId);
}

public sealed record EnqueueE2eRunRequest(string TestCaseId, string? BatchId = null);

public sealed record EnqueueBulkE2eRunsRequest(IReadOnlyList<string> TestCaseIds, string? BatchId = null);

public sealed record EnqueueBulkE2eRunsResponse(string BatchId, IReadOnlyList<E2eRunDto> Runs);

public sealed record E2eRunDto(
    string Id,
    string TestCaseId,
    E2eRunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Result,
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
    bool Complete,
    IReadOnlyList<E2eRunDto> Runs);
