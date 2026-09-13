using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed append-only delegation event log. Writes to the same
/// database file as <see cref="SqliteWorkItemStore"/>; WAL mode allows
/// concurrent readers, and the shared <see cref="SqliteDatabaseWriteGate"/>
/// serialises writers across the stores that point at this file. Mirrors the
/// shape of <see cref="SqliteFailureEventStore"/>.
/// </summary>
public sealed class SqliteDelegationEventStore : IDelegationEventStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly SqliteCommand _insertCmd;
    private readonly Func<DelegationOptions> _optionsAccessor;
    private int _disposed;

    public SqliteDelegationEventStore(
        string path,
        Func<DelegationOptions>? optionsAccessor = null,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null)
    {
        _optionsAccessor = optionsAccessor ?? (() => new DelegationOptions());
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var walCmd = _conn.CreateCommand())
            {
                // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- PRAGMA takes no parameters; hardcoded DDL only
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                walCmd.ExecuteNonQuery();
            }

            using var createCmd = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- hardcoded DDL only
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS delegation_events (
                    id              TEXT PRIMARY KEY,
                    work_item_id    TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    attempt         INTEGER NOT NULL,
                    brief           TEXT NOT NULL,
                    agent           TEXT NOT NULL,
                    model           TEXT,
                    outcome         TEXT NOT NULL,
                    reason          TEXT,
                    diff_stat       TEXT NOT NULL,
                    result_diff     TEXT NOT NULL,
                    occurred_at     TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_delegation_events_work_item
                    ON delegation_events(work_item_id, attempt);
                """;
            createCmd.ExecuteNonQuery();

            _insertCmd = _conn.CreateCommand();
            _insertCmd.CommandText = """
                INSERT INTO delegation_events
                    (id, work_item_id, attempt, brief, agent, model, outcome, reason, diff_stat, result_diff, occurred_at)
                VALUES ($id, $wid, $attempt, $brief, $agent, $model, $outcome, $reason, $stat, $diff, $occurred)
                """;
            _insertCmd.Parameters.Add("$id", SqliteType.Text);
            _insertCmd.Parameters.Add("$wid", SqliteType.Text);
            _insertCmd.Parameters.Add("$attempt", SqliteType.Integer);
            _insertCmd.Parameters.Add("$brief", SqliteType.Text);
            _insertCmd.Parameters.Add("$agent", SqliteType.Text);
            _insertCmd.Parameters.Add("$model", SqliteType.Text);
            _insertCmd.Parameters.Add("$outcome", SqliteType.Text);
            _insertCmd.Parameters.Add("$reason", SqliteType.Text);
            _insertCmd.Parameters.Add("$stat", SqliteType.Text);
            _insertCmd.Parameters.Add("$diff", SqliteType.Text);
            _insertCmd.Parameters.Add("$occurred", SqliteType.Text);
            _insertCmd.Prepare();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordAsync(DelegationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var options = _optionsAccessor();
        var brief = Truncate(@event.Brief, options.MaxBriefChars);
        var reason = Truncate(@event.Reason, options.MaxReasonChars);
        var stat = Truncate(@event.DiffStat, options.MaxDiffStatChars);
        var diff = Truncate(@event.ResultDiff, options.MaxResultDiffChars);

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _insertCmd.Parameters["$id"].Value = @event.Id;
                _insertCmd.Parameters["$wid"].Value = @event.WorkItemId.ToString();
                _insertCmd.Parameters["$attempt"].Value = @event.Attempt;
                _insertCmd.Parameters["$brief"].Value = brief;
                _insertCmd.Parameters["$agent"].Value = @event.Agent.Value;
                _insertCmd.Parameters["$model"].Value = (object?)@event.Model ?? DBNull.Value;
                _insertCmd.Parameters["$outcome"].Value = @event.Outcome;
                _insertCmd.Parameters["$reason"].Value = (object?)reason ?? DBNull.Value;
                _insertCmd.Parameters["$stat"].Value = stat;
                _insertCmd.Parameters["$diff"].Value = diff;
                // Normalise to UTC so the stored ISO-8601 text sorts and
                // range-filters lexicographically regardless of offset.
                _insertCmd.Parameters["$occurred"].Value = @event.OccurredAt.ToUniversalTime().ToString("O");
                await _insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<DelegationEvent>> ListByWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, work_item_id, attempt, brief, agent, model, outcome, reason, diff_stat, result_diff, occurred_at
                FROM delegation_events
                WHERE work_item_id = $wid
                ORDER BY attempt ASC, rowid ASC;
                """;
            cmd.Parameters.AddWithValue("$wid", workItemId.ToString());

            var results = new List<DelegationEvent>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(ReadRecord(reader));
            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength
            ? value[..maxLength]
            : value;

    private static DelegationEvent ReadRecord(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkItemId = WorkItemId.Parse(r.GetString(1)),
        Attempt = r.GetInt32(2),
        Brief = r.GetString(3),
        Agent = new AgentKind(r.GetString(4)),
        Model = r.IsDBNull(5) ? null : r.GetString(5),
        Outcome = r.GetString(6),
        Reason = r.IsDBNull(7) ? null : r.GetString(7),
        DiffStat = r.GetString(8),
        ResultDiff = r.GetString(9),
        OccurredAt = DateTimeOffset.Parse(r.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _insertCmd.Dispose();
        _conn.Dispose();
        _connectionLock.Dispose();
    }
}
