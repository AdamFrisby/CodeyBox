using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Guards against drift between <c>docs/EVENT_SCHEMA.md</c> and the
/// programmatic <see cref="EventSchema"/>. Drift is checked in both directions:
/// every entry in <see cref="EventSchema.KnownEventTypes"/> must appear in the
/// doc, and every row in the doc's event-type table must exist in code. That
/// way a stale row left over after an event-type removal also fails CI.
/// </summary>
public sealed class EventSchemaDocSyncTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeyBox.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string ReadDoc() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "EVENT_SCHEMA.md"));

    [Fact]
    public void Doc_MentionsEveryKnownEventType()
    {
        var doc = ReadDoc();
        foreach (var name in EventSchema.KnownEventTypes)
        {
            Assert.True(
                doc.Contains($"`{name}`"),
                $"docs/EVENT_SCHEMA.md must include a row for event type `{name}` (was added to EventSchema.KnownEventTypes but the doc was not updated)");
        }
    }

    [Fact]
    public void Doc_DoesNotMentionEventTypesThatNoLongerExist()
    {
        // Reverse direction: parse the event-types table in the doc and assert
        // every name resolves to a KnownEventTypes entry. Catches stale rows
        // that linger after an event is removed from code.
        var doc = ReadDoc();
        var known = new HashSet<string>(EventSchema.KnownEventTypes, StringComparer.Ordinal);

        // The table rows are of the form `| `event.type.name` | 1.0 | … |`.
        // Only event-type rows have a dotted identifier wrapped in backticks
        // in the first column, so the regex is unambiguous.
        var rowRegex = new Regex(@"^\|\s*`([a-z][a-z0-9_]*\.[a-z0-9_.]+)`\s*\|", RegexOptions.Multiline);
        var docTypes = rowRegex.Matches(doc).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(docTypes);
        var stale = docTypes.Except(known).ToList();
        Assert.True(
            stale.Count == 0,
            $"docs/EVENT_SCHEMA.md still lists event types that are no longer in EventSchema.KnownEventTypes: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Doc_DeclaresCurrentSchemaVersion()
    {
        var doc = ReadDoc();
        Assert.Equal("1.2", EventSchema.CurrentVersion);

        var declaration = Regex.Match(doc, @"^eventSchemaVersion\s*=\s*""(?<version>\d+\.\d+)""\s*$", RegexOptions.Multiline);
        Assert.True(declaration.Success, "docs/EVENT_SCHEMA.md must declare the current eventSchemaVersion");
        Assert.Equal("1.2", declaration.Groups["version"].Value);
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

        Assert.Equal("1.2", root.GetProperty("eventSchemaVersion").GetString());
        Assert.True(root.TryGetProperty("evolutionRules", out _));
        Assert.True(root.TryGetProperty("envelope", out var envelope));
        Assert.True(envelope.TryGetProperty("eventSchemaVersion", out _));
        Assert.True(envelope.TryGetProperty("eventType", out _));
        Assert.True(envelope.TryGetProperty("emittedAt", out _));
        Assert.True(root.TryGetProperty("eventTypes", out var types));
        Assert.True(types.TryGetProperty("work_item.done", out _));
    }

    [Fact]
    public void Schema_PinsExistingEnvelopeFieldsAndEventTypesToInitialVersion()
    {
        // Current schema is 1.2, but these fields and event names existed in
        // 1.0. This guards the compatibility metadata trackers use to decide
        // whether a payload is safe for their minimum supported schema.
        var schema = EventSchema.GetSchema();

        Assert.All(schema.Envelope.Values, field =>
            Assert.Equal("1.0", field.IntroducedIn));
        Assert.All(
            schema.EventTypes.Where(kv => !kv.Key.StartsWith("worker_pool.", StringComparison.Ordinal)),
            kv => Assert.Equal("1.0", kv.Value.IntroducedIn));
        Assert.Equal("1.2", schema.EventTypes["worker_pool.stalled"].IntroducedIn);
        Assert.Equal("1.2", schema.EventTypes["worker_pool.restart_required"].IntroducedIn);
    }
}
