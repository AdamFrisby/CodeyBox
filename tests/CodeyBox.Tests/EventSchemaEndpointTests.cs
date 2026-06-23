using System.Net;
using System.Text.Json;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level test for <c>GET /events/schema</c>. Trackers call this at
/// startup to validate which majors and event types they understand; verify
/// the endpoint returns the same shape as <see cref="EventSchema.GetSchema"/>.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class EventSchemaEndpointTests : IDisposable
{
    private readonly SseApiFactory _factory = new();
    private readonly HttpClient _client;

    public EventSchemaEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetSchema_ReturnsCurrentVersionAndKnownEventTypes()
    {
        using var resp = await _client.GetAsync("/events/schema");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1.5", root.GetProperty("eventSchemaVersion").GetString());

        var eventTypes = root.GetProperty("eventTypes");
        // Every event type the code knows about must appear in the endpoint payload.
        foreach (var known in EventSchema.KnownEventTypes)
        {
            Assert.True(
                eventTypes.TryGetProperty(known, out _),
                $"GET /events/schema response missing event type `{known}`");
        }

        var envelope = root.GetProperty("envelope");
        Assert.True(envelope.TryGetProperty("eventSchemaVersion", out _));
        Assert.True(envelope.TryGetProperty("eventType", out _));
        Assert.True(envelope.TryGetProperty("emittedAt", out _));
    }
}
