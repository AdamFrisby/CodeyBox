using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the auto-sentinel fan-out path in <see cref="GeminiQuotaProbe"/>.
/// Verifies the bucket-list iteration, the 200/429/mixed aggregation rules,
/// the Retry-After-derived reset, and the (token, "auto") cache key.
/// </summary>
public sealed class GeminiQuotaProbeAutoFanOutTests
{
    private static readonly AgentMembership AutoMember = new()
    {
        Agent = AgentKind.Gemini,
        Billing = AgentBilling.Subscription,
        ModelId = GeminiKnownModels.AutoSentinel,
        QualityScore = 95,
        ReasoningMode = "high",
    };

    private static readonly AgentMembership FixedMember = new()
    {
        Agent = AgentKind.Gemini,
        Billing = AgentBilling.Subscription,
        ModelId = "gemini-2.5-pro",
        QualityScore = 95,
        ReasoningMode = "high",
    };

    private static GeminiQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string token = "test-token",
        TimeSpan? cacheTtl = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new GeminiQuotaProbe(
            factory,
            () => new AgentQuotaCredentials(token),
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<GeminiQuotaProbe>.Instance);
    }

    [Fact]
    public async Task AutoMember_AllOk_Reports100PctAndRoutesViaFirstSuccess()
    {
        var handler = new GeminiAutoFanOutHandler(modelStatus: _ => HttpStatusCode.OK);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);

        Assert.Equal(100, snapshot.AvailablePct);
        Assert.NotNull(snapshot.Notes);
        Assert.StartsWith("auto routed via ", snapshot.Notes);
        // Endpoint targeted is the OAuth Code Assist generateContent path.
        Assert.All(handler.Requests, r =>
            Assert.Equal(new Uri(GeminiQuotaProbe.GenerateContentEndpoint), r.Uri));
        // One call per known model.
        Assert.Equal(GeminiKnownModels.All.Count, handler.Requests.Count);
    }

    [Fact]
    public async Task AutoMember_AllRateLimited_Reports0PctWithEarliestReset()
    {
        var earliest = new DateTimeOffset(2026, 5, 17, 11, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 5, 17, 14, 0, 0, TimeSpan.Zero);

        var handler = new GeminiAutoFanOutHandler(
            modelStatus: _ => (HttpStatusCode)429,
            // Vary Retry-After so the aggregation has to pick the min.
            retryAfterFor: modelId => modelId == "gemini-2.5-pro" ? later : earliest);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);

        Assert.Equal(0, snapshot.AvailablePct);
        Assert.NotNull(snapshot.ResetAt);
        // Earliest reset across all rate-limited models — within a small clock-skew margin.
        var skew = (snapshot.ResetAt!.Value - earliest).Duration();
        Assert.True(skew < TimeSpan.FromSeconds(5),
            $"expected reset close to {earliest:o}, got {snapshot.ResetAt:o}");
        Assert.Contains("rate-limited", snapshot.Notes!);
    }

    [Fact]
    public async Task AutoMember_Mixed_Still100Pct()
    {
        // Two of the known models 200, two 429 — auto-routing will land on
        // whichever is up, so the aggregate stays "available".
        var handler = new GeminiAutoFanOutHandler(modelStatus: modelId =>
            modelId.Contains("pro") ? HttpStatusCode.OK : (HttpStatusCode)429);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);

        Assert.Equal(100, snapshot.AvailablePct);
        Assert.StartsWith("auto routed via ", snapshot.Notes!);
        // The PerModel map records the actual per-model state for diagnostics.
        Assert.NotEmpty(snapshot.PerModel);
        Assert.Equal(100, snapshot.PerModel["gemini-2.5-pro"].AvailablePct);
        Assert.Equal(0, snapshot.PerModel["gemini-2.5-flash"].AvailablePct);
    }

    [Fact]
    public async Task AutoMember_CachedUnderAutoKey_DoesNotReprobeWithinTtl()
    {
        var handler = new GeminiAutoFanOutHandler(modelStatus: _ => HttpStatusCode.OK);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));
        await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);
        var firstCalls = handler.Requests.Count;
        await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);

        // Second call within TTL must be a cache hit — no additional HTTP traffic.
        Assert.Equal(firstCalls, handler.Requests.Count);
    }

    [Fact]
    public async Task FixedMember_DoesNotInvokeAutoFanOut_UsesUsageEndpoint()
    {
        // For a non-auto member, the existing retrieveUserQuota path applies —
        // a single HTTP call to that endpoint, not the fan-out generateContent.
        var handler = new GeminiAutoFanOutHandler(
            modelStatus: _ => HttpStatusCode.OK,
            usageBody: """{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":0.8,"resetTime":"2026-05-17T12:00:00Z","tokenType":"REQUESTS"}]}""");

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(FixedMember, CancellationToken.None);

        Assert.Equal(80, snapshot.AvailablePct);
        // Single request to the retrieveUserQuota endpoint, not generateContent.
        var req = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(GeminiQuotaProbe.UsageEndpoint), req.Uri);
    }

    [Fact]
    public async Task AutoAndFixed_CacheKeysAreIndependent()
    {
        var handler = new GeminiAutoFanOutHandler(
            modelStatus: _ => HttpStatusCode.OK,
            usageBody: """{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":1.0,"tokenType":"REQUESTS"}]}""");

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));

        var autoSnap = await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);
        var fixedSnap = await probe.GetAvailabilityAsync(FixedMember, CancellationToken.None);

        Assert.Equal(100, autoSnap.AvailablePct);
        Assert.Equal(100, fixedSnap.AvailablePct);

        // The fixed-member fetch hit the usage endpoint; the auto fetch hit
        // generateContent four times. Verify both cache entries coexist by
        // calling each again with no extra HTTP traffic.
        var totalAfterPopulate = handler.Requests.Count;
        await probe.GetAvailabilityAsync(AutoMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(FixedMember, CancellationToken.None);
        Assert.Equal(totalAfterPopulate, handler.Requests.Count);
    }

    /// <summary>
    /// Routes by request URL: the retrieveUserQuota POST is answered with
    /// <c>usageBody</c>; per-model generateContent POSTs are answered using
    /// <c>modelStatus</c> applied to the parsed model id from the request body.
    /// </summary>
    private sealed class GeminiAutoFanOutHandler : HttpMessageHandler
    {
        public record CapturedRequest(Uri Uri, string Body);
        public List<CapturedRequest> Requests { get; } = [];

        private readonly Func<string, HttpStatusCode> _modelStatus;
        private readonly Func<string, DateTimeOffset>? _retryAfterFor;
        private readonly string _usageBody;

        public GeminiAutoFanOutHandler(
            Func<string, HttpStatusCode> modelStatus,
            Func<string, DateTimeOffset>? retryAfterFor = null,
            string usageBody = """{"buckets":[]}""")
        {
            _modelStatus = modelStatus;
            _retryAfterFor = retryAfterFor;
            _usageBody = usageBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.RequestUri!, body));

            if (request.RequestUri == new Uri(GeminiQuotaProbe.UsageEndpoint))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_usageBody),
                };
            }

            // generateContent — pull the model id from the body.
            var modelId = ParseModelId(body);
            var status = _modelStatus(modelId);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? "{}" : "{\"error\":\"quota\"}"),
            };
            if ((int)status == 429 && _retryAfterFor is not null)
            {
                var when = _retryAfterFor(modelId);
                var delta = when - DateTimeOffset.UtcNow;
                if (delta < TimeSpan.Zero) delta = TimeSpan.FromSeconds(1);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            }
            return response;
        }

        private static string ParseModelId(string body)
        {
            // Body looks like: {"model":"models/gemini-2.5-pro","request":{...}}
            const string needle = "\"model\":\"models/";
            var idx = body.IndexOf(needle);
            if (idx < 0) return "";
            var start = idx + needle.Length;
            var end = body.IndexOf('"', start);
            return end < 0 ? "" : body[start..end];
        }
    }
}
