using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IMajordomoProposalStore"/>. Shares the state
/// database file with the work-item and suggestion stores via an additive
/// table, so queued proposals survive orchestrator restarts. The tool call's
/// argument payload and the operator-reviewed change set are persisted as
/// JSON through <see cref="MajordomoJson"/> — the same serializer the MCP
/// surface binds — so approval replays exactly what the assistant sent.
/// </summary>
/// <remarks>
/// Writes join <see cref="SqliteDatabaseWriteGate"/>, the shared per-file
/// write gate every Sqlite* store on this database coordinates through;
/// <see cref="_gate"/> additionally serializes use of this store's single
/// connection. Lock order is always <see cref="_gate"/> then
/// <see cref="_writeLock"/> — reads take only <see cref="_gate"/>, so a
/// reader can never deadlock a writer.
/// </remarks>
public sealed class SqliteMajordomoProposalStore : IMajordomoProposalStore, IDisposable
{
    // SQLITE_CONSTRAINT_PRIMARYKEY: a duplicate Enqueue id.
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>
    /// The columns every read projects, in the ordinal order
    /// <see cref="Read"/> consumes. Listed explicitly rather than
    /// <c>SELECT *</c> because migrated tables can carry columns in a
    /// different physical order than a freshly created one.
    /// </summary>
    private const string ReadColumns =
        "id, tool, args_json, reasoning, plan_json, proposed_by, proposed_at, " +
        "state, decided_at, decided_by, decision_reason, result_ids_json";

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly ILogger<SqliteMajordomoProposalStore>? _log;
    private int _disposed;

    public SqliteMajordomoProposalStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null,
        ILogger<SqliteMajordomoProposalStore>? log = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _log = log;
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragmaCmd = _conn.CreateCommand())
            {
                // WAL for concurrent readers; busy_timeout absorbs the brief
                // write contention with the other stores sharing this file.
                // Foreign keys stay off: proposals reference work items that may
                // be gone by approval time, and that drift is revalidated at
                // apply rather than refused by the schema.
                pragmaCmd.CommandText =
                    $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={SqliteDefaults.BusyTimeoutMilliseconds}; PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS majordomo_proposals (
                    id                TEXT PRIMARY KEY,
                    tool              TEXT NOT NULL,
                    args_json         TEXT NOT NULL,
                    reasoning         TEXT,
                    plan_json         TEXT,
                    proposed_by       TEXT NOT NULL,
                    proposed_at       TEXT NOT NULL,
                    state             TEXT NOT NULL,
                    decided_at        TEXT,
                    decided_by        TEXT,
                    decision_reason   TEXT,
                    result_ids_json   TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_majordomo_proposals_state_time
                    ON majordomo_proposals(state, proposed_at);
                """;
            cmd.ExecuteNonQuery();

            // Additive migration for tables created before the reviewed plan
            // was persisted: ADD COLUMN appends, which is exactly why reads
            // project ReadColumns rather than relying on physical order.
            AddColumnIfMissing("plan_json", "ALTER TABLE majordomo_proposals ADD COLUMN plan_json TEXT;");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task EnqueueAsync(
        MajordomoProposalRecord proposal,
        MajordomoOptions policy,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // The sweep and the cap ride the same write critical section
                // as the insert. Expired pending rows are reaped BEFORE the
                // cap counts, so a stale backlog drains as new proposals
                // arrive instead of deadlocking the queue; applying rows are
                // never reaped — a claimed commit stays for inspection.
                using (var sweep = _conn.CreateCommand())
                {
                    sweep.CommandText = $"""
                        DELETE FROM majordomo_proposals
                        WHERE state IN ('{MajordomoProposalState.Approved}', '{MajordomoProposalState.Rejected}',
                                        '{MajordomoProposalState.Expired}', '{MajordomoProposalState.Superseded}')
                          AND julianday(COALESCE(decided_at, proposed_at)) < julianday($retentionCutoff);
                        DELETE FROM majordomo_proposals
                        WHERE state = '{MajordomoProposalState.Pending}'
                          AND julianday(proposed_at) <= julianday($expiryCutoff);
                        """;
                    sweep.Parameters.AddWithValue(
                        "$retentionCutoff", (now - policy.DecidedProposalRetention).ToString("O"));
                    sweep.Parameters.AddWithValue(
                        "$expiryCutoff", (now - policy.ProposalTimeToLive).ToString("O"));
                    await sweep.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                using var cmd = _conn.CreateCommand();
                // The cap is a guard INSIDE the insert statement: the count
                // is evaluated under the statement's own write lock, so even
                // a writer in another process cannot slip a row between the
                // count and the insert. Zero rows written means the queue
                // was already at the bound — a refusal, not a silent drop.
                cmd.CommandText = $"""
                    INSERT INTO majordomo_proposals (id, tool, args_json, reasoning, plan_json, proposed_by,
                        proposed_at, state, decided_at, decided_by, decision_reason, result_ids_json)
                    SELECT $id, $tool, $args, $reasoning, $plan, $by, $at, $state, $decidedAt, $decidedBy, $decisionReason, $resultIds
                    WHERE (SELECT COUNT(*) FROM majordomo_proposals
                           WHERE state IN ('{MajordomoProposalState.Pending}', '{MajordomoProposalState.Applying}'))
                          < $maxPending;
                    """;
                Bind(cmd, proposal);
                cmd.Parameters.AddWithValue("$maxPending", policy.MaxPendingProposals);
                try
                {
                    if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                        throw new MajordomoProposalQueueFullException(policy.MaxPendingProposals);
                }
                catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey)
                {
                    throw new InvalidOperationException($"proposal '{proposal.Id}' is already queued", ex);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MajordomoProposalRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {ReadColumns} FROM majordomo_proposals WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MajordomoProposalRecord>> ListAsync(
        MajordomoProposalState? state = null,
        int limit = IMajordomoProposalStore.MaxListLimit,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > IMajordomoProposalStore.MaxListLimit)
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit,
                $"limit must be within [1, {IMajordomoProposalStore.MaxListLimit}]");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            // Corrupt rows are skipped here so one unreadable proposal cannot
            // take the whole operator queue down; the row stays for
            // inspection via GetAsync, which reports the corruption, and
            // approval of it is refused with a reason.
            cmd.CommandText = state is null
                ? $"SELECT {ReadColumns} FROM majordomo_proposals ORDER BY proposed_at DESC, id LIMIT $limit;"
                : $"SELECT {ReadColumns} FROM majordomo_proposals WHERE state = $state ORDER BY proposed_at DESC, id LIMIT $limit;";
            if (state is not null)
                cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.Parameters.AddWithValue("$limit", limit);
            var rows = new List<MajordomoProposalRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    rows.Add(Read(reader));
                }
                catch (MajordomoProposalCorruptException ex)
                {
                    // Skipped per above; GetAsync still surfaces the detail.
                    // Logged so the unreadable row is visible somewhere — the
                    // operator list is the queue's only discovery surface.
                    _log?.LogWarning(
                        "Skipping corrupt majordomo proposal row {ProposalId}: {Detail}",
                        Validation.DescribeUntrustedValue(ex.ProposalId),
                        Validation.DescribeUntrustedValue(ex.Message));
                }
            }

            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryTransitionAsync(
        string id,
        MajordomoProposalState expectedCurrent,
        MajordomoProposalRecord decided,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decided);
        if (!string.Equals(id, decided.Id, StringComparison.Ordinal))
            throw new ArgumentException("decided record id must match the transition id", nameof(decided));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE majordomo_proposals SET
                        state = $state,
                        decided_at = $decidedAt,
                        decided_by = $decidedBy,
                        decision_reason = $decisionReason,
                        result_ids_json = $resultIds
                    WHERE id = $id AND state = $expected;
                    """;
                cmd.Parameters.AddWithValue("$state", decided.State.ToString());
                cmd.Parameters.AddWithValue("$decidedAt", decided.DecidedAt?.ToString("O") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$decidedBy", decided.DecidedBy ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$decisionReason", decided.DecisionReason ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$resultIds", ToResultIdsJson(decided.ResultAffectedItems) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$expected", expectedCurrent.ToString());
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Bind(SqliteCommand cmd, MajordomoProposalRecord proposal)
    {
        cmd.Parameters.AddWithValue("$id", proposal.Id);
        cmd.Parameters.AddWithValue("$tool", proposal.ToolName);
        cmd.Parameters.AddWithValue(
            "$args",
            JsonSerializer.Serialize(proposal.Arguments, proposal.Arguments.GetType(), MajordomoJson.Options));
        cmd.Parameters.AddWithValue("$reasoning", proposal.Reasoning ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$plan",
            proposal.ReviewedChangeSet is { } plan
                ? JsonSerializer.Serialize(plan, MajordomoJson.Options)
                : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$by", proposal.ProposedBy);
        cmd.Parameters.AddWithValue("$at", proposal.ProposedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$state", proposal.State.ToString());
        cmd.Parameters.AddWithValue("$decidedAt", proposal.DecidedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$decidedBy", proposal.DecidedBy ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$decisionReason", proposal.DecisionReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$resultIds", ToResultIdsJson(proposal.ResultAffectedItems) ?? (object)DBNull.Value);
    }

    private static MajordomoProposalRecord Read(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var toolName = reader.GetString(1);
        var argsJson = reader.GetString(2);
        try
        {
            if (!MajordomoTools.TryGetMutate(toolName, out var tool))
            {
                throw new MajordomoProposalCorruptException(
                    id, $"tool '{Validation.DescribeUntrustedValue(toolName)}' is not a known MUTATE tool");
            }

            MajordomoMutateArgs arguments;
            try
            {
                var bound = JsonSerializer.Deserialize(argsJson, tool.ArgumentsType, MajordomoJson.Options);
                if (bound is not MajordomoMutateArgs typed)
                    throw new MajordomoProposalCorruptException(id, $"arguments for '{Validation.DescribeUntrustedValue(toolName)}' did not bind to {tool.ArgumentsType.Name}");
                arguments = typed;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
            {
                throw new MajordomoProposalCorruptException(
                    id, $"arguments for '{Validation.DescribeUntrustedValue(toolName)}' are unreadable: {Validation.DescribeUntrustedValue(ex.Message)}");
            }

            return new MajordomoProposalRecord
            {
                Id = id,
                ToolName = tool.Name,
                Arguments = arguments,
                Reasoning = reader.IsDBNull(3) ? null : reader.GetString(3),
                ReviewedChangeSet = ReadPlan(id, reader, 4),
                ProposedBy = reader.GetString(5),
                ProposedAt = DateTimeOffset.Parse(reader.GetString(6), null),
                // TryParse alone would accept a stored numeric like '7' as
                // an undefined member — IsDefined keeps only named states.
                State = Enum.TryParse<MajordomoProposalState>(reader.GetString(7), out var state)
                        && Enum.IsDefined(state)
                    ? state
                    : throw new MajordomoProposalCorruptException(
                        id, $"unknown state '{Validation.DescribeUntrustedValue(reader.GetString(7))}'"),
                DecidedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), null),
                DecidedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
                DecisionReason = reader.IsDBNull(10) ? null : reader.GetString(10),
                ResultAffectedItems = reader.IsDBNull(11) ? null : FromResultIdsJson(id, reader.GetString(11)),
            };
        }
        catch (MajordomoProposalCorruptException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or IndexOutOfRangeException)
        {
            // ex.Message can echo the raw stored value; the detail surfaces
            // in HTTP responses and audit records, so it goes through the
            // echo guard like the column reads above.
            throw new MajordomoProposalCorruptException(
                id, $"row is unreadable: {Validation.DescribeUntrustedValue(ex.Message)}");
        }
    }

    private static MajordomoChangeSet? ReadPlan(string id, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MajordomoChangeSet>(reader.GetString(ordinal), MajordomoJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new MajordomoProposalCorruptException(
                id, $"reviewed plan is unreadable: {Validation.DescribeUntrustedValue(ex.Message)}");
        }
    }

    private void AddColumnIfMissing(string columnName, string sql)
    {
        using var check = _conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(majordomo_proposals);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = _conn.CreateCommand();
        alter.CommandText = sql;
        alter.ExecuteNonQuery();
    }

    private static string? ToResultIdsJson(IReadOnlyList<WorkItemId>? ids) =>
        ids is null ? null : JsonSerializer.Serialize(ids.Select(i => i.ToString()).ToList(), MajordomoJson.Options);

    private static IReadOnlyList<WorkItemId> FromResultIdsJson(string id, string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<List<string>>(json, MajordomoJson.Options) ?? [];
            return raw.Select(s => new WorkItemId(Guid.Parse(s))).ToList();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new MajordomoProposalCorruptException(
                id, $"result ids are unreadable: {Validation.DescribeUntrustedValue(ex.Message)}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _conn.Dispose();
            _gate.Dispose();
            _writeLock.Dispose();
        }
    }
}
