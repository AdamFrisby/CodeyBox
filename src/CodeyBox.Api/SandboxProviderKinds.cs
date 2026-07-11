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

    internal static bool SupportsHotReload(string providerId) =>
        providerId is Incus or Multipass;
}
