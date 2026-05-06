using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentStreamRetentionService : BackgroundService
{
    private readonly IAgentStreamStore _streams;
    private readonly ILogger<AgentStreamRetentionService> _log;
    private readonly TimeSpan _period;

    public AgentStreamRetentionService(
        IAgentStreamStore streams,
        ILogger<AgentStreamRetentionService> log,
        TimeSpan? period = null)
    {
        _streams = streams;
        _log = log;
        _period = period ?? TimeSpan.FromDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(_period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepOnceAsync(stoppingToken);
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            var deleted = await _streams.SweepAsync(DateTimeOffset.UtcNow, ct);
            if (deleted > 0)
                _log.LogInformation("Agent stream retention sweep deleted {Count} file(s)", deleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Agent stream retention sweep failed");
        }
    }
}
