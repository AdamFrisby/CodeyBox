using System.Net.Http.Json;
using CodeyBox.Cli.Models;

namespace CodeyBox.Cli.Tests.Helpers;

internal static class SampleData
{
    internal static WorkItemDto WorkItem(string state = "Queued") => new()
    {
        Id = "aabbccdd-0000-0000-0000-000000000000",
        ProjectId = "testproject",
        Title = "Test work item",
        Prompt = "Do the thing",
        Agent = "claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
    };

    internal static HttpResponseMessage WorkItemResponse(WorkItemDto? item = null) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(item ?? WorkItem(), CliJsonContext.Default.WorkItemDto),
        };

    internal static HttpResponseMessage WorkItemListResponse(IEnumerable<WorkItemDto>? items = null) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                (items ?? [WorkItem()]).ToList(),
                CliJsonContext.Default.ListWorkItemDto),
        };

    internal static HttpResponseMessage CreatedWorkItemResponse(WorkItemDto? item = null) =>
        new(System.Net.HttpStatusCode.Created)
        {
            Content = JsonContent.Create(item ?? WorkItem(), CliJsonContext.Default.WorkItemDto),
        };
}
