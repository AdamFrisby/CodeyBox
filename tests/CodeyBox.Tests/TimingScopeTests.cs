using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class TimingScopeTests
{
    private static readonly WorkItemId TestId = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task BeginAsync_NullStore_ReturnsScope_NoStoreInteraction()
    {
        // Null store → no-op scope, DisposeAsync completes without error.
        await using var scope = await TimingScope.BeginAsync(null, TestId, "work", "agent.exec");
        // No assertion needed beyond no exception.
    }

    [Fact]
    public async Task BeginAndDispose_WritesBeginRowThenEnd()
    {
        var store = new CapturingTimingStore();
        await using (var scope = await TimingScope.BeginAsync(store, TestId, "work", "agent.exec"))
        {
            Assert.Single(store.BegunRecords);
            Assert.Empty(store.EndedIds);
            var rec = store.BegunRecords[0];
            Assert.Equal(TestId, rec.WorkItemId);
            Assert.Equal("work", rec.Phase);
            Assert.Equal("agent.exec", rec.Step);
            Assert.Null(rec.Iteration);
        }

        Assert.Single(store.EndedIds);
        Assert.Equal(store.BegunRecords[0].Id, store.EndedIds[0].Id);
        Assert.True(store.EndedIds[0].DurationMs >= 0);
    }

    [Fact]
    public async Task BeginAsync_WithIteration_PropagatesIteration()
    {
        var store = new CapturingTimingStore();
        await using var scope = await TimingScope.BeginAsync(store, TestId, "audit", "auditor.build", iteration: 2);
        Assert.Equal(2, store.BegunRecords[0].Iteration);
    }

    [Fact]
    public async Task BeginAsync_WithMetadata_SerializesMetadata()
    {
        var store = new CapturingTimingStore();
        var meta = new Dictionary<string, object> { ["agent"] = "claude", ["count"] = 5 };
        await using var scope = await TimingScope.BeginAsync(store, TestId, "work", "agent.exec", metadata: meta);
        var json = store.BegunRecords[0].MetadataJson;
        Assert.Contains("claude", json);
        Assert.Contains("count", json);
    }

    [Fact]
    public async Task DisposeAsync_Idempotent_SecondDisposeIsNoop()
    {
        var store = new CapturingTimingStore();
        var scope = await TimingScope.BeginAsync(store, TestId, "work", "step");
        await scope.DisposeAsync();
        await scope.DisposeAsync(); // Second call must be a no-op.
        Assert.Single(store.EndedIds);
    }

    [Fact]
    public async Task BeginAsync_StoreBeginThrows_ReturnsScopeWithNullStore_DisposeIsNoop()
    {
        var store = new ThrowingTimingStore();
        // Begin failure must not propagate.
        await using var scope = await TimingScope.BeginAsync(store, TestId, "work", "step");
        // Dispose also must not throw even though no row was inserted.
    }

    [Fact]
    public async Task DisposeAsync_StoreEndThrows_DoesNotPropagate()
    {
        var store = new EndThrowingTimingStore();
        await using var scope = await TimingScope.BeginAsync(store, TestId, "work", "step");
        // DisposeAsync catching the exception internally.
    }

    [Fact]
    public async Task MultipleConcurrentScopes_DoNotInterfere()
    {
        var store = new CapturingTimingStore();
        var scope1 = await TimingScope.BeginAsync(store, TestId, "work", "agent.exec");
        var scope2 = await TimingScope.BeginAsync(store, TestId, "work", "git.clone");
        Assert.Equal(2, store.BegunRecords.Count);
        await scope1.DisposeAsync();
        await scope2.DisposeAsync();
        Assert.Equal(2, store.EndedIds.Count);
        // Verify each scope ends its own record.
        var endedIds = store.EndedIds.Select(e => e.Id).ToHashSet();
        Assert.Contains(store.BegunRecords[0].Id, endedIds);
        Assert.Contains(store.BegunRecords[1].Id, endedIds);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class CapturingTimingStore : ITimingStore
    {
        public List<TimingRecord> BegunRecords { get; } = [];
        public List<(string Id, long DurationMs)> EndedIds { get; } = [];

        public Task BeginAsync(TimingRecord record, CancellationToken ct = default)
        {
            BegunRecords.Add(record);
            return Task.CompletedTask;
        }

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
        {
            EndedIds.Add((id, durationMs));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimingRecord>>([]);

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(int workItemLimit, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingTimingStore : ITimingStore
    {
        public Task BeginAsync(TimingRecord record, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("simulated begin failure"));

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimingRecord>>([]);

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(int workItemLimit, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EndThrowingTimingStore : ITimingStore
    {
        public Task BeginAsync(TimingRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("simulated end failure"));

        public Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimingRecord>>([]);

        public Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<TimingRecord> StreamCompletedAsync(int workItemLimit, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
