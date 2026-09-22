namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Shared capped stream copy for credential-provider plugins. The cap is
/// enforced <em>before</em> buffering completes so a lying or missing
/// content-length can never fill host memory. Returns the bytes plus
/// whether the source exceeded the cap; callers map over-cap to their own
/// backend-typed failure (throw) or truncation policy. One loop so
/// buffer sizing and cancellation behaviour cannot diverge per call site.
/// </summary>
public static class CredentialBodies
{
    /// <summary>Read buffer size for capped copies.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Copies at most <paramref name="cap"/>+1 bytes from
    /// <paramref name="source"/>. When the source exceeds
    /// <paramref name="cap"/>, copying stops early and
    /// <c>Truncated</c> is true (the returned bytes are the
    /// <paramref name="cap"/>+1-byte prefix read so far).
    /// </summary>
    public static async Task<(byte[] Bytes, bool Truncated)> CopyCappedAsync(
        Stream source, int cap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cap, 0);
        var buffer = new byte[Math.Min(cap, BufferSize)];
        using var sink = new MemoryStream();
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sink.Write(buffer, 0, read);
            if (sink.Length > cap)
                return (sink.ToArray(), true);
        }
        return (sink.ToArray(), false);
    }
}
