using CodeyBox.Core;

namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Resolves the BYOK provider a single Copilot invocation runs against: the
/// member's named override when it carries one, else the agent-global
/// provider. Pure, so the member/global precedence is unit-testable without
/// launching anything.
/// </summary>
public static class CopilotProviderResolver
{
    /// <summary>
    /// Returns the provider <paramref name="member"/> runs against: the
    /// <c>CodeyBox:Copilot:Providers</c> entry its
    /// <see cref="AgentProviderReference.Name"/> selects, or
    /// <paramref name="global"/>'s <see cref="CopilotOptions.Provider"/> when
    /// the member names none. A named entry that is present but unconfigured
    /// (no base URL) resolves successfully and means native subscription auth
    /// for that member — the same rule
    /// <see cref="CopilotAgentRunner.BuildProviderEnvironment(CopilotOptions)"/>
    /// applies at the member level.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The member names a provider with no catalog entry. This throws rather
    /// than falling back: silently running an unintended backend is worse
    /// than refusing the invocation. Configuration validation rejects such
    /// members at startup; this is the fail-closed backstop for members built
    /// programmatically.
    /// </exception>
    public static CopilotProviderOptions ResolveEffectiveProvider(AgentMembership? member, CopilotOptions global)
    {
        ArgumentNullException.ThrowIfNull(global);
        var name = member?.ProviderReference?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return global.Provider;

        var trimmed = name.Trim();
        if (TryFind(global.Providers, trimmed, out var named) && named is not null)
            return named;

        var fallback = global.Provider.IsConfigured
            ? "the agent-global provider"
            : "native subscription auth";
        throw new InvalidOperationException(
            $"AgentClass member '{member!.RouteKey}' names Copilot provider '{trimmed}' which is not configured. " +
            $"Add it under CodeyBox:Copilot:Providers:{trimmed} or remove the member's Provider reference. " +
            $"The member is NOT silently falling back to {fallback}.");
    }

    /// <summary>
    /// Case-insensitive catalog lookup. Scans entries instead of trusting the
    /// dictionary comparer so configuration-bound catalogs resolve the same
    /// way regardless of how the binder constructed them.
    /// </summary>
    public static bool TryFind(
        IReadOnlyDictionary<string, CopilotProviderOptions>? catalog,
        string name,
        out CopilotProviderOptions? provider)
    {
        provider = null;
        if (catalog is null || string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        foreach (var (key, value) in catalog)
        {
            if (string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                provider = value;
                return value is not null;
            }
        }

        return false;
    }
}
