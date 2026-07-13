using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

public sealed class AgentTurnResumeCheckpointPersistenceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codeybox-turn-checkpoint-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task CreateAndConditionalUpdate_RoundTripEveryCheckpointField()
    {
        var path = DatabasePath();
        using var store = new SqliteWorkItemStore(path);
        var checkpoint = CreateCheckpoint(attemptCount: 1);
        var original = Sample();
        var item = original with
        {
            State = WorkItemState.Failed,
            FailureKind = WorkItemFailureKinds.Infrastructure,
            WorkBranch = "codeybox/durable-resume",
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{original.Id}",
            AgentTurnResumeCheckpoint = checkpoint,
        };
        await store.CreateAsync(item);

        var created = await store.GetAsync(item.Id);

        Assert.NotNull(created);
        Assert.Equal(checkpoint, created!.AgentTurnResumeCheckpoint);
        Assert.Equal(item.PreemptCheckpoint, created.PreemptCheckpoint);

        var dispatchClaimId = Guid.Parse("d398246a-6ff8-47a2-b423-9a2f2979afaf");
        var directlyUpdated = created with
        {
            AgentTurnResumeCheckpoint = checkpoint.ClaimDispatch(dispatchClaimId),
        };
        await store.UpdateAsync(directlyUpdated);
        var afterDirectUpdate = await store.GetAsync(item.Id);
        Assert.NotNull(afterDirectUpdate);
        Assert.Equal(2, afterDirectUpdate!.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Equal(dispatchClaimId, afterDirectUpdate.AgentTurnResumeCheckpoint.DispatchClaimId);

        var updated = afterDirectUpdate.With(
            WorkItemState.WaitingForTransientRetry,
            "network transport failed",
            failureKind: "transient") with
        {
            AgentTurnResumeCheckpoint = afterDirectUpdate.AgentTurnResumeCheckpoint
                .ReleaseDispatchClaim()
                .IncrementAttemptCount(),
        };
        Assert.True(await store.TryUpdateIfStateAsync(updated, WorkItemState.Failed));

        var roundTripped = await store.GetAsync(item.Id);
        Assert.NotNull(roundTripped);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, roundTripped!.State);
        Assert.Equal(3, roundTripped.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Null(roundTripped.AgentTurnResumeCheckpoint.DispatchClaimId);
        Assert.Equal(AgentKind.Claude, roundTripped.AgentTurnResumeCheckpoint.Agent);
        Assert.Equal("claude/acct-a", roundTripped.AgentTurnResumeCheckpoint.AgentInstanceRoute);
        Assert.Equal("claude-opus-4-7", roundTripped.AgentTurnResumeCheckpoint.ModelId);
        Assert.Equal("high", roundTripped.AgentTurnResumeCheckpoint.ReasoningMode);
        Assert.Equal("native-session-persisted", roundTripped.AgentTurnResumeCheckpoint.NativeSessionId?.Value);
        Assert.Equal(WorkItemState.Reworking, roundTripped.AgentTurnResumeCheckpoint.ResumeState);
        Assert.Equal(AgentTurnResumePhase.Rework, roundTripped.AgentTurnResumeCheckpoint.Phase);
        Assert.Equal(4, roundTripped.AgentTurnResumeCheckpoint.Iteration);
        Assert.Equal(9, roundTripped.AgentTurnResumeCheckpoint.PromptRevision);
        Assert.Equal(CheckpointCreatedAt, roundTripped.AgentTurnResumeCheckpoint.CreatedAt);
    }

    [Fact]
    public async Task Migration_AddsNullableCheckpointColumnAndReadsLegacyRowAsNull()
    {
        var path = DatabasePath();
        var id = WorkItemId.New();
        var now = CheckpointCreatedAt.ToString("O");
        using (var raw = new SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var create = raw.CreateCommand();
            create.CommandText = """
                CREATE TABLE work_items (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    prompt TEXT NOT NULL,
                    base_branch TEXT,
                    work_branch TEXT,
                    agent TEXT,
                    agent_instance_id TEXT,
                    work_timeout_ticks INTEGER NOT NULL,
                    merge_timeout_ticks INTEGER NOT NULL,
                    push_upstream INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_error TEXT,
                    upstream_push_attempts INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO work_items (
                    id, project_id, title, prompt,
                    work_timeout_ticks, merge_timeout_ticks, push_upstream,
                    state, created_at, updated_at)
                VALUES (
                    $id, 'legacy', 'legacy row', 'continue',
                    $work_timeout, $merge_timeout, 1,
                    $state, $created_at, $updated_at);
                """;
            create.Parameters.AddWithValue("$id", id.ToString());
            create.Parameters.AddWithValue("$work_timeout", TimeSpan.FromMinutes(240).Ticks);
            create.Parameters.AddWithValue("$merge_timeout", TimeSpan.FromMinutes(60).Ticks);
            create.Parameters.AddWithValue("$state", (int)WorkItemState.Queued);
            create.Parameters.AddWithValue("$created_at", now);
            create.Parameters.AddWithValue("$updated_at", now);
            create.ExecuteNonQuery();
        }

        using var store = new SqliteWorkItemStore(path);
        var migrated = await store.GetAsync(id);

        Assert.NotNull(migrated);
        Assert.Null(migrated!.AgentTurnResumeCheckpoint);
        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var column = verify.CreateCommand();
        column.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('work_items')
            WHERE name = 'agent_turn_resume_checkpoint_json';
            """;
        Assert.Equal(1L, (long)column.ExecuteScalar()!);
    }

    [Fact]
    public async Task Read_SyntacticallyValidButInvalidCheckpointJsonFailsVisibly()
    {
        var path = DatabasePath();
        var original = Sample();
        var item = original with
        {
            State = WorkItemState.Failed,
            FailureKind = WorkItemFailureKinds.Infrastructure,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{original.Id}",
            AgentTurnResumeCheckpoint = CreateCheckpoint(attemptCount: 0),
        };
        using (var store = new SqliteWorkItemStore(path))
            await store.CreateAsync(item);

        using (var raw = new SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            string json;
            using (var read = raw.CreateCommand())
            {
                read.CommandText = "SELECT agent_turn_resume_checkpoint_json FROM work_items WHERE id = $id;";
                read.Parameters.AddWithValue("$id", item.Id.ToString());
                json = Assert.IsType<string>(read.ExecuteScalar());
            }

            var corrupted = json.Replace("\"attemptCount\":0", "\"attemptCount\":-1", StringComparison.Ordinal);
            Assert.NotEqual(json, corrupted);
            using var update = raw.CreateCommand();
            update.CommandText = "UPDATE work_items SET agent_turn_resume_checkpoint_json = $json WHERE id = $id;";
            update.Parameters.AddWithValue("$json", corrupted);
            update.Parameters.AddWithValue("$id", item.Id.ToString());
            update.ExecuteNonQuery();
        }

        using var reopened = new SqliteWorkItemStore(path);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.GetAsync(item.Id));

        Assert.Contains(item.Id.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("agent_turn_resume_checkpoint_json is corrupt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_OversizedCheckpointJsonFailsBeforeDeserialization()
    {
        var path = DatabasePath();
        var item = Sample();
        using (var store = new SqliteWorkItemStore(path))
            await store.CreateAsync(item);

        using (var raw = new SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var update = raw.CreateCommand();
            update.CommandText = "UPDATE work_items SET agent_turn_resume_checkpoint_json = $json WHERE id = $id;";
            update.Parameters.AddWithValue("$json", new string('x', 4097));
            update.Parameters.AddWithValue("$id", item.Id.ToString());
            update.ExecuteNonQuery();
        }

        using var reopened = new SqliteWorkItemStore(path);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.GetAsync(item.Id));

        Assert.Contains("JSON length", exception.Message, StringComparison.Ordinal);
    }

    private string DatabasePath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.db");

    private static AgentTurnResumeCheckpoint CreateCheckpoint(int attemptCount) => new(
        AgentKind.Claude,
        "claude/acct-a",
        "claude-opus-4-7",
        "high",
        new AgentNativeSessionId("native-session-persisted"),
        WorkItemState.Reworking,
        AgentTurnResumePhase.Rework,
        iteration: 4,
        promptRevision: 9,
        CheckpointCreatedAt,
        attemptCount);

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("checkpoint-persistence"),
        Title = "durable checkpoint",
        Prompt = "continue",
        PromptRevision = 9,
    };

    private static readonly DateTimeOffset CheckpointCreatedAt =
        new(2026, 7, 12, 1, 2, 3, TimeSpan.Zero);
}
