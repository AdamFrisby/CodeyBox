using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

internal static class TestCaseEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/testcases");
        group.MapPost("/", CreateAsync);
        group.MapPost("/bulk", BulkCreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);

        app.MapGet("/workitems/{workItemId}/testcases", ListByWorkItemAsync);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateTestCaseRequest req,
        ITestCaseStore store,
        IWorkItemStore workItemStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(req.SourceWorkItemId))
            return Results.BadRequest(new { error = "SourceWorkItemId is required" });

        if (!Guid.TryParse(req.SourceWorkItemId, out var g))
            return Results.BadRequest(new { error = $"Invalid SourceWorkItemId format: '{req.SourceWorkItemId}'" });

        var normalisedSourceWorkItemId = new WorkItemId(g).ToString();

        var workItem = await workItemStore.GetAsync(new WorkItemId(g), ct);
        if (workItem is null)
            return Results.BadRequest(new { error = $"Linked WorkItem '{req.SourceWorkItemId}' not found" });

        var id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id;

        var testCase = new TestCase
        {
            Id = id,
            Name = req.Name,
            Description = req.Description ?? string.Empty,
            SourceWorkItemId = normalisedSourceWorkItemId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsArchived = req.IsArchived,
            AutomationKind = req.AutomationKind,
            ExecutableArtifactJson = req.ExecutableArtifactJson,
            ConformanceJson = req.ConformanceJson,
            Label = req.Label,
            LastRunPassed = req.LastRunPassed,
            LastRunAt = req.LastRunAt,
            LastRunResult = req.LastRunResult
        };

        await store.CreateAsync(testCase, ct);
        return Results.Created($"/testcases/{testCase.Id}", ToDto(testCase));
    }

    private static async Task<IResult> BulkCreateAsync(
        [FromBody] IReadOnlyList<CreateTestCaseRequest> reqList,
        ITestCaseStore store,
        IWorkItemStore workItemStore,
        IOptions<CodeyBoxOptions> options,
        CancellationToken ct)
    {
        if (reqList == null || reqList.Count == 0)
            return Results.BadRequest(new { error = "List of test cases cannot be null or empty" });

        var maxBulk = options.Value.MaxBulkItems;
        if (reqList.Count > maxBulk)
            return Results.BadRequest(new { error = $"List of test cases exceeds maximum limit of {maxBulk} items" });

        var testCases = new List<TestCase>();
        foreach (var req in reqList)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required for all test cases" });
            if (string.IsNullOrWhiteSpace(req.SourceWorkItemId))
                return Results.BadRequest(new { error = "SourceWorkItemId is required for all test cases" });

            if (!Guid.TryParse(req.SourceWorkItemId, out var g))
                return Results.BadRequest(new { error = $"Invalid SourceWorkItemId format: '{req.SourceWorkItemId}'" });

            var normalisedSourceWorkItemId = new WorkItemId(g).ToString();

            var workItem = await workItemStore.GetAsync(new WorkItemId(g), ct);
            if (workItem is null)
                return Results.BadRequest(new { error = $"Linked WorkItem '{req.SourceWorkItemId}' not found" });

            var id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id;

            testCases.Add(new TestCase
            {
                Id = id,
                Name = req.Name,
                Description = req.Description ?? string.Empty,
                SourceWorkItemId = normalisedSourceWorkItemId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsArchived = req.IsArchived,
                AutomationKind = req.AutomationKind,
                ExecutableArtifactJson = req.ExecutableArtifactJson,
                ConformanceJson = req.ConformanceJson,
                Label = req.Label,
                LastRunPassed = req.LastRunPassed,
                LastRunAt = req.LastRunAt,
                LastRunResult = req.LastRunResult
            });
        }

        await store.BulkCreateAsync(testCases, ct);
        return Results.Ok(testCases.Select(ToDto).ToList());
    }

    private static async Task<IResult> ListAsync(
        ITestCaseStore store,
        CancellationToken ct)
    {
        var list = new List<TestCase>();
        await foreach (var tc in store.ListAsync(ct))
        {
            list.Add(tc);
        }
        return Results.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<IResult> ListByWorkItemAsync(
        string workItemId,
        ITestCaseStore store,
        IWorkItemStore workItemStore,
        CancellationToken ct)
    {
        if (!Guid.TryParse(workItemId, out var g))
            return Results.BadRequest(new { error = $"Invalid WorkItemId format: '{workItemId}'" });

        var normalisedWorkItemId = new WorkItemId(g).ToString();

        var workItem = await workItemStore.GetAsync(new WorkItemId(g), ct);
        if (workItem is null)
            return Results.NotFound(new { error = $"WorkItem '{workItemId}' not found" });

        var list = new List<TestCase>();
        await foreach (var tc in store.ListByWorkItemAsync(normalisedWorkItemId, ct))
        {
            list.Add(tc);
        }
        return Results.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetAsync(
        string id,
        ITestCaseStore store,
        CancellationToken ct)
    {
        var testCase = await store.GetAsync(id, ct);
        if (testCase is null) return Results.NotFound();
        return Results.Ok(ToDto(testCase));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        [FromBody] UpdateTestCaseRequest req,
        ITestCaseStore store,
        IWorkItemStore workItemStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(req.SourceWorkItemId))
            return Results.BadRequest(new { error = "SourceWorkItemId is required" });

        if (!Guid.TryParse(req.SourceWorkItemId, out var g))
            return Results.BadRequest(new { error = $"Invalid SourceWorkItemId format: '{req.SourceWorkItemId}'" });

        var normalisedSourceWorkItemId = new WorkItemId(g).ToString();

        var workItem = await workItemStore.GetAsync(new WorkItemId(g), ct);
        if (workItem is null)
            return Results.BadRequest(new { error = $"Linked WorkItem '{req.SourceWorkItemId}' not found" });

        var existing = await store.GetAsync(id, ct);
        if (existing is null) return Results.NotFound();

        // SourceWorkItemId is the stable cross-system provenance link a later JobTrack
        // propagation item relies on; reject mutation that would silently break that mapping
        // rather than rewriting the link.
        if (!string.Equals(existing.SourceWorkItemId, normalisedSourceWorkItemId, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "SourceWorkItemId cannot be changed after creation" });

        var updated = existing with
        {
            Name = req.Name,
            Description = req.Description ?? string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsArchived = req.IsArchived,
            AutomationKind = req.AutomationKind,
            ExecutableArtifactJson = req.ExecutableArtifactJson,
            ConformanceJson = req.ConformanceJson,
            Label = req.Label,
            LastRunPassed = req.LastRunPassed,
            LastRunAt = req.LastRunAt,
            LastRunResult = req.LastRunResult
        };

        // Single atomic UPDATE; if the row vanished after the existence check (concurrent
        // delete), the affected-row count is zero and we surface that as 404 rather than
        // returning OK with stale state.
        var ok = await store.UpdateAsync(updated, ct);
        return ok ? Results.Ok(ToDto(updated)) : Results.NotFound();
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        ITestCaseStore store,
        CancellationToken ct)
    {
        var ok = await store.DeleteAsync(id, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static TestCaseDto ToDto(TestCase tc) => new(
        tc.Id,
        tc.Name,
        tc.Description,
        tc.SourceWorkItemId,
        tc.CreatedAt,
        tc.UpdatedAt,
        tc.IsArchived,
        tc.AutomationKind,
        tc.ExecutableArtifactJson,
        tc.ConformanceJson,
        tc.Label,
        tc.LastRunPassed,
        tc.LastRunAt,
        tc.LastRunResult
    );
}

public record CreateTestCaseRequest(
    string? Id,
    string Name,
    string? Description,
    string SourceWorkItemId,
    AutomationKind? AutomationKind = null,
    string? ExecutableArtifactJson = null,
    string? ConformanceJson = null,
    string? Label = null,
    bool IsArchived = false,
    bool? LastRunPassed = null,
    DateTimeOffset? LastRunAt = null,
    string? LastRunResult = null
);

public record UpdateTestCaseRequest(
    string Name,
    string? Description,
    string SourceWorkItemId,
    AutomationKind? AutomationKind = null,
    string? ExecutableArtifactJson = null,
    string? ConformanceJson = null,
    string? Label = null,
    bool IsArchived = false,
    bool? LastRunPassed = null,
    DateTimeOffset? LastRunAt = null,
    string? LastRunResult = null
);

public record TestCaseDto(
    string Id,
    string Name,
    string Description,
    string SourceWorkItemId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsArchived,
    AutomationKind? AutomationKind,
    string? ExecutableArtifactJson,
    string? ConformanceJson,
    string? Label,
    bool? LastRunPassed,
    DateTimeOffset? LastRunAt,
    string? LastRunResult
);
