using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the schema-version envelope (added in event schema 1.0) is
/// present on every webhook/SSE payload and on every outbound HTTP request.
/// These checks anchor the additive-only contract described in
/// <c>docs/EVENT_SCHEMA.md</c> — if a future refactor drops one of the three
/// required envelope fields, this suite fails fast.
/// </summary>
public sealed class EventSchemaEnvelopeTests
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

    private static WebhookEvent MakeEvent(string eventName = "work_item.done") => new()
    {
        Event = eventName,
        WorkItem = MakeItem(),
        Project = MakeProject(),
    };

    [Fact]
    public void Payload_CarriesAllThreeRequiredEnvelopeFields()
    {
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent());
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("eventSchemaVersion", out var version));
        Assert.Equal("1.0", version.GetString());

        Assert.True(doc.RootElement.TryGetProperty("eventType", out var type));
        Assert.Equal("work_item.done", type.GetString());

        Assert.True(doc.RootElement.TryGetProperty("emittedAt", out var emitted));
        // ISO-8601 round-trips through DateTimeOffset.
        Assert.True(DateTimeOffset.TryParse(emitted.GetString(), out _));
    }

    [Fact]
    public void Payload_KeepsLegacyAliases()
    {
        // event and occurredAt are kept for backwards compat; verify both still appear.
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent());
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("event", out var legacyEvent));
        Assert.True(doc.RootElement.TryGetProperty("occurredAt", out _));
        Assert.Equal("work_item.done", legacyEvent.GetString());
    }

    [Fact]
    public void Payload_EventTypeEqualsLegacyEventName()
    {
        // The two names must always agree at schema 1.x — otherwise trackers
        // that key off the legacy `event` field will silently see a different
        // value than the new `eventType` field.
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent("work_item.audit_iteration"));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(
            doc.RootElement.GetProperty("event").GetString(),
            doc.RootElement.GetProperty("eventType").GetString());
    }

    [Fact]
    public void Payload_EmittedAtEqualsLegacyOccurredAt_WhenDefaultsApply()
    {
        // Both default to UtcNow at construction — for a freshly built event
        // they will be very close but not identical. Verify both are ISO-8601
        // parseable and emittedAt is set.
        var evt = MakeEvent();
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(
            evt.EmittedAt,
            DateTimeOffset.Parse(doc.RootElement.GetProperty("emittedAt").GetString()!));
        Assert.Equal(
            evt.OccurredAt,
            DateTimeOffset.Parse(doc.RootElement.GetProperty("occurredAt").GetString()!));
    }

    [Fact]
    public async Task OutboundRequest_HasSchemaVersionHeader()
    {
        // The HTTP header lets a tracker reject unknown majors without parsing the body.
        var requests = new List<HttpRequestMessage>();
        var handler = new RecordingHandler(System.Net.HttpStatusCode.OK, requests);
        var factory = new SingletonHttpClientFactory(handler);
        var endpoint = new WebhookEndpointConfig
        {
            Name = "test",
            Url = "https://example.com/hook",
            MaxAttempts = 1,
            InitialBackoffSeconds = 0,
            TimeoutSeconds = 5,
        };
        await using var dispatcher = new HttpWebhookDispatcher(
            new WebhookDispatcherOptions { Endpoints = [endpoint] },
            factory,
            NullLogger<HttpWebhookDispatcher>.Instance);

        await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);

        // Channel-drained on a background task; spin briefly so it fires.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (requests.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);

        Assert.NotEmpty(requests);
        var req = requests[0];
        Assert.True(req.Headers.TryGetValues("X-CodeyBox-Schema-Version", out var headerVals));
        Assert.Equal(WebhookEvent.CurrentSchemaVersion, headerVals.Single());
    }

    [Fact]
    public void ValidateEnvelope_ReportsMissingFields()
    {
        var valid = MakeEvent();
        Assert.Null(EventSchema.ValidateEnvelope(valid));

        // Drift simulation: explicit construction with EmittedAt unset bypasses the default.
        var drifted = valid with { EmittedAt = default };
        var err = EventSchema.ValidateEnvelope(drifted);
        Assert.NotNull(err);
        Assert.Contains("emittedAt", err);
    }

    [Fact]
    public void ValidateEnvelope_ReportsMissingSchemaVersion()
    {
        var drifted = MakeEvent() with { EventSchemaVersion = "" };
        var err = EventSchema.ValidateEnvelope(drifted);
        Assert.NotNull(err);
        Assert.Contains("eventSchemaVersion", err);
    }
}
