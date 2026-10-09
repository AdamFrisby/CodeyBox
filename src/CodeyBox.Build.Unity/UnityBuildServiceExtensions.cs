using CodeyBox.Core.ExternalBuilds;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Capability-based registration for the Unity Build Automation adapter.
/// Registers the hot-reloadable options (disabled by default) and the provider
/// under the neutral <see cref="IExternalBuildProvider"/> contract so the
/// shared lifecycle depends only on Core. The host supplies the approval
/// lookup and wires <see cref="IUnityBuildCredentialProvider"/> as explicit
/// activation steps; until then every provider call fails closed.
/// </summary>
public static class UnityBuildServiceExtensions
{
    public static IServiceCollection AddUnityBuildAutomation(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<string, ExternalBuildTargetApproval?> approvalLookup,
        IUnityBuildCredentialProvider? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(approvalLookup);

        services.AddOptions<UnityBuildAutomationOptions>()
            .Bind(configuration.GetSection(UnityBuildAutomationOptions.SectionName))
            .Validate(
                static opts => UnityBuildAutomationOptions.IsValid(opts),
                $"{UnityBuildAutomationOptions.SectionName} is invalid");
        services.AddOptions<ExternalBuildOptions>()
            .Bind(configuration.GetSection(ExternalBuildOptions.SectionName));
        services.AddSingleton<IUnityBuildCredentialProvider>(
            credentials ?? new NullUnityBuildCredentialProvider());
        services.AddHttpClient("unity-build-automation", client =>
        {
            client.BaseAddress = new Uri(UnityBuildAutomationOptions.ApiBaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IExternalBuildProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var unity = sp.GetRequiredService<IOptionsMonitor<UnityBuildAutomationOptions>>();
            var framework = sp.GetRequiredService<IOptionsMonitor<ExternalBuildOptions>>();
            return new UnityBuildAutomationProvider(
                factory.CreateClient("unity-build-automation"),
                () => unity.CurrentValue,
                () => framework.CurrentValue,
                approvalLookup,
                sp.GetRequiredService<IUnityBuildCredentialProvider>());
        });
        return services;
    }
}
