using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SuggestionsPersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-suggestions-persist-{Guid.NewGuid():N}.db");
    private readonly SqliteSuggestionStore _store;

    public SuggestionsPersistenceTests() => _store = new SqliteSuggestionStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static Suggestion Make(
        string? id = null,
        string projectId = "proj",
        string category = "test-coverage",
        string severity = "minor",
        string state = "open") => new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            SourceWorkItemId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            Title = "Test suggestion",
            Rationale = "Some rationale",
            Category = category,
            Severity = severity,
            EstimatedEffort = "small",
            CreatedAt = DateTimeOffset.UtcNow,
            State = state,
        };

    [Fact]
    public async Task CreateAndGet_RoundTrips_AllFields()
    {
        var s = new Suggestion
        {
            Id = "test-id-001",
            SourceWorkItemId = "wi-aabb0011",
            ProjectId = "my-project",
            Title = "Add tests",
            Rationale = "Missing coverage",
            Category = "test-coverage",
            Severity = "notable",
            EstimatedEffort = "medium",
            FilesReferenced = ["src/Foo.cs", "tests/FooTests.cs"],
            CreatedAt = DateTimeOffset.Parse("2026-01-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            State = "open",
        };

        await _store.CreateAsync(s);
        var got = await _store.GetAsync(s.Id);

        Assert.NotNull(got);
        Assert.Equal(s.Id, got.Id);
        Assert.Equal(s.SourceWorkItemId, got.SourceWorkItemId);
        Assert.Equal(s.ProjectId, got.ProjectId);
        Assert.Equal(s.Title, got.Title);
        Assert.Equal(s.Rationale, got.Rationale);
        Assert.Equal(s.Category, got.Category);
        Assert.Equal(s.Severity, got.Severity);
        Assert.Equal(s.EstimatedEffort, got.EstimatedEffort);
        Assert.Equal(2, got.FilesReferenced.Count);
        Assert.Equal("src/Foo.cs", got.FilesReferenced[0]);
        Assert.Equal("tests/FooTests.cs", got.FilesReferenced[1]);
        Assert.Equal("open", got.State);
        Assert.Null(got.DismissReason);
        Assert.Null(got.PromotedToWorkItemId);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var got = await _store.GetAsync("no-such-id");
        Assert.Null(got);
    }

    [Fact]
    public async Task UpdateAsync_Dismiss_PersistsStateAndReason()
    {
        var s = Make();
        await _store.CreateAsync(s);

        await _store.UpdateAsync(s with { State = "dismissed", DismissReason = "not on roadmap" });

        var got = await _store.GetAsync(s.Id);
        Assert.Equal("dismissed", got!.State);
        Assert.Equal("not on roadmap", got.DismissReason);
    }

    [Fact]
    public async Task UpdateAsync_Accept_PersistsPromotedWorkItemId()
    {
        var s = Make();
        await _store.CreateAsync(s);

        var workItemId = Guid.NewGuid().ToString();
        await _store.UpdateAsync(s with { State = "accepted", PromotedToWorkItemId = workItemId });

        var got = await _store.GetAsync(s.Id);
        Assert.Equal("accepted", got!.State);
        Assert.Equal(workItemId, got.PromotedToWorkItemId);
    }

    [Fact]
    public async Task ListAsync_DefaultState_OnlyReturnsOpen()
    {
        var open = Make();
        var toBeDisabled = Make();
        await _store.CreateAsync(open);
        await _store.CreateAsync(toBeDisabled);
        await _store.UpdateAsync(toBeDisabled with { State = "dismissed" });

        var results = await ToList(_store.ListAsync());
        Assert.All(results, r => Assert.Equal("open", r.State));
        Assert.Contains(results, r => r.Id == open.Id);
        Assert.DoesNotContain(results, r => r.Id == toBeDisabled.Id);
    }

    [Fact]
    public async Task ListAsync_ProjectFilter_OnlyMatchingProject()
    {
        var a = Make(projectId: "proj-a");
        var b = Make(projectId: "proj-b");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        var results = await ToList(_store.ListAsync(projectId: "proj-a"));
        Assert.Single(results);
        Assert.Equal("proj-a", results[0].ProjectId);
    }

    [Fact]
    public async Task ListAsync_CategoryFilter_OnlyMatchingCategory()
    {
        var sec = Make(category: "security");
        var docs = Make(category: "docs");
        await _store.CreateAsync(sec);
        await _store.CreateAsync(docs);

        var results = await ToList(_store.ListAsync(category: "security"));
        Assert.Single(results);
        Assert.Equal("security", results[0].Category);
    }

    [Fact]
    public async Task ListAsync_SeverityFilter_OnlyMatchingSeverity()
    {
        var important = Make(severity: "important");
        var minor = Make(severity: "minor");
        await _store.CreateAsync(important);
        await _store.CreateAsync(minor);

        var results = await ToList(_store.ListAsync(severity: "important"));
        Assert.Single(results);
        Assert.Equal("important", results[0].Severity);
    }

    [Fact]
    public async Task ListAsync_CombinedFilters_Intersection()
    {
        var match = Make(projectId: "proj-x", category: "security", severity: "important");
        var wrongProj = Make(projectId: "proj-y", category: "security", severity: "important");
        var wrongCat = Make(projectId: "proj-x", category: "docs", severity: "important");
        await _store.CreateAsync(match);
        await _store.CreateAsync(wrongProj);
        await _store.CreateAsync(wrongCat);

        var results = await ToList(_store.ListAsync(projectId: "proj-x", category: "security", severity: "important"));
        Assert.Single(results);
        Assert.Equal(match.Id, results[0].Id);
    }

    [Fact]
    public async Task CountOpenAsync_ReturnsOnlyOpenCount()
    {
        var a = Make();
        var b = Make();
        var c = Make();
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);
        await _store.UpdateAsync(c with { State = "dismissed" });

        var count = await _store.CountOpenAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountOpenAsync_EmptyStore_ReturnsZero()
    {
        Assert.Equal(0, await _store.CountOpenAsync());
    }

    [Fact]
    public async Task ListAsync_EmptyStore_ReturnsEmpty()
    {
        var results = await ToList(_store.ListAsync());
        Assert.Empty(results);
    }

    [Fact]
    public async Task ListAsync_NullStateFilter_ReturnsAllStates()
    {
        var open = Make();
        var dismissed = Make();
        await _store.CreateAsync(open);
        await _store.CreateAsync(dismissed);
        await _store.UpdateAsync(dismissed with { State = "dismissed" });

        // null state = no state filter (returns all)
        var all = await ToList(_store.ListAsync(state: null));
        Assert.Equal(2, all.Count);
    }

    // ── TryAcceptAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAcceptAsync_OpenSuggestion_ReturnsTrueAndSetsAccepted()
    {
        var s = Make();
        await _store.CreateAsync(s);
        var workItemId = Guid.NewGuid().ToString();

        var result = await _store.TryAcceptAsync(s.Id, workItemId);

        Assert.True(result);
        var got = await _store.GetAsync(s.Id);
        Assert.Equal("accepted", got!.State);
        Assert.Equal(workItemId, got.PromotedToWorkItemId);
    }

    [Fact]
    public async Task TryAcceptAsync_AlreadyAccepted_ReturnsFalse()
    {
        var s = Make();
        await _store.CreateAsync(s);
        await _store.TryAcceptAsync(s.Id, Guid.NewGuid().ToString());

        var result = await _store.TryAcceptAsync(s.Id, Guid.NewGuid().ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task TryAcceptAsync_AlreadyDismissed_ReturnsFalse()
    {
        var s = Make();
        await _store.CreateAsync(s);
        await _store.TryDismissAsync(s.Id, null);

        var result = await _store.TryAcceptAsync(s.Id, Guid.NewGuid().ToString());

        Assert.False(result);
    }

    // ── TryDismissAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task TryDismissAsync_OpenSuggestion_ReturnsTrueAndSetsDismissed()
    {
        var s = Make();
        await _store.CreateAsync(s);

        var result = await _store.TryDismissAsync(s.Id, "not on roadmap");

        Assert.True(result);
        var got = await _store.GetAsync(s.Id);
        Assert.Equal("dismissed", got!.State);
        Assert.Equal("not on roadmap", got.DismissReason);
    }

    [Fact]
    public async Task TryDismissAsync_NoReason_ReturnsTrueAndSetsDismissed()
    {
        var s = Make();
        await _store.CreateAsync(s);

        var result = await _store.TryDismissAsync(s.Id, null);

        Assert.True(result);
        var got = await _store.GetAsync(s.Id);
        Assert.Equal("dismissed", got!.State);
        Assert.Null(got.DismissReason);
    }

    [Fact]
    public async Task TryDismissAsync_AlreadyDismissed_ReturnsFalse()
    {
        var s = Make();
        await _store.CreateAsync(s);
        await _store.TryDismissAsync(s.Id, null);

        var result = await _store.TryDismissAsync(s.Id, "second attempt");

        Assert.False(result);
    }

    [Fact]
    public async Task TryDismissAsync_AlreadyAccepted_ReturnsFalse()
    {
        var s = Make();
        await _store.CreateAsync(s);
        await _store.TryAcceptAsync(s.Id, Guid.NewGuid().ToString());

        var result = await _store.TryDismissAsync(s.Id, "reason");

        Assert.False(result);
    }

    private static async Task<List<T>> ToList<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
