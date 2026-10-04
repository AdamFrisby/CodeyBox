using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;

namespace CodeyBox.Tests;

/// <summary>
/// Protocol tests for <see cref="GitHubCheckRunsClient"/> against a real
/// loopback HTTP server: request serialization, response parsing, auth,
/// rate-limit (<c>Retry-After</c>), validation, transient mapping, headers,
/// and cancellation — over actual sockets, not handler stubs.
/// </summary>
public sealed class GitHubCheckRunsClientLoopbackTests
{
    private sealed class FixedTokenProvider(string token) : IGitHubTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(token);
    }

    private sealed class LoopbackClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("github-upstream", name);
            return client;
        }
    }

    private static (GitHubCheckRunsClient Client, HttpClient Http) BuildClient(
        LoopbackChecksServer server, string owner = "myorg", string repo = "myrepo")
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var factory = new LoopbackClientFactory(http);
        var client = new GitHubCheckRunsClient(
            factory, new FixedTokenProvider("loopback-test-token"), owner, repo,
            apiBaseUrl: server.BaseUri.ToString().TrimEnd('/'));
        return (client, http);
    }

    private static GitHubCreateCheckRunRequest CreateBody(string sha) =>
        new("codeybox-audit", sha, "completed", "ext-1",
            new GitHubCheckRunOutput("title", "summary"), "success",
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task CreateCheckRun_SerializesContract_AndParsesResponse()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(201, """{"id":987,"html_url":"https://github.com/myorg/myrepo/runs/987","status":"completed","conclusion":"success"}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            var run = await client.CreateCheckRunAsync(CreateBody(Sha));
            Assert.Equal(987, run.Id);
            Assert.Equal("completed", run.Status);
            Assert.Equal("success", run.Conclusion);
        }

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Contains("/repos/myorg/myrepo/check-runs", request.PathAndQuery, StringComparison.Ordinal);
        Assert.True(request.Headers.TryGetValue("Authorization", out var auth));
        Assert.Equal("token loopback-test-token", auth);
        Assert.True(request.Headers.TryGetValue("X-GitHub-Api-Version", out var version));
        Assert.Equal(GitHubCheckRunsClient.GitHubApiVersion, version);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("codeybox-audit", root.GetProperty("name").GetString());
        Assert.Equal(Sha, root.GetProperty("head_sha").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("success", root.GetProperty("conclusion").GetString());
        Assert.Equal("ext-1", root.GetProperty("external_id").GetString());
    }

    [Fact]
    public async Task Unauthorized_MapsToBlockedAuthError()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(401, """{"message":"Bad credentials"}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<AuditCheckAuthException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
            Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ForbiddenWithoutRateLimitSignal_MapsToBlockedAuthError()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(403, """{"message":"Resource not accessible by integration"}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<AuditCheckAuthException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
            Assert.Contains("checks:write", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TooManyRequests_HonorsRetryAfterHeader()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(429, """{"message":"API rate limit exceeded"}""",
            new Dictionary<string, string> { ["Retry-After"] = "7" });
        var (client, http) = BuildClient(server);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<AuditCheckRateLimitedException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
            Assert.NotNull(ex.RetryAfter);
            Assert.True(ex.RetryAfter.Value.TotalSeconds is >= 6 and <= 8);
        }
    }

    [Fact]
    public async Task ForbiddenWithRateLimitBody_MapsToRateLimited()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(403, """{"message":"You have exceeded a secondary rate limit"}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            await Assert.ThrowsAsync<AuditCheckRateLimitedException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
        }
    }

    [Fact]
    public async Task UnprocessableEntity_MapsToValidationError()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(422, """{"message":"Validation Failed","errors":[{"code":"custom"}]}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            await Assert.ThrowsAsync<AuditCheckValidationException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
        }
    }

    [Fact]
    public async Task ServerError_MapsToRetryableTransient()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(500, """{"message":"boom"}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            await Assert.ThrowsAsync<AuditCheckTransientException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
        }
    }

    [Fact]
    public async Task ListCheckRunsForRef_ParsesEnvelope_AndSendsCheckNameFilter()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(200, """{"total_count":2,"check_runs":[{"id":11,"external_id":"ext-1"},{"id":22,"external_id":"ext-2"}]}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            var runs = await client.ListCheckRunsForRefAsync(Sha, "codeybox-audit");
            Assert.Equal(2, runs.Count);
            Assert.Equal(11, runs[0].Id);
            Assert.Equal("ext-1", runs[0].ExternalId);
        }
        var request = Assert.Single(server.Requests);
        Assert.Contains($"/commits/{Sha}/check-runs", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("check_name=codeybox-audit", request.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateCheckRun_PatchesById()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueJson(200, """{"id":55,"status":"completed","conclusion":"failure"}""");
        var (client, http) = BuildClient(server);
        using (http)
        {
            var run = await client.UpdateCheckRunAsync(55, new GitHubUpdateCheckRunRequest(
                Status: "completed", Conclusion: "failure"));
            Assert.Equal(55, run.Id);
        }
        var request = Assert.Single(server.Requests);
        Assert.Equal("PATCH", request.Method);
        Assert.Contains("/check-runs/55", request.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRepositoryMatch_RejectsForkOrTypo()
    {
        await using var server = LoopbackChecksServer.Start();
        var (client, http) = BuildClient(server, owner: "myorg", repo: "myrepo");
        using (http)
        {
            var request = new AuditCheckPublicationRequest
            {
                Owner = "attacker-fork",
                Repository = "myrepo",
                HeadSha = Sha,
                WorkItemId = "w",
                Target = AuditTarget.Code,
                Iteration = 1,
                Scope = "aggregate",
                CheckName = "codeybox-audit",
                ExternalId = "ext",
                Lifecycle = AuditCheckLifecycle.Completed,
                Verdict = AuditCheckVerdict.Passed,
            };
            Assert.Throws<AuditCheckValidationException>(() => client.ValidateRepositoryMatch(request));
            Assert.Empty(server.Requests);
        }
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutWrapping()
    {
        await using var server = LoopbackChecksServer.Start();
        // Never respond within the test window: the client's cancellation must
        // surface unchanged. The gate is released after the assertion so the
        // connection task can drain.
        using var unblock = new ManualResetEventSlim();
        server.Enqueue(_ =>
        {
            unblock.Wait(TimeSpan.FromSeconds(20));
            return new LoopbackChecksServer.ScriptedResponse(200, "{}");
        });
        var (client, http) = BuildClient(server);
        using (http)
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => client.CreateCheckRunAsync(CreateBody(Sha), cts.Token));
            }
            finally
            {
                unblock.Set();
            }
        }
    }

    [Fact]
    public async Task LostResponse_CloseWithoutResponse_MapsToAmbiguousTransient()
    {
        await using var server = LoopbackChecksServer.Start();
        server.EnqueueCloseWithoutResponse();
        var (client, http) = BuildClient(server);
        using (http)
        {
            await Assert.ThrowsAsync<AuditCheckTransientException>(
                () => client.CreateCheckRunAsync(CreateBody(Sha)));
        }
    }
}
