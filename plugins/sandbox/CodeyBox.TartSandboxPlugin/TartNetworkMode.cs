namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Guest-network backend for <c>tart run</c>. <c>Nat</c> is today's behaviour
/// (the guest shares the Mac host's network); <c>Softnet</c> attaches the
/// Softnet userspace packet filter with a block-all default plus an explicit
/// IPv4 allowlist. This never changes the host's egress classification: the
/// kind stays <c>NotEnforced</c> in host-owned code regardless of mode.
/// </summary>
public enum TartNetworkMode
{
    Nat = 0,
    Softnet = 1,
}
