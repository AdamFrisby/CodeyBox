using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class QuestionPersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteWorkItemQuestionStore _store;

    public QuestionPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-q-test-{Guid.NewGuid():N}.db");
        _store = new SqliteWorkItemQuestionStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItemQuestion NewQuestion(string workItemId = "wi-1", string questionId = "q-001") => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = workItemId,
        QuestionId = questionId,
        QuestionText = "Which approach should I use?",
    };

    [Fact]
    public async Task CreateIfNotExists_NewQuestion_ReturnsTrue()
    {
        var q = NewQuestion();
        var created = await _store.CreateIfNotExistsAsync(q);
        Assert.True(created);
    }

    [Fact]
    public async Task CreateIfNotExists_DuplicateKey_ReturnsFalse()
    {
        var q = NewQuestion("wi-1", "q-001");
        await _store.CreateIfNotExistsAsync(q);

        // Same (workItemId, questionId) — different UUID but same composite key.
        var duplicate = q with { Id = Guid.NewGuid().ToString(), QuestionText = "Updated text" };
        var created = await _store.CreateIfNotExistsAsync(duplicate);

        Assert.False(created);

        // Original text must be retained (not overwritten).
        var persisted = await _store.GetAsync("wi-1", "q-001");
        Assert.Equal("Which approach should I use?", persisted!.QuestionText);
    }

    [Fact]
    public async Task Get_NonExistent_ReturnsNull()
    {
        var result = await _store.GetAsync("wi-x", "q-nope");
        Assert.Null(result);
    }

    [Fact]
    public async Task RoundTrip_AllFieldsPreserved()
    {
        var q = NewQuestion("wi-2", "q-abc");
        await _store.CreateIfNotExistsAsync(q);

        var loaded = await _store.GetAsync("wi-2", "q-abc");
        Assert.NotNull(loaded);
        Assert.Equal(q.Id, loaded.Id);
        Assert.Equal("wi-2", loaded.WorkItemId);
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
        await _store.CreateIfNotExistsAsync(new WorkItemQuestion { Id = Guid.NewGuid().ToString(), WorkItemId = "wi-3", QuestionId = "q-001", QuestionText = "First", AskedAt = DateTimeOffset.UtcNow.AddSeconds(-5) });
        await _store.CreateIfNotExistsAsync(new WorkItemQuestion { Id = Guid.NewGuid().ToString(), WorkItemId = "wi-3", QuestionId = "q-002", QuestionText = "Second", AskedAt = DateTimeOffset.UtcNow });

        var list = await _store.ListByWorkItemAsync("wi-3");
        Assert.Equal(2, list.Count);
        Assert.Equal("q-001", list[0].QuestionId);
        Assert.Equal("q-002", list[1].QuestionId);
    }

    [Fact]
    public async Task Answer_TransitionsToAnswered()
    {
        var q = NewQuestion("wi-4", "q-001");
        await _store.CreateIfNotExistsAsync(q);

        await _store.AnswerAsync("wi-4", "q-001", "Use approach B.", "alice");

        var loaded = await _store.GetAsync("wi-4", "q-001");
        Assert.Equal("answered", loaded!.State);
        Assert.Equal("Use approach B.", loaded.AnswerText);
        Assert.Equal("alice", loaded.AnsweredBy);
        Assert.NotNull(loaded.AnsweredAt);
    }

    [Fact]
    public async Task Dismiss_TransitionsToDismissed()
    {
        var q = NewQuestion("wi-5", "q-001");
        await _store.CreateIfNotExistsAsync(q);

        await _store.DismissAsync("wi-5", "q-001", "Out of scope for this PR.");

        var loaded = await _store.GetAsync("wi-5", "q-001");
        Assert.Equal("dismissed", loaded!.State);
        Assert.Equal("Out of scope for this PR.", loaded.DismissReason);
        Assert.NotNull(loaded.DismissedAt);
    }

    [Fact]
    public async Task Answer_AlreadyAnswered_IsNoOp()
    {
        var q = NewQuestion("wi-6", "q-001");
        await _store.CreateIfNotExistsAsync(q);
        await _store.AnswerAsync("wi-6", "q-001", "First answer.", null);
        await _store.AnswerAsync("wi-6", "q-001", "Second answer (should be ignored).", null);

        var loaded = await _store.GetAsync("wi-6", "q-001");
        Assert.Equal("First answer.", loaded!.AnswerText);
    }

    [Fact]
    public async Task Dismiss_AlreadyDismissed_IsNoOp()
    {
        var q = NewQuestion("wi-7", "q-001");
        await _store.CreateIfNotExistsAsync(q);
        await _store.DismissAsync("wi-7", "q-001", "First reason.");
        await _store.DismissAsync("wi-7", "q-001", "Second reason (should be ignored).");

        var loaded = await _store.GetAsync("wi-7", "q-001");
        Assert.Equal("First reason.", loaded!.DismissReason);
    }

    [Fact]
    public async Task SameWorkItemDifferentQuestionId_StoredSeparately()
    {
        await _store.CreateIfNotExistsAsync(NewQuestion("wi-8", "q-001"));
        await _store.CreateIfNotExistsAsync(NewQuestion("wi-8", "q-002"));

        var list = await _store.ListByWorkItemAsync("wi-8");
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task DifferentWorkItemSameQuestionId_StoredSeparately()
    {
        await _store.CreateIfNotExistsAsync(NewQuestion("wi-a", "q-001"));
        await _store.CreateIfNotExistsAsync(NewQuestion("wi-b", "q-001"));

        var a = await _store.GetAsync("wi-a", "q-001");
        var b = await _store.GetAsync("wi-b", "q-001");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a!.Id, b!.Id);
    }
}
