using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Registration for the GitHub Actions execution/evidence adapter. The
/// base overload registers only the hot-reloadable options (disabled by
/// default) and the binding store: safe to call everywhere, contacts
/// nothing. The credentialed overload additionally wires the HTTP transport
/// (reusing the existing GitHub credential identity) and the provider under
/// the neutral <see cref="IExternalBuildProvider"/> contract. Tests register
/// the fake transport instead and construct the provider directly.
/// </summary>
public static class GitHubActionsServiceExtensions
{
    public static IServiceCollection AddGitHubActionsExternalBuilds(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GitHubActionsExternalBuildOptions>()
            .Bind(configuration.GetSection(GitHubActionsExternalBuildOptions.SectionName))
            .Validate(
                static opts => GitHubActionsExternalBuildOptions.IsValid(opts),
                $"{GitHubActionsExternalBuildOptions.SectionName} is invalid");
        services.AddSingleton<IGitHubActionsBindingStore, InMemoryGitHubActionsBindingStore>();
        return services;
    }

    public static IServiceCollection AddGitHubActionsExternalBuilds(
        this IServiceCollection services,
        IConfiguration configuration,
        IGitHubTokenProvider tokenProvider,
        Func<ExternalBuildOptions>? frameworkOptions = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        AddGitHubActionsExternalBuilds(services, configuration);
        services.AddSingleton<IGitHubActionsCredentialProvider>(
            new GitHubTokenCredentialAdapter(tokenProvider));
        services.AddSingleton<IGitHubActionsTransport, GitHubActionsHttpTransport>();
        services.AddSingleton<IExternalBuildProvider>(
            sp => new GitHubActionsBuildProvider(
                sp.GetRequiredService<IGitHubActionsTransport>(),
                sp.GetRequiredService<IGitHubActionsBindingStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GitHubActionsExternalBuildOptions>>().CurrentValueAccessor(),
                frameworkOptions ?? (() => new ExternalBuildOptions()),
                clock));
        return services;
    }

    private static Func<GitHubActionsExternalBuildOptions> CurrentValueAccessor(
        this Microsoft.Extensions.Options.IOptionsMonitor<GitHubActionsExternalBuildOptions> monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return () => monitor.CurrentValue;
    }
}
