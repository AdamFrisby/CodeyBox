namespace CodeyBox.Orchestrator;

public static class QuotaRouterDefaults
{
    public const int DefaultQuotaRecoveryProbeIntervalSeconds = 5;

    public static TimeSpan DefaultQuotaRecoveryProbeInterval { get; } =
        TimeSpan.FromSeconds(DefaultQuotaRecoveryProbeIntervalSeconds);
}
