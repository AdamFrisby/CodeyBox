using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Build.Unity;
using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

// CBX-NEXT-106: Unity Build Automation adapter behind the neutral
// external-build framework. Every test below drives the REAL production
// provider, service, evidence gate, park coordinator, and artifact guard
// through a synthetic in-memory HTTP transport — no real Unity endpoint is
// contacted, no mocks assert calls. Fixture payloads use obvious FAKE
// placeholders so scanners stay quiet while still matching redaction rules.
public sealed class UnityBuildAutomationTests
{
    private const string Org = "org-FAKE-001";
    private const string Project = "proj-FAKE-002";
    private const string BuildTarget = "android-main";
    private const string Editor = "2022.3.62f1";
    private const string Platform = "Android";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string SourceDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class FakeCredentials(string token = "FAKE-unity-token") : IUnityBuildCredentialProvider
    {
        public Task<string> GetApiTokenAsync(CancellationToken ct) => Task.FromResult(token);
    }

    private sealed class FakeUnityBuild
    {
        public long Number;
        public string Commit = string.Empty;
        public string Label = string.Empty;
        public Queue<string> Progression = new();
        public string TerminalOutcome = "success";
        public string FailurePhase = string.Empty;
        public string? TestReportKind;
        public bool MismatchCheckout;
        public bool MismatchEditor;
        public bool LatestCheckout;
        public bool Cancelled;
        public string ArtifactKind = "valid";
        public bool CorruptDownload;
        public bool ExpireDownload;
        public int Polls;
    }

    private sealed class FakeUnityHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
        private readonly object _gate = new();
        private long _sequence;
        private readonly Dictionary<long, FakeUnityBuild> _builds = new();
        private int _requests;

        public bool FailNextSubmitTimeout;
        public bool CreateThenTimeout;
        public bool FailNextSubmitRateLimited;
        public bool FailSubmitAuth;
        public bool FailNextStatusRateLimited;
        public bool FailSubmitUnavailable;
        public bool NextCancelPending = true;
        public string TerminalOutcome = "success";
        public string FailurePhase = string.Empty;
        public string? TestReportKind = "passing";
        public string ArtifactKind = "valid";

        public int Requests { get { lock (_gate) return _requests; } }
        public int BuildsCreated { get { lock (_gate) return _builds.Count; } }
        public List<string> SeenIdempotencyKeys { get; } = [];
        public List<string> SeenCreateBodies { get; } = [];

        public FakeUnityBuild? FindByLabel(string label)
        {
            lock (_gate) return _builds.Values.FirstOrDefault(b => b.Label == label);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate) _requests++;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;

            if (request.Headers.Authorization?.Scheme != "Basic")
                return Status(HttpStatusCode.Unauthorized, "missing basic auth");

            if (request.Method == HttpMethod.Post && path.EndsWith("/builds", StringComparison.Ordinal)
                && !path.Contains("/cancel", StringComparison.Ordinal))
            {
                if (FailSubmitAuth)
                    return Status(HttpStatusCode.Unauthorized, "bad token");
                if (FailNextSubmitRateLimited)
                {
                    FailNextSubmitRateLimited = false;
                    var limited = Status((HttpStatusCode)429, "throttled");
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                    return limited;
                }
                if (FailSubmitUnavailable)
                    return Status(HttpStatusCode.ServiceUnavailable, "down for maintenance");
                var body = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
                var create = JsonSerializer.Deserialize<UnityCreateBuildRequest>(body, Json)
                    ?? new UnityCreateBuildRequest();
                lock (_gate)
                {
                    if (request.Headers.TryGetValues("X-Idempotency-Key", out var keys))
                        SeenIdempotencyKeys.AddRange(keys);
                    SeenCreateBodies.Add(body);
                }
                if (FailNextSubmitTimeout)
                {
                    FailNextSubmitTimeout = false;
                    throw new OperationCanceledException("simulated submit timeout before acceptance");
                }
                FakeUnityBuild build;
                lock (_gate)
                {
                    build = new FakeUnityBuild
                    {
                        Number = ++_sequence,
                        Commit = create.Commit,
                        Label = create.Label,
                        TerminalOutcome = TerminalOutcome,
                        FailurePhase = FailurePhase,
                        TestReportKind = TestReportKind,
                        ArtifactKind = ArtifactKind,
                    };
                    build.Progression = new Queue<string>(["queued", "running"]);
                    _builds[build.Number] = build;
                }
                if (CreateThenTimeout)
                {
                    CreateThenTimeout = false;
                    throw new OperationCanceledException("simulated timeout after provider acceptance");
                }
                return JsonResponse(HttpStatusCode.Created, new { build = build.Number });
            }

