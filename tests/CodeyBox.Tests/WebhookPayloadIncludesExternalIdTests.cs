using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that webhook event payloads include externalId when set, and omit
/// it (null / absent under WhenWritingNull) when not set.
/// </summary>
public sealed class WebhookPayloadIncludesExternalIdTests
{
    private static WorkItem MakeItem(string? externalId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "test",
        Prompt = "do the thing",
        ExternalIds = externalId is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["legacy"] = externalId },
    };

    private static Project MakeProject() => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.com/repo.git",
    };

    private static WebhookEvent MakeEvent(string? externalId) => new()
    {
        Event = "work_item.done",
        WorkItem = MakeItem(externalId),
        Project = MakeProject(),
    };

    [Fact]
    public void Payload_WithExternalId_IncludesField()
    {
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent("JIRA-1234"));
        using var doc = JsonDocument.Parse(json);

        var wi = doc.RootElement.GetProperty("workItem");
        Assert.True(wi.TryGetProperty("externalId", out var extProp));
        Assert.Equal("JIRA-1234", extProp.GetString());
    }

    [Fact]
    public void Payload_WithoutExternalId_OmitsOrNullsField()
    {
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent(null));
        using var doc = JsonDocument.Parse(json);

        var wi = doc.RootElement.GetProperty("workItem");
        // WhenWritingNull omits the key; if present it must be JsonValueKind.Null
        if (wi.TryGetProperty("externalId", out var extProp))
            Assert.Equal(JsonValueKind.Null, extProp.ValueKind);
    }

    [Fact]
    public void Payload_WithExternalId_IdIsAlsoPresent()
    {
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent("GH-99"));
        using var doc = JsonDocument.Parse(json);

        var wi = doc.RootElement.GetProperty("workItem");
        Assert.True(wi.TryGetProperty("id", out _));
        Assert.True(wi.TryGetProperty("externalId", out var extProp));
        Assert.Equal("GH-99", extProp.GetString());
    }
}
