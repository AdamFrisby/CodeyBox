namespace CodeyBox.Core;

/// <summary>
/// Where sandbox network-egress enforcement happens for a provider.
/// Only <see cref="EnforcedOnOrchestratorHost"/> claims host-side isolation,
/// and that mechanism is Linux-only (nftables on per-profile bridges).
/// </summary>
public enum EgressEnforcementLocation
{
    /// <summary>
    /// Dropped in the orchestrator host kernel (Linux nftables bridges).
    /// Requires a Linux orchestrator host; see scripts/setup-host-networks.sh.
    /// </summary>
    EnforcedOnOrchestratorHost,

    /// <summary>
    /// Enforcement stays on the remote Linux executor host that runs the VM.
    /// The orchestrator host needs no packet filter; the executor does.
    /// </summary>
    EnforcedOnRemoteExecutorHost,

    /// <summary>No egress enforcement. Must never be described as isolation.</summary>
    NotEnforced,
}

/// <summary>
/// Declares which sandbox providers are supported on which orchestrator-host
/// operating systems, and where their egress enforcement lives.
///
/// <para>Assessment (2026-09, recorded in docs/concepts/host-platforms.md):
/// host-side egress equivalent to the nftables allowlist is not achievable on
/// macOS (<c>pf</c>) or Windows (WFP) at equivalent strength — per-VM
/// bridge attachment with an unbypassable host-kernel drop path does not
/// exist there, and in-guest filtering is bypassable by a root agent. Local
/// VM sandboxes are therefore Linux-only. On macOS/Windows only the
/// remote-executor topology is supported: the orchestrator runs locally while
/// VMs execute on a Linux executor host where the nftables enforcement holds.
/// </para>
/// </summary>
public static class HostPlatformSupport
{
    public const string Incus = "incus";
    public const string Multipass = "multipass";
    public const string MultipassRemote = "multipass-remote";
    public const string Sprites = "sprites";
    public const string Bubblewrap = "bubblewrap";
    public const string Process = "process";

    /// <summary>Every provider id the orchestrator can route to.</summary>
    public static IReadOnlyList<string> AllProviderIds { get; } =
        [Incus, Multipass, MultipassRemote, Sprites, Bubblewrap, Process];

    /// <summary>
    /// Pure, injectable OS query. Defaults to the real runtime; tests pass
    /// explicit values. No global mutable state.
    /// </summary>
    public readonly record struct HostOperatingSystem(bool IsLinux, bool IsMacOS, bool IsWindows)
    {
        public static HostOperatingSystem Current { get; } = new(
            OperatingSystem.IsLinux(),
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsWindows());

        public string Name => IsLinux ? "linux" : IsMacOS ? "macos" : IsWindows ? "windows" : "unknown";
    }

