using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CodexSmokeProbe"/> using a fake HTTP handler.
/// </summary>
public sealed class CodexSmokeProbeTests
{
    private static AgentCredential ValidCred(string key = "sk-test") =>
        new(AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Codex,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    private static CodexSmokeProbe BuildProbe(HttpMessageHandler handler) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<CodexSmokeProbe>.Instance);

    [Fact]
    public async Task Probe_PostsToCompletionsEndpoint()
    {
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        await BuildProbe(handler).SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.Equal(new Uri(CodexSmokeProbe.CompletionsEndpoint), captured);
    }

    [Fact]
    public async Task ValidCred_SendsBearerToken()
    {
        string? auth = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            auth = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(ValidCred("my-key"), CancellationToken.None);
        Assert.Equal("Bearer my-key", auth);
    }

    [Fact]
    public async Task Http200_ReturnsOk()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Http401_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http403_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http500_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task NetworkException_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeThrowingHandler(new HttpRequestException("net error")))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task Cancellation_ReturnsFail_Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(new SmokeHangingHandler())
            .SmokeTestAsync(ValidCred(), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.FailureReason);
    }

    [Fact]
    public async Task NoToken_ReturnsFail_WithoutHttpCall()
    {
        int calls = 0;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(EmptyCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(0, calls);
    }
}
