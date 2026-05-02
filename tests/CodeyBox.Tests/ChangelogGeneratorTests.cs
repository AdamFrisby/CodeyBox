using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeChangelogGenerator"/>.
/// Uses a fake HTTP message handler to avoid real API calls.
/// </summary>
public sealed class ChangelogGeneratorTests
{
    private static ChangelogOptions DefaultOpts => new()
    {
        Enabled = true,
        GeneratorAgent = "claude",
        GeneratorModelId = "claude-opus-4-7",
        ChangelogPath = "CHANGELOG.md",
        SectionHeaderFormat = "## [{tag}] - {date:yyyy-MM-dd}",
    };

    private static ClaudeChangelogGenerator Build(string? apiResponse = null, int statusCode = 200)
    {
        var handler = new FakeHttpHandler(apiResponse ?? BuildAnthropicResponse("""
            ## [v1.3.0] - 2026-05-02

            ### Added
            - New audit-replay timeline UI ([#16])

            ### Fixed
            - Race condition in queue drain ([#17])
            """), statusCode);
        var factory = new SingleClientFactory(handler);
        return new ClaudeChangelogGenerator(factory, NullLogger<ClaudeChangelogGenerator>.Instance, DefaultOpts);
    }

    [Fact]
    public async Task GenerateAsync_EmptyPrList_ReturnsPlaceholder()
    {
        var gen = Build();
        var result = await gen.GenerateAsync(new ChangelogRequest
        {
            ProjectId = new ProjectId("test"),
            FromTag = "v1.2.0",
            ToTag = "v1.3.0",
            PullRequests = [],
        }, CancellationToken.None);

        Assert.Equal("v1.3.0", result.ToTag);
        Assert.Contains("no pull requests found", result.Markdown);
        Assert.Empty(result.CategoryToPrNumbers);
    }

    [Fact]
    public async Task GenerateAsync_CallsLlm_ReturnsMarkdown()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", "sk-ant-test-key");
        try
        {
            var gen = Build();
            var result = await gen.GenerateAsync(new ChangelogRequest
            {
                ProjectId = new ProjectId("test"),
                FromTag = "v1.2.0",
                ToTag = "v1.3.0",
                PullRequests = [new(16, "Audit timeline UI", "Adds a timeline view", "2026-05-01", [], [])],
            }, CancellationToken.None);

            Assert.Equal("v1.3.0", result.ToTag);
            Assert.Contains("v1.3.0", result.Markdown);
            Assert.NotEmpty(result.Markdown);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", null);
        }
    }

    [Fact]
    public async Task GenerateAsync_ParsesCategories_FromMarkdown()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", "sk-ant-test-key");
        try
        {
            var gen = Build();
            var result = await gen.GenerateAsync(new ChangelogRequest
            {
                ProjectId = new ProjectId("test"),
                FromTag = "v1.2.0",
                ToTag = "v1.3.0",
                PullRequests = [
                    new(16, "Timeline UI", "body", "2026-05-01", [], []),
                    new(17, "Queue race fix", "body", "2026-05-01", [], []),
                ],
            }, CancellationToken.None);

            Assert.True(result.CategoryToPrNumbers.ContainsKey("Added")
                || result.CategoryToPrNumbers.ContainsKey("Fixed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", null);
        }
    }

    [Fact]
    public async Task GenerateAsync_RedactsPrBodies_BeforeSending()
    {
        string? capturedBody = null;
        var handler = new CapturingFakeHandler(
            BuildAnthropicResponse("## [v1.3.0]\n### Added\n- stuff ([#1])\n"),
            body => capturedBody = body);
        var factory = new SingleClientFactory(handler);
        var gen = new ClaudeChangelogGenerator(factory, NullLogger<ClaudeChangelogGenerator>.Instance, DefaultOpts);

        Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", "sk-ant-test-key");
        try
        {
            await gen.GenerateAsync(new ChangelogRequest
            {
                ProjectId = new ProjectId("test"),
                FromTag = "v1.2.0",
                ToTag = "v1.3.0",
                PullRequests = [new(1, "title", "body contains ghp_abcDEF123 token", "2026-05-01", [], [])],
            }, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", null);
        }

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("ghp_abcDEF123", capturedBody);
        Assert.Contains("***", capturedBody);
    }

    [Fact]
    public async Task GenerateAsync_NoApiKey_Throws()
    {
        var prev = Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY");
        Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", null);
        try
        {
            var gen = Build();
            await Assert.ThrowsAsync<InvalidOperationException>(() => gen.GenerateAsync(new ChangelogRequest
            {
                ProjectId = new ProjectId("test"),
                FromTag = "v1.2.0",
                ToTag = "v1.3.0",
                PullRequests = [new(1, "title", "body", "2026-05-01", [], [])],
            }, CancellationToken.None));
        }
        finally
        {
            if (prev is not null)
                Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", prev);
        }
    }

    [Fact]
    public async Task GenerateAsync_ApiError_Throws()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", "sk-ant-test-key");
        try
        {
            var gen = Build(statusCode: 500);
            await Assert.ThrowsAsync<HttpRequestException>(() => gen.GenerateAsync(new ChangelogRequest
            {
                ProjectId = new ProjectId("test"),
                FromTag = "v1.2.0",
                ToTag = "v1.3.0",
                PullRequests = [new(1, "title", "body", "2026-05-01", [], [])],
            }, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", null);
        }
    }

    [Fact]
    public async Task GenerateAsync_RequestShape_ContainsPrTitleAndNumber()
    {
        string? capturedBody = null;
        var handler = new CapturingFakeHandler(
            BuildAnthropicResponse("## [v1.3.0]\n### Added\n- stuff ([#42])\n"),
            body => capturedBody = body);
        var factory = new SingleClientFactory(handler);
        var gen = new ClaudeChangelogGenerator(factory, NullLogger<ClaudeChangelogGenerator>.Instance, DefaultOpts);

        Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", "sk-ant-test-key");
        try
        {
            await gen.GenerateAsync(new ChangelogRequest
            {
                ProjectId = new ProjectId("test"),
                FromTag = "v1.2.0",
                ToTag = "v1.3.0",
                PullRequests = [new(42, "My special feature", "body", "2026-05-01", [], [])],
            }, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY", null);
        }

        Assert.NotNull(capturedBody);
        Assert.Contains("#42", capturedBody);
        Assert.Contains("My special feature", capturedBody);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildAnthropicResponse(string text) => JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        content = new[] { new { type = "text", text } },
        model = "claude-opus-4-7",
        stop_reason = "end_turn",
        usage = new { input_tokens = 100, output_tokens = 50 },
    });
}

// ── Fake HTTP infrastructure ──────────────────────────────────────────────────

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly int _statusCode;

    public FakeHttpHandler(string responseBody, int statusCode = 200)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(resp);
    }
}

internal sealed class CapturingFakeHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly Action<string> _capture;

    public CapturingFakeHandler(string responseBody, Action<string> capture)
    {
        _responseBody = responseBody;
        _capture = capture;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            _capture(await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
