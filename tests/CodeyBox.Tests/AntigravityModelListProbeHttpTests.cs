using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-path coverage for <see cref="AntigravityModelListProbe.GetModelListAsync"/>:
/// no-token early return, summary-success, summary→legacy fall-through, and
/// the "both fail → summary failure wins" tie-break. The parser shape itself
/// is covered separately by
/// <see cref="AntigravityModelListProbeParserTests"/>.
/// </summary>
public sealed class AntigravityModelListProbeHttpTests
{
    private static AntigravityModelListProbe BuildProbe(
        HttpMessageHandler handler,
        string? token = "agy-test-token")
    {
        var factory = new QuotaFakeHttpClientFactory("agent-modellist", handler);
        return new AntigravityModelListProbe(
            factory,
            () => token,
            NullLogger<AntigravityModelListProbe>.Instance);
    }

    [Fact]
    public void Kind_IsAntigravity()
    {
        var probe = BuildProbe(new ModelListRouter());
        Assert.Equal(AgentKind.Antigravity, probe.Kind);
    }

    [Fact]
    public async Task GetModelListAsync_NoToken_ReturnsFailed_DoesNotIssueHttp()
    {
        // Without a token the probe must NOT hit the gateway — there's no
        // Authorization header to send and the request would 401 anyway,
        // wasting startup-validator budget.
        var handler = new ModelListRouter();
        var probe = BuildProbe(handler, token: null);

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("no credential configured", result.FailureReason);
        Assert.Empty(result.ModelIds);
        Assert.Equal(0, handler.SummaryRequests);
        Assert.Equal(0, handler.QuotaRequests);
    }

    [Fact]
    public async Task GetModelListAsync_SummarySucceeds_DoesNotCallLegacyEndpoint()
    {
        // Happy path: :retrieveUserQuotaSummary returns models → probe returns
        // them and skips the legacy :retrieveUserQuota fallback entirely.
        var summaryBody = """{"perModel":[{"modelId":"gemini-3.5-flash-high"},{"modelId":"claude-opus-4-6-thinking"}]}""";
        var handler = new ModelListRouter(
            summaryStatus: HttpStatusCode.OK,
            summaryBody: summaryBody);

        var probe = BuildProbe(handler);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("gemini-3.5-flash-high", result.ModelIds);
        Assert.Contains("claude-opus-4-6-thinking", result.ModelIds);
        Assert.Equal(1, handler.SummaryRequests);
        Assert.Equal(0, handler.QuotaRequests);
    }

    [Fact]
    public async Task GetModelListAsync_SummaryFails_FallsThroughToLegacyEndpoint()
    {
        // Summary returns non-2xx (gateway hasn't rolled out the new endpoint
        // to this tier, etc.) → probe must try :retrieveUserQuota and surface
        // its models when that endpoint succeeds.
        var legacyBody = """{"buckets":[{"modelId":"gpt-oss-120b-medium"}]}""";
        var handler = new ModelListRouter(
            summaryStatus: HttpStatusCode.NotFound,
            summaryBody: "{}",
            quotaStatus: HttpStatusCode.OK,
            quotaBody: legacyBody);

        var probe = BuildProbe(handler);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("gpt-oss-120b-medium", result.ModelIds);
        Assert.Equal(1, handler.SummaryRequests);
        Assert.Equal(1, handler.QuotaRequests);
    }

    [Fact]
    public async Task GetModelListAsync_BothFail_ReturnsSummaryFailureAsTieBreak()
    {
        // Both endpoints fail with different reasons; the contract is "return
        // the summary failure" (the preferred endpoint's failure is the
        // operator-actionable one — surface that, not the legacy's). Flipping
        // the tie-break in source would silently regress to the legacy reason.
        var handler = new ModelListRouter(
            summaryStatus: HttpStatusCode.ServiceUnavailable,
            quotaStatus: HttpStatusCode.Forbidden);

        var probe = BuildProbe(handler);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Equal($"HTTP {(int)HttpStatusCode.ServiceUnavailable}", result.FailureReason);
        Assert.Empty(result.ModelIds);
        Assert.Equal(1, handler.SummaryRequests);
        Assert.Equal(1, handler.QuotaRequests);
    }

    [Fact]
    public async Task GetModelListAsync_SummaryEmpty_LegacyHasModels_PrefersLegacy()
    {
        // Summary returns 200 but no models in the response → counts as a
        // failure ("no models in response") and the probe falls through to
        // the legacy endpoint, returning its models. Guards against a future
        // change that mistakenly accepts a model-less 200 as a success.
        var handler = new ModelListRouter(
            summaryStatus: HttpStatusCode.OK,
            summaryBody: "{}",
            quotaStatus: HttpStatusCode.OK,
            quotaBody: """{"perModel":[{"modelId":"claude-sonnet-4-6-thinking"}]}""");

        var probe = BuildProbe(handler);
        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("claude-sonnet-4-6-thinking", result.ModelIds);
        Assert.Equal(1, handler.SummaryRequests);
        Assert.Equal(1, handler.QuotaRequests);
    }

    [Fact]
    public async Task GetModelListAsync_RequestCarriesBearerToken()
    {
        // The Authorization header must be Bearer <token>; the gateway 401s
        // anything else. Silent regression here would break model discovery
        // for every operator at startup.
        var handler = new ModelListRouter(
            summaryStatus: HttpStatusCode.OK,
            summaryBody: """{"perModel":[{"modelId":"gemini-3.5-flash-high"}]}""");

        var probe = BuildProbe(handler, token: "ya29.live-test-token");
        _ = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(handler.LastAuthorization);
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        Assert.Equal("ya29.live-test-token", handler.LastAuthorization.Parameter);
    }

    private sealed class ModelListRouter : HttpMessageHandler
    {
        private readonly HttpStatusCode _summaryStatus;
        private readonly string _summaryBody;
        private readonly HttpStatusCode _quotaStatus;
        private readonly string _quotaBody;

        public int SummaryRequests { get; private set; }
        public int QuotaRequests { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        public ModelListRouter(
            HttpStatusCode summaryStatus = HttpStatusCode.NotFound,
            string summaryBody = "",
            HttpStatusCode quotaStatus = HttpStatusCode.NotFound,
            string quotaBody = "")
        {
            _summaryStatus = summaryStatus;
            _summaryBody = summaryBody;
            _quotaStatus = quotaStatus;
            _quotaBody = quotaBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.Authorization;
            var uri = request.RequestUri!.ToString();
            if (uri == AntigravityModelListProbe.SummaryEndpoint)
            {
                SummaryRequests++;
                return Task.FromResult(new HttpResponseMessage(_summaryStatus)
                {
                    Content = new StringContent(_summaryBody),
                });
            }
            if (uri == AntigravityModelListProbe.QuotaEndpoint)
            {
                QuotaRequests++;
                return Task.FromResult(new HttpResponseMessage(_quotaStatus)
                {
                    Content = new StringContent(_quotaBody),
                });
            }
            throw new InvalidOperationException($"Unexpected endpoint {uri}");
        }
    }
}
