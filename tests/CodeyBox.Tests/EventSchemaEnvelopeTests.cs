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

    [Theory]
    [InlineData("queue.paused")]
    [InlineData("agent.smoke_failed")]
    [InlineData("sandbox.leak_detected")]
    [InlineData("project.budget_warning")]
    [InlineData("work_item.done")]
    [InlineData("release.published")]
    public void Payload_CarriesAllThreeRequiredEnvelopeFields(string eventName)
    {
        // One representative event from each category — a future refactor that
        // skips BuildPayload for a particular category (e.g. queue.*) gets
        // caught here rather than only by integration tests.
        var json = HttpWebhookDispatcher.BuildPayload(MakeEvent(eventName));
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("eventSchemaVersion", out var version));
        Assert.Equal("1.0", version.GetString());

        Assert.True(doc.RootElement.TryGetProperty("eventType", out var type));
        Assert.Equal(eventName, type.GetString());

        Assert.True(doc.RootElement.TryGetProperty("emittedAt", out var emitted));
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
    public void Payload_EmittedAtAndOccurredAt_RoundTripThroughJson()
    {
        // Both timestamps default to UtcNow at construction; they will be very
        // close but not tick-identical. Assert each round-trips back to its
        // source property — the schema-1.0 docs describe them as a stable
        // alias, not a tick-for-tick guarantee.
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
    public void Payload_ReleaseEnvelopeFieldIsSerialised()
    {
        // The schema/docs advertise `release` as a top-level envelope field.
        // BuildPayload must serialise evt.Release so trackers validating
        // strict-against-schema don't see a missing field on release.* events.
        var release = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = new ProjectId("proj"),
            Name = "v1.4.0",
            State = ReleaseState.Open,
            BranchName = "release/v1.4.0",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var evt = MakeEvent("release.created") with { Release = release };
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("release", out var releaseEl));
        Assert.Equal(JsonValueKind.Object, releaseEl.ValueKind);
        Assert.Equal(release.Name, releaseEl.GetProperty("name").GetString());
        Assert.Equal("Open", releaseEl.GetProperty("state").GetString());
        Assert.Equal("release/v1.4.0", releaseEl.GetProperty("branchName").GetString());
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

    [Fact]
    public void ValidateEnvelope_ReportsMissingEventType()
    {
        // Event is `required` so the only way to land here in production is a
        // future builder forgetting to forward the event name, or an upstream
        // string source that resolves to "". Pin both the null and empty cases.
        var drifted = MakeEvent() with { Event = "" };
        var err = EventSchema.ValidateEnvelope(drifted);
        Assert.NotNull(err);
        Assert.Contains("eventType", err);
    }

    [Fact]
    public void ValidateEnvelope_ReportsNullEvent()
    {
        // ValidateEnvelope is a public static — callers can pass null. The
        // broadcaster's ArgumentNullException.ThrowIfNull means production
        // never hits this, but pin the contract.
        var err = EventSchema.ValidateEnvelope(null);
        Assert.NotNull(err);
    }

    [Fact]
    public void Publish_StrictMode_ThrowsWhenEnvelopeIsInvalid()
    {
        // The strict-mode safeguard itself needs a guard — a future refactor
        // that demotes the throw to a log call must fail CI here.
        var broadcaster = new WebhookEventBroadcaster();
        var bad = MakeEvent() with { EventSchemaVersion = "" };

        var prev = WebhookEventBroadcaster.StrictSchemaValidationForTests;
        WebhookEventBroadcaster.StrictSchemaValidationForTests = true;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => broadcaster.Publish(bad));
            Assert.Contains("eventSchemaVersion", ex.Message);
        }
        finally
        {
            WebhookEventBroadcaster.StrictSchemaValidationForTests = prev;
        }
    }

    [Fact]
    public async Task SsePayload_CarriesAllThreeRequiredEnvelopeFields()
    {
        // SSE reuses HttpWebhookDispatcher.BuildPayload today, but a future
        // refactor that introduces a separate builder must not silently strip
        // the envelope. Drive the broadcaster end-to-end via Subscribe/Publish
        // and assert the envelope on the materialised event.
        var broadcaster = new WebhookEventBroadcaster();
        await using var subscription = broadcaster.Subscribe(new SubscriptionFilter(), lastEventId: null);

        var evt = MakeEvent("work_item.audit_iteration");
        broadcaster.Publish(evt);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        BroadcastedEvent? received = null;
        await foreach (var b in subscription.ReadAsync(cts.Token))
        {
            received = b;
            break;
        }
        Assert.NotNull(received);

        // Same builder the SSE WriteEventAsync calls — if it's ever replaced
        // by an SSE-specific builder, this assertion must move with it.
        var json = HttpWebhookDispatcher.BuildPayload(received!.Event);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.0", doc.RootElement.GetProperty("eventSchemaVersion").GetString());
        Assert.Equal("work_item.audit_iteration", doc.RootElement.GetProperty("eventType").GetString());
        Assert.True(doc.RootElement.TryGetProperty("emittedAt", out _));
    }
}
