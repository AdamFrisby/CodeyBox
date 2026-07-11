using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Builds provider-scoped durable session references and restores their opaque
/// lifecycle scope before asking a provider composite to resume them.
/// </summary>
internal static class AgentSessionSandboxRouting
{
    internal static AgentSessionSandboxRef CreateReference(ISandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        var owned = SandboxCapability.Find<IProviderOwnedSandbox>(sandbox);
        if (owned is not null)
        {
            ValidateProviderId(owned.ProviderId);
            return new AgentSessionSandboxRef(owned.Id, owned.ProviderId);
        }
        return new AgentSessionSandboxRef(sandbox.Id);
    }

    internal static Task ResumeAsync(
        ISuspendingSandboxProvider provider,
        AgentSessionSandboxRef sandbox,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sandbox);
        return provider.ResumeSandboxAsync(
            new ManagedSandboxInfo(
                sandbox.Id,
                CreatedAt: null,
                DiskBytes: null,
                IsTrackedActive: false,
                LifecycleProviderId: sandbox.Provider),
            ct);
    }

    internal static AgentSessionSandboxRef AddProviderScopeIfMissing(
        AgentSessionSandboxRef sandbox,
        string legacyProviderId)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ValidateProviderId(legacyProviderId);
        return sandbox.Provider is null
            ? sandbox with { Provider = legacyProviderId }
            : sandbox;
    }

    private static void ValidateProviderId(string providerId)
    {
        if (!SandboxProviderIdPolicy.IsValidOpaque(providerId))
        {
            throw new InvalidOperationException(
                "A provider-owned sandbox returned an invalid durable provider identifier.");
        }
    }
}
