using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Implements RFC-style <c>Idempotency-Key</c> for the mutating endpoints
/// (POST, PUT, PATCH, DELETE). The first request with a given key processes
/// normally and the (status + content-type + body) is cached for 24 hours;
/// subsequent replays with the same key and identical body return the cached
/// response, and replays with the same key but a different body return 409.
/// Requests without the header pass through unchanged.
///
/// <para>The cache catches network-flake retries that would otherwise apply
/// the same mutation twice. It is independent of the prompt-revision system,
/// which catches the harder race where a prompt edit lands while an iteration
/// is already in flight.</para>
/// </summary>
internal static class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxKeyLength = 200;

    /// <summary>
    /// Hard cap on cached response body size. Responses larger than this skip
    /// the cache write entirely — the response still returns to the caller,
    /// but a replay with the same key falls through to the downstream
    /// endpoint. Prevents storage exhaustion: without this, a single key
    /// could pin up to Kestrel's body-size limit (~30 MB) in the SQLite
    /// state DB per entry, and an unauthenticated /webhooks/* caller could
    /// fill the table at attacker-controlled cardinality.
    /// </summary>
    public const int MaxCachedResponseBytes = 256 * 1024;

    /// <summary>
    /// TTL applied to every cached response. The spec calls out a 24-hour
    /// window; this is currently a single global value with no per-route
    /// override (a follow-up could add one if a real use case appears).
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    public static void Use(IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!IsMutatingMethod(ctx.Request.Method))
            {
                await next();
                return;
            }
            if (!ctx.Request.Headers.TryGetValue(HeaderName, out var keyValues))
            {
                await next();
                return;
            }
            var key = keyValues.ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                await next();
                return;
            }
            if (key.Length > MaxKeyLength)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    $"{{\"error\":\"{HeaderName} must be <= {MaxKeyLength} chars\"}}");
                return;
            }

            var store = ctx.RequestServices.GetService<IIdempotencyStore>();
            if (store is null)
            {
                // Store not registered — degrade to passthrough so a misconfigured
                // deployment never silently drops mutations.
                await next();
                return;
            }

            ctx.Request.EnableBuffering();
            // Scope the dedupe by (method, path, body) so a key reused against a
            // different endpoint with a coincidentally identical body (e.g. two
            // empty-body mutations like DELETE /workitems/{id} and POST
            // /workitems/{id}/retry) is treated as Conflict rather than silently
            // replaying the first endpoint's response and skipping the second
            // endpoint's mutation. RFC-style idempotency keys are intended to
            // make a SINGLE logical operation safe to retry — they are not a
            // cross-endpoint replay cache.
            var bodyHash = await ComputeBodyHashAsync(
                ctx.Request, ctx.Request.Method, ctx.Request.Path.ToString());

            var lookup = await store.LookupAsync(key, bodyHash, DateTimeOffset.UtcNow, ctx.RequestAborted);
            switch (lookup.Outcome)
            {
                case IdempotencyLookupOutcome.Hit:
                    var hit = lookup.Entry!;
                    ctx.Response.StatusCode = hit.ResponseStatus;
                    ctx.Response.ContentType = hit.ResponseContentType;
                    ctx.Response.Headers["Idempotent-Replayed"] = "true";
                    await ctx.Response.Body.WriteAsync(hit.ResponseBody, ctx.RequestAborted);
                    return;
                case IdempotencyLookupOutcome.Conflict:
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(
                        $"{{\"error\":\"{HeaderName} reused with a different request body\"}}");
                    return;
                case IdempotencyLookupOutcome.Miss:
                    break;
            }

            // Capture downstream response so we can persist it on success.
            // The finally block is the single restore point for ctx.Response.Body
            // so the invariant is: "the capture stream is in place from the line
            // below until the finally executes."
            var originalBody = ctx.Response.Body;
            using var capture = new MemoryStream();
            ctx.Response.Body = capture;
            try
            {
                await next();
                capture.Position = 0;
                var bytes = capture.ToArray();

                // Cache only 2xx outcomes. 4xx replies often reflect transient
                // state (a 404 because the resource has not been created YET, a
                // 409 because the item is in a state that cannot accept the
                // mutation right now) and caching them for 24 hours would lock
                // a retry into the failure even after the underlying state
                // changes. 5xx are always skipped — the client should be allowed
                // to retry against a recovered backend.
                //
                // Bytes.Length cap prevents a single oversized response from
                // dominating storage (see <see cref="MaxCachedResponseBytes"/>).
                var status = ctx.Response.StatusCode;
                var shouldCache = status >= 200 && status < 300;
                if (shouldCache && bytes.Length <= MaxCachedResponseBytes)
                {
                    var entry = new IdempotencyEntry(
                        Key: key,
                        BodyHash: bodyHash,
                        ResponseStatus: status,
                        ResponseBody: bytes,
                        ResponseContentType: ctx.Response.ContentType ?? "application/json",
                        ExpiresAt: DateTimeOffset.UtcNow + DefaultTtl);
                    await store.PutAsync(entry, ctx.RequestAborted);
                }
                if (bytes.Length > 0)
                    await originalBody.WriteAsync(bytes, ctx.RequestAborted);
            }
            finally
            {
                ctx.Response.Body = originalBody;
            }
        });
    }

    private static bool IsMutatingMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static async Task<string> ComputeBodyHashAsync(HttpRequest request, string method, string path)
    {
        request.Body.Position = 0;
        using var sha = SHA256.Create();

        // Mix method + path into the digest BEFORE the body, newline-separated
        // (HTTP methods and paths contain no embedded newlines), so an
        // empty-body DELETE /a and an empty-body POST /b can never collide.
        // A request body that begins with "POST\n/a\n" cannot masquerade as a
        // different request either because the prefix is consumed before the
        // body bytes are fed in — the digest sees "POST\n/b\nPOST\n/a\n…",
        // not "POST\n/a\n…".
        var prefix = Encoding.UTF8.GetBytes(
            $"{method.ToUpperInvariant()}\n{path}\n");
        sha.TransformBlock(prefix, 0, prefix.Length, null, 0);

        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        request.Body.Position = 0;

        // Empty body still produces a stable hash (SHA-256 of "METHOD\nPATH\n")
        // so empty-payload mutations dedupe correctly across replays of the
        // SAME endpoint without colliding across DIFFERENT endpoints.
        return ToHex(sha.Hash ?? []);
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
