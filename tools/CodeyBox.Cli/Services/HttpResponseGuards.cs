namespace CodeyBox.Cli.Services;

internal static class HttpResponseGuards
{
    internal static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Truncate verbose 5xx bodies to avoid leaking server internals (stack traces, hostnames).
        if ((int)resp.StatusCode >= 500 && body.Length > 200)
            body = body[..200] + "... (truncated)";
        throw new CodeyBoxApiException((int)resp.StatusCode, body);
    }
}
