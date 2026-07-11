using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class QuestionPersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _itemStore;
    private readonly SqliteWorkItemQuestionStore _store;

    public QuestionPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-q-test-{Guid.NewGuid():N}.db");
        // Create the item store first so the work_items table exists for FK references.
        _itemStore = new SqliteWorkItemStore(_dbPath);
        _store = new SqliteWorkItemQuestionStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _itemStore.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private async Task<string> SeedWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Persistence test item",
            Prompt = "test",
        };
        await _itemStore.CreateAsync(item);
        return item.Id.ToString();
    }

    private static WorkItemQuestion NewQuestion(string workItemId, string questionId = "q-001") => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = workItemId,
        QuestionId = questionId,
        QuestionText = "Which approach should I use?",
    };

    [Fact]
    public async Task CreateIfNotExists_NewQuestion_ReturnsTrue()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid);
        var created = await _store.CreateIfNotExistsAsync(q);
        Assert.True(created);
    }

    [Fact]
    public async Task CreateIfNotExists_DuplicateKey_ReturnsFalse()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid, "q-001");
        await _store.CreateIfNotExistsAsync(q);

        // Same (workItemId, questionId) — different UUID but same composite key.
        var duplicate = q with { Id = Guid.NewGuid().ToString(), QuestionText = "Updated text" };
        var created = await _store.CreateIfNotExistsAsync(duplicate);

        Assert.False(created);

        // Original text must be retained (not overwritten).
        var persisted = await _store.GetAsync(wid, "q-001");
        Assert.Equal("Which approach should I use?", persisted!.QuestionText);
    }

    [Fact]
    public async Task Get_NonExistent_ReturnsNull()
    {
        var wid = await SeedWorkItemAsync();
        var result = await _store.GetAsync(wid, "q-nope");
        Assert.Null(result);
    }

    [Fact]
    public async Task RoundTrip_AllFieldsPreserved()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid, "q-abc");
        await _store.CreateIfNotExistsAsync(q);

        var loaded = await _store.GetAsync(wid, "q-abc");
        Assert.NotNull(loaded);
        Assert.Equal(q.Id, loaded.Id);
        Assert.Equal(wid, loaded.WorkItemId);
        Assert.Equal("q-abc", loaded.QuestionId);
        Assert.Equal(q.QuestionText, loaded.QuestionText);
        Assert.Equal("open", loaded.State);
        Assert.Null(loaded.AnswerText);
        Assert.Null(loaded.AnsweredAt);
        Assert.Null(loaded.DismissedAt);
    }

    [Fact]
    public async Task ListByWorkItem_MultipleQuestions_OrderedByAskedAt()
    {
        var wid = await SeedWorkItemAsync();
        await _store.CreateIfNotExistsAsync(new WorkItemQuestion { Id = Guid.NewGuid().ToString(), WorkItemId = wid, QuestionId = "q-001", QuestionText = "First", AskedAt = DateTimeOffset.UtcNow.AddSeconds(-5) });
        await _store.CreateIfNotExistsAsync(new WorkItemQuestion { Id = Guid.NewGuid().ToString(), WorkItemId = wid, QuestionId = "q-002", QuestionText = "Second", AskedAt = DateTimeOffset.UtcNow });

        var list = await _store.ListByWorkItemAsync(wid);
        Assert.Equal(2, list.Count);
        Assert.Equal("q-001", list[0].QuestionId);
        Assert.Equal("q-002", list[1].QuestionId);
    }

    [Fact]
    public async Task Answer_TransitionsToAnswered()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid, "q-001");
        await _store.CreateIfNotExistsAsync(q);

        await _store.AnswerAsync(wid, "q-001", "Use approach B.", "alice");

        var loaded = await _store.GetAsync(wid, "q-001");
        Assert.Equal("answered", loaded!.State);
        Assert.Equal("Use approach B.", loaded.AnswerText);
        Assert.Equal("alice", loaded.AnsweredBy);
        Assert.NotNull(loaded.AnsweredAt);
    }

    [Fact]
    public async Task Dismiss_TransitionsToDismissed()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid, "q-001");
        await _store.CreateIfNotExistsAsync(q);

        await _store.DismissAsync(wid, "q-001", "Out of scope for this PR.");

        var loaded = await _store.GetAsync(wid, "q-001");
        Assert.Equal("dismissed", loaded!.State);
        Assert.Equal("Out of scope for this PR.", loaded.DismissReason);
        Assert.NotNull(loaded.DismissedAt);
    }

    [Fact]
    public async Task Answer_AlreadyAnswered_IsNoOp()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid, "q-001");
        await _store.CreateIfNotExistsAsync(q);
        await _store.AnswerAsync(wid, "q-001", "First answer.", null);
        await _store.AnswerAsync(wid, "q-001", "Second answer (should be ignored).", null);

        var loaded = await _store.GetAsync(wid, "q-001");
        Assert.Equal("First answer.", loaded!.AnswerText);
    }

    [Fact]
    public async Task Dismiss_AlreadyDismissed_IsNoOp()
    {
        var wid = await SeedWorkItemAsync();
        var q = NewQuestion(wid, "q-001");
        await _store.CreateIfNotExistsAsync(q);
        await _store.DismissAsync(wid, "q-001", "First reason.");
        await _store.DismissAsync(wid, "q-001", "Second reason (should be ignored).");

        var loaded = await _store.GetAsync(wid, "q-001");
        Assert.Equal("First reason.", loaded!.DismissReason);
    }

    [Fact]
    public async Task SameWorkItemDifferentQuestionId_StoredSeparately()
    {
        var wid = await SeedWorkItemAsync();
        await _store.CreateIfNotExistsAsync(NewQuestion(wid, "q-001"));
        await _store.CreateIfNotExistsAsync(NewQuestion(wid, "q-002"));

        var list = await _store.ListByWorkItemAsync(wid);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task DifferentWorkItemSameQuestionId_StoredSeparately()
    {
        var widA = await SeedWorkItemAsync();
        var widB = await SeedWorkItemAsync();
        await _store.CreateIfNotExistsAsync(NewQuestion(widA, "q-001"));
        await _store.CreateIfNotExistsAsync(NewQuestion(widB, "q-001"));

        var a = await _store.GetAsync(widA, "q-001");
        var b = await _store.GetAsync(widB, "q-001");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a!.Id, b!.Id);
    }

    /// <summary>
    /// Simulates TryParkForQuestionsAsync's cap logic: when 10 questions already
    /// exist for a work item, additional questions are silently dropped (the cap
    /// is enforced by the caller before calling CreateIfNotExistsAsync).
    /// </summary>
    [Fact]
    public async Task QuestionCap_SurplusQuestionsDropped_OnlyTenStored()
    {
        const int Cap = 10;
        var wid = await SeedWorkItemAsync();

        // Fill up to the cap.
        for (var i = 1; i <= Cap; i++)
            await _store.CreateIfNotExistsAsync(NewQuestion(wid, $"q-{i:D3}"));

        // Simulate PipelineRunner's cap guard: check existing count before each insert.
        var existing = await _store.ListByWorkItemAsync(wid);
        var newCount = 0;
        for (var i = Cap + 1; i <= Cap + 5; i++)
        {
            if (existing.Count + newCount >= Cap) break; // cap reached — skip
            await _store.CreateIfNotExistsAsync(NewQuestion(wid, $"q-{i:D3}"));
            newCount++;
        }

        var final = await _store.ListByWorkItemAsync(wid);
        Assert.Equal(Cap, final.Count);
    }
}
