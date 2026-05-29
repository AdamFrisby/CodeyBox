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

    internal static HttpResponseMessage SseEventsResponse(params string[] states) =>
        SseEventsResponse((IEnumerable<string>)states);

    internal static HttpResponseMessage SseEventsResponse(IEnumerable<string> states)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var state in states)
        {
            if (state.StartsWith("raw:", StringComparison.Ordinal))
            {
                sb.Append("data: ").Append(state["raw:".Length..]).Append('\n').Append('\n');
                continue;
            }

            var item = WorkItem(state);
            sb.Append("id: 1\n");
            sb.Append("event: work_item.state\n");
            sb.Append("data: {\"event\":\"work_item.state\",\"workItem\":{\"id\":\"")
                .Append(item.Id)
                .Append("\",\"state\":\"")
                .Append(state)
                .Append("\"}}\n\n");
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), System.Text.Encoding.UTF8, "text/event-stream"),
        };
    }
}
