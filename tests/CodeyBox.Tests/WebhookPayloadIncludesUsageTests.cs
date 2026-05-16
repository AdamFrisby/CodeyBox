using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that webhook event payloads carry the <c>usage</c> and
/// <c>usageTotal</c> blocks when the event has them set, and omit both when
/// usage data is unavailable (the <c>WhenWritingNull</c> ignore policy applies
/// — downstream consumers treat absent as "unknown").
/// </summary>
public sealed class WebhookPayloadIncludesUsageTests
{
    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "test",
        Prompt = "do the thing",
    };

    private static Project MakeProject() => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.com/repo.git",
    };

    [Fact]
    public void Payload_WithUsage_IncludesBothBlocks()
    {
        var evt = new WebhookEvent
        {
            Event = "work_item.audit_iteration",
            WorkItem = MakeItem(),
            Project = MakeProject(),
            Usage = new WorkItemIterationUsage(
                Iteration: 2,
                TokensInput: 8000,
                TokensOutput: 900,
                TokensReasoning: 0,
                TokensCached: 500,
                CostUsd: 0.2310,
                ElapsedMs: 6500),
            UsageTotal = new WorkItemUsageTotal(
                TokensInput: 16500,
                TokensOutput: 1590,
                TokensReasoning: 0,
                TokensCached: 500,
                CostUsd: 0.4012,
                ElapsedMs: 14000),
        };

        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        var usage = doc.RootElement.GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("iteration").GetInt32());
        Assert.Equal(8000, usage.GetProperty("tokensInput").GetInt32());
        Assert.Equal(900, usage.GetProperty("tokensOutput").GetInt32());
        Assert.Equal(0, usage.GetProperty("tokensReasoning").GetInt32());
        Assert.Equal(500, usage.GetProperty("tokensCached").GetInt32());
        Assert.Equal(0.2310, usage.GetProperty("costUsd").GetDouble());
        Assert.Equal(6500, usage.GetProperty("elapsedMs").GetInt64());

        var total = doc.RootElement.GetProperty("usageTotal");
        Assert.Equal(16500, total.GetProperty("tokensInput").GetInt32());
        Assert.Equal(1590, total.GetProperty("tokensOutput").GetInt32());
        Assert.Equal(0, total.GetProperty("tokensReasoning").GetInt32());
        Assert.Equal(0.4012, total.GetProperty("costUsd").GetDouble());
        Assert.Equal(14000, total.GetProperty("elapsedMs").GetInt64());
        Assert.False(total.TryGetProperty("iteration", out _));
    }

    [Fact]
    public void Payload_WithoutUsage_OmitsBothBlocks()
    {
        var evt = new WebhookEvent
        {
            Event = "work_item.done",
            WorkItem = MakeItem(),
            Project = MakeProject(),
        };

        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        // WhenWritingNull is configured on the dispatcher's serializer, so
        // null usage / usageTotal must be entirely absent from the payload.
        Assert.False(doc.RootElement.TryGetProperty("usage", out _));
        Assert.False(doc.RootElement.TryGetProperty("usageTotal", out _));
    }
}
