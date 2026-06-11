using CodeyBox.Agents;
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
        var existingSummaries = (await _summaries.GetByWorkItemAsync(item.Id, ct).ConfigureAwait(false))
            .ToDictionary(r => r.FileName, StringComparer.Ordinal);
        IReadOnlyList<WorkItemCost> costs = _costs is null
            ? Array.Empty<WorkItemCost>()
            : await _costs.GetByWorkItemAsync(item.Id.ToString(), ct).ConfigureAwait(false);

        foreach (var file in files)
        {
            if (existingSummaries.TryGetValue(file.FileName, out var existing))
            {
                if (_costs is SqliteWorkItemCostStore cachedSummaryCosts)
                    await cachedSummaryCosts.ReconcileFromAgentStreamSummaryAsync(existing, ct).ConfigureAwait(false);
                continue;
            }

            var sniffedKind = await SniffKindAsync(item.Id, file.FileName, ct).ConfigureAwait(false);
            var kind = AgentStreamParserSelection.ResolveKind(item, file, sniffedKind, costs, _parsers.Keys);
            if (!_parsers.TryGetValue(kind, out var parser))
                parser = _parsers.Values.FirstOrDefault(p => p.Kind.Value == "unknown") ?? new UnknownAgentStreamParser();

            await using var stream = await _streams.OpenReadAsync(item.Id, file.FileName, ct).ConfigureAwait(false);
            if (stream is null)
                continue;

            var context = AgentStreamParserSelection.ResolveTimingContext(item, file, kind, costs);
            var summary = parser is IAgentStreamParserWithContext contextParser
                ? await contextParser.ParseAsync(stream, context, ct).ConfigureAwait(false)
                : await parser.ParseAsync(stream, ct).ConfigureAwait(false);
            var rowKind = parser.Kind;
            if (AgentStreamParserSelection.ShouldTreatAsUnsupported(rowKind, summary))
            {
                // The kind-specific parser did not recognise any events
                // (plaintext output from an agent whose CLI does not emit
                // structured stream-json, or partial / truncated structured
                // output). Re-open the capture file and run it through the
                // plaintext-fallback parser so the row carries a non-empty
                // tail summary instead of falling all the way back to
                // Unsupported. The rowKind keeps the originally-resolved
                // kind so agent_stream_summaries still attributes the run
                // to the right agent.
                var fallback = await RunPlaintextFallbackAsync(item.Id, file.FileName, context, ct).ConfigureAwait(false);
                summary = fallback ?? AgentStreamParserSelection.UnsupportedSummary();
                if (fallback is null)
                    rowKind = new AgentKind("unknown");
            }
            var row = new AgentStreamSummaryRow(
                item.Id,
                file.FileName,
                file.Phase,
                file.Iteration,
                rowKind,
                summary,
                DateTimeOffset.UtcNow);
            await _summaries.UpsertAsync(row, ct).ConfigureAwait(false);
            if (_costs is SqliteWorkItemCostStore sqliteCosts)
                await sqliteCosts.ReconcileFromAgentStreamSummaryAsync(row, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private async Task<AgentKind?> SniffKindAsync(WorkItemId id, string fileName, CancellationToken ct)
    {
        await using var stream = await _streams.OpenReadAsync(id, fileName, ct).ConfigureAwait(false);
        return stream is null
            ? null
            : await AgentStreamParserSelection.SniffKindAsync(stream, _parsers.Values, ct).ConfigureAwait(false);
    }

    private async Task<AgentStreamSummary?> RunPlaintextFallbackAsync(
        WorkItemId id,
        string fileName,
        AgentStreamParserContext? context,
        CancellationToken ct)
    {
        await using var stream = await _streams.OpenReadAsync(id, fileName, ct).ConfigureAwait(false);
        if (stream is null)
            return null;

        // Reuse the registered unknown parser so the fallback path stays
        // replaceable via DI (an operator can swap in a richer plaintext
        // summariser without touching this service). Fall back to the
        // in-process implementation only when the unknown parser was not
        // registered — keeps the test wiring (which constructs a minimal
        // parser list) working without forcing every caller to add the
        // unknown parser.
        var fallback = _parsers.Values.OfType<IAgentStreamParserWithContext>()
            .FirstOrDefault(p => p.Kind.Value == "unknown")
            ?? (IAgentStreamParserWithContext)new UnknownAgentStreamParser();
        return await fallback.ParseAsync(stream, context, ct).ConfigureAwait(false);
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
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.Cancelled
            or WorkItemState.AbandonedAfterRecoveryAttempts;
}