            if (request.Method == HttpMethod.Get && query.Contains("label=", StringComparison.Ordinal))
            {
                var label = query.Split("label=", 2)[1];
                label = Uri.UnescapeDataString(label.Split('&')[0]);
                List<object> matches;
                lock (_gate)
                    matches = _builds.Values
                        .Where(b => b.Label == label)
                        .Select(b => (object)new { build = b.Number, label = b.Label, commit = b.Commit })
                        .ToList();
                return JsonResponse(HttpStatusCode.OK, matches);
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var buildsIndex = Array.IndexOf(segments, "builds");
            if (buildsIndex < 0 || buildsIndex + 1 >= segments.Length)
                return Status(HttpStatusCode.NotFound, "no such route");
            if (!long.TryParse(segments[buildsIndex + 1], out var number))
                return Status(HttpStatusCode.NotFound, "bad build number");
            FakeUnityBuild? target;
            lock (_gate) _builds.TryGetValue(number, out target);
            if (target is null)
                return Status(HttpStatusCode.NotFound, "no such build");

            if (request.Method == HttpMethod.Get && buildsIndex + 2 >= segments.Length)
            {
                if (FailNextStatusRateLimited)
                {
                    FailNextStatusRateLimited = false;
                    return Status((HttpStatusCode)429, "poll throttled");
                }
                if (target.Cancelled)
                    return JsonResponse(HttpStatusCode.OK, Details(target, "canceled"));
                string status;
                lock (_gate)
                {
                    target.Polls++;
                    status = target.Progression.Count > 0 ? target.Progression.Dequeue() : target.TerminalOutcome;
                    if (target.Progression.Count == 0 && status is "queued" or "running")
                        status = target.TerminalOutcome;
                }
                return JsonResponse(HttpStatusCode.OK, Details(target, status));
            }

            if (request.Method == HttpMethod.Post && buildsIndex + 2 < segments.Length
                && segments[buildsIndex + 2] == "cancel")
            {
                lock (_gate)
                {
                    if (target.Cancelled || IsTerminalStatus(CurrentStatus(target)))
                        return Status(HttpStatusCode.Gone, "already terminal");
                    if (NextCancelPending)
                    {
                        NextCancelPending = false;
                        return JsonResponse(HttpStatusCode.OK, new { status = "cancelPending" });
                    }
                    target.Cancelled = true;
                    return JsonResponse(HttpStatusCode.OK, new { status = "canceled" });
                }
            }

            if (request.Method == HttpMethod.Get && buildsIndex + 2 < segments.Length
                && segments[buildsIndex + 2] == "artifacts")
            {
                if (buildsIndex + 3 >= segments.Length)
                    return JsonResponse(HttpStatusCode.OK, ArtifactEntries(target));
                var name = Uri.UnescapeDataString(segments[buildsIndex + 3]);
                if (target.ExpireDownload)
                    return Status(HttpStatusCode.Gone, "artifact expired");
                var bytes = ArtifactBytes(target, name);
                if (target.CorruptDownload)
                    bytes = Encoding.UTF8.GetBytes("substituted-bytes");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                };
                return response;
            }

