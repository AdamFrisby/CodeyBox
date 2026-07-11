using System.Net;
using System.Text;

namespace CodeyBox.ExploratoryTesting;

internal static class BoundedHttpBodyReader
{
    public const int DefaultMaxBodyBytes = 256 * 1024;
    public const int MaximumBodyBytes = 4 * 1024 * 1024;

    public static async Task<(HttpStatusCode StatusCode, string? Body, bool BodyTooLarge)> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        int maxBodyBytes = DefaultMaxBodyBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        if (maxBodyBytes is < 1 or > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBodyBytes)
            return (response.StatusCode, null, BodyTooLarge: true);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[maxBodyBytes + 1];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        if (totalRead > maxBodyBytes)
            return (response.StatusCode, null, BodyTooLarge: true);

        return (
            response.StatusCode,
            Encoding.UTF8.GetString(buffer, 0, totalRead),
            BodyTooLarge: false);
    }
}
