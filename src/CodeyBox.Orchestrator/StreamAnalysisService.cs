using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class StreamAnalysisService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly IWorkItemStore _workItems;
    private readonly IAgentStreamStore _streams;
    private readonly IAgentStreamSummaryStore _summaries;
    private readonly IReadOnlyDictionary<AgentKind, IAgentStreamParser> _parsers;
    private readonly ILogger<StreamAnalysisService> _log;
    private readonly IWorkItemCostStore? _costs;

    public StreamAnalysisService(
        IWorkItemStore workItems,
        IAgentStreamStore streams,
        IAgentStreamSummaryStore summaries,
        IEnumerable<IAgentStreamParser> parsers,
        ILogger<StreamAnalysisService> log,
        IWorkItemCostStore? costs = null)
    {
        _workItems = workItems;
        _streams = streams;
        _summaries = summaries;
        _parsers = parsers.ToDictionary(p => p.Kind, p => p);
        _log = log;
        _costs = costs;
    }

    public async Task<int> AnalyzeRecentTerminalWorkItemsAsync(
        DateTimeOffset now,
        TimeSpan lookback,
        CancellationToken ct = default)
    {
        var count = 0;
        await foreach (var item in _workItems.ListAsync(ct))
        {
            if (!IsTerminal(item.State) || item.UpdatedAt < now - lookback)
                continue;

            count += await AnalyzeWorkItemAsync(item, ct).ConfigureAwait(false);
        }

        return count;
    }

    public async Task<int> AnalyzeWorkItemAsync(WorkItem item, CancellationToken ct = default)
    {
        var files = await _streams.ListAsync(item.Id, AgentStreamStore.MaxListLimit, includeLineCount: false, ct).ConfigureAwait(false);
        var count = 0;
        var kind = item.Agent ?? new AgentKind("unknown");
        if (!_parsers.TryGetValue(kind, out var parser))
            parser = _parsers.Values.FirstOrDefault(p => p.Kind.Value == "unknown") ?? new UnknownAgentStreamParser();

        foreach (var file in files)
        {
            await using var stream = await _streams.OpenReadAsync(item.Id, file.FileName, ct).ConfigureAwait(false);
            if (stream is null)
                continue;

            try
            {
                var summary = await parser.ParseAsync(stream, ct).ConfigureAwait(false);
                var row = new AgentStreamSummaryRow(
                    item.Id,
                    file.FileName,
                    file.Phase,
                    file.Iteration,
                    parser.Kind,
                    summary,
                    DateTimeOffset.UtcNow);
                await _summaries.UpsertAsync(row, ct).ConfigureAwait(false);
                if (_costs is SqliteWorkItemCostStore sqliteCosts)
                    await sqliteCosts.ReconcileFromAgentStreamSummaryAsync(row, ct).ConfigureAwait(false);
                count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex,
                    "Failed to analyse agent stream {FileName} for work item {WorkItemId}",
                    file.FileName, item.Id);
            }
        }

        return count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agent stream analysis sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool IsTerminal(WorkItemState state) =>
        state is WorkItemState.Done
            or WorkItemState.Failed
            or WorkItemState.AuditFailed
            or WorkItemState.Cancelled
            or WorkItemState.AbandonedAfterRecoveryAttempts;
}
