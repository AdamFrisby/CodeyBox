using CodeyBox.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Build.MSBuild;

/// <summary>
/// Capability-based registration for the MSBuild structured-diagnostics
/// adapter. Registers the hot-reloadable options (disabled by default) and
/// the producer under the neutral <see cref="IBuildDiagnosticsProducer"/>
/// contract so the build lifecycle depends only on Core. The shared
/// <see cref="BuildDiagnosticsProducerRegistry"/> is registered when the
/// composition root has not provided one.
/// </summary>
public static class MSBuildDiagnosticsServiceExtensions
{
    public static IServiceCollection AddMSBuildBuildDiagnostics(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MSBuildDiagnosticsOptions>()
            .Bind(configuration.GetSection(MSBuildDiagnosticsOptions.SectionName))
            .Validate(
                static opts => MSBuildDiagnosticsOptions.IsValid(opts),
                $"{MSBuildDiagnosticsOptions.SectionName} is invalid");
        services.AddSingleton<IBuildDiagnosticsProducer, MSBuildBinlogProducer>();
        services.AddSingleton<BuildDiagnosticsProducerRegistry>();
        return services;
    }
}
