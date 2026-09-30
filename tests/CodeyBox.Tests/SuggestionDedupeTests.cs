using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Dedupe of agent-filed suggestion repeats: differently-worded repeats of
/// the same finding converge on one row (count + sources bumped), repeats of
/// a recently-dismissed suggestion stay dismissed, and genuinely different
/// findings sharing a file are not merged. All through the real
/// <see cref="SqliteSuggestionStore"/> with a controllable clock.
/// </summary>
public sealed class SuggestionDedupeTests : IDisposable
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestScratchDirectory _scratch =
        TestScratchDirectory.Create("codeybox-suggestion-dedupe-");
    private readonly ControllableTimeProvider _time = new(Start);
    private readonly List<SqliteSuggestionStore> _stores = [];
    private int _dbSeq;

    public void Dispose()
    {
        foreach (var s in _stores)
            s.Dispose();
        _scratch.Dispose();
    }

    private SqliteSuggestionStore NewStore()
    {
        var store = new SqliteSuggestionStore(
            _scratch.DbPath($"dedupe-{_dbSeq++}.db"), timeProvider: _time);
        _stores.Add(store);
        return store;
    }

    private static Suggestion Make(
        string sourceWorkItemId,
        string title,
        string category = "docs",
        string projectId = "proj",
        IReadOnlyList<string>? files = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            SourceWorkItemId = sourceWorkItemId,
            ProjectId = projectId,
            Title = title,
            Rationale = "Rationale for " + title,
            Category = category,
            Severity = "minor",
            EstimatedEffort = "small",
            FilesReferenced = files ?? ["docs/post-work-checks/runbook.md"],
            CreatedAt = Start,
        };

    private static SuggestionDedupePolicy Policy(
        double threshold = 0.6,
        TimeSpan? window = null,
        int maxSources = 25) => new()
        {
            SimilarityThreshold = threshold,
            DismissedMatchWindow = window ?? TimeSpan.FromDays(30),
            MaxRecordedSourceIds = maxSources,
        };

    [Fact]
    public async Task DifferentlyWordedRepeat_MergesIntoOneSuggestion()
    {
        var store = NewStore();
        var policy = Policy();

        var first = Make(
            "work-item-aaaa1111",
            "docs/post-work-checks/ referenced by work-item prompts does not exist");
        var firstOutcome = await store.CreateOrMergeAsync(first, policy);

        var repeat = Make(
            "work-item-bbbb2222",
            "Missing docs/post-work-checks directory referenced in work item prompts");
        var repeatOutcome = await store.CreateOrMergeAsync(repeat, policy);

        Assert.False(firstOutcome.Merged);
        Assert.True(repeatOutcome.Merged);
        Assert.Equal(firstOutcome.Suggestion.Id, repeatOutcome.Suggestion.Id);
        Assert.Equal(2, repeatOutcome.Suggestion.OccurrenceCount);
        Assert.Equal(
            ["work-item-aaaa1111", "work-item-bbbb2222"],
            repeatOutcome.Suggestion.SourceWorkItemIds);
        Assert.Equal("open", repeatOutcome.Suggestion.State);

        var count = await store.CountAsync(state: null);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DismissedRepeat_WithinWindow_StaysDismissedAndBumpsCount()
    {
        var store = NewStore();
        var policy = Policy();

        var first = Make("work-item-aaaa1111", "Kubeconform auditor warns-as-errors on pristine tree");
        var created = await store.CreateOrMergeAsync(first, policy);
        Assert.True(await store.TryDismissAsync(created.Suggestion.Id, "Will fix separately"));

        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromDays(7));
        var repeat = Make(
            "work-item-bbbb2222",
            "Kubeconform auditor warnings-as-errors break on a pristine tree");
        var outcome = await store.CreateOrMergeAsync(repeat, policy);

        Assert.True(outcome.Merged);
        Assert.Equal("dismissed", outcome.Suggestion.State);
        Assert.Equal("Will fix separately", outcome.Suggestion.DismissReason);
        Assert.Equal(2, outcome.Suggestion.OccurrenceCount);
        Assert.Equal(
            ["work-item-aaaa1111", "work-item-bbbb2222"],
            outcome.Suggestion.SourceWorkItemIds);

        var count = await store.CountAsync(state: null);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DismissedRepeat_OutsideWindow_CreatesNewRow()
    {
        var store = NewStore();
        var policy = Policy(window: TimeSpan.FromDays(30));

        var first = Make("work-item-aaaa1111", "Kubeconform auditor warns-as-errors on pristine tree");
        var created = await store.CreateOrMergeAsync(first, policy);
        Assert.True(await store.TryDismissAsync(created.Suggestion.Id, "Stale"));

        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromDays(31));
        var repeat = Make(
            "work-item-bbbb2222",
            "Kubeconform auditor warnings-as-errors break on a pristine tree");
        var outcome = await store.CreateOrMergeAsync(repeat, policy);

        Assert.False(outcome.Merged);
        Assert.Equal("open", outcome.Suggestion.State);
        Assert.Equal(1, outcome.Suggestion.OccurrenceCount);

        var count = await store.CountAsync(state: null);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task DifferentFindings_SameFile_NotMerged()
    {
        var store = NewStore();
        var policy = Policy();

        var first = Make(
            "work-item-aaaa1111",
            "Add missing unit tests for the query parser",
            category: "test-coverage",
            files: ["src/Parser.cs"]);
        var second = Make(
            "work-item-bbbb2222",
            "Rotate the signing key used by webhook delivery",
            category: "test-coverage",
            files: ["src/Parser.cs"]);

        Assert.False((await store.CreateOrMergeAsync(first, policy)).Merged);
        var outcome = await store.CreateOrMergeAsync(second, policy);

        Assert.False(outcome.Merged);
        Assert.Equal(1, outcome.Suggestion.OccurrenceCount);
        Assert.Equal(
            ["work-item-bbbb2222"],
            outcome.Suggestion.SourceWorkItemIds);

        var count = await store.CountAsync(state: null);
        Assert.Equal(2, count);
    }

    [Fact]
    public void DedupeKey_IgnoresCasePunctuationStopWordsAndFileOrder()
    {
        var baseline = SuggestionDedupe.ComputeDedupeKey(
            "Docs", ["docs/a.md", "docs/b.md"], "The missing runbook does not exist!");
        var variant = SuggestionDedupe.ComputeDedupeKey(
            "docs", ["docs/b.md", "docs/a.md"], "missing runbook exist");

        Assert.Equal(baseline, variant);
    }

    [Fact]
    public void DedupeKey_DiffersByCategory()
    {
        var docs = SuggestionDedupe.ComputeDedupeKey("docs", ["docs/a.md"], "missing runbook");
        var tests = SuggestionDedupe.ComputeDedupeKey("test-coverage", ["docs/a.md"], "missing runbook");

        Assert.NotEqual(docs, tests);
    }

    [Fact]
    public async Task SourceList_BoundedByPolicy()
    {
        var store = NewStore();
        var policy = Policy(maxSources: 2);

        var first = Make("work-item-0001", "Missing runbook for the deploy pipeline");
        var created = await store.CreateOrMergeAsync(first, policy);
        Assert.False(created.Merged);

        for (var i = 2; i <= 4; i++)
        {
            var repeat = Make(
                $"work-item-000{i}",
                "The deploy pipeline runbook is still missing");
            var outcome = await store.CreateOrMergeAsync(repeat, policy);
            Assert.True(outcome.Merged);
        }

        var got = await store.GetAsync(created.Suggestion.Id);
        Assert.NotNull(got);
        Assert.Equal(4, got.OccurrenceCount);
        Assert.Equal(2, got.SourceWorkItemIds.Count);
        Assert.Equal("work-item-0001", got.SourceWorkItemIds[0]);
    }
}
