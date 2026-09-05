using System.Text;
using Tmds.DBus;

namespace CodeyBox.Api;

[DBusInterface("org.freedesktop.Secret.Service")]
public interface ISecretService : IDBusObject
{
    Task<(object output, ObjectPath result)> OpenSessionAsync(string algorithm, object input);
    Task<(ObjectPath[] unlocked, ObjectPath[] locked)> SearchItemsAsync(IDictionary<string, string> attributes);
    Task<IDictionary<ObjectPath, SecretValue>> GetSecretsAsync(ObjectPath[] items, ObjectPath session);
    Task<(ObjectPath[] unlocked, ObjectPath prompt)> UnlockAsync(ObjectPath[] objects);
}

[DBusInterface("org.freedesktop.Secret.Item")]
public interface ISecretItem : IDBusObject
{
    Task<SecretValue> GetSecretAsync(ObjectPath session);
}

[DBusInterface("org.freedesktop.Secret.Session")]
public interface ISecretSession : IDBusObject
{
    Task CloseAsync();
}

public struct SecretValue
{
    public ObjectPath Session;
    public byte[] Parameters;
    public byte[] Value;
    public string ContentType;
}

/// <summary>
/// Managed D-Bus client for the freedesktop Secret Service specification
/// (<c>org.freedesktop.secrets</c>). Reads credentials directly from the session
/// bus without requiring the native <c>libsecret-1</c> library or <c>secret-tool</c>.
/// </summary>
public static class SecretServiceClient
{
    internal const string ServiceName = "org.freedesktop.secrets";
    internal const string ServicePath = "/org/freedesktop/secrets";

    /// <summary>
    /// Reads a secret item matching the specified attributes from the Secret Service.
    /// Returns <c>null</c> on any error, missing socket, or absent item without throwing.
    /// </summary>
    public static async Task<string?> ReadSecretAsync(
        string service,
        string username,
        string? busAddress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var address = busAddress ?? ResolveSessionBusAddress();
        if (string.IsNullOrEmpty(address))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        try
        {
            using var connection = new Connection(address);
            await connection.ConnectAsync().WaitAsync(cts.Token).ConfigureAwait(false);

            var secretService = connection.CreateProxy<ISecretService>(ServiceName, ServicePath);

            var (_, sessionPath) = await secretService.OpenSessionAsync("plain", "").WaitAsync(cts.Token).ConfigureAwait(false);

            try
            {
                var attributes = new Dictionary<string, string>
                {
                    ["service"] = service,
                    ["username"] = username,
                };

                var (unlocked, locked) = await secretService.SearchItemsAsync(attributes).WaitAsync(cts.Token).ConfigureAwait(false);

                ObjectPath? targetItem = null;
                if (unlocked is { Length: > 0 })
                {
                    targetItem = unlocked[0];
                }
                else if (locked is { Length: > 0 })
                {
                    var (unlockedAfter, _) = await secretService.UnlockAsync(locked).WaitAsync(cts.Token).ConfigureAwait(false);
                    if (unlockedAfter is { Length: > 0 })
                    {
                        targetItem = unlockedAfter[0];
                    }
                }

                if (targetItem is null)
                    return null;

                var item = connection.CreateProxy<ISecretItem>(ServiceName, targetItem.Value);
                var secret = await item.GetSecretAsync(sessionPath).WaitAsync(cts.Token).ConfigureAwait(false);

                if (secret.Value is null || secret.Value.Length == 0)
                    return null;

                return Encoding.UTF8.GetString(secret.Value);
            }
            finally
            {
                try
                {
                    var session = connection.CreateProxy<ISecretSession>(ServiceName, sessionPath);
                    await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort session close.
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveSessionBusAddress()
    {
        var envAddress = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(envAddress))
            return envAddress;

        return Address.Session;
    }
}
