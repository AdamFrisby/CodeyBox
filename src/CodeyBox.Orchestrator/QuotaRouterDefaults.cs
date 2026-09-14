namespace CodeyBox.Orchestrator;

public static class QuotaRouterDefaults
{
    public const int DefaultRampWindowSeconds = 7 * 24 * 60 * 60;
    public const int DefaultQuotaRecoveryProbeIntervalSeconds = 5;
    public const int DefaultQuotaRecoveryProbeEligibilityScanLimit = 128;
    public const int DefaultQuotaReservationMaxAgeSeconds = 6 * 60 * 60;
    public const int DefaultReportedReadingMaxAgeSeconds = 5 * 60;

    public static TimeSpan DefaultRampWindow { get; } =
        TimeSpan.FromSeconds(DefaultRampWindowSeconds);

    public static TimeSpan DefaultQuotaRecoveryProbeInterval { get; } =
        TimeSpan.FromSeconds(DefaultQuotaRecoveryProbeIntervalSeconds);

    public static TimeSpan DefaultQuotaReservationMaxAge { get; } =
        TimeSpan.FromSeconds(DefaultQuotaReservationMaxAgeSeconds);

    public const int DefaultReportedReadingClockSkewSeconds = 5 * 60;

    /// <summary>
    /// Tolerance for clock skew between executor and orchestrator hosts when
    /// validating an executor-reported reading's observed time. A report dated
    /// further in the future than this is rejected (a future-dated report
    /// would otherwise read as fresh for longer than its bound allows).
    /// </summary>
    public static TimeSpan DefaultReportedReadingClockSkew { get; } =
        TimeSpan.FromSeconds(DefaultReportedReadingClockSkewSeconds);

    /// <summary>
    /// Default maximum age of an executor-reported quota reading before the
    /// pool reads as unknown. Matches the last-known-good retention horizon
    /// so executor silence and probe silence age out on the same cadence.
    /// </summary>
    public static TimeSpan DefaultReportedReadingMaxAge { get; } =
        TimeSpan.FromSeconds(DefaultReportedReadingMaxAgeSeconds);
}
