using System.Text.Json;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Thrown when a persisted proposal row cannot be rebound to its typed
/// argument contract — the vocabulary changed under a queued proposal, or
/// the row was tampered with. Approval of such a proposal is refused with a
/// reason rather than applied blindly; the row itself is left for inspection.
/// </summary>
internal sealed class MajordomoProposalCorruptException(string proposalId, string detail)
    : InvalidOperationException($"proposal '{proposalId}' cannot be read back: {detail}")
{
    public string ProposalId { get; } = proposalId;
}

/// <summary>
/// SQLite-backed <see cref="IMajordomoProposalStore"/>. Shares the state
/// database file with the work-item and suggestion stores via an additive
/// table, so queued proposals survive orchestrator restarts. The tool call's
/// argument payload is persisted as JSON through <see cref="MajordomoJson"/>
/// — the same serializer the MCP surface binds — so approval replays exactly
/// what the assistant sent.
/// </summary>
internal sealed class SqliteMajordomoProposalStore : IMajordomoProposalStore, IDisposable
{
    // SQLITE_CONSTRAINT_PRIMARYKEY: a duplicate Enqueue id.
    private const int SqliteConstraintPrimaryKey = 1555;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public SqliteMajordomoProposalStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        using (var pragmaCmd = _conn.CreateCommand())
        {
            // WAL for concurrent readers; busy_timeout absorbs the brief
            // write contention with the work-item store sharing this file.
            // Foreign keys stay off: proposals reference work items that may
            // be gone by approval time, and that drift is revalidated at
            // apply rather than refused by the schema.
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys = OFF;";
            pragmaCmd.ExecuteNonQuery();
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS majordomo_proposals (
                id                TEXT PRIMARY KEY,
                tool              TEXT NOT NULL,
                args_json         TEXT NOT NULL,
                reasoning         TEXT,
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
    }

    public async Task EnqueueAsync(MajordomoProposalRecord proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO majordomo_proposals (id, tool, args_json, reasoning, proposed_by, proposed_at,
                    state, decided_at, decided_by, decision_reason, result_ids_json)
                VALUES ($id, $tool, $args, $reasoning, $by, $at, $state, $decidedAt, $decidedBy, $decisionReason, $resultIds);
                """;
            Bind(cmd, proposal);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey)
            {
                throw new InvalidOperationException($"proposal '{proposal.Id}' is already queued", ex);
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
            cmd.CommandText = "SELECT * FROM majordomo_proposals WHERE id = $id;";
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
                ? "SELECT * FROM majordomo_proposals ORDER BY proposed_at DESC, id LIMIT $limit;"
                : "SELECT * FROM majordomo_proposals WHERE state = $state ORDER BY proposed_at DESC, id LIMIT $limit;";
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
                catch (MajordomoProposalCorruptException)
                {
                    // Skipped per above; GetAsync still surfaces the detail.
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
            if (!MajordomoTools.TryGet(toolName, out var tool) || tool.Class != MajordomoToolClass.Mutate)
                throw new MajordomoProposalCorruptException(id, $"tool '{toolName}' is not a known MUTATE tool");

            MajordomoMutateArgs arguments;
            try
            {
                var bound = JsonSerializer.Deserialize(argsJson, tool.ArgumentsType, MajordomoJson.Options);
                if (bound is not MajordomoMutateArgs typed)
                    throw new MajordomoProposalCorruptException(id, $"arguments for '{toolName}' did not bind to {tool.ArgumentsType.Name}");
                arguments = typed;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
            {
                throw new MajordomoProposalCorruptException(id, $"arguments for '{toolName}' are unreadable: {ex.Message}");
            }

            return new MajordomoProposalRecord
            {
                Id = id,
                ToolName = tool.Name,
                Arguments = arguments,
                Reasoning = reader.IsDBNull(3) ? null : reader.GetString(3),
                ProposedBy = reader.GetString(4),
                ProposedAt = DateTimeOffset.Parse(reader.GetString(5), null),
                State = Enum.TryParse<MajordomoProposalState>(reader.GetString(6), out var state)
                    ? state
                    : throw new MajordomoProposalCorruptException(id, $"unknown state '{reader.GetString(6)}'"),
                DecidedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), null),
                DecidedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                DecisionReason = reader.IsDBNull(9) ? null : reader.GetString(9),
                ResultAffectedItems = reader.IsDBNull(10) ? null : FromResultIdsJson(id, reader.GetString(10)),
            };
        }
        catch (MajordomoProposalCorruptException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or IndexOutOfRangeException)
        {
            throw new MajordomoProposalCorruptException(id, $"row is unreadable: {ex.Message}");
        }
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
            throw new MajordomoProposalCorruptException(id, $"result ids are unreadable: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _conn.Dispose();
            _gate.Dispose();
        }
    }
}
