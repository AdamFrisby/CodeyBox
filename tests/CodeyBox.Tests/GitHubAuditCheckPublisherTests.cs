using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Single-pass publish/reconcile tests for
/// <see cref="GitHubAuditCheckPublisher"/> through the
/// <see cref="IUpstreamRemote"/> capability seam: exact-SHA attribution,
/// conclusion mapping, create-or-reconcile by <c>external_id</c>, lost-response
/// adoption, bounded batches, ambiguous-write reconciliation vs. uncertainty,
/// auth failures, cancellation, and explicit unsupported forges.
/// Reuses the <see cref="FakeHttpMessageHandler"/> infrastructure so request
/// serialization and response parsing are genuinely exercised.
/// </summary>
public sealed class GitHubAuditCheckPublisherTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly AuditCheckPublicationOptions TestOptions = new()
    {
        Enabled = true,
        MaxAnnotationsPerBatch = 50,
        MaxAnnotationBatches = 2,
        MaxSummaryChars = 32_768,
        MaxAnnotationMessageChars = 4_096,
        MaxAnnotationTitleChars = 120,
        MaxAnnotationLineSpan = 20,
        MaxOmittedFindingsInSummary = 20,
    };

    private sealed class FixedTokenProvider(string token) : IGitHubTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(token);
    }

    private static GitHubAuditCheckPublisher BuildPublisher(
        FakeHttpMessageHandler handler, string owner = "myorg", string repo = "myrepo")
    {
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var client = new GitHubCheckRunsClient(
            factory, new FixedTokenProvider("test-token-not-a-real-pat"), owner, repo);
        return new GitHubAuditCheckPublisher(client);
    }

    private static AuditCheckPublicationRequest Request(
        AuditCheckVerdict verdict = AuditCheckVerdict.Failed,
        AuditCheckUnavailabilityReason? reason = null,
        IReadOnlyList<AuditReportFinding>? findings = null,
        string sha = Sha,
        string owner = "myorg",
        string repo = "myrepo")
        => new()
        {
            Owner = owner,
            Repository = repo,
            HeadSha = sha,
            WorkItemId = "work-9",
            Target = AuditTarget.Code,
            Iteration = 2,
            Attempt = 1,
            Scope = "aggregate",
            CheckName = "codeybox-audit",
            ExternalId = $"codeybox/work-9/code/2/1/aggregate/{sha}",
            Lifecycle = AuditCheckLifecycle.Completed,
            Verdict = verdict,
            UnavailabilityReason = reason,
            Findings = findings ?? [],
            DetailsUrl = "https://codeybox.local/reports/work-9",
            SourceRevision = "rev-123",
        };

    private static AuditReportFinding Finding(int i) => new(
        $"finding-{i}",
        "error",
        $"Finding {i}",
        $"Message {i}",
        [$"src/File{i}.cs"],
        [i + 1]);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string EmptyList() => """{"total_count":0,"check_runs":[]}""";

    [Fact]
    public async Task Publish_FailedVerdict_CreatesFailureCheckOnExactSha()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Created,
            """{"id":101,"html_url":"https://github.com/myorg/myrepo/runs/101","status":"completed","conclusion":"failure"}"""));

        var result = await BuildPublisher(handler).PublishAsync(
            Request(findings: [Finding(1)]), TestOptions);

        Assert.Equal(101, result.CheckRunId);
        Assert.Equal("completed", result.Status);
        Assert.Equal("failure", result.Conclusion);
        Assert.Equal(1, result.AnnotationsPublished);
        Assert.False(result.BatchUncertain);

        var create = handler.Requests[1];
        Assert.Contains("/repos/myorg/myrepo/check-runs", create.RequestUri!.PathAndQuery, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(Sha, body.RootElement.GetProperty("head_sha").GetString());
        Assert.Equal("failure", body.RootElement.GetProperty("conclusion").GetString());
        var annotations = body.RootElement.GetProperty("output").GetProperty("annotations");
        Assert.Equal("src/File1.cs", annotations[0].GetProperty("path").GetString());
        var summary = body.RootElement.GetProperty("output").GetProperty("summary").GetString()!;
        Assert.Contains(Sha, summary, StringComparison.Ordinal);
        Assert.Contains("work-9", summary, StringComparison.Ordinal);
        Assert.Contains("rev-123", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_MissingAudit_PublishesSkippedNeverSuccess()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Created,
            """{"id":102,"status":"completed","conclusion":"skipped"}"""));

        var result = await BuildPublisher(handler).PublishAsync(
            Request(AuditCheckVerdict.NotRun, AuditCheckUnavailabilityReason.Missing), TestOptions);

        Assert.Equal("skipped", result.Conclusion);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("skipped", body.RootElement.GetProperty("conclusion").GetString());
        var summary = body.RootElement.GetProperty("output").GetProperty("summary").GetString()!;
        Assert.Contains("not successful coverage", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_ExistingExternalId_ReusesInsteadOfCreating()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK,
            "{\"total_count\":1,\"check_runs\":[{\"id\":77,\"external_id\":\"codeybox/work-9/code/2/1/aggregate/" + Sha + "\"}]}"));
        handler.Enqueue(Json(HttpStatusCode.OK,
            """{"id":77,"html_url":"https://github.com/myorg/myrepo/runs/77","status":"completed","conclusion":"failure"}"""));

        var result = await BuildPublisher(handler).PublishAsync(
            Request(findings: [Finding(1)]), TestOptions);

        Assert.Equal(77, result.CheckRunId);
        // No POST /check-runs create: reconcile converged on the intended run.
        Assert.DoesNotContain(handler.Requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.PathAndQuery.EndsWith("/check-runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_LostCreateResponse_AdoptsReconciledRun()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.EnqueueException(new HttpRequestException("connection reset by peer"));
        handler.Enqueue(Json(HttpStatusCode.OK,
            "{\"total_count\":1,\"check_runs\":[{\"id\":88,\"external_id\":\"codeybox/work-9/code/2/1/aggregate/" + Sha + "\"}]}"));

        var result = await BuildPublisher(handler).PublishAsync(Request(), TestOptions);

        Assert.Equal(88, result.CheckRunId);
        // Exactly one create attempt: the retry became a reconcile read.
        Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.PathAndQuery.EndsWith("/check-runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_MoreThanOneBatch_AppendsBoundedBatches()
    {
        var findings = Enumerable.Range(1, 55).Select(Finding).ToList();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Created, """{"id":201,"status":"completed","conclusion":"failure"}"""));
        handler.Enqueue(Json(HttpStatusCode.OK, """{"id":201,"status":"completed","conclusion":"failure"}"""));

        var result = await BuildPublisher(handler).PublishAsync(
            Request(findings: findings), TestOptions);

        Assert.Equal(55, result.AnnotationsPublished);
        Assert.Equal(2, result.BatchesSent);
        Assert.False(result.BatchUncertain);
        using var createBody = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(50, createBody.RootElement.GetProperty("output").GetProperty("annotations").GetArrayLength());
        using var updateBody = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal(5, updateBody.RootElement.GetProperty("output").GetProperty("annotations").GetArrayLength());
        Assert.Equal("PATCH", handler.Requests[2].Method.Method);
    }

    [Fact]
    public async Task Publish_AmbiguousBatchWrite_ReconcilesPresentAnnotations()
    {
        var findings = Enumerable.Range(1, 55).Select(Finding).ToList();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Created, """{"id":301,"status":"completed","conclusion":"failure"}"""));
        // Second batch write dies ambiguously (timeout after the server applied it).
        handler.EnqueueException(new HttpRequestException("socket hang up"));
        // Re-read proves all five batch-2 annotations present — skip, don't duplicate.
        var present = string.Join(",", Enumerable.Range(51, 5).Select(i =>
            $"{{\"path\":\"src/File{i}.cs\",\"start_line\":{i + 1},\"end_line\":{i + 1},\"title\":\"Finding {i}\"}}"));
        handler.Enqueue(Json(HttpStatusCode.OK, $"[{present}]"));

        var result = await BuildPublisher(handler).PublishAsync(
            Request(findings: findings), TestOptions);

        Assert.False(result.BatchUncertain);
        Assert.Equal(55, result.AnnotationsPublished);
        // No duplicate batch send: list + (no resend) — requests are list, create, update(failed), annotations-list.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("/annotations", handler.Requests[3].RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_UnreconcilableBatchWrite_SurfacesUncertainty()
    {
        var findings = Enumerable.Range(1, 55).Select(Finding).ToList();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Created, """{"id":401,"status":"completed","conclusion":"failure"}"""));
        handler.EnqueueException(new HttpRequestException("socket hang up"));
        // The re-read also fails: uncertainty must surface, not duplicate.
        handler.EnqueueException(new HttpRequestException("still down"));
        // Best-effort disclosure update succeeds.
        handler.Enqueue(Json(HttpStatusCode.OK, """{"id":401,"status":"completed","conclusion":"failure"}"""));

        var result = await BuildPublisher(handler).PublishAsync(
            Request(findings: findings), TestOptions);

        Assert.True(result.BatchUncertain);
        Assert.Equal(50, result.AnnotationsPublished);
        var disclosure = handler.RequestBodies[^1];
        Assert.Contains("could not be reconciled", disclosure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_Unauthorized_StopsWithoutReconcileWrite()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""));

        await Assert.ThrowsAsync<AuditCheckAuthException>(
            () => BuildPublisher(handler).PublishAsync(Request(), TestOptions));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Publish_RateLimitedCreateWithoutExisting_PropagatesRetryHint()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        var limited = Json((HttpStatusCode)429, """{"message":"API rate limit exceeded"}""");
        limited.Headers.TryAddWithoutValidation("Retry-After", "9");
        handler.Enqueue(limited);
        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));

        var ex = await Assert.ThrowsAsync<AuditCheckRateLimitedException>(
            () => BuildPublisher(handler).PublishAsync(Request(), TestOptions));
        Assert.NotNull(ex.RetryAfter);
    }

    [Fact]
    public async Task Publish_RepositoryMismatch_SendsNothing()
    {
        var handler = new FakeHttpMessageHandler();
        await Assert.ThrowsAsync<AuditCheckValidationException>(
            () => BuildPublisher(handler).PublishAsync(Request(owner: "fork-owner"), TestOptions));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_RelativeDetailsUrl_IsRejected()
    {
        var handler = new FakeHttpMessageHandler();
        var request = Request() with { DetailsUrl = "/relative/report" };
        await Assert.ThrowsAsync<AuditCheckValidationException>(
            () => BuildPublisher(handler).PublishAsync(request, TestOptions));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_CancelledRequest_PropagatesCancellation()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueCallback(ct =>
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            ct.ThrowIfCancellationRequested();
            return Json(HttpStatusCode.OK, EmptyList());
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuildPublisher(handler).PublishAsync(Request(), TestOptions, cts.Token));
    }

    [Fact]
    public async Task GitHubRemote_SupportsPublication_ThroughSeam()
    {
        var handler = new FakeHttpMessageHandler();
        var factory = new FakeHttpClientFactory(handler, userAgent: "codeybox");
        var remote = new GitHubUpstreamRemote(
            new FakeGitHost(),
            factory,
            NullLogger<GitHubUpstreamRemote>.Instance,
            new GitHubUpstreamOptions
            {
                Owner = "myorg",
                Repository = "myrepo",
                Token = "test-token-not-a-real-pat",
            });
        var support = await remote.GetAuditCheckPublicationSupportAsync();
        Assert.True(support.Supported);

        handler.Enqueue(Json(HttpStatusCode.OK, EmptyList()));
        handler.Enqueue(Json(HttpStatusCode.Created, """{"id":501,"status":"completed","conclusion":"success"}"""));
        var result = await remote.PublishAuditCheckAsync(
            Request(AuditCheckVerdict.Passed), TestOptions);
        Assert.Equal(501, result.CheckRunId);
    }

    [Theory]
    [InlineData("noop")]
    public async Task UnsupportedForges_AreExplicit(string kind)
    {
        IUpstreamRemote remote = kind switch
        {
            "noop" => new NoopUpstreamRemote(),
            _ => throw new NotSupportedException(),
        };
        var support = await remote.GetAuditCheckPublicationSupportAsync();
        Assert.False(support.Supported);
        Assert.NotEmpty(support.Reason);
        await Assert.ThrowsAsync<AuditCheckUnsupportedException>(
            () => remote.PublishAuditCheckAsync(Request(), TestOptions));
    }

    [Fact]
    public async Task GitGenericRemote_IsExplicitlyUnsupported()
    {
        IUpstreamRemote remote = new GitGenericUpstreamRemote(
            new FakeGitHost(),
            new GitGenericUpstreamOptions { UpstreamUrl = "https://example.com/repo.git" });
        var support = await remote.GetAuditCheckPublicationSupportAsync();
        Assert.False(support.Supported);
        await Assert.ThrowsAsync<AuditCheckUnsupportedException>(
            () => remote.PublishAuditCheckAsync(Request(), TestOptions));
    }
}