    /// <summary>
    /// Is this provider supported on this orchestrator host OS?
    /// Comparison is exact (ordinal, lowercase ids); no substring matching.
    /// </summary>
    /// <param name="providerId">Provider kind to check (trimmed, lowercased before compare).</param>
    /// <param name="host">Orchestrator host OS under test.</param>
    /// <param name="pluginKinds">
    /// Host-registered plugin-contributed kinds (normalised lowercase ids, e.g. from the
    /// composition root's plugin catalog). A kind in this set is supported on every host OS:
    /// the host holds no OS-specific enforcement mechanism for a plugin backend, so there is
    /// no OS-gated enforcement to check — containment for such kinds is bounded instead by
    /// the <see cref="EgressEnforcementLocation.NotEnforced"/> classification in
    /// <see cref="GetEgressEnforcement"/> plus workload-trust validation, both of which run
    /// for plugin kinds exactly as for built-ins. The set itself is host-owned (built from
    /// allowlisted plugins); a plugin never asserts its own support. Null means no plugin kinds.
    /// </param>
    public static bool IsProviderSupportedOnHost(string providerId, HostOperatingSystem host, IReadOnlySet<string>? pluginKinds = null)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        var id = providerId.Trim().ToLowerInvariant();
        if (pluginKinds is not null && pluginKinds.Contains(id))
            return true;
        return (id, host.IsLinux, host.IsMacOS, host.IsWindows) switch
        {
            (Incus, true, _, _) => true,
            (Multipass, true, _, _) => true,
            (MultipassRemote, _, _, _) => true,
            (Sprites, _, _, _) => true,
            // Bubblewrap is a Linux shared-kernel mechanism; process is a
            // dev-only runner with no isolation. Both only run on Linux hosts
            // (process additionally gated to Development at startup).
            (Bubblewrap, true, _, _) => true,
            (Process, true, _, _) => true,
            _ => false,
        };
    }

    /// <summary>Providers supported on the given host.</summary>
    public static IReadOnlyList<string> SupportedProvidersOnHost(HostOperatingSystem host) =>
        AllProviderIds.Where(id => IsProviderSupportedOnHost(id, host)).ToArray();

    /// <summary>
    /// Where egress enforcement lives for a provider. Unknown ids report NotEnforced.
    /// </summary>
    /// <remarks>
    /// This classification is host-owned: the switch names exactly the in-tree kinds whose
    /// enforcement mechanism the host has reviewed (Linux nftables bridges, applied by
    /// <c>scripts/setup-host-networks.sh</c>). A plugin-contributed kind always falls through
    /// to <see cref="EgressEnforcementLocation.NotEnforced"/> — a plugin cannot promote itself
    /// to an enforced classification by configuration or by any value it returns. Promotion to
    /// an enforced classification is an in-tree change subject to review, never a plugin
    /// capability. See <see cref="SandboxEgressPolicy"/> for where <c>NotEnforced</c>
    /// providers may and may not be used.
    /// </remarks>
    public static EgressEnforcementLocation GetEgressEnforcement(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        return providerId.Trim().ToLowerInvariant() switch
        {
            Incus or Multipass => EgressEnforcementLocation.EnforcedOnOrchestratorHost,
            MultipassRemote or Sprites => EgressEnforcementLocation.EnforcedOnRemoteExecutorHost,
            _ => EgressEnforcementLocation.NotEnforced,
        };
    }

    /// <summary>
    /// True only where the enforcement mechanism has actually been exercised:
    /// local providers on a Linux host (nftables bridges), or remote providers
    /// whose executor host is Linux with setup-host-networks.sh applied.
    /// </summary>
    public static bool ClaimsNetworkIsolation(string providerId, HostOperatingSystem host, bool remoteExecutorIsLinuxEnforced = true)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        var id = providerId.Trim().ToLowerInvariant();
        return id switch
        {
            Incus or Multipass => host.IsLinux,
            MultipassRemote or Sprites => remoteExecutorIsLinuxEnforced,
            _ => false,
        };
    }

    /// <summary>
    /// Human-readable reason when <see cref="IsProviderSupportedOnHost"/> is false.
    /// Empty string when supported. Never returns null.
    /// </summary>
    /// <param name="providerId">Provider kind to explain.</param>
    /// <param name="host">Orchestrator host OS under test.</param>
    /// <param name="pluginKinds">
    /// Host-registered plugin-contributed kinds, named alongside built-ins in the
    /// unknown-kind message. Null means no plugin kinds.
    /// </param>
    public static string GetUnsupportedReason(string providerId, HostOperatingSystem host, IReadOnlySet<string>? pluginKinds = null)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        var id = providerId.Trim().ToLowerInvariant();
        if (IsProviderSupportedOnHost(providerId, host, pluginKinds))
            return string.Empty;
        if (!AllProviderIds.Contains(id, StringComparer.Ordinal)
            && (pluginKinds is null || !pluginKinds.Contains(id)))
            return $"Unknown sandbox provider '{providerId}'. Valid: {FormatKnownProviders(pluginKinds)}.";
        return id switch
        {
            Incus or Multipass or Bubblewrap =>
                $"Provider '{id}' requires a Linux orchestrator host with nftables egress enforcement " +
                $"(scripts/setup-host-networks.sh); it is not supported on {host.Name}. " +
                $"On {host.Name}, use 'multipass-remote' or 'sprites' with a Linux executor host " +
                $"(see docs/concepts/host-platforms.md).",
            Process =>
                $"Provider 'process' is not supported on {host.Name}: it is a dev-only runner with no isolation, only available on Linux hosts. " +
                $"On {host.Name}, use 'multipass-remote' or 'sprites' with a Linux executor host.",
            _ => $"Provider '{id}' is not supported on {host.Name}.",
        };
    }

    /// <summary>
    /// Every known provider kind — built-ins plus host-registered plugin kinds —
    /// ordered ordinally so messages are independent of registration order.
    /// </summary>
    public static string FormatKnownProviders(IReadOnlySet<string>? pluginKinds)
    {
        if (pluginKinds is null || pluginKinds.Count == 0)
            return string.Join(", ", AllProviderIds);
        return string.Join(", ", AllProviderIds.Concat(pluginKinds).OrderBy(static s => s, StringComparer.Ordinal));
    }
}
