using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Api;

/// <summary>
/// Provider IDs supported by the API composition root. Concrete providers own
/// their stable IDs; configuration and routing reuse them from here.
/// </summary>
internal static class SandboxProviderKinds
{
    internal const string Incus = IncusSandboxProvider.ProviderId;
    internal const string Multipass = MultipassSandboxProvider.ProviderId;
    internal const string MultipassRemote = "multipass-remote";
    internal const string Sprites = "sprites";
    internal const string Bubblewrap = "bubblewrap";
    internal const string Process = "process";

    /// <summary>
    /// Every provider kind the composition root can build (see
    /// <c>Program.BuildSandboxProviderInner</c>). The sandbox class catalog
    /// validates member provider kinds against this set.
    /// </summary>
    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Incus,
        Multipass,
        MultipassRemote,
        Sprites,
        Bubblewrap,
        Process,
    };

    internal static bool IsRegistered(string? providerKind) =>
        !string.IsNullOrWhiteSpace(providerKind) && All.Contains(providerKind.Trim());

    internal static bool SupportsHotReload(string providerId) =>
        providerId is Incus or Multipass;
}
