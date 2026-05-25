using System.Net;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class QuotaRetryStatusEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public QuotaRetryStatusEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetQuotaRetryStatus_GroupsParkedItemsByStateAndHoursSinceDeadline()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        await CreateItemAsync(WorkItemState.WaitingForQuotaReset, "quota", now.AddHours(-130));
        await CreateItemAsync(WorkItemState.WaitingForQuotaReset, "quota", now.AddHours(-130));
        await CreateItemAsync(WorkItemState.WaitingForQuotaReset, "quota", now.AddHours(3));
        await CreateItemAsync(WorkItemState.WaitingForQuotaReset, "quota", nextRetryAt: null);
        await CreateItemAsync(WorkItemState.Failed, "quota", now.AddHours(-5));
        await CreateItemAsync(WorkItemState.Failed, "agent", now.AddHours(-10));

        var response = await _client.GetAsync("/admin/quota-retry-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(5, root.GetProperty("totalParked").GetInt32());
        var buckets = root.GetProperty("buckets").EnumerateArray().ToArray();

        Assert.Contains(buckets, b =>
            b.GetProperty("state").GetString() == "WaitingForQuotaReset"
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").ValueKind == JsonValueKind.Number
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").GetInt32() == -3
            && b.GetProperty("count").GetInt32() == 1);
        Assert.Contains(buckets, b =>
            b.GetProperty("state").GetString() == "WaitingForQuotaReset"
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").ValueKind == JsonValueKind.Number
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").GetInt32() == 130
            && b.GetProperty("count").GetInt32() == 2);
        Assert.Contains(buckets, b =>
            b.GetProperty("state").GetString() == "Failed"
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").ValueKind == JsonValueKind.Number
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").GetInt32() == 5
            && b.GetProperty("count").GetInt32() == 1);
        Assert.Contains(buckets, b =>
            b.GetProperty("state").GetString() == "WaitingForQuotaReset"
            && b.GetProperty("hoursSinceNextQuotaRetryAtDeadline").ValueKind == JsonValueKind.Null
            && b.GetProperty("count").GetInt32() == 1);
    }

    private async Task CreateItemAsync(WorkItemState state, string failureKind, DateTimeOffset? nextRetryAt)
    {
        await _factory.Store.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "quota status",
            Prompt = "p",
            State = state,
            FailureKind = failureKind,
            NextQuotaRetryAt = nextRetryAt,
        });
    }
}
