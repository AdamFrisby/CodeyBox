using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed per-agent pause controller. The in-memory dictionary keeps the
/// router hot path cheap; every mutation is persisted so restart preserves the
/// operator's paused set.
/// </summary>
public sealed class SqliteAgentPauseController : IAgentPauseController, IAgentPauseSignal, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _lock;
    private readonly ILogger<SqliteAgentPauseController> _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, AgentPauseState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action? AgentPauseChanged;

    public SqliteAgentPauseController(
        string dbPath,
        ILogger<SqliteAgentPauseController> log,
        TimeProvider? timeProvider = null,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        _log = log;
        _time = timeProvider ?? TimeProvider.System;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _lock = (writeGateFactory ?? SqliteDatabaseWriteGateFactory.Default).ForPath(dbPath);
        _lock.Wait();
        try
        {
            _conn.Open();
            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                walCmd.ExecuteNonQuery();
            }

            EnsureSchema();

            LoadState();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentPauseState> PauseAsync(
        AgentKind agent,
        string reason,
        string pausedBy,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default,
        string? agentInstanceId = null)
    {
        var key = PauseKey(agent, agentInstanceId);
        await PruneIfExpiredAsync(key, ct).ConfigureAwait(false);

        AgentPauseState state;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var pausedAt = _states.TryGetValue(key, out var existing)
                ? existing.PausedAt ?? now
                : now;
            state = new AgentPauseState(
                agent,
                Paused: true,
                PausedAt: pausedAt,
                PausedReason: reason,
                PausedBy: pausedBy,
                ExpiresAt: expiresAt,
                UpdatedAt: now,
                AgentInstanceId: InstanceStateId(agent, agentInstanceId));

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_pause_state
                    (pause_key, agent_kind, agent_instance_id, paused, paused_at, paused_reason, paused_by, expires_at, updated_at)
                VALUES
                    ($pause_key, $agent, $instance, 1, $paused_at, $reason, $paused_by, $expires_at, $updated_at)
                ON CONFLICT(pause_key) DO UPDATE SET
                    agent_kind        = $agent,
                    agent_instance_id = $instance,
                    paused        = 1,
                    paused_at     = COALESCE(agent_pause_state.paused_at, $paused_at),
                    paused_reason = $reason,
                    paused_by     = $paused_by,
                    expires_at    = $expires_at,
                    updated_at    = $updated_at;
                """;
            BindState(cmd, state);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _states[key] = state;
        }
        finally
        {
            _lock.Release();
        }

        AuditLog.AgentPaused(agent, reason, pausedBy, expiresAt);
        NotifyPauseChanged();
        return state;
    }

    public async Task<bool> ResumeAsync(
        AgentKind agent,
        string resumedBy,
        string? reason = null,
        CancellationToken ct = default,
        string? agentInstanceId = null)
    {
        var key = PauseKey(agent, agentInstanceId);
        await PruneIfExpiredAsync(key, ct).ConfigureAwait(false);

        AgentPauseState? previous;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_states.TryRemove(key, out previous))
                return false;

            await PersistRunningAsync(key, agent, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        AuditLog.AgentResumed(agent, resumedBy, reason ?? previous.PausedReason);
        NotifyPauseChanged();
        return true;
    }

    public async Task<AgentPauseState?> GetAgentStateAsync(
        AgentKind agent,
        CancellationToken ct = default,
        string? agentInstanceId = null)
    {
        var key = PauseKey(agent, agentInstanceId);
        if (!_states.TryGetValue(key, out var state))
        {
            if (agentInstanceId is null)
                return null;

            key = PauseKey(agent, null);
            if (!_states.TryGetValue(key, out state))
                return null;
        }

        if (IsExpired(state, _time.GetUtcNow()))
        {
            await ResumeExpiredAsync(key, state, ct).ConfigureAwait(false);
            if (agentInstanceId is not null)
                return await GetAgentStateAsync(agent, ct).ConfigureAwait(false);
            return null;
        }

        return state;
    }

    public async Task<IReadOnlyList<AgentPauseState>> ListPausedAsync(CancellationToken ct = default)
    {
        foreach (var state in _states.Values)
        {
            if (IsExpired(state, _time.GetUtcNow()))
                await ResumeExpiredAsync(PauseKey(state.Agent, state.AgentInstanceId), state, ct).ConfigureAwait(false);
        }

        return _states.Values
            .OrderBy(s => s.AgentInstanceId ?? s.Agent.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        _conn.Dispose();
        _lock.Dispose();
    }

    private void LoadState()
    {
        var now = _time.GetUtcNow();
        var expired = new List<AgentPauseState>();
        var startedPaused = new List<AgentPauseState>();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pause_key, agent_kind, agent_instance_id, paused, paused_at, paused_reason, paused_by, expires_at, updated_at
                FROM agent_pause_state
                WHERE paused = 1;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var state = ReadState(reader);
                if (IsExpired(state, now))
                {
                    expired.Add(state);
                    continue;
                }

                _states[PauseKey(state.Agent, state.AgentInstanceId)] = state;
                startedPaused.Add(state);
            }
        }

        foreach (var state in expired)
        {
            PersistRunningAsync(PauseKey(state.Agent, state.AgentInstanceId), state.Agent, now, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        foreach (var state in startedPaused)
            AuditLog.AgentStartedWhilePaused(state.Agent, state.PausedReason);
        foreach (var state in expired)
            AuditLog.AgentPauseExpired(state.Agent, state.PausedReason);
    }

    private async Task PruneIfExpiredAsync(string key, CancellationToken ct)
    {
        if (_states.TryGetValue(key, out var state) && IsExpired(state, _time.GetUtcNow()))
            await ResumeExpiredAsync(key, state, ct).ConfigureAwait(false);
    }

    private async Task ResumeExpiredAsync(string key, AgentPauseState observed, CancellationToken ct)
    {
        AgentPauseState? expired = null;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(key, out var current))
                return;
            if (!Equals(current, observed) || !IsExpired(current, _time.GetUtcNow()))
                return;

            _states.TryRemove(key, out _);
            await PersistRunningAsync(key, current.Agent, _time.GetUtcNow(), ct).ConfigureAwait(false);
            expired = current;
        }
        finally
        {
            _lock.Release();
        }

        if (expired is not null)
            AuditLog.AgentPauseExpired(expired.Agent, expired.PausedReason);
        NotifyPauseChanged();
    }

    private async Task PersistRunningAsync(string key, AgentKind agent, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_pause_state
                (pause_key, agent_kind, agent_instance_id, paused, paused_at, paused_reason, paused_by, expires_at, updated_at)
            VALUES
                ($pause_key, $agent, NULL, 0, NULL, NULL, NULL, NULL, $updated_at)
            ON CONFLICT(pause_key) DO UPDATE SET
                paused        = 0,
                paused_at     = NULL,
                paused_reason = NULL,
                paused_by     = NULL,
                expires_at    = NULL,
                updated_at    = $updated_at;
            """;
        cmd.Parameters.AddWithValue("$pause_key", key);
        cmd.Parameters.AddWithValue("$agent", agent.Value);
        cmd.Parameters.AddWithValue("$updated_at", now.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static bool IsExpired(AgentPauseState state, DateTimeOffset now) =>
        state.ExpiresAt is { } expiresAt && expiresAt <= now;

    private static AgentPauseState ReadState(SqliteDataReader reader)
    {
        var agent = new AgentKind(reader.GetString(1));
        var instance = reader.IsDBNull(2) ? null : reader.GetString(2);
        var paused = reader.GetInt32(3) == 1;
        var pausedAt = ReadDateTimeOffset(reader, 4);
        var reason = reader.IsDBNull(5) ? null : reader.GetString(5);
        var pausedBy = reader.IsDBNull(6) ? null : reader.GetString(6);
        var expiresAt = ReadDateTimeOffset(reader, 7);
        var updatedAt = ReadDateTimeOffset(reader, 8) ?? DateTimeOffset.MinValue;
        return new AgentPauseState(agent, paused, pausedAt, reason, pausedBy, expiresAt, updatedAt, instance);
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static void BindState(SqliteCommand cmd, AgentPauseState state)
    {
        cmd.Parameters.AddWithValue("$pause_key", PauseKey(state.Agent, state.AgentInstanceId));
        cmd.Parameters.AddWithValue("$agent", state.Agent.Value);
        cmd.Parameters.AddWithValue("$instance", state.AgentInstanceId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$paused_at",
            state.PausedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", state.PausedReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$paused_by", state.PausedBy ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$expires_at",
            state.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", state.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private void EnsureSchema()
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_pause_state (
                    pause_key         TEXT PRIMARY KEY,
                    agent_kind        TEXT NOT NULL,
                    agent_instance_id TEXT,
                    paused            INTEGER NOT NULL DEFAULT 0,
                    paused_at         TEXT,
                    paused_reason     TEXT,
                    paused_by         TEXT,
                    expires_at        TEXT,
                    updated_at        TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        if (!HasColumn("agent_pause_state", "pause_key"))
            MigrateLegacySchema();

        using var index = _conn.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_agent_pause_kind
                ON agent_pause_state(agent_kind);
            """;
        index.ExecuteNonQuery();
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table name is a hardcoded migration helper input
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void MigrateLegacySchema()
    {
        using var migrate = _conn.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE agent_pause_state RENAME TO agent_pause_state_legacy;
            CREATE TABLE agent_pause_state (
                pause_key         TEXT PRIMARY KEY,
                agent_kind        TEXT NOT NULL,
                agent_instance_id TEXT,
                paused            INTEGER NOT NULL DEFAULT 0,
                paused_at         TEXT,
                paused_reason     TEXT,
                paused_by         TEXT,
                expires_at        TEXT,
                updated_at        TEXT NOT NULL
            );
            INSERT OR IGNORE INTO agent_pause_state
                (pause_key, agent_kind, agent_instance_id, paused, paused_at, paused_reason, paused_by, expires_at, updated_at)
            SELECT agent_kind, agent_kind, NULL, paused, paused_at, paused_reason, paused_by, expires_at, updated_at
            FROM agent_pause_state_legacy;
            DROP TABLE agent_pause_state_legacy;
            """;
        migrate.ExecuteNonQuery();
    }

    private static string PauseKey(AgentKind agent, string? agentInstanceId)
        => AgentInstanceIds.RouteKey(agent, agentInstanceId);

    private static string? InstanceStateId(AgentKind agent, string? agentInstanceId)
    {
        var key = PauseKey(agent, agentInstanceId);
        return string.Equals(key, agent.Value, StringComparison.OrdinalIgnoreCase) ? null : key;
    }

    private void NotifyPauseChanged()
    {
        var handlers = AgentPauseChanged;
        if (handlers is null) return;

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agent pause subscriber threw; pause state is still committed");
            }
        }
    }
}
