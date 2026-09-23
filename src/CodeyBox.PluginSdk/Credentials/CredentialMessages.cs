using System.Text;
using System.Text.Json;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Safe exception-message text shared by credential-provider plugins.
/// Messages flow into host logs and the persisted lease store, so embedded
/// control characters must not forge log lines or smuggle terminal escapes
/// into rendered output regardless of which backend constructed the text.
/// One implementation so the sanitisation policy cannot fork per backend.
/// </summary>
public static class CredentialMessages
{
    /// <summary>Cap on an error response body read for detail extraction.</summary>
    public const int MaxErrorBodyBytes = 2048;

    /// <summary>Maximum characters kept from a server-echoed error detail.</summary>
    public const int MaxServerDetailChars = 200;

    /// <summary>Detail used when the server sent no usable error text.</summary>
    public const string NoReadableDetail = "no readable error body";

    /// <summary>
    /// Replaces every control character with a space — a newline-bearing
    /// server message must not forge log lines, and ESC/BEL/NUL must not
    /// smuggle terminal escapes into rendered output — then caps the
    /// message at <paramref name="maxChars"/>. Empty input yields
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string Truncate(string? message, string fallback, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxChars, 0);
        if (string.IsNullOrEmpty(message))
            return fallback;
        var flat = string.Create(message.Length, message, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
        });
        if (flat.Length <= maxChars)
            return flat;
        // Never split a UTF-16 surrogate pair: a trailing lone high
        // surrogate would mangle under the strict UTF-8 encoders that
        // persist or render this message.
        var cut = char.IsHighSurrogate(flat[maxChars - 1]) ? maxChars - 1 : maxChars;
        return flat[..cut];
    }

    /// <summary>
    /// Reads a bounded error body and extracts the detail a backend may
    /// safely relay into an exception message. The body is capped at
    /// <see cref="MaxErrorBodyBytes"/> before decoding; read/connection
    /// failures yield <see cref="NoReadableDetail"/> while caller
    /// cancellation still propagates (it is never swallowed as "no body").
    /// The first string field in <paramref name="detailFields"/> with a
    /// non-empty value wins; <paramref name="allowlist"/>, when set,
    /// additionally restricts the relayed value to known machine-readable
    /// codes so reflected free text can never reach logs or the lease
    /// store. When no field yields a usable value,
    /// <paramref name="relayRawText"/> decides between the sanitised raw
    /// body and the fixed placeholder. Every returned string passes
    /// through <see cref="Truncate"/>: control characters flattened,
    /// capped at <see cref="MaxServerDetailChars"/>.
    /// </summary>
    public static async Task<string> ReadServerDetailAsync(
        HttpResponseMessage response,
        IReadOnlyList<string> detailFields,
        bool relayRawText,
        CancellationToken ct,
        IReadOnlySet<string>? allowlist = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(detailFields);
        string text;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var (bytes, _) = await CredentialBodies.CopyCappedAsync(stream, MaxErrorBodyBytes, ct).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            // Only read/connection faults mean "no readable body"; caller
            // cancellation is never swallowed.
            return NoReadableDetail;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in detailFields)
                {
                    if (!doc.RootElement.TryGetProperty(field, out var property)
                        || property.ValueKind != JsonValueKind.String)
                        continue;
                    var detail = (property.GetString() ?? string.Empty).Trim();
                    if (detail.Length == 0)
                        continue;
                    if (allowlist is not null && !allowlist.Contains(detail))
                        continue;
                    return Truncate(detail, NoReadableDetail, MaxServerDetailChars);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the raw/placeholder branch.
        }
        return relayRawText
            ? Truncate(text, NoReadableDetail, MaxServerDetailChars)
            : NoReadableDetail;
    }
}
