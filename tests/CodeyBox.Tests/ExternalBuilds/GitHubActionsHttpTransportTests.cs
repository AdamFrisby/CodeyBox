using System.Net;
using System.Text;
using CodeyBox.Build.GitHubActions;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>
/// Production HTTP transport coverage through stubbed HTTP handlers — the
/// real request building, status mapping, JSON parsing, redirect policy,
/// and credential scoping run; no live provider is touched. Proves the
/// supported API version header, the non-hardcoded dispatch acceptance
/// (201/204 both reconcile-by-correlation), and expiring-URL/auth handling.
/// </summary>
public sealed class GitHubActionsHttpTransportTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubCredentials : IGitHubActionsCredentialProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult("test-token");
    }

    private static GitHubActionsHttpTransport BuildTransport(
        StubHandler api, StubHandler? downloads, GitHubActionsExternalBuildOptions? options = null)
    {
        options ??= new GitHubActionsExternalBuildOptions
        {
            ApiBaseUrl = "https://api.test",
            AllowedArtifactHosts = ["dl.test"],
        };
        return new GitHubActionsHttpTransport(
            new StubFactory(api), new StubCredentials(), () => options,
            TimeProvider.System, downloads);
    }

    private static GitHubActionsDispatchRequest Dispatch() => new(
        "acme", "game", ".github/workflows/build.yml", "codeybox-candidates/build-1",
        "corr-1", "refs/heads/codeybox-candidates/build-1",
        "abcdef0123456789abcdef0123456789abcdef01", null,
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static HttpResponseMessage Empty(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty) };

    [Fact]
    public async Task Dispatch_Accepts204_AsUncertain()
    {
        var api = new StubHandler(_ => Empty(HttpStatusCode.NoContent));
        var transport = BuildTransport(api, null);
        var outcome = await transport.DispatchAsync(Dispatch(), CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.True(outcome.Uncertain);
        Assert.Null(outcome.RunId);
        Assert.Equal(204, outcome.HttpStatus);
        var request = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.True(request.Headers.Contains("X-GitHub-Api-Version"));
        Assert.Equal("2022-11-28", string.Join(",", request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal("token", request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Dispatch_Accepts201_WithoutAssuming204()
    {
        var api = new StubHandler(_ => Empty(HttpStatusCode.Created));
        var transport = BuildTransport(api, null);
        var outcome = await transport.DispatchAsync(Dispatch(), CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.True(outcome.Uncertain);
        Assert.Equal(201, outcome.HttpStatus);
    }

    [Fact]
    public async Task Dispatch_MapsRateLimit_WithRetryAfter()
    {
        var api = new StubHandler(_ =>
        {
            var resp = Empty((HttpStatusCode)429);
            resp.Headers.Add("Retry-After", "5");
            return resp;
        });
        var transport = BuildTransport(api, null);
        var ex = await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(
            () => transport.DispatchAsync(Dispatch(), CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
    }

    [Fact]
    public async Task Dispatch_MapsAuthBlocked()
    {
        var api = new StubHandler(_ => Empty(HttpStatusCode.Unauthorized));
        var transport = BuildTransport(api, null);
        await Assert.ThrowsAsync<GitHubActionsAuthException>(
            () => transport.DispatchAsync(Dispatch(), CancellationToken.None));
    }

    [Fact]
    public async Task GetRun_ParsesAuthoritativeFields()
    {
        var body = """
            {"id":1234,"run_attempt":2,"status":"completed","conclusion":"success",
             "head_sha":"abcdef0123456789abcdef0123456789abcdef01",
             "head_branch":"codeybox-candidates/build-1",
             "head_repository":{"full_name":"acme/game"},
             "repository":{"full_name":"acme/game"},
             "path":".github/workflows/build.yml","event":"workflow_dispatch",
             "created_at":"2026-10-01T00:00:00Z","updated_at":"2026-10-01T00:05:00Z"}
            """;
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var transport = BuildTransport(api, null);
        var run = await transport.GetRunAsync("acme", "game", 1234, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(1234, run.Id);
        Assert.Equal(2, run.RunAttempt);
        Assert.Equal("completed", run.Status);
        Assert.Equal("success", run.Conclusion);
        Assert.Equal("acme/game", run.HeadRepository);
        Assert.Equal(".github/workflows/build.yml", run.WorkflowPath);
    }

    [Fact]
    public async Task GetRun_Missing_ReturnsNull()
    {
        var api = new StubHandler(_ => Empty(HttpStatusCode.NotFound));
        var transport = BuildTransport(api, null);
        Assert.Null(await transport.GetRunAsync("acme", "game", 999, CancellationToken.None));
    }

    [Fact]
    public async Task FindRun_MatchesExactBranch_NotLatest()
    {
        var body = """
            {"workflow_runs":[
              {"id":200,"run_attempt":1,"status":"completed","conclusion":"success",
               "head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
               "head_branch":"someone-elses-branch",
               "head_repository":{"full_name":"acme/game"},
               "repository":{"full_name":"acme/game"},
               "path":".github/workflows/build.yml","event":"workflow_dispatch",
               "created_at":"2026-10-02T00:00:00Z","updated_at":"2026-10-02T00:05:00Z"},
              {"id":100,"run_attempt":1,"status":"in_progress","conclusion":null,
               "head_sha":"abcdef0123456789abcdef0123456789abcdef01",
               "head_branch":"codeybox-candidates/build-1",
               "head_repository":{"full_name":"acme/game"},
               "repository":{"full_name":"acme/game"},
               "path":".github/workflows/build.yml","event":"workflow_dispatch",
               "created_at":"2026-10-01T00:00:00Z","updated_at":"2026-10-01T00:01:00Z"}]}
            """;
        var api = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var transport = BuildTransport(api, null);
        var run = await transport.FindRunByCorrelationAsync(
            "acme", "game", ".github/workflows/build.yml", "corr-1",
            "codeybox-candidates/build-1", new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(100, run.Id);
    }

    [Fact]
    public async Task Download_FollowsAllowlistedRedirect_WithoutLeakingToken()
    {
        var api = new StubHandler(_ => throw new InvalidOperationException("API client must not serve downloads"));
        var leg = 0;
        var downloads = new StubHandler(req =>
        {
            leg++;
            if (leg == 1)
            {
                Assert.Equal("token", req.Headers.Authorization?.Scheme);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://dl.test/blob/11");
                return redirect;
            }
            Assert.Null(req.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x01, 0x02, 0x03]),
            };
        });
        var transport = BuildTransport(api, downloads);
        var artifact = new GitHubActionsArtifactInfo(
            11, "codeybox-reports", 3, false,
            "https://api.test/fake/1/artifacts/11", DateTimeOffset.UtcNow.AddHours(1));
        var bytes = await transport.DownloadArtifactAsync(artifact, 1024, CancellationToken.None);
        Assert.Equal([0x01, 0x02, 0x03], bytes);
        Assert.Equal(2, leg);
    }

    [Fact]
    public async Task Download_RejectsOffAllowlistRedirect()
    {
        var api = new StubHandler(_ => throw new InvalidOperationException("API client must not serve downloads"));
        var downloads = new StubHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.example/blob/11");
            return redirect;
        });
        var transport = BuildTransport(api, downloads);
        var artifact = new GitHubActionsArtifactInfo(
            11, "codeybox-reports", 3, false,
            "https://api.test/fake/1/artifacts/11", DateTimeOffset.UtcNow.AddHours(1));
        await Assert.ThrowsAsync<GitHubActionsValidationException>(
            () => transport.DownloadArtifactAsync(artifact, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task Download_GoneArtifact_IsUnavailable()
    {
        var api = new StubHandler(_ => throw new InvalidOperationException("API client must not serve downloads"));
        var downloads = new StubHandler(_ => Empty(HttpStatusCode.Gone));
        var transport = BuildTransport(api, downloads);
        var artifact = new GitHubActionsArtifactInfo(
            11, "codeybox-reports", 3, false,
            "https://api.test/fake/1/artifacts/11", DateTimeOffset.UtcNow.AddHours(1));
        await Assert.ThrowsAsync<GitHubActionsEvidenceUnavailableException>(
            () => transport.DownloadArtifactAsync(artifact, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task Download_RefusesToBufferOverCap()
    {
        var api = new StubHandler(_ => throw new InvalidOperationException("API client must not serve downloads"));
        var downloads = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            var content = new ByteArrayContent(new byte[4096]);
            content.Headers.ContentLength = 4096;
            resp.Content = content;
            return resp;
        });
        var transport = BuildTransport(api, downloads);
        var artifact = new GitHubActionsArtifactInfo(
            11, "codeybox-reports", 4096, false,
            "https://api.test/fake/1/artifacts/11", DateTimeOffset.UtcNow.AddHours(1));
        var ex = await Assert.ThrowsAsync<GitHubActionsEvidenceUnavailableException>(
            () => transport.DownloadArtifactAsync(artifact, 1024, CancellationToken.None));
        Assert.Contains("refusing to buffer", ex.Reason, StringComparison.Ordinal);
    }
}
