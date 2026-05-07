using Bunit;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using WorkItemTimingsPage = CodeyBox.Admin.Web.Components.Pages.WorkItemTimings;

namespace CodeyBox.Admin.Tests;

public sealed class AgentActivitySectionTests : TestContext
{
    private const string ItemId = "aabbccdd-0000-0000-0000-000000000001";

    [Fact]
    public void WorkItemTimings_ShowsAgentActivityAndExpandableToolRows()
    {
        var fake = new FakeApiClient([]);
        fake.TimingsOverride[ItemId] = new WorkItemTimingsDto
        {
            WorkItemId = ItemId,
            TotalDurationMs = 12_000,
        };
        fake.AgentStreamAggregateOverride[ItemId] = new AgentStreamAggregateDto
        {
            WorkItemId = ItemId,
            TotalAgentDurationMs = 12_000,
            TotalToolCalls = 1,
            ThinkingMs = 8_000,
            ExecutingMs = 4_000,
            StallCount = 1,
            Invocations =
            [
                new AgentStreamInvocationDto
                {
                    FileName = "work-1-abcdef.jsonl",
                    Phase = "work",
                    Iteration = 1,
                    TotalDurationMs = 12_000,
                    ToolCalls =
                    [
                        new AgentStreamToolCallDto
                        {
                            ToolUseId = "t1",
                            ToolName = "Bash",
                            InputSummary = "{\"command\":\"dotnet test\"}",
                            DurationMs = 4_000,
                            Succeeded = true,
                        },
                    ],
                    Stalls =
                    [
                        new AgentStreamStallDto
                        {
                            GapDurationMs = 31_000,
                            Classification = "tool_execution",
                        },
                    ],
                },
            ],
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("Agent activity", cut.Markup);
        Assert.Contains("agent-activity-table", cut.Markup);
        Assert.Contains("Bash", cut.Markup);
        Assert.Contains("dotnet test", cut.Markup);
        Assert.Contains("agent-stall", cut.Markup);
    }

    [Fact]
    public void WorkItemTimings_ShowsUnsupportedMessageWhenNoStreamSummariesExist()
    {
        var fake = new FakeApiClient([]);
        fake.TimingsOverride[ItemId] = new WorkItemTimingsDto
        {
            WorkItemId = ItemId,
            TotalDurationMs = 12_000,
        };
        fake.AgentStreamAggregateOverride[ItemId] = new AgentStreamAggregateDto
        {
            WorkItemId = ItemId,
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("Agent activity", cut.Markup);
        Assert.Contains("stream-json not supported by this agent", cut.Markup);
    }
}