            return Status(HttpStatusCode.NotFound, "no such route");
        }

        private static bool IsTerminalStatus(string status) =>
            status is "success" or "failure" or "canceled";

        private static string CurrentStatus(FakeUnityBuild build) =>
            build.Progression.Count > 0 ? build.Progression.Peek() : build.TerminalOutcome;

        private static object Details(FakeUnityBuild build, string status) => new Dictionary<string, object?>
        {
            ["build"] = build.Number,
            ["buildStatus"] = status,
            ["failurePhase"] = build.FailurePhase == string.Empty ? null : build.FailurePhase,
            ["failureReason"] = status == "failure" ? "Player build failed; token=FAKE-should-be-redacted" : null,
            ["checkoutCommit"] = build.MismatchCheckout ? new string('9', 40) : build.Commit,
            ["checkoutMode"] = build.LatestCheckout ? "latest" : "exact",
            ["editorVersion"] = build.MismatchEditor ? "1999.9.9f9" : Editor,
            ["platform"] = Platform,
            ["logExcerpt"] = "line one\napi_key=FAKE-should-be-redacted\nline three",
            ["testReport"] = TestReport(build),
            ["queuedSeconds"] = 42,
            ["artifacts"] = status == "success" ? ArtifactEntries(build) : [],
        };

        private static object? TestReport(FakeUnityBuild build) => build.TestReportKind switch
        {
            "passing" => new { total = 12, passed = 12, failed = 0, skipped = 0 },
            "failing" => new { total = 12, passed = 9, failed = 3, skipped = 0 },
            _ => null,
        };

        private static List<object> ArtifactEntries(FakeUnityBuild build)
        {
            var bytes = ArtifactBytes(build, "player.apk");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            return build.ArtifactKind switch
            {
                "oversized" => [new Dictionary<string, object?>
                {
                    ["name"] = "player.apk", ["size"] = long.MaxValue,
                    ["sha256"] = digest, ["url"] = ArtifactUrl("player.apk"),
                }],
                "badhost" => [new Dictionary<string, object?>
                {
                    ["name"] = "player.apk", ["size"] = bytes.Length,
                    ["sha256"] = digest, ["url"] = "https://evil.example/player.apk",
                }],
                "nodigest" => [new Dictionary<string, object?>
                {
                    ["name"] = "player.apk", ["size"] = bytes.Length,
                    ["sha256"] = null, ["url"] = ArtifactUrl("player.apk"),
                }],
                _ => [new Dictionary<string, object?>
                {
                    ["name"] = "player.apk", ["size"] = bytes.Length,
                    ["sha256"] = digest, ["url"] = ArtifactUrl("player.apk"), ["mediaType"] = "application/vnd.android.package-archive",
                }],
            };
        }

        private static string ArtifactUrl(string name) =>
            $"{UnityBuildAutomationOptions.ApiBaseUrl}/download/FAKE/{name}";

        private static byte[] ArtifactBytes(FakeUnityBuild build, string name) =>
            Encoding.UTF8.GetBytes($"unity-artifact:{build.Number}:{name}");

        private static HttpResponseMessage Status(HttpStatusCode code, string message) =>
            new(code) { Content = new StringContent("{\"error\":\"" + message + "\"}", Encoding.UTF8, "application/json") };

        private static HttpResponseMessage JsonResponse(HttpStatusCode code, object body) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private sealed class Harness
    {
        public FakeUnityHandler Handler = new();
        public UnityBuildAutomationOptions UnityOptions = new()
        {
            Enabled = true,
            AllowedOrganizationIds = [Org],
            AllowedProjectIds = [Project],
            SupportedEditorVersions = [Editor],
        };
        public ExternalBuildOptions FrameworkOptions = new()
        {
            Enabled = true,
            ApprovedTargets =
            {
                ["unity-android"] = new ExternalBuildTargetApproval
                {
                    ProviderId = UnityBuildAutomationOptions.ProviderId,
                    TargetId = BuildTarget,
                    Configuration = "Release",
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [UnityBuildTarget.ParamOrgId] = Org,
                        [UnityBuildTarget.ParamProjectId] = Project,
                        [UnityBuildTarget.ParamEditorVersion] = Editor,
                        [UnityBuildTarget.ParamPlatform] = Platform,
                    },
                    AllowGitPublication = true,
                    AllowSnapshotUpload = false,
                },
            },
        };
        public ControllableClock Clock = new(DateTimeOffset.UtcNow);
        public InMemoryExternalBuildStore Store = new();

        public UnityBuildAutomationProvider Provider()
        {
            var http = new HttpClient(Handler)
            {
                BaseAddress = new Uri(UnityBuildAutomationOptions.ApiBaseUrl + "/"),
            };
            var unity = UnityOptions;
            var framework = FrameworkOptions;
            var approvals = FrameworkOptions.ApprovedTargets.Values.ToDictionary(
                a => a.TargetId, a => a, StringComparer.Ordinal);
            return new UnityBuildAutomationProvider(
                http, () => unity, () => framework,
                targetId => approvals.TryGetValue(targetId, out var approval) ? approval : null,
                new FakeCredentials(), Clock);
        }

        public ExternalBuildService Service(UnityBuildAutomationProvider? provider = null)
        {
            var framework = FrameworkOptions;
            return new ExternalBuildService(Store, [provider ?? Provider()], () => framework, Clock);
        }

        public ExternalBuildSourceIdentity Source(string? commit = Commit) => new()
        {
            SourceDigestSha256 = SourceDigest,
            BaseDigestSha256 = BaseDigest,
            CandidateRef = commit is null ? null : "refs/candidates/" + commit,
            ByteSize = 100,
            FileCount = 2,
        };

        public ExternalBuildStartRequest Request(string workItem = "w1", string? commit = Commit) => new()
        {
            ProjectId = "proj",
            WorkItemId = workItem,
            Phase = "work",
            Iteration = 1,
            Attempt = 1,
            ApprovedTargetName = "unity-android",
            Source = Source(commit),
            IdempotencyKey = "idem-" + workItem + "-" + (commit ?? "none"),
        };

        public ExternalBuildSubmitInput Input(string? commit = Commit) => new()
        {
            CandidateRef = commit is null ? null : "refs/candidates/" + commit,
        };
    }

    private sealed class ControllableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void Adapter_IsDisabledByDefault()
    {
        Assert.False(new UnityBuildAutomationOptions().Enabled);
        Assert.True(UnityBuildAutomationOptions.IsValid(new UnityBuildAutomationOptions()));
        Assert.Equal("unity-build-automation", UnityBuildAutomationOptions.ProviderId);
    }

    [Fact]
    public async Task ValidBuild_SubmitPollGatePasses()
    {
        var harness = new Harness();
        var service = harness.Service();

        var record = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.StartsWith("unity:", record.ProviderRunId);
        Assert.Contains(Commit, record.ProviderRunId);
        Assert.Contains(SourceDigest, record.ProviderRunId);

        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Running, record.State);
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);

        var evidence = record.Evidence;
        Assert.NotNull(evidence);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, evidence.Compile);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, evidence.Tests);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, evidence.Package);
        Assert.True(evidence.Authoritative);
        Assert.Equal(SourceDigest, evidence.SourceDigestSha256);
        Assert.Equal(record.ProviderRunId, evidence.ProviderRunId);
        Assert.Equal($"unity:{Org}/{Project}/{BuildTarget}@{Editor}", evidence.WorkflowIdentity);
        Assert.NotEmpty(evidence.ArtifactDigests);
        var createBody = Assert.Single(harness.Handler.SeenCreateBodies);
        Assert.Contains(Commit, createBody);
        Assert.Contains("refs/candidates/" + Commit, createBody);

        var gate = ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest);
        Assert.True(gate.Passed, gate.Reason);
    }

    [Fact]
    public async Task QueuedRunningTransitions_AreObservedInOrder()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.Queued, record.State);
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Running, record.State);
    }

    [Fact]
    public async Task FailedBuild_MapsCompileFailureSeparately()
    {
        var harness = new Harness();
        harness.Handler.TerminalOutcome = "failure";
        harness.Handler.FailurePhase = "compile";
        harness.Handler.TestReportKind = null;
        var service = harness.Service();

        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Failed, record.State);
        var evidence = record.Evidence;
        Assert.NotNull(evidence);
        Assert.Equal(ExternalBuildDimensionOutcome.Failed, evidence.Compile);
        Assert.Equal(ExternalBuildDimensionOutcome.NotRun, evidence.Tests);
        Assert.DoesNotContain("FAKE-should-be-redacted", record.FailureDetail ?? string.Empty);
        Assert.False(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);
    }

    [Fact]
    public async Task TestFailure_MapsToFailedTests()
    {
        var harness = new Harness();
        harness.Handler.TerminalOutcome = "failure";
        harness.Handler.FailurePhase = "test";
        harness.Handler.TestReportKind = "failing";
        var service = harness.Service();

        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Failed, record.State);
        var evidence = record.Evidence;
        Assert.NotNull(evidence);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, evidence.Compile);
        Assert.Equal(ExternalBuildDimensionOutcome.Failed, evidence.Tests);
        Assert.False(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);
    }

    [Fact]
    public async Task CompilationOrPackageWithoutTestReport_NeverCountsAsTestPass()
    {
        var harness = new Harness();
        harness.Handler.TestReportKind = null;
        var service = harness.Service();

        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        var evidence = record.Evidence;
        Assert.NotNull(evidence);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, evidence.Compile);
        Assert.Equal(ExternalBuildDimensionOutcome.NotRun, evidence.Tests);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, evidence.Package);
        var gate = ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest);
        Assert.False(gate.Passed);
        Assert.Contains("test", gate.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MismatchedCheckout_RefusesSubstitutedEvidence()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        var build = harness.Handler.FindByLabel(record.RequestId);
        Assert.NotNull(build);
        build.MismatchCheckout = true;

        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Null(record.Evidence);
        Assert.False(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);
    }

    [Fact]
    public async Task MismatchedEditor_FailsClosed()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        var build = harness.Handler.FindByLabel(record.RequestId);
        Assert.NotNull(build);
        build.MismatchEditor = true;

        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Null(record.Evidence);
    }

    [Fact]
    public async Task LatestOnBranch_IsNeverProof()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        var build = harness.Handler.FindByLabel(record.RequestId);
        Assert.NotNull(build);
        build.LatestCheckout = true;

        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);

        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Null(record.Evidence);
        Assert.False(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);
    }

    [Fact]
    public async Task SnapshotOnlyHandoff_FailsBeforeAnyProviderCall()
    {
        var harness = new Harness();
        var provider = harness.Provider();
        var record = new ExternalBuildRecord
        {
            Id = "xb-test",
            ProjectId = "proj",
            WorkItemId = "w1",
            Phase = "work",
            Iteration = 1,
            Attempt = 1,
            State = ExternalBuildState.IntentRecorded,
            Target = new ExternalBuildTargetKey { ProviderId = "unity-build-automation", TargetId = BuildTarget, Configuration = "Release" },
            Source = new ExternalBuildSourceIdentity
            {
                SourceDigestSha256 = SourceDigest,
                BaseDigestSha256 = BaseDigest,
                SnapshotId = "snap-x",
            },
            ConfigDigest = "cfg",
            RequestId = "req-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var before = harness.Handler.Requests;
        await Assert.ThrowsAsync<UnityBuildSourceException>(() =>
            provider.SubmitAsync(record, new ExternalBuildSubmitInput(), CancellationToken.None));
        Assert.Equal(before, harness.Handler.Requests);
    }

    [Fact]
    public async Task BranchNameCandidate_FailsBeforeAnyProviderCall()
    {
        var harness = new Harness();
        var service = harness.Service();
        var before = harness.Handler.Requests;
        var record = await service.StartAsync(
            harness.Request(commit: null) with { Source = harness.Source(null) with { CandidateRef = "refs/candidates/feature-branch" } },
            harness.Input(null));
        Assert.Equal(ExternalBuildState.SubmitUncertain, record.State);
        Assert.Equal(before, harness.Handler.Requests);
    }

    [Fact]
    public async Task ForbiddenSourceOverride_FailsBeforeAnyProviderCall()
    {
        var harness = new Harness();
        harness.FrameworkOptions.ApprovedTargets["unity-android"].Parameters["unity.branch"] = "main";
        var service = harness.Service();
        var before = harness.Handler.Requests;
        var record = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, record.State);
        Assert.Equal(before, harness.Handler.Requests);
        Assert.Contains("unity.branch", record.FailureDetail ?? string.Empty);
    }

    [Fact]
    public async Task UnapprovedEditor_FailsBeforeAnyProviderCall()
    {
        var harness = new Harness();
        harness.UnityOptions.SupportedEditorVersions.Clear();
        var provider = harness.Provider();
        var service = harness.Service(provider);
        var before = harness.Handler.Requests;
        var record = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, record.State);
        Assert.Equal(before, harness.Handler.Requests);
    }

    [Fact]
    public async Task DuplicateConcurrentSubmit_DispatchesOnePaidRun()
    {
        var harness = new Harness();
        var service = harness.Service();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            service.StartAsync(harness.Request(), harness.Input())));
        Assert.All(results, r => Assert.Equal(results[0].Id, r.Id));
        Assert.Equal(1, harness.Handler.BuildsCreated);
        Assert.Single(harness.Handler.SeenIdempotencyKeys.Distinct());
    }

    [Fact]
    public async Task UncertainSubmit_ReconcilesWithoutDuplicatePaidRun()
    {
        var harness = new Harness();
        harness.Handler.CreateThenTimeout = true;
        var service = harness.Service();

        var uncertain = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, uncertain.State);
        Assert.Equal(1, harness.Handler.BuildsCreated);

        var reconciled = await service.ReconcileAsync(uncertain.Id);
        Assert.Equal(ExternalBuildState.Queued, reconciled.State);
        Assert.Equal(1, harness.Handler.BuildsCreated);
        Assert.NotNull(reconciled.ProviderRunId);
    }

    [Fact]
    public async Task RestartBeforeAcceptance_RecoversViaLabelDedup()
    {
        var harness = new Harness();
        harness.Handler.CreateThenTimeout = true;
        var before = harness.Service();
        var uncertain = await before.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, uncertain.State);

        var after = harness.Service(harness.Provider());
        var recovered = await after.ReconcileAsync(uncertain.Id);
        Assert.Equal(ExternalBuildState.Queued, recovered.State);
        Assert.Equal(1, harness.Handler.BuildsCreated);
    }

    [Fact]
    public async Task RestartAfterCompletion_DeliversExactlyOnceToParkedOwner()
    {
        var harness = new Harness();
        var before = harness.Service();
        var record = await before.StartAsync(harness.Request(), harness.Input());
        record = await before.ReconcileAsync(record.Id);
        record = await before.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);

        var after = harness.Service(harness.Provider());
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(after, harness.Store, history, () => harness.FrameworkOptions, harness.Clock);
        var delivered = new List<string>();
        var first = await coordinator.DeliverCompletionsAsync((r, kind, ct) =>
        {
            delivered.Add(r.Id + ":" + kind);
            return Task.CompletedTask;
        });
        var second = await coordinator.DeliverCompletionsAsync((r, kind, ct) =>
        {
            delivered.Add(r.Id + ":" + kind);
            return Task.CompletedTask;
        });
        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(delivered);
    }

    [Fact]
    public async Task ObsoleteAttempt_CannotOverwriteNewerState()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        var stale = (await harness.Store.GetAsync(record.Id))!;
        var advanced = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Running, advanced.State);
        Assert.False(await harness.Store.TryClaimAsync(stale.Id, stale.State, stale.FenceOwner, stale));
        var gate = ExternalBuildEvidenceGate.Check(advanced, new string('c', 64), BaseDigest);
        Assert.False(gate.Passed);
    }

    [Fact]
    public async Task CancelPending_DistinguishedFromConfirmedStop()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Running, record.State);

        var pending = await service.CancelAsync(record.Id, ExternalBuildTerminalCause.UserCancelled);
        Assert.Equal(ExternalBuildState.Cancelled, pending.State);
        Assert.Equal(ExternalBuildTerminalCause.UserCancelled, pending.TerminalCause);
        Assert.Contains("pending", pending.FailureDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var status = await harness.Provider().CancelAsync(pending.ProviderRunId!, CancellationToken.None);
        Assert.True(status.Confirmed);
    }

    [Fact]
    public async Task ProviderCancelled_MapsToConfirmedCancellation()
    {
        var harness = new Harness();
        harness.Handler.TerminalOutcome = "canceled";
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Cancelled, record.State);
        Assert.Equal(ExternalBuildTerminalCause.ProviderConfirmedCancellation, record.TerminalCause);
        Assert.False(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);
    }

    [Fact]
    public async Task LateCompletion_SurvivesCancelRace()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        var afterCancel = await service.CancelAsync(record.Id, ExternalBuildTerminalCause.UserCancelled);
        Assert.Equal(ExternalBuildState.Succeeded, afterCancel.State);
        Assert.NotNull(afterCancel.Evidence);
    }

    [Fact]
    public async Task AuthFailure_IsTypedAndNeverSilent()
    {
        var harness = new Harness();
        harness.Handler.FailSubmitAuth = true;
        var provider = harness.Provider();
        var record = await harness.Service(provider).StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, record.State);
        Assert.Contains("credentials", record.FailureDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<UnityBuildAuthException>(() =>
            provider.SubmitAsync(record, harness.Input(), CancellationToken.None));
    }

    [Fact]
    public async Task RateLimit_SurfacesRetryAfterAndKeepsUncertainIntent()
    {
        var harness = new Harness();
        harness.Handler.FailNextSubmitRateLimited = true;
        var service = harness.Service();
        var error = await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(() =>
            service.StartAsync(harness.Request(), harness.Input()));
        Assert.NotNull(error.RetryAfter);
        var stored = (await harness.Store.ListActiveAsync("proj")).Single();
        Assert.Equal(ExternalBuildState.SubmitUncertain, stored.State);
    }

    [Fact]
    public async Task UnavailableProvider_YieldsUncertainNotSilentPass()
    {
        var harness = new Harness();
        harness.Handler.FailSubmitUnavailable = true;
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, record.State);
        Assert.False(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);
    }

    [Fact]
    public async Task UnsupportedEditorPlatformAnswer_FailsClosed()
    {
        var harness = new Harness();
        var handler = new FailingStatusHandler(HttpStatusCode.UnprocessableEntity, "unsupported platform");
        var http = new HttpClient(handler) { BaseAddress = new Uri(UnityBuildAutomationOptions.ApiBaseUrl + "/") };
        var denying = new UnityBuildAutomationProvider(
            http, () => harness.UnityOptions, () => harness.FrameworkOptions,
            targetId => harness.FrameworkOptions.ApprovedTargets.Values.FirstOrDefault(a => a.TargetId == targetId),
            new FakeCredentials(), harness.Clock);
        var service = harness.Service(denying);
        var record = await service.StartAsync(harness.Request(), harness.Input());
        Assert.Equal(ExternalBuildState.Queued, record.State);
        await Assert.ThrowsAsync<UnityBuildUnavailableException>(() =>
            denying.GetStatusAsync(record.ProviderRunId!, CancellationToken.None));
    }

    [Fact]
    public async Task PollRateLimit_RetriesThenSucceeds()
    {
        var harness = new Harness();
        harness.Handler.FailNextStatusRateLimited = true;
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.Contains("rate-limited", record.FailureDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Running, record.State);
    }

    [Fact]
    public async Task ArtifactBounds_RedactionAndFailures_AreSafe()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        var provider = harness.Provider();

        var refs = await provider.ListArtifactsAsync(record.ProviderRunId!, CancellationToken.None);
        var listed = Assert.Single(refs);
        Assert.Equal("player.apk", listed.Name);
        var payload = await provider.ReadArtifactAsync(record.ProviderRunId!, "player.apk", CancellationToken.None);
        Assert.Equal(listed.ContentDigestSha256, payload.ContentDigestSha256);

        await Assert.ThrowsAsync<UnityBuildArtifactException>(() =>
            provider.ReadArtifactAsync(record.ProviderRunId!, "../escape.zip", CancellationToken.None));
        await Assert.ThrowsAsync<UnityBuildArtifactException>(() =>
            provider.ReadArtifactAsync(record.ProviderRunId!, "missing.zip", CancellationToken.None));
    }

    [Fact]
    public async Task OversizedOrOffHostArtifacts_AreExcludedFromListing()
    {
        foreach (var kind in new[] { "oversized", "badhost", "nodigest" })
        {
            var harness = new Harness();
            harness.Handler.ArtifactKind = kind;
            var service = harness.Service();
            var record = await service.StartAsync(harness.Request(), harness.Input());
            record = await service.ReconcileAsync(record.Id);
            record = await service.ReconcileAsync(record.Id);
            Assert.Equal(ExternalBuildState.Succeeded, record.State);
            var refs = await harness.Provider().ListArtifactsAsync(record.ProviderRunId!, CancellationToken.None);
            Assert.Empty(refs);
        }
    }

    [Fact]
    public async Task CorruptOrExpiredDownload_FailsClosed()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);
        var build = harness.Handler.FindByLabel(record.RequestId);
        Assert.NotNull(build);
        var provider = harness.Provider();

        build.CorruptDownload = true;
        await Assert.ThrowsAsync<UnityBuildArtifactException>(() =>
            provider.ReadArtifactAsync(record.ProviderRunId!, "player.apk", CancellationToken.None));
        build.CorruptDownload = false;
        build.ExpireDownload = true;
        await Assert.ThrowsAsync<UnityBuildArtifactException>(() =>
            provider.ReadArtifactAsync(record.ProviderRunId!, "player.apk", CancellationToken.None));
    }

    [Fact]
    public void PureValidation_MakesNoProviderCalls()
    {
        var harness = new Harness();
        var before = harness.Handler.Requests;
        var approval = harness.FrameworkOptions.ApprovedTargets["unity-android"];
        var target = UnityBuildTarget.Resolve(BuildTarget, approval, harness.UnityOptions);
        Assert.Equal($"unity:{Org}/{Project}/{BuildTarget}@{Editor}", target.WorkflowIdentity);
        var key = target.ToTargetKey();
        Assert.Equal("unity-editor/" + Editor, key.Toolchain);
        Assert.Equal(Platform, key.Platform);
        Assert.Throws<UnityBuildSourceException>(() =>
            UnityBuildSourceValidator.RequireCommit(
                harness.Source(null), new ExternalBuildSubmitInput(), harness.UnityOptions));
        var mapped = UnityBuildStatusMapper.Map(
            new UnityBuildDetails { BuildStatus = "bogus" }, target, Commit, SourceDigest,
            "unity:x", 8192, 1024 * 1024, ["build-api.cloud.unity3d.com"], DateTimeOffset.UtcNow);
        Assert.Equal(ExternalBuildExecutionPhase.Unknown, mapped.Phase);
        Assert.Null(mapped.Evidence);
        var otherProvider = new ExternalBuildTargetApproval
        {
            ProviderId = "other",
            TargetId = approval.TargetId,
            Configuration = approval.Configuration,
            Parameters = approval.Parameters,
            AllowGitPublication = approval.AllowGitPublication,
            AllowSnapshotUpload = approval.AllowSnapshotUpload,
        };
        Assert.Throws<UnityBuildTargetRejectedException>(() =>
            UnityBuildTarget.Resolve(BuildTarget, otherProvider, harness.UnityOptions));
        Assert.Equal(before, harness.Handler.Requests);
    }

    [Fact]
    public void CacheClasses_ProduceDistinctComparableKeys()
    {
        var harness = new Harness();
        var approval = harness.FrameworkOptions.ApprovedTargets["unity-android"];
        var warm = UnityBuildTarget.Resolve(BuildTarget, approval, harness.UnityOptions);
        var coldApproval = new ExternalBuildTargetApproval
        {
            ProviderId = approval.ProviderId,
            TargetId = approval.TargetId,
            Configuration = approval.Configuration,
            Parameters = new Dictionary<string, string>(approval.Parameters, StringComparer.Ordinal)
            {
                [UnityBuildTarget.ParamCleanBuild] = "true",
            },
            AllowGitPublication = approval.AllowGitPublication,
            AllowSnapshotUpload = approval.AllowSnapshotUpload,
        };
        var cold = UnityBuildTarget.Resolve(BuildTarget, coldApproval, harness.UnityOptions);
        Assert.Equal("warm", warm.CacheClass);
        Assert.Equal("cold", cold.CacheClass);
        Assert.False(warm.ToTargetKey().Matches(cold.ToTargetKey()));
    }

    [Fact]
    public async Task NeutralContracts_AcceptUnityAndOtherToolchains()
    {
        var harness = new Harness();
        var service = harness.Service();
        var record = await service.StartAsync(harness.Request(), harness.Input());
        record = await service.ReconcileAsync(record.Id);
        record = await service.ReconcileAsync(record.Id);
        Assert.True(ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest).Passed);

        var other = new ExternalBuildEvidence
        {
            Compile = ExternalBuildDimensionOutcome.Passed,
            Tests = ExternalBuildDimensionOutcome.Passed,
            Package = ExternalBuildDimensionOutcome.NotRun,
            SourceDigestSha256 = SourceDigest,
            ProviderRunId = "other-toolchain-run-1",
            WorkflowIdentity = "other-toolchain@pinned",
            ApprovedTargetName = "other-target",
            Toolchain = "other-toolchain-1",
            Platform = "other-platform-1",
            Configuration = "release",
            Authoritative = true,
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var otherBuild = record with
        {
            Id = "xb-other",
            ProviderRunId = "other-toolchain-run-1",
            Evidence = other,
            Source = record.Source,
        };
        Assert.True(ExternalBuildEvidenceEvaluator.Evaluate(
            other, otherBuild, SourceDigest, BaseDigest).Passed);
        Assert.False(record.Target.Matches(new ExternalBuildTargetKey
        {
            ProviderId = "other",
            TargetId = "other-target",
        }));
    }

    private sealed class FailingStatusHandler(HttpStatusCode code, string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"build\":7}", Encoding.UTF8, "application/json"),
                });
            if (request.RequestUri?.Query.Contains("label=") == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent("{\"error\":\"" + message + "\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
