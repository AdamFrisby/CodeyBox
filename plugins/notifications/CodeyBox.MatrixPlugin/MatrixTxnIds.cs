using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;

namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Stable transaction IDs for Matrix event sends. The Client-Server API
/// deduplicates on <c>(access token, transaction ID)</c>: re-PUTting the same
/// path returns the original <c>event_id</c> instead of sending a duplicate.
/// The provider generates one ID per notification and reuses it across every
/// retry of that send, so a rate-limit retry can never double-post. The ID is
/// derived deterministically from the notification's own identity, so a
/// redelivered notification object maps to the same transaction rather than a
/// second message. IDs are URL-safe (<c>cbx-</c> plus lowercase hex).
/// </summary>
internal static class MatrixTxnIds
{
    /// <summary>Prefix marking transaction IDs minted by this plugin.</summary>
    public const string Prefix = "cbx-";

    /// <summary>Hex characters kept from the identity digest. 128 bits is far
    /// beyond the collision budget for per-notification dedup while keeping
    /// the path segment short.</summary>
    public const int DigestHexChars = 32;

    /// <summary>Derive the stable transaction ID for a notification.</summary>
    public static string ForNotification(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var material = string.Join('\0',
            notification.ConditionId,
            notification.CorrelationToken ?? string.Empty,
            notification.Timestamp.ToUnixTimeSeconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            notification.Title);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Prefix + Convert.ToHexString(digest)[..DigestHexChars].ToLowerInvariant();
    }
}
