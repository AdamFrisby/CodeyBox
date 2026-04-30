using System.Net;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

// ── HTTP handler fakes ────────────────────────────────────────────────────────

internal sealed class SmokeFakeHttpClientFactory : IHttpClientFactory
{
    private readonly string _clientName;
    private readonly HttpMessageHandler _handler;

    public SmokeFakeHttpClientFactory(string clientName, HttpMessageHandler handler)
    {
        _clientName = clientName;
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        if (name != _clientName)
            throw new InvalidOperationException($"Unexpected client name '{name}'; expected '{_clientName}'");
        return new HttpClient(_handler, disposeHandler: false);
    }
}

internal sealed class SmokeCapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    private readonly Action<HttpRequestMessage> _capture;

    public SmokeCapturingHandler(HttpStatusCode status, string body, Action<HttpRequestMessage> capture)
    {
        _status = status;
        _body = body;
        _capture = capture;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _capture(request);
        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        });
    }
}

internal sealed class SmokeThrowingHandler : HttpMessageHandler
{
    private readonly Exception _ex;
    public SmokeThrowingHandler(Exception ex) { _ex = ex; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromException<HttpResponseMessage>(_ex);
}

internal sealed class SmokeHangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct);
    }
}

// ── Shared probe fakes ────────────────────────────────────────────────────────

/// <summary>
/// Programmable smoke probe for tests. Returns Ok or Fail based on
/// <see cref="ShouldPass"/>, which can be changed between calls.
/// </summary>
internal sealed class FakeSmokeProbe : IAgentSmokeProbe
{
    public AgentKind Kind { get; }
    public bool ShouldPass { get; set; }
    public int CallCount { get; private set; }

    public FakeSmokeProbe(AgentKind kind, bool shouldPass = true)
    {
        Kind = kind;
        ShouldPass = shouldPass;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        CallCount++;
        var result = ShouldPass
            ? new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10))
            : new AgentSmokeResult(false, "auth", TimeSpan.FromMilliseconds(10));
        return Task.FromResult(result);
    }
}

/// <summary>
/// Credential provider that always returns the same credential for every agent.
/// </summary>
internal sealed class ConstantCredentialProvider : ICredentialProvider
{
    private readonly AgentCredential _credential;
    public ConstantCredentialProvider(AgentCredential credential) => _credential = credential;

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(_credential);
}

/// <summary>
/// Builds a <see cref="CredentialSmokeGate"/> wired to a single
/// <see cref="FakeSmokeProbe"/> and a <see cref="ConstantCredentialProvider"/>.
/// </summary>
internal static class SmokeGateFactory
{
    private static readonly AgentCredential AnyCred = new(
        AgentKind.Claude,
        new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "test-key" },
        new Dictionary<string, string>());

    public static (CredentialSmokeGate Gate, FakeSmokeProbe Probe) Build(
        bool probePass = true,
        bool enabled = true,
        ICredentialProvider? credentials = null)
    {
        var probe = new FakeSmokeProbe(AgentKind.Claude, probePass);
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        var opts = new SmokeOptions { Enabled = enabled };
        var gate = new CredentialSmokeGate(
            credentials ?? new ConstantCredentialProvider(AnyCred),
            [probe],
            cache,
            opts,
            NullLogger<CredentialSmokeGate>.Instance);
        return (gate, probe);
    }
}
