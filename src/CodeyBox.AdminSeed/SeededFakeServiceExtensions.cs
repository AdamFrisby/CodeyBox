using CodeyBox.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeyBox.AdminSeed;

/// <summary>
/// One-line wiring for the seeded fake-agent run mode. The orchestrator host
/// calls <see cref="AddSeededFakeAgents"/> when
/// <c>CodeyBox:SeededFakeAgents:Enabled</c> is true; otherwise the fake
/// runner and probe are never registered and production behavior is
/// untouched.
/// </summary>
public static class SeededFakeServiceExtensions
{
    public const string ConfigSectionPath = "CodeyBox:SeededFakeAgents";

    public static IServiceCollection AddSeededFakeAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentRunner>(sp =>
            new SeededFakeAgentRunner(
                sp.GetRequiredService<IOptionsMonitor<SeededFakeAgentOptions>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IAgentQuotaProbe>(sp =>
            new SeededFakeQuotaProbe(
                sp.GetRequiredService<IOptionsMonitor<SeededFakeAgentOptions>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
