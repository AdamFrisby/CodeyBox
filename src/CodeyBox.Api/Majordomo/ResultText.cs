using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Reads the error text out of an <see cref="IResult"/> the existing
/// creation/commit contracts produce. The result is executed against a
/// synthetic response buffer — it is never written to a wire — so the
/// majordomo refusal carries the exact text the REST caller would have seen.
/// </summary>
internal static class ResultText
{
    /// <summary>
    /// Bound on untrusted error body text echoed into a refusal detail —
    /// without it a pathological response body would ride the refusal
    /// unbounded.
    /// </summary>
    private const int MaxErrorBodyChars = 2000;

    // Minimal service provider for result execution — the JSON writer only
    // needs IOptions<HttpJsonOptions>-style lookups, which resolve to defaults
    // here. Shared and read-only; constructed once.
    private static readonly IServiceProvider EmptyServices =
        new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();

    public static async Task<string> ReadErrorTextAsync(IResult result)
    {
        var buffer = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = EmptyServices };
        context.Response.Body = buffer;
        await result.ExecuteAsync(context).ConfigureAwait(false);
        buffer.Position = 0;
        var body = await new StreamReader(buffer).ReadToEndAsync().ConfigureAwait(false);
        return ExtractError(body) ?? "request rejected";
    }

    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString();
        }
        catch (JsonException)
        {
            // fall through — return the raw body below
        }

        return body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body;
    }
}
