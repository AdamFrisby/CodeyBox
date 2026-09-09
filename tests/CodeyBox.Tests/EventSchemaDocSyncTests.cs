using System.Text.Json;
using System.Text.RegularExpressions;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Guards against drift between <c>docs/reference/events.md</c> and the
/// programmatic <see cref="EventSchema"/>. Drift is checked in both directions:
/// every entry in <see cref="EventSchema.KnownEventTypes"/> must appear in the
/// doc, and every row in the doc's event-type table must exist in code. That
/// way a stale row left over after an event-type removal also fails CI.
/// </summary>
public sealed class EventSchemaDocSyncTests
{
    private const string ExpectedCurrentSchemaVersion = "1.5";

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
        File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "reference", "events.md"));

    [Fact]
    public void Doc_MentionsEveryKnownEventType()
    {
        var doc = ReadDoc();
        foreach (var name in EventSchema.KnownEventTypes)
        {
            Assert.True(
                doc.Contains($"`{name}`"),
                $"docs/reference/events.md must include a row for event type `{name}` (was added to EventSchema.KnownEventTypes but the doc was not updated)");
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
            $"docs/reference/events.md still lists event types that are no longer in EventSchema.KnownEventTypes: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Doc_EventTypeIntroducedVersionsMatchSchema()
    {
        var docTypes = ParseDocEventTypeVersions(ReadDoc());
        var schema = EventSchema.GetSchema();

        Assert.NotEmpty(docTypes);
        foreach (var (name, introducedIn) in docTypes)
        {
            Assert.True(
                schema.EventTypes.TryGetValue(name, out var eventType),
                $"docs/reference/events.md lists event type `{name}` but EventSchema does not");
            Assert.Equal(introducedIn, eventType.IntroducedIn);
        }
    }

    [Fact]
    public void Doc_DeclaresCurrentSchemaVersion()
    {
        var doc = ReadDoc();
        Assert.Equal(ExpectedCurrentSchemaVersion, EventSchema.CurrentVersion);

        var declaration = Regex.Match(doc, @"^eventSchemaVersion\s*=\s*""(?<version>\d+\.\d+)""\s*$", RegexOptions.Multiline);
        Assert.True(declaration.Success, "docs/reference/events.md must declare the current eventSchemaVersion");
        Assert.Equal(ExpectedCurrentSchemaVersion, declaration.Groups["version"].Value);
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

        Assert.Equal(ExpectedCurrentSchemaVersion, root.GetProperty("eventSchemaVersion").GetString());
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
        // Current schema is 1.5, but these fields and event names existed in
        // 1.0. This guards the compatibility metadata trackers use to decide
        // whether a payload is safe for their minimum supported schema.
        var schema = EventSchema.GetSchema();
        var nonInitialEventTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "upstream.pr_stale_base",
            "worker_pool.stalled",
            "worker_pool.restart_required",
            "agent.paused",
            "agent.resumed",
            "agent.restore_requeue_swept",
            "work_item.waiting_for_agent_resume",
            "work_item.waiting_for_transient_retry",
            "work_item.agent_restore_requeued",
            "work_item.planning",
            "work_item.plan_review",
            "work_item.plan_approved",
            "audit.auditor_timed_out",
        };

        Assert.All(schema.Envelope.Values, field =>
            Assert.Equal("1.0", field.IntroducedIn));
        Assert.All(schema.EventTypes.Where(kv => !nonInitialEventTypes.Contains(kv.Key)),
            kv => Assert.Equal("1.0", kv.Value.IntroducedIn));
        Assert.Equal("1.1", schema.EventTypes["upstream.pr_stale_base"].IntroducedIn);
        Assert.Equal("1.2", schema.EventTypes["worker_pool.stalled"].IntroducedIn);
        Assert.Equal("1.2", schema.EventTypes["worker_pool.restart_required"].IntroducedIn);
        Assert.Equal("1.3", schema.EventTypes["agent.paused"].IntroducedIn);
        Assert.Equal("1.3", schema.EventTypes["agent.resumed"].IntroducedIn);
        Assert.Equal("1.3", schema.EventTypes["work_item.waiting_for_agent_resume"].IntroducedIn);
        Assert.Equal("1.4", schema.EventTypes["work_item.waiting_for_transient_retry"].IntroducedIn);
        Assert.Equal("1.5", schema.EventTypes["agent.restore_requeue_swept"].IntroducedIn);
        Assert.Equal("1.5", schema.EventTypes["work_item.agent_restore_requeued"].IntroducedIn);
        Assert.Equal("1.5", schema.EventTypes["work_item.planning"].IntroducedIn);
        Assert.Equal("1.5", schema.EventTypes["work_item.plan_review"].IntroducedIn);
        Assert.Equal("1.5", schema.EventTypes["work_item.plan_approved"].IntroducedIn);
        Assert.Equal("1.5", schema.EventTypes["audit.auditor_timed_out"].IntroducedIn);
    }

    private static IReadOnlyDictionary<string, string> ParseDocEventTypeVersions(string doc)
    {
        var rowRegex = new Regex(
            @"^\|\s*`([a-z][a-z0-9_]*\.[a-z0-9_.]+)`\s*\|\s*(\d+\.\d+)\s*\|",
            RegexOptions.Multiline);
        return rowRegex
            .Matches(doc)
            .ToDictionary(
                m => m.Groups[1].Value,
                m => m.Groups[2].Value,
                StringComparer.Ordinal);
    }
}
