using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests.Uat.AuditingAndReports;

internal static class AuditingAndReportsHelpers
{
    public static AuditContext Context(int iteration = 1) =>
        new(WorkItemId.New(), "feature/audit-uat", "main", iteration, "make the requested change");

    public static WorkItem NewItem(string workBranch = "feature/audit-uat") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Auditing and reports UAT",
        Prompt = "make the requested change",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    public static AuditReport Report(
        string workItemId,
        int iteration,
        string auditorName,
        string auditorKind = "tool",
        IReadOnlyList<AuditReportFinding>? findings = null,
        string? rawOutput = null,
        DateTimeOffset? startedAt = null)
    {
        var start = startedAt ?? new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero);
        return new AuditReport
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = iteration,
            AuditorName = auditorName,
            AuditorKind = auditorKind,
            WorstSeverity = WorstSeverity(findings),
            StartedAt = start,
            EndedAt = start.AddMilliseconds(25),
            DurationMs = 25,
            Findings = findings ?? [],
            RawOutput = rawOutput,
        };
    }

    private static string WorstSeverity(IReadOnlyList<AuditReportFinding>? findings)
    {
        if (findings is null || findings.Count == 0)
            return "none";
        if (findings.Any(f => string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
            return "Error";
        if (findings.Any(f => string.Equals(f.Severity, "Warning", StringComparison.OrdinalIgnoreCase)))
            return "Warning";
        return "Info";
    }

    public static async Task SeedWorkItemAsync(string dbPath, WorkItemId workItemId)
    {
        using var workItems = new SqliteWorkItemStore(dbPath);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO work_items
                (id, project_id, title, prompt, work_timeout_ticks, merge_timeout_ticks,
                 push_upstream, state, created_at, updated_at)
            VALUES
                ($id, 'test-project', 'test', 'test', $workTimeout, $mergeTimeout,
                 0, 0, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", workItemId.ToString());
        cmd.Parameters.AddWithValue("$workTimeout", TimeSpan.FromMinutes(30).Ticks);
        cmd.Parameters.AddWithValue("$mergeTimeout", TimeSpan.FromMinutes(15).Ticks);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }
}

internal sealed class RecordingSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
{
    public List<SandboxExec> Executions { get; } = [];
    public string Id => "recording-audit-sandbox";

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        Executions.Add(exec);
        return Task.FromResult(onExec(exec));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CapturingAuditReportStore : IAuditReportStore
{
    public List<AuditReport> Reports { get; } = [];

    public Task CreateAsync(AuditReport report, CancellationToken ct = default)
    {
        Reports.Add(report);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditReport>>(
            Reports.Where(r => r.WorkItemId == workItemId)
                .OrderBy(r => r.Iteration)
                .ThenBy(r => r.AuditorName, StringComparer.Ordinal)
                .ToList());

    public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult(Reports.FirstOrDefault(r =>
            r.WorkItemId == workItemId &&
            r.Iteration == iteration &&
            r.AuditorName == auditorName)?.RawOutput);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => Task.FromResult(0);
}

internal sealed class BlockingAuditor(
    string name,
    AuditSeverity severity,
    string title,
    string rawOutput) : IAuditor
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name { get; } = name;
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        Started.TrySetResult();
        await Release.Task.WaitAsync(ct);
        return new AuditResult(
            Passed: severity != AuditSeverity.Error,
            Findings: [new AuditFinding(Name, severity, title, "reported by UAT auditor", "src/A.cs:42")],
            RawOutput: rawOutput);
    }
}

internal sealed class StartupSweepStore : IAuditReportStore
{
    private readonly int _deleteResult;
    private readonly Exception? _exception;

    public StartupSweepStore(int deleteResult, Exception? exception = null)
    {
        _deleteResult = deleteResult;
        _exception = exception;
    }

    public TaskCompletionSource<DateTimeOffset> CutoffObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task CreateAsync(AuditReport report, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditReport>>([]);
    public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        CutoffObserved.TrySetResult(cutoff);
        if (_exception is not null)
            throw _exception;
        return Task.FromResult(_deleteResult);
    }
}
