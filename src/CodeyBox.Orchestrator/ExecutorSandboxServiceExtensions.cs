using CodeyBox.Core;
using CodeyBox.HostProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Executor-side sandbox provider composition. Registers the shared
/// <see cref="ISandboxProviderRegistry"/> — built through
/// <see cref="SharedSandboxProviderFactory"/>, the same composition the
/// orchestrator uses — plus the singleton <see cref="ISandboxProvider"/>
/// for the primary declared kind. Provider options bind from the
/// executor's own <c>CodeyBox:Executor</c> section (see
/// <see cref="ExecutorOptions"/>), so an operator tunes an executor's
/// Incus or Bubblewrap settings the same way they tune the
/// orchestrator's. An executor may declare more than one kind (see
/// <see cref="ExecutorOptions.SandboxProviders"/>); each kind is built
/// once and shared across every member naming it.
/// </summary>
public static class ExecutorSandboxServiceExtensions
{
    /// <summary>
    /// Adds the executor's provider registry and primary provider. Requires
    /// <c>Func{ExecutorOptions}</c> to be registered (the executor host
    /// does this from <c>CodeyBox:Executor</c> before calling here).
    /// </summary>
    public static IServiceCollection AddExecutorSandboxProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISandboxProviderRegistry>(sp =>
        {
            var options = sp.GetRequiredService<Func<ExecutorOptions>>();
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var runner = sp.GetService<IProcessRunner>();
            return new SandboxProviderRegistry(
                kind => SharedSandboxProviderFactory.Build(
                    kind,
                    SandboxProviderBuildArgs.FromExecutorOptions(options, loggers, runner)),
                sp.GetService<ILogger<SandboxProviderRegistry>>());
        });
        services.AddSingleton<ISandboxProvider>(sp =>
        {
            var options = sp.GetRequiredService<Func<ExecutorOptions>>()();
            return sp.GetRequiredService<ISandboxProviderRegistry>().EnsureKind(options.GetPrimarySandboxKind());
        });
        return services;
    }
}
