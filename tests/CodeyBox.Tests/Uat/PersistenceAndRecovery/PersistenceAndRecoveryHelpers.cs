using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.PersistenceAndRecovery;

internal sealed class PersistenceAndRecoveryWorkspace : IDisposable
{
    private readonly TestTempDirectory _temp = TestTempDirectory.Create("codeybox-uat-persistence-");

    public string Root => _temp.Root;

    public string NewDatabasePath(string name = "state")
        => Path.Combine(Root, $"{name}-{Guid.NewGuid():N}.db");

    public void Dispose() => _temp.Dispose();
}

internal static class PersistenceAndRecoveryHelpers
{
    public static ProjectId ProjectId { get; } = new("uat-persistence");

    public static WorkItem Item(WorkItemState state = WorkItemState.Queued, int recoveryAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = ProjectId,
        Title = "Persistence and recovery UAT",
        Prompt = "exercise durable state",
        BaseBranch = "main",
        WorkBranch = "codeybox/uat-persistence",
        State = state,
        RecoveryAttempts = recoveryAttempts,
        StartedAt = state is WorkItemState.Queued or WorkItemState.Done ? null : DateTimeOffset.Parse("2026-05-14T00:00:00Z"),
    };

    public static OrchestratorService BuildReplayService(
        IWorkItemStore store,
        ITaskQueue queue,
        int maxRecoveryAttempts = 3)
        => new(
            queue,
            store,
            new RecordingPipelineRunner(),
            new CancellationRegistry(),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MaxRecoveryAttempts = maxRecoveryAttempts,
            },
            NullLogger<OrchestratorService>.Instance);

    public static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    public static IReadOnlySet<string> GetTableNames(string dbPath)
        => GetSchemaObjectNames(dbPath, "table");

    public static IReadOnlySet<string> GetIndexNames(string dbPath)
        => GetSchemaObjectNames(dbPath, "index");

    public static IReadOnlySet<string> GetColumnNames(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(reader.GetOrdinal("name")));
        return names;
    }

    public static void CreateLegacyWorkItemsDatabase(string dbPath, WorkItem item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE work_items (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                title TEXT NOT NULL,
                prompt TEXT NOT NULL,
                base_branch TEXT,
                work_branch TEXT,
                agent TEXT,
                work_timeout_ticks INTEGER NOT NULL,
                merge_timeout_ticks INTEGER NOT NULL,
                push_upstream INTEGER NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                upstream_push_attempts INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX idx_work_items_state ON work_items(state);
            CREATE INDEX idx_work_items_project ON work_items(project_id);

            INSERT INTO work_items (id, project_id, title, prompt, base_branch, work_branch, agent,
                work_timeout_ticks, merge_timeout_ticks, push_upstream, state, created_at, updated_at,
                last_error, upstream_push_attempts)
            VALUES ($id, $project, $title, $prompt, $base, $work, $agent, $work_timeout,
                $merge_timeout, $push_upstream, $state, $created, $updated, $last_error, $upstream_attempts);
            """;
        cmd.Parameters.AddWithValue("$id", item.Id.ToString());
        cmd.Parameters.AddWithValue("$project", item.ProjectId.Value);
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$prompt", item.Prompt);
        cmd.Parameters.AddWithValue("$base", (object?)item.BaseBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$work", (object?)item.WorkBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", (object?)item.Agent?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$work_timeout", item.WorkTimeout.Ticks);
        cmd.Parameters.AddWithValue("$merge_timeout", item.MergeTimeout.Ticks);
        cmd.Parameters.AddWithValue("$push_upstream", item.PushUpstream ? 1 : 0);
        cmd.Parameters.AddWithValue("$state", (int)item.State);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_error", (object?)item.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$upstream_attempts", item.UpstreamPushAttempts);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlySet<string> GetSchemaObjectNames(string dbPath, string type)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        cmd.Parameters.AddWithValue("$type", type);
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }
}

internal sealed class RecordingPipelineRunner : IPipelineRunner
{
    public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        => Task.CompletedTask;
}
