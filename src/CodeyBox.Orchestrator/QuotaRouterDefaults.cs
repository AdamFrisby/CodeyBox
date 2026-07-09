namespace CodeyBox.Orchestrator;

public static class QuotaRouterDefaults
{
    public const int DefaultRampWindowSeconds = 7 * 24 * 60 * 60;
    public const int DefaultQuotaRecoveryProbeIntervalSeconds = 5;
    public const int DefaultQuotaRecoveryProbeEligibilityScanLimit = 128;

    public static TimeSpan DefaultRampWindow { get; } =
        TimeSpan.FromSeconds(DefaultRampWindowSeconds);

    public static TimeSpan DefaultQuotaRecoveryProbeInterval { get; } =
        TimeSpan.FromSeconds(DefaultQuotaRecoveryProbeIntervalSeconds);
}
