namespace CodeyBox.Core;

/// <summary>
/// Host-owned opt-in for per-sandbox egress verification of provider-host
/// packet filters (for example Tart Softnet on the Mac host). Bound under
/// <c>CodeyBox:EgressVerification</c> and hot-reloadable: the placement
/// acquirer reads the current value on every acquisition, so edits land
/// without a restart. A plugin cannot set this section — it lives in the
/// host's configuration, and only kinds named here are ever eligible for
/// the <see cref="EgressEnforcementLocation.EnforcedOnProviderHostVerified"/>
/// classification.
/// </summary>
public sealed class EgressVerificationOptions
{
    /// <summary>Config section name under <c>CodeyBox:</c>.</summary>
    public const string SectionName = "EgressVerification";

    /// <summary>Full config path prefix used in validation messages.</summary>
    public const string ConfigPath = "CodeyBox:EgressVerification";

    /// <summary>Maximum characters accepted in one configured host name or IP literal.</summary>
    public const int MaxHostLength = 253;

    /// <summary>Upper bound on the per-check TCP timeout. Larger values stall placement.</summary>
    public static readonly TimeSpan MaxPerCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Lower bound on the per-check TCP timeout. Smaller values flake on slow guests.</summary>
    public static readonly TimeSpan MinPerCheckTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Default per-check TCP probe timeout.</summary>
    public static readonly TimeSpan DefaultPerCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Default demotion cool-down after a failed canary.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(15);

    /// <summary>Default cap on probe output retained per exec.</summary>
    public const int DefaultMaxProbeOutputBytes = 4096;

    /// <summary>
    /// Provider kinds the operator opts into per-sandbox canary verification
    /// (for example <c>["tart"]</c>). Compared exact (ordinal, case-insensitive)
    /// after trimming; unknown or blank entries fail validation. Empty means
    /// no kind is verifiable and every plugin kind stays
    /// <see cref="EgressEnforcementLocation.NotEnforced"/>. Opt-in alone never
    /// grants enforcement — each sandbox still needs a passing canary.
    /// </summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>
    /// Allowed-destination probe: a host the operator guarantees is reachable
    /// through the filter (it must be on the sandbox allowlist). The canary
    /// passes only if a guest TCP connect here succeeds. Empty disables
    /// verification fail-closed (every canary fails until configured).
    /// </summary>
    public string AllowedHost { get; set; } = string.Empty;

    /// <summary>TCP port for <see cref="AllowedHost"/> (1–65535).</summary>
    public int AllowedPort { get; set; } = 443;

    /// <summary>
    /// Blocked-destination canary: a dedicated IP or host the operator
    /// guarantees is NOT on any allowlist. Never a third-party host the
    /// operator has not configured — use a TEST-NET address or an
    /// operator-owned sink. The canary passes only if a guest TCP connect
    /// here fails within <see cref="PerCheckTimeout"/>. Empty disables
    /// verification fail-closed.
    /// </summary>
    public string BlockedHost { get; set; } = string.Empty;

    /// <summary>TCP port for <see cref="BlockedHost"/> (1–65535).</summary>
    public int BlockedPort { get; set; } = 443;

    /// <summary>
    /// Global IPv6 probe: the canary passes only if a guest TCP connect here
    /// fails (provider-host filters document IPv4 only, so IPv6 must be
    /// proven blocked). Defaults to a TEST-NET-6 documentation address that
    /// is never a real host.
    /// </summary>
    public string Ipv6Host { get; set; } = "2001:db8::1";

    /// <summary>TCP port for <see cref="Ipv6Host"/> (1–65535).</summary>
    public int Ipv6Port { get; set; } = 443;

    /// <summary>
    /// Provider-host LAN probe: the Mac host's own LAN address on the guest
    /// network. The canary passes only if a guest TCP connect here fails.
    /// Empty disables verification fail-closed.
    /// </summary>
    public string LanHost { get; set; } = string.Empty;

    /// <summary>TCP port for <see cref="LanHost"/> (1–65535).</summary>
    public int LanPort { get; set; } = 22;

    /// <summary>
    /// Bound on one guest TCP probe. The in-guest connect timeout and the
    /// host-side exec bound both derive from this single knob, so probes
    /// cannot stall placement past roughly four times this value.
    /// </summary>
    public TimeSpan PerCheckTimeout { get; set; } = DefaultPerCheckTimeout;

