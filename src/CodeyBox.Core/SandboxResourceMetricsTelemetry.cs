namespace CodeyBox.Core;

/// <summary>Records provider-neutral sandbox resource metrics on the shared instruments.</summary>
public static class SandboxResourceMetricsTelemetry
{
    public static void Record(SandboxResourceMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var phaseTag = new KeyValuePair<string, object?>("phase", metrics.Phase);
        var networkTag = new KeyValuePair<string, object?>("network_profile", metrics.NetworkProfile ?? string.Empty);
        if (ToMegabytes(metrics.PeakRamBytes) is { } peak)
            CodeyBoxMeters.SandboxPeakRamMb.Record(peak, phaseTag, networkTag);
        if (SandboxResourceMetricValidation.NormalizeFiniteDouble(
                metrics.AvgCpuPercent,
                minimumInclusive: 0,
                maximumInclusive: 100) is { } cpu)
            CodeyBoxMeters.SandboxAvgCpuPercent.Record(cpu, phaseTag, networkTag);
        if (ToMegabytes(metrics.NetRxBytes) is { } rx)
            CodeyBoxMeters.SandboxNetRxMb.Record(rx, phaseTag, networkTag);
        if (ToMegabytes(metrics.NetTxBytes) is { } tx)
            CodeyBoxMeters.SandboxNetTxMb.Record(tx, phaseTag, networkTag);
    }

    private static double? ToMegabytes(long? bytes) =>
        bytes is >= 0 ? bytes.Value / (1024d * 1024d) : null;
}
