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
    private readonly ConcurrentDictionary<AgentKind, AgentPauseState> _states = new();

    public event Action? AgentPauseChanged;

    public SqliteAgentPauseController(
        string dbPath,
        ILogger<SqliteAgentPauseController> log,
        TimeProvider? timeProvider = null)
    {
        _log = log;
        _time = timeProvider ?? TimeProvider.System;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _lock = SqliteDatabaseWriteGate.ForPath(dbPath);
        _lock.Wait();
        try
        {
            _conn.Open();
            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                walCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_pause_state (
                    agent_kind    TEXT PRIMARY KEY,
                    paused        INTEGER NOT NULL DEFAULT 0,
                    paused_at     TEXT,
                    paused_reason TEXT,
                    paused_by     TEXT,
                    expires_at    TEXT,
                    updated_at    TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

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
        CancellationToken ct = default)
    {
        await PruneIfExpiredAsync(agent, ct).ConfigureAwait(false);

        AgentPauseState state;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var pausedAt = _states.TryGetValue(agent, out var existing)
                ? existing.PausedAt ?? now
                : now;
            state = new AgentPauseState(
                agent,
                Paused: true,
                PausedAt: pausedAt,
                PausedReason: reason,
                PausedBy: pausedBy,
                ExpiresAt: expiresAt,
                UpdatedAt: now);

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_pause_state
                    (agent_kind, paused, paused_at, paused_reason, paused_by, expires_at, updated_at)
                VALUES
                    ($agent, 1, $paused_at, $reason, $paused_by, $expires_at, $updated_at)
                ON CONFLICT(agent_kind) DO UPDATE SET
                    paused        = 1,
                    paused_at     = COALESCE(agent_pause_state.paused_at, $paused_at),
                    paused_reason = $reason,
                    paused_by     = $paused_by,
                    expires_at    = $expires_at,
                    updated_at    = $updated_at;
                """;
            BindState(cmd, state);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _states[agent] = state;
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
        CancellationToken ct = default)
    {
        await PruneIfExpiredAsync(agent, ct).ConfigureAwait(false);

        AgentPauseState? previous;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_states.TryRemove(agent, out previous))
                return false;

            await PersistRunningAsync(agent, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        AuditLog.AgentResumed(agent, resumedBy, reason ?? previous.PausedReason);
        NotifyPauseChanged();
        return true;
    }

    public async Task<AgentPauseState?> GetAgentStateAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (!_states.TryGetValue(agent, out var state))
            return null;

        if (IsExpired(state, _time.GetUtcNow()))
        {
            await ResumeExpiredAsync(agent, state, ct).ConfigureAwait(false);
            return null;
        }

        return state;
    }

    public async Task<IReadOnlyList<AgentPauseState>> ListPausedAsync(CancellationToken ct = default)
    {
        foreach (var state in _states.Values)
        {
            if (IsExpired(state, _time.GetUtcNow()))
                await ResumeExpiredAsync(state.Agent, state, ct).ConfigureAwait(false);
        }

        return _states.Values
            .OrderBy(s => s.Agent.Value, StringComparer.OrdinalIgnoreCase)
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

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT agent_kind, paused, paused_at, paused_reason, paused_by, expires_at, updated_at
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

                _states[state.Agent] = state;
                AuditLog.AgentStartedWhilePaused(state.Agent, state.PausedReason);
            }
        }

        foreach (var state in expired)
        {
            PersistRunningAsync(state.Agent, now, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            AuditLog.AgentPauseExpired(state.Agent, state.PausedReason);
        }
    }

    private async Task PruneIfExpiredAsync(AgentKind agent, CancellationToken ct)
    {
        if (_states.TryGetValue(agent, out var state) && IsExpired(state, _time.GetUtcNow()))
            await ResumeExpiredAsync(agent, state, ct).ConfigureAwait(false);
    }

    private async Task ResumeExpiredAsync(AgentKind agent, AgentPauseState observed, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_states.TryGetValue(agent, out var current))
                return;
            if (!Equals(current, observed) || !IsExpired(current, _time.GetUtcNow()))
                return;

            _states.TryRemove(agent, out _);
            await PersistRunningAsync(agent, _time.GetUtcNow(), ct).ConfigureAwait(false);
            AuditLog.AgentPauseExpired(agent, current.PausedReason);
        }
        finally
        {
            _lock.Release();
        }

        NotifyPauseChanged();
    }

    private async Task PersistRunningAsync(AgentKind agent, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_pause_state
                (agent_kind, paused, paused_at, paused_reason, paused_by, expires_at, updated_at)
            VALUES
                ($agent, 0, NULL, NULL, NULL, NULL, $updated_at)
            ON CONFLICT(agent_kind) DO UPDATE SET
                paused        = 0,
                paused_at     = NULL,
                paused_reason = NULL,
                paused_by     = NULL,
                expires_at    = NULL,
                updated_at    = $updated_at;
            """;
        cmd.Parameters.AddWithValue("$agent", agent.Value);
        cmd.Parameters.AddWithValue("$updated_at", now.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static bool IsExpired(AgentPauseState state, DateTimeOffset now) =>
        state.ExpiresAt is { } expiresAt && expiresAt <= now;

    private static AgentPauseState ReadState(SqliteDataReader reader)
    {
        var agent = new AgentKind(reader.GetString(0));
        var paused = reader.GetInt32(1) == 1;
        var pausedAt = ReadDateTimeOffset(reader, 2);
        var reason = reader.IsDBNull(3) ? null : reader.GetString(3);
        var pausedBy = reader.IsDBNull(4) ? null : reader.GetString(4);
        var expiresAt = ReadDateTimeOffset(reader, 5);
        var updatedAt = ReadDateTimeOffset(reader, 6) ?? DateTimeOffset.MinValue;
        return new AgentPauseState(agent, paused, pausedAt, reason, pausedBy, expiresAt, updatedAt);
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static void BindState(SqliteCommand cmd, AgentPauseState state)
    {
        cmd.Parameters.AddWithValue("$agent", state.Agent.Value);
        cmd.Parameters.AddWithValue("$paused_at",
            state.PausedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", state.PausedReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$paused_by", state.PausedBy ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$expires_at",
            state.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", state.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
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
