using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

public sealed class MultipassAgentOutputHttpIngestSessionTests
{
    [Fact]
    public async Task Post_RejectsWrongTokenAndRunId()
    {
        var chunks = new List<string>();
        await using var session = await StartAsync(chunks.Add);
        using var client = new HttpClient();

        var wrongToken = new string('A', session.Token.Length);
        var wrongTokenStatus = await PostAsync(client, session, "run-x", "stdout", 0, wrongToken, "blocked");
        var streamTokenForExitStatus = await PostAsync(client, session, "run-x", "exit", 0, session.Token, "0\n");
        var wrongRunStatus = await PostAsync(client, session, "run-y", "stdout", 0, session.Token, "blocked");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongTokenStatus);
        Assert.Equal(HttpStatusCode.Unauthorized, streamTokenForExitStatus);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRunStatus);
        Assert.Empty(chunks);
        Assert.Equal("", session.Stdout);
        Assert.False(session.TryGetExitCode(out _));
    }

    [Fact]
    public async Task Post_AppendsOnlyAuthorizedRunOutput()
    {
        var chunks = new List<string>();
        await using var session = await StartAsync(chunks.Add);
        using var client = new HttpClient();

        var readyStatus = await PostAsync(client, session, "run-x", "ready", 0, session.Token, "");
        var stdoutStatus = await PostAsync(client, session, "run-x", "stdout", 0, session.Token, "hello\n");

        Assert.Equal(HttpStatusCode.NoContent, readyStatus);
        Assert.Equal(HttpStatusCode.OK, stdoutStatus);
        Assert.Equal("hello\n", session.Stdout);
        Assert.Equal(["hello\n"], chunks);
        Assert.True(session.ReceivedAgentBytes);
    }

    [Fact]
    public async Task Post_ExitStreamUsesSeparateTokenAndRecordsExitCode()
    {
        var chunks = new List<string>();
        await using var session = await StartAsync(chunks.Add);
        using var client = new HttpClient();

        var status = await PostAsync(client, session, "run-x", "exit", 0, session.ExitToken, "7\n");
        var duplicate = await PostAsync(client, session, "run-x", "exit", 0, session.ExitToken, "7\n");
        var conflicting = await PostAsync(client, session, "run-x", "exit", 0, session.ExitToken, "8\n");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, duplicate);
        Assert.Equal(HttpStatusCode.Conflict, conflicting);
        Assert.True(session.TryGetExitCode(out var exitCode));
        Assert.Equal(7, exitCode);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Post_EnforcesPerStreamOrderingAndIgnoresDuplicateRetries()
    {
        var chunks = new List<string>();
        await using var session = await StartAsync(chunks.Add);
        using var client = new HttpClient();

        var outOfOrder = await PostAsync(client, session, "run-x", "stdout", 1, session.Token, "late");
        var first = await PostAsync(client, session, "run-x", "stdout", 0, session.Token, "a");
        var duplicate = await PostAsync(client, session, "run-x", "stdout", 0, session.Token, "duplicate");
        var second = await PostAsync(client, session, "run-x", "stdout", 1, session.Token, "b");

        Assert.Equal(HttpStatusCode.Conflict, outOfOrder);
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, duplicate);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Equal("ab", session.Stdout);
        Assert.Equal(["a", "b"], chunks);
    }

    [Fact]
    public async Task Post_RejectsOversizedChunkWithoutPartialAppend()
    {
        var chunks = new List<string>();
        await using var session = await StartAsync(chunks.Add);
        using var client = new HttpClient();
        var oversized = new string('x', MultipassAgentOutputHttpIngestSession.MaxChunkBytes + 1);

        var status = await PostAsync(client, session, "run-x", "stdout", 0, session.Token, oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("", session.Stdout);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Post_ReturnsTooManyRequestsWhenRateLimitExceededAndResetsWindow()
    {
        var chunks = new List<string>();
        await using var session = await StartAsync(chunks.Add);
        using var client = new HttpClient();

        SetRateWindow(
            session,
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
            MultipassAgentOutputHttpIngestSession.MaxRequestsPerSecond);
        var throttled = await PostAsync(client, session, "run-x", "ready", 0, session.Token, "");

        SetRateWindow(
            session,
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2),
            MultipassAgentOutputHttpIngestSession.MaxRequestsPerSecond);
        var afterReset = await PostAsync(client, session, "run-x", "ready", 0, session.Token, "");

        Assert.Equal((HttpStatusCode)429, throttled);
        Assert.Equal(HttpStatusCode.NoContent, afterReset);
        Assert.Empty(chunks);
    }

    private static async Task<MultipassAgentOutputHttpIngestSession> StartAsync(Action<string>? stdout)
        => await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            IPAddress.Loopback,
            "run-x",
            NullLogger.Instance,
            stdout,
            stderrChunkCallback: null,
            CancellationToken.None)
        ?? throw new InvalidOperationException("Failed to bind test ingest listener.");

    private static async Task<HttpStatusCode> PostAsync(
        HttpClient client,
        MultipassAgentOutputHttpIngestSession session,
        string runId,
        string stream,
        long seq,
        string token,
        string body)
    {
        using var request = NewPostRequest(session, runId, stream, seq, token);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static HttpRequestMessage NewPostRequest(
        MultipassAgentOutputHttpIngestSession session,
        string runId,
        string stream,
        long seq,
        string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{session.BaseUrl}/{Uri.EscapeDataString(runId)}/{Uri.EscapeDataString(stream)}/{seq}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return request;
    }

    private static void SetRateWindow(
        MultipassAgentOutputHttpIngestSession session,
        DateTimeOffset windowStart,
        int count)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MultipassAgentOutputHttpIngestSession)
            .GetField("_rateWindowStart", flags)!
            .SetValue(session, windowStart);
        typeof(MultipassAgentOutputHttpIngestSession)
            .GetField("_rateWindowCount", flags)!
            .SetValue(session, count);
    }

}
