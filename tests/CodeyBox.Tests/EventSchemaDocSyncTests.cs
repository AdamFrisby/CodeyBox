using System.Text.Json;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Guards against drift between <c>docs/EVENT_SCHEMA.md</c> and the
/// programmatic <see cref="EventSchema"/>. If a new event type is added to
/// the code without a matching row in the doc (or vice versa), the
/// integration test fails fast — that's the cheapest place to catch it.
/// </summary>
public sealed class EventSchemaDocSyncTests
{
    private static string FindRepoRoot()
    {
        // Walk up from the assembly directory until we hit the slnx or the
        // repo .git folder. This is the same trick other tests use to find
        // checked-in source files without hard-coding paths.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeyBox.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Doc_MentionsEveryKnownEventType()
    {
        var docPath = Path.Combine(FindRepoRoot(), "docs", "EVENT_SCHEMA.md");
        Assert.True(File.Exists(docPath), "docs/EVENT_SCHEMA.md must exist");

        var doc = File.ReadAllText(docPath);
        foreach (var name in EventSchema.KnownEventTypes)
        {
            Assert.True(
                doc.Contains($"`{name}`"),
                $"docs/EVENT_SCHEMA.md must include a row for event type `{name}` (was added to EventSchema.KnownEventTypes but the doc was not updated)");
        }
    }

    [Fact]
    public void Doc_DeclaresCurrentSchemaVersion()
    {
        var docPath = Path.Combine(FindRepoRoot(), "docs", "EVENT_SCHEMA.md");
        var doc = File.ReadAllText(docPath);
        Assert.Contains(EventSchema.CurrentVersion, doc);
    }

    [Fact]
    public void Schema_SerializesAsExpectedShape()
    {
        // Sanity that the GET /events/schema response carries the keys
        // downstream trackers will rely on.
        var schema = EventSchema.GetSchema();
        var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal("1.0", root.GetProperty("eventSchemaVersion").GetString());
        Assert.True(root.TryGetProperty("evolutionRules", out _));
        Assert.True(root.TryGetProperty("envelope", out var envelope));
        Assert.True(envelope.TryGetProperty("eventSchemaVersion", out _));
        Assert.True(envelope.TryGetProperty("eventType", out _));
        Assert.True(envelope.TryGetProperty("emittedAt", out _));
        Assert.True(root.TryGetProperty("eventTypes", out var types));
        Assert.True(types.TryGetProperty("work_item.done", out _));
    }
}