    /// <summary>
    /// How long a kind stays demoted to
    /// <see cref="EgressEnforcementLocation.NotEnforced"/> after a failed
    /// canary before it becomes eligible for a fresh canary again.
    /// </summary>
    public TimeSpan Cooldown { get; set; } = DefaultCooldown;

    /// <summary>
    /// Cadence for re-running the canary on a handed-out verified sandbox.
    /// Catches a filter that dies mid-run. Zero or negative disables
    /// periodic re-verification (creation-time verification still applies).
    /// </summary>
    public TimeSpan ReverifyInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Maximum probe stdout/stderr bytes retained per exec.</summary>
    public int MaxProbeOutputBytes { get; set; } = DefaultMaxProbeOutputBytes;

    /// <summary>
    /// Normalised opted-in kinds: trimmed, lowercased, deduplicated,
    /// ordinally sorted. Empty when nothing is opted in.
    /// </summary>
    public IReadOnlyList<string> NormalizedKinds() =>
        Kinds
            .Where(static k => !string.IsNullOrWhiteSpace(k))
            .Select(static k => k.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// True when the operator named <paramref name="providerKind"/> in
    /// <see cref="Kinds"/>. Comparison is exact on the normalised kind.
    /// </summary>
    public bool IsKindOptedIn(string providerKind)
    {
        if (string.IsNullOrWhiteSpace(providerKind))
            return false;
        var kind = providerKind.Trim().ToLowerInvariant();
        return Kinds.Any(k => string.Equals(k?.Trim(), kind, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when every probe endpoint the canary requires is configured.
    /// A missing endpoint fails verification closed — never skipped.
    /// </summary>
    public bool AreCanaryEndpointsConfigured() =>
        !string.IsNullOrWhiteSpace(AllowedHost)
        && !string.IsNullOrWhiteSpace(BlockedHost)
        && !string.IsNullOrWhiteSpace(Ipv6Host)
        && !string.IsNullOrWhiteSpace(LanHost);

    /// <summary>
    /// Fails closed on any misconfiguration, naming the offending key.
    /// </summary>
    /// <exception cref="InvalidOperationException">Any value is out of range.</exception>
    public void Validate()
    {
        foreach (var kind in Kinds)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new InvalidOperationException(
                    $"{ConfigPath}:Kinds must not contain blank entries.");
        }
        ValidateHost(AllowedHost, nameof(AllowedHost), required: false);
        ValidateHost(BlockedHost, nameof(BlockedHost), required: false);
        ValidateHost(Ipv6Host, nameof(Ipv6Host), required: false);
        ValidateHost(LanHost, nameof(LanHost), required: false);
        ValidatePort(AllowedPort, nameof(AllowedPort));
        ValidatePort(BlockedPort, nameof(BlockedPort));
        ValidatePort(Ipv6Port, nameof(Ipv6Port));
        ValidatePort(LanPort, nameof(LanPort));
        if (PerCheckTimeout < MinPerCheckTimeout || PerCheckTimeout > MaxPerCheckTimeout)
            throw new InvalidOperationException(
                $"{ConfigPath}:{nameof(PerCheckTimeout)} must be between {MinPerCheckTimeout.TotalSeconds:g} and {MaxPerCheckTimeout.TotalSeconds:g} seconds.");
        if (Cooldown <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{ConfigPath}:{nameof(Cooldown)} must be positive.");
        if (MaxProbeOutputBytes <= 0)
            throw new InvalidOperationException(
                $"{ConfigPath}:{nameof(MaxProbeOutputBytes)} must be > 0.");
    }

    private static void ValidateHost(string host, string property, bool required)
    {
        if (host.Length == 0)
        {
            if (required)
                throw new InvalidOperationException($"{ConfigPath}:{property} must be configured.");
            return;
        }
        if (host.Length > MaxHostLength
            || host.Any(char.IsWhiteSpace)
            || host.Contains('/'))
            throw new InvalidOperationException(
                $"{ConfigPath}:{property} must be a bare hostname or IP literal (no scheme, port, or path).");
        if (host.Contains("://", StringComparison.Ordinal) || host.Contains('@'))
            throw new InvalidOperationException(
                $"{ConfigPath}:{property} must be a bare hostname or IP literal (no scheme, port, or path).");
    }

    private static void ValidatePort(int port, string property)
    {
        if (port is < 1 or > 65535)
            throw new InvalidOperationException(
                $"{ConfigPath}:{property} must be a TCP port (1–65535).");
    }
}
