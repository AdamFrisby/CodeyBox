namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Seam for reading instance guest state (CPU usage) from Incus.
/// </summary>
internal interface IIncusInstanceStateReader
{
    Task<IncusGuestCpuSample?> ReadStateAsync(
        IncusSandboxOptions options,
        string instanceName,
        CancellationToken ct);
}

/// <summary>
/// Default implementation that queries Incus instance state via the CLI/API runner.
/// </summary>
internal sealed class DefaultIncusInstanceStateReader(
    IncusCliRunner cli,
    TimeProvider timeProvider) : IIncusInstanceStateReader
{
    public async Task<IncusGuestCpuSample?> ReadStateAsync(
        IncusSandboxOptions options,
        string instanceName,
        CancellationToken ct)
    {
        // The instance name and project are interpolated into the Incus REST
        // path below, so they must carry the same identifier guard every other
        // Incus call site applies — an unvalidated name could smuggle '/',
        // '?', or '%' escapes and redirect the query at a different endpoint.
        IncusInputValidation.ValidateOptionsIdentity(options);
        IncusInputValidation.ValidateInstanceName(instanceName, nameof(instanceName));

        var result = await cli.RunAllowFailureAsync(
            options,
            [options.BinaryPath, "query", $"/1.0/instances/{instanceName}/state?project={options.ProjectName}"],
            stdin: null,
            timeout: options.ActivityQueryTimeout,
            ct: ct,
            heavyOperation: false,
            maxStdoutBytes: 64 * 1024,
            maxStderrBytes: 4096).ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return null;

        if (IncusStateParser.TryParseGuestCpuUsage(result.Stdout, out var cpuUsageNs))
        {
            return new IncusGuestCpuSample(cpuUsageNs, timeProvider.GetUtcNow());
        }

        return null;
    }
}
