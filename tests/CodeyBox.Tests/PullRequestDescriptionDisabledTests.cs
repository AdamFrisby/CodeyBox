using System.Net;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when <see cref="PrDescriptionOptions.Enabled"/> is false,
/// the generator is never called and the static template is used directly.
/// </summary>
public sealed class PullRequestDescriptionDisabledTests
{
    private static readonly GitHubUpstreamOptions DisabledOpts = new()
    {
        Owner = "myorg",
        Repository = "myrepo",
        Token = "test-token-not-a-real-pat",
        PrDescription = new PrDescriptionOptions { Enabled = false },
    };

    private static readonly UpstreamCompletionRequest SampleRequest = new()
    {
        RepositoryId = "repo-id",
        WorkItemId = new WorkItemId(Guid.Parse("00000000-0000-0000-0000-000000000020")),
        ProjectId = new ProjectId("test-project"),
        WorkBranch = "codeybox/disabled1",
        BaseBranch = "main",
        MergeSha = "deadbeef",
        Title = "Test disabled",
        Description = "Static description only",
    };

    private static HttpResponseMessage PrCreatedResponse(int number, string htmlUrl) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""{"number":{{number}},"html_url":"{{htmlUrl}}"}""",
                Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task CompleteAsync_Disabled_GeneratorNeverCalled()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(1, "https://github.com/myorg/myrepo/pull/1"));

        var trackingGenerator = new TrackingDescriptionGenerator();
        var gitHost = new FakeGitHost();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var remote = new GitHubUpstreamRemote(
            gitHost, factory, NullLogger<GitHubUpstreamRemote>.Instance,
            DisabledOpts, timings: null, descriptionGenerator: trackingGenerator);

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.False(trackingGenerator.WasCalled, "Generator must not be called when Enabled=false");
    }

    [Fact]
    public async Task CompleteAsync_Disabled_UsesStaticTemplate()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(PrCreatedResponse(2, "https://github.com/myorg/myrepo/pull/2"));

        var gitHost = new FakeGitHost();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var remote = new GitHubUpstreamRemote(
            gitHost, factory, NullLogger<GitHubUpstreamRemote>.Instance,
            DisabledOpts, timings: null, descriptionGenerator: new TrackingDescriptionGenerator());

        await remote.CompleteAsync(SampleRequest, CancellationToken.None);

        Assert.Contains("Static description only", handler.RequestBodies[0]);
    }
}

internal sealed class TrackingDescriptionGenerator : IPullRequestDescriptionGenerator
{
    public bool WasCalled { get; private set; }

    public Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct)
    {
        WasCalled = true;
        return Task.FromResult("LLM-generated text");
    }
}
