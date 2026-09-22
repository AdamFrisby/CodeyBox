using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// Bitwarden Secrets Manager end-to-end decryption: type-2
/// (<c>Aes256Cbc_HmacSha256_B64</c>) CipherString decryption plus parsing of
/// the single machine-account access token (<c>0.{id}.{secret}:{key}</c>)
/// that carries both the client-credentials grant material and the 64-byte
/// symmetric key. The construction mirrors the public SDK
/// (<c>Aes256CbcHmacSha256</c>: AES-256-CBC PKCS7 Encrypt-then-MAC with
/// <c>HMAC-SHA256(macKey, iv || ciphertext)</c> over raw bytes, standard
/// base64) using only <see cref="System.Security.Cryptography"/> primitives.
/// <para>Only type 2 is supported: it is the current Secrets Manager
/// default. Any other envelope fails closed (never served, never logged).
/// Plaintext values — which real servers never produce but fixtures and
/// some self-hosted shapes do — are not handled here; the caller passes
/// those through unchanged.</para>
/// </summary>
internal static class BitwardenCrypto
{
    internal const int EncKeySize = 32;
    internal const int MacKeySize = 32;
    internal const int KeySize = EncKeySize + MacKeySize;
    internal const int IvSize = 16;
    internal const int MacSize = 32;

    /// <summary>Maximum decrypted plaintext accepted, in bytes (values are small).</summary>
    internal const int MaxPlaintextBytes = 1024 * 1024;

    /// <summary>
    /// Parses a machine-account credential from the host credential chain.
    /// Accepts the single access-token string Bitwarden issues
    /// (<c>0.{clientId}.{clientSecret}:{base64Key}</c>, where the key is the
    /// 64-byte <c>encKey || macKey</c> material) or a legacy bare client
    /// secret. Returns false for the legacy shape, with <paramref name="key"/>
    /// null — the caller then authenticates without decryption material and
    /// any CipherString value fails loudly instead of being served.
    /// </summary>
    internal static bool TryParseMachineCredential(
        string? raw, out string clientId, out string clientSecret, out byte[]? key)
    {
        clientId = string.Empty;
        clientSecret = string.Empty;
        key = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("0.", StringComparison.Ordinal))
            return false;
        var remainder = trimmed[2..];
        var colon = remainder.LastIndexOf(':');
        if (colon <= 0 || colon >= remainder.Length - 1)
            return false;
        var left = remainder[..colon];
        var keyText = remainder[(colon + 1)..].Trim();
        var keyBytes = DecodeKeyMaterial(keyText);
        if (keyBytes is null)
            return false;
        var segments = left.Split('.', StringSplitOptions.None);
        if (segments.Length < 2
            || string.IsNullOrWhiteSpace(segments[0])
            || !Guid.TryParse(segments[0].Trim(), out _))
            return false;
        var secret = string.Join(".", segments.Skip(1)).Trim();
        if (string.IsNullOrEmpty(secret))
            return false;
        clientId = segments[0].Trim();
        clientSecret = secret;
        key = keyBytes;
        return true;
    }

    /// <summary>
    /// Decodes 64-byte (<c>encKey || macKey</c>) symmetric key material from
    /// standard (or URL-safe) base64, tolerating missing padding. Any other
    /// length — including 32-byte halves — yields null: only the full
    /// expanded key can open a type-2 envelope.
    /// </summary>
    internal static byte[]? DecodeKeyMaterial(string? text)
    {
        var bytes = DecodeB64(text);
        return bytes is { Length: KeySize } ? bytes : null;
    }

    /// <summary>
    /// Decrypts a type-2 CipherString (<c>2.{iv}|{data}|{mac}</c>) with the
    /// 64-byte key. The MAC is verified in constant time before any
    /// decryption; any parse, length, MAC, padding, or UTF-8 failure yields
    /// false with no plaintext and no exception detail (callers surface a
    /// fixed message so ciphertext and key material never reach logs).
    /// </summary>
    internal static bool TryDecryptCipherString(string? cipher, byte[] key, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(cipher) || key is not { Length: KeySize })
            return false;
        if (!cipher.StartsWith("2.", StringComparison.Ordinal))
            return false;
        var parts = cipher[2..].Split('|');
        if (parts.Length != 3)
            return false;
        var iv = DecodeB64(parts[0]);
        var data = DecodeB64(parts[1]);
        var mac = DecodeB64(parts[2]);
        if (iv is not { Length: IvSize }
            || mac is not { Length: MacSize }
            || data is null
            || data.Length == 0
            || data.Length > MaxPlaintextBytes + 64
            || data.Length % 16 != 0)
            return false;

        var encKey = new byte[EncKeySize];
        var macKey = new byte[MacKeySize];
        Buffer.BlockCopy(key, 0, encKey, 0, EncKeySize);
        Buffer.BlockCopy(key, EncKeySize, macKey, 0, MacKeySize);
        try
        {
            using var hmac = new HMACSHA256(macKey);
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(data, 0, data.Length);
            var expected = hmac.Hash;
            if (expected is null || !CryptographicOperations.FixedTimeEquals(expected, mac))
                return false;

            using var aes = Aes.Create();
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            byte[] decrypted;
            try
            {
                using var decryptor = aes.CreateDecryptor();
                decrypted = decryptor.TransformFinalBlock(data, 0, data.Length);
            }
            catch (CryptographicException)
            {
                return false;
            }
            if (decrypted.Length == 0 || decrypted.Length > MaxPlaintextBytes)
                return false;
            string text;
            try
            {
                text = Encoding.UTF8.GetString(decrypted);
            }
            catch (ArgumentException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
            if (string.IsNullOrEmpty(text))
                return false;
            plaintext = text;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    /// <summary>
    /// Recovers the organisation/project decryption key from the identity
    /// token response's <c>encrypted_payload</c> (a CipherString encrypted
    /// with the access-token key). Accepts a JSON envelope carrying the key
    /// under a <c>key</c>/<c>encryptionKey</c>-style field, or a bare
    /// base64 key. Returns null when the payload is absent or unusable —
    /// the caller then falls back to decrypting values directly with the
    /// access-token key.
    /// </summary>
    internal static byte[]? DeriveOrganizationKey(string? encryptedPayload, byte[] accessKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedPayload) || accessKey is not { Length: KeySize })
            return null;
        if (!TryDecryptCipherString(encryptedPayload.Trim(), accessKey, out var json))
            return null;
        var trimmed = json.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 16 * 1024)
            return null;
        var direct = DecodeKeyMaterial(trimmed.Trim('"'));
        if (direct is not null)
            return direct;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var name in new[] { "key", "encryptionKey", "encryption_key", "organizationKey", "organization_key", "sharedKey" })
            {
                if (doc.RootElement.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.String)
                {
                    var candidate = DecodeKeyMaterial(property.GetString());
                    if (candidate is not null)
                        return candidate;
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a secret <c>value</c> wears a Bitwarden CipherString
    /// envelope rather than a usable plaintext value.
    /// </summary>
    internal static bool IsCipherString(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length > 2
            && char.IsAsciiDigit(value[0])
            && value[1] == '.'
            && value.Contains('|');

    private static byte[]? DecodeB64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var normalized = text.Trim().Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding == 1)
            return null;
        if (padding != 0)
            normalized += new string('=', 4 - padding);
        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
