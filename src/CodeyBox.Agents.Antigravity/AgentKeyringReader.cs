using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Platform-agnostic read surface for OS credential stores (freedesktop Secret
/// Service on Linux, Keychain on macOS, Credential Manager on Windows).
/// Only the Linux backend ships today; see <see cref="SecretServiceKeyringReader"/>.
/// Where no backend is available for the host platform, composition roots must
/// surface that gap explicitly via <see cref="AntigravityKeyringStartup"/>
/// rather than degrading silently.
/// </summary>
public interface IAgentKeyringReader
{
    /// <summary>
    /// Reads the secret stored under <paramref name="service"/> /
    /// <paramref name="username"/>. Returns <c>null</c> when the item is
    /// absent or unreadable; implementations must not throw for those cases.
    /// </summary>
    Task<string?> ReadSecretAsync(string service, string username, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAgentKeyringReader"/> backed by the freedesktop Secret Service
/// over D-Bus. Linux-only: use <see cref="TryCreate"/> so non-Linux hosts get
/// <c>null</c> instead of a reader that can never succeed.
/// </summary>
public sealed class SecretServiceKeyringReader : IAgentKeyringReader
{
    private readonly ILogger? _log;
    private readonly string? _busAddress;
    private readonly TimeSpan? _timeout;

    public SecretServiceKeyringReader(ILogger? log = null, string? busAddress = null, TimeSpan? timeout = null)
    {
        _log = log;
        _busAddress = busAddress;
        _timeout = timeout;
    }

    public Task<string?> ReadSecretAsync(string service, string username, CancellationToken ct = default)
        => SecretServiceClient.ReadSecretAsync(service, username, _busAddress, _timeout, _log, ct);

    /// <summary>
    /// Creates the Secret Service reader when the host platform supports it.
    /// Returns <c>null</c> on platforms without a freedesktop Secret Service
    /// (today: anything but Linux). The platform check is injectable so tests
    /// can cover both outcomes without branching on the real OS.
    /// </summary>
    /// <param name="isPlatformSupported">Optional test seam; defaults to
    /// <see cref="OperatingSystem.IsLinux"/>.</param>
    public static IAgentKeyringReader? TryCreate(Func<bool>? isPlatformSupported = null, ILogger? log = null)
    {
        if (!(isPlatformSupported?.Invoke() ?? OperatingSystem.IsLinux()))
            return null;
        return new SecretServiceKeyringReader(log);
    }
}

/// <summary>
/// Well-known Secret Service item coordinates for the Antigravity token
/// bundle, plus the adapter from <see cref="IAgentKeyringReader"/> to the
/// keyring delegate the refresher accepts. Single source of truth for the
/// service/username pair so DI wiring and the refresher default cannot drift.
/// </summary>
public static class AntigravityKeyring
{
    /// <summary>Secret Service <c>service</c> attribute for the agy bundle.</summary>
    public const string Service = "gemini";

    /// <summary>Secret Service <c>username</c> attribute for the agy bundle.</summary>
    public const string Username = "antigravity";

    /// <summary>
    /// Adapts a keyring reader to the refresh delegate shape consumed by
    /// <see cref="AntigravityOauthCredentialFileRefresher"/>.
    /// </summary>
    public static Func<CancellationToken, Task<string?>> ToRefreshDelegate(IAgentKeyringReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ct => reader.ReadSecretAsync(Service, Username, ct);
    }
}

/// <summary>
/// Emits the one-time startup warning for hosts with no usable keyring
/// backend. A missing backend is a visible gap, not a silent one: without
/// refresh the probe's token expires after about an hour and quota reads
/// UNKNOWN (the exact failure #400 fixed on Linux).
/// </summary>
public static class AntigravityKeyringStartup
{
    /// <summary>
    /// Logs a warning naming the consequence when <paramref name="reader"/>
    /// is <c>null</c>; silent otherwise. Call once at startup.
    /// </summary>
    public static void LogKeyringStatus(ILogger logger, IAgentKeyringReader? reader)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (reader is not null)
            return;
        logger.LogWarning(
            "No supported keyring backend is available on this host "
            + "(the Secret Service reader requires Linux with a session bus). "
            + "The Antigravity OAuth token cannot be refreshed in-orchestrator, "
            + "so the quota probe token will expire after about an hour and quota "
            + "will read UNKNOWN until the host agy CLI refreshes it out-of-band.");
    }
}
