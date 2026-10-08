using CodeyBox.Core;

namespace CodeyBox.Build.MSBuild;

/// <summary>
/// Capability-based <see cref="IBuildDiagnosticsProducer"/> for MSBuild
/// binary logs. Disabled by default (<see cref="MSBuildDiagnosticsOptions.Enabled"/>);
/// the <see cref="BuildDiagnosticsProducerRegistry"/> never invokes a
/// disabled producer and reports <see cref="BuildDiagnosticsStatus.Disabled"/>
/// without touching the payload.
/// </summary>
public sealed class MSBuildBinlogProducer : IBuildDiagnosticsProducer
{
    private readonly Func<MSBuildDiagnosticsOptions> _optionsAccessor;

    public MSBuildBinlogProducer()
        : this(() => new MSBuildDiagnosticsOptions()) { }

    public MSBuildBinlogProducer(MSBuildDiagnosticsOptions options)
        : this(() => options) { }

    /// <summary>
    /// Hot-reload accessor: reads live options on every production so an
    /// operator enable/disable or bound edit applies without a restart.
    /// </summary>
    public MSBuildBinlogProducer(Func<MSBuildDiagnosticsOptions> optionsAccessor) =>
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));

    public string ProviderId => MSBuildDiagnosticsOptions.ProviderId;

    public bool IsEnabled => _optionsAccessor().Enabled;

    public Task<BuildDiagnosticsEvidence> ProduceAsync(
        BuildDiagnosticsProductionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _optionsAccessor();
        if (!options.Enabled)
            return Task.FromResult(BuildDiagnosticsEvidence.Disabled(ProviderId, request.ExpectedBinding));
        if (!string.Equals(request.PayloadFormat, MSBuildDiagnosticsOptions.PayloadFormat, StringComparison.Ordinal))
            return Task.FromResult(BuildDiagnosticsEvidence.Insufficient(
                ProviderId,
                request.ExpectedBinding,
                $"unsupported-payload: format '{request.PayloadFormat}' is not '{MSBuildDiagnosticsOptions.PayloadFormat}'"));
        return MSBuildBinlogParser.ParseAsync(request.PayloadBytes ?? [], request.ExpectedBinding, options, ct);
    }
}
