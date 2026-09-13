using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the human deployment-review seam: the pure policy
/// (brief, answer mapping, verdict findings, endpoint display, finding
/// round-trips), the auditor's declaration (kind/targets/cost/ordering),
/// composition (opt-in knob, per-project exclusion), and the durable store's
/// compare-and-set transitions.
/// </summary>
public sealed class HumanDeploymentReviewPolicyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-human-policy-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Pure policy ─────────────────────────────────────────────────────────

    [Fact]
    public void QuestionIdFor_IsStableAndPerIteration()
    {
        Assert.Equal("human-deployment-review-1", HumanDeploymentReviewPolicy.QuestionIdFor(1));
        Assert.Equal("human-deployment-review-3", HumanDeploymentReviewPolicy.QuestionIdFor(3));
        Assert.NotEqual(
            HumanDeploymentReviewPolicy.QuestionIdFor(1),
            HumanDeploymentReviewPolicy.QuestionIdFor(2));
    }

    [Theory]
    [InlineData("approve", true)]
    [InlineData("APPROVE", true)]
    [InlineData("  approve  ", true)]
    [InlineData("approved", false)]
    [InlineData("approve this", false)]
    [InlineData("no, the header is wrong", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsApprovalAnswer_RequiresExactApprove(string? answer, bool expected)
    {
        // Exact equality only — never substring: "approved" must reject.
        Assert.Equal(expected, HumanDeploymentReviewPolicy.IsApprovalAnswer(answer));
    }

    [Fact]
    public void IsReviewQuestion_MatchesMarkerAndSuffixedIds()
    {
        Assert.True(HumanDeploymentReviewPolicy.IsReviewQuestion("human-deployment-review-1"));
        Assert.True(HumanDeploymentReviewPolicy.IsReviewQuestion("human-deployment-review"));
        Assert.False(HumanDeploymentReviewPolicy.IsReviewQuestion("q-001"));
        Assert.False(HumanDeploymentReviewPolicy.IsReviewQuestion("human-deployment-review-1-evil"));
        Assert.False(HumanDeploymentReviewPolicy.IsReviewQuestion(null));
    }

    [Fact]
    public void BuildBrief_ContainsEndpointExpiryAndCriteria_AndBoundsInputs()
    {
        var criteria = Enumerable.Range(0, 30)
            .Select(i => ($"criterion-{i}", new string('x', 600)))
            .ToList();
        var brief = HumanDeploymentReviewPolicy.BuildBrief(
            "Serve widgets",
            new string('p', 5000),
            "http://127.0.0.1:8080",
            new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
            criteria,
            iteration: 2,
            deploymentId: "dep-7");

        Assert.Contains("http://127.0.0.1:8080", brief, StringComparison.Ordinal);
        Assert.Contains("2026-09-12", brief, StringComparison.Ordinal);
        Assert.Contains("approve", brief, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[truncated]", brief, StringComparison.Ordinal);
        Assert.Contains("more, truncated", brief, StringComparison.Ordinal);
        Assert.True(brief.Length < 20000, $"brief unexpectedly large: {brief.Length}");
    }

    [Fact]
    public void BuildVerdictFindings_ApprovePasses_RejectAndExpiryBlock()
    {
        Assert.Empty(HumanDeploymentReviewPolicy.BuildVerdictFindings(
            "human:deployment-review", HumanDeploymentReviewStatus.Approved, null));

        var rejected = HumanDeploymentReviewPolicy.BuildVerdictFindings(
            "human:deployment-review", HumanDeploymentReviewStatus.Rejected, "header is wrong");
        var reject = Assert.Single(rejected);
        Assert.Equal(AuditSeverity.Error, reject.Severity);
        Assert.Equal("human:deployment-review", reject.AuditorName);
        Assert.Contains("header is wrong", reject.Description, StringComparison.Ordinal);

        var expired = HumanDeploymentReviewPolicy.BuildVerdictFindings(
            "human:deployment-review",
            HumanDeploymentReviewStatus.Expired,
            null,
            new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));
        var expiry = Assert.Single(expired);
        Assert.Equal(AuditSeverity.Error, expiry.Severity);
        Assert.Contains("expired unreviewed", expiry.Title, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HumanDeploymentReviewPolicy.BuildVerdictFindings(
                "human:deployment-review", HumanDeploymentReviewStatus.Pending, null));
    }

    [Fact]
    public void DescribeEndpoint_PrefersUrlThenHostPortThenPath()
    {
        Assert.Equal(
            "http://h:1",
            HumanDeploymentReviewPolicy.DescribeEndpoint(new DeploymentEndpoint
            {
                Kind = DeploymentEndpointKind.Http,
                Url = "http://h:1",
            }));
        Assert.Equal(
            "example.com:8080",
            HumanDeploymentReviewPolicy.DescribeEndpoint(new DeploymentEndpoint
            {
                Kind = DeploymentEndpointKind.Http,
                Host = "example.com",
                Port = 8080,
            }));
        Assert.Equal(
            "(endpoint unavailable)",
            HumanDeploymentReviewPolicy.DescribeEndpoint(null));
    }

    [Fact]
    public void FindingsRoundTrip_PreservesShape_CorruptFailsClosed()
    {
        var findings = new List<AuditFinding>
        {
            new("deploy:smoke", AuditSeverity.Error, "health failed", "body", "http://h/health"),
            new("deploy:tls", AuditSeverity.Warning, "weak cipher", "body2"),
        };
        var json = HumanDeploymentReviewPolicy.SerializeFindings(findings);
        var back = HumanDeploymentReviewPolicy.DeserializeFindings(json);
        Assert.Equal(2, back.Count);
        Assert.Equal("health failed", back[0].Title);
        Assert.Equal(AuditSeverity.Warning, back[1].Severity);
        Assert.Equal("http://h/health", back[0].Location);

        Assert.Throws<InvalidOperationException>(() =>
            HumanDeploymentReviewPolicy.DeserializeFindings("not-json{{{"));
        var strings = HumanDeploymentReviewPolicy.DeserializeStrings(
            HumanDeploymentReviewPolicy.SerializeStrings(["a", "b"]));
        Assert.Equal(["a", "b"], strings);
        Assert.Throws<InvalidOperationException>(() =>
            HumanDeploymentReviewPolicy.DeserializeStrings("not-json{{{"));
    }

    // ── Auditor declaration ─────────────────────────────────────────────────

    [Fact]
    public async Task HumanAuditor_NeverRunsInline()
    {
        var auditor = new HumanDeploymentReviewAuditor(new HumanDeploymentReviewOptions());
        Assert.Equal("human:deployment-review", auditor.Name);
        Assert.Equal("human", auditor.Kind);
        Assert.Equal(AuditCapabilities.None, auditor.Required);
        Assert.Equal(AuditTargets.DeploymentOnly, auditor.Targets);
        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            auditor.RunAsync(null!, "/tmp", null!, CancellationToken.None));
    }

    [Fact]
    public void HumanAuditor_CostClassAndOrdering_SortAfterAutomated()
    {
        var human = new HumanDeploymentReviewAuditor(new HumanDeploymentReviewOptions());
        Assert.Equal(AuditCostClass.Reviewer, AuditPhaseLadder.CostClassOf(human));
        Assert.True(AuditPhaseLadder.RunsInDeploymentStage(human));
        Assert.False(AuditPhaseLadder.RunsInCodeStage(human));
        Assert.True(AuditorOrdering.IsHuman(human));

        var tool = new FakeToolAuditor();
        Assert.True(AuditorOrdering.TierOf(human) > AuditorOrdering.TierOf(tool));
        Assert.Equal(
            new[] { tool.Name, human.Name },
            AuditPhaseLadder.OrderDeploymentStage([human, tool]).Select(a => a.Name));
    }

    private sealed class FakeToolAuditor : IAuditor
    {
        public string Name => "tool:fake";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    // ── Composition ─────────────────────────────────────────────────────────

    private static ProjectAuditorComposer Composer(Func<HumanDeploymentReviewOptions>? humanReview) =>
        new(new PresetCatalog(), [], NullLogger<ProjectAuditorComposer>.Instance,
            catalogOptions: null, testRunOptions: null, planAdherenceOptions: null,
            humanReviewOptions: humanReview);

    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Codex;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", "", null));
    }

    private static Project ProjectWith(params string[] excluded) => new()
    {
        Id = new ProjectId("alpha"),
        DisplayName = "Alpha",
        RepositoryUrl = "https://example.com/repo.git",
        Audit = new ProjectAudit
        {
            AuditTypes = ["security"],
            ExcludedAuditors = excluded,
        },
    };

    [Fact]
    public void DeploymentTarget_Enabled_IncludesHumanReviewer()
    {
        var composer = Composer(() => new HumanDeploymentReviewOptions { Enabled = true });

        var deployment = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Deployment)
            .Select(a => a.Name)
            .ToArray();
        Assert.Contains("human:deployment-review", deployment);

        var code = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();
        Assert.DoesNotContain("human:deployment-review", code);
    }

    [Fact]
    public void NoAccessorOrDisabled_HumanReviewerAbsent()
    {
        foreach (var composer in new[]
                 {
                     Composer(humanReview: null),
                     Composer(() => new HumanDeploymentReviewOptions { Enabled = false }),
                 })
        {
            var names = composer
                .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Deployment)
                .Select(a => a.Name)
                .ToArray();
            Assert.DoesNotContain("human:deployment-review", names);
        }
    }

    [Fact]
    public void ExcludedByName_HumanReviewerRemoved()
    {
        var composer = Composer(() => new HumanDeploymentReviewOptions { Enabled = true });

        var names = composer
            .ComposeForTarget(ProjectWith("human:deployment-review"), new FakeAgent(), AuditTarget.Deployment)
            .Select(a => a.Name)
            .ToArray();
        Assert.DoesNotContain("human:deployment-review", names);
    }

    // ── Durable store ───────────────────────────────────────────────────────

    private static HumanDeploymentReview Row(string wid = "item-1", int iteration = 1) => new()
    {
        WorkItemId = wid,
        Iteration = iteration,
        DeploymentId = "dep-1",
        EndpointJson = """{"Kind":0}""",
        Deadline = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
        RequestedAt = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
        Brief = "verify the widget",
        QuestionId = "human-deployment-review-1",
        HumanAuditorsJson = """["human:deployment-review"]""",
        CodeFindingsJson = "[]",
        CodeCompletedJson = """["code:scripted"]""",
        AutomatedFindingsJson = "[]",
        AutomatedCompletedJson = "[]",
        AutomatedIncompleteJson = "[]",
    };

    [Fact]
    public async Task Store_GetOrCreate_Verdict_Consume_ExpiryCas()
    {
        using var store = new SqliteHumanDeploymentReviewStore(_dbPath);

        var created = await store.GetOrCreatePendingAsync(Row());
        Assert.Equal(HumanDeploymentReviewStatus.Pending, created.Status);
        var same = await store.GetOrCreatePendingAsync(Row());
        Assert.Equal(created.DeploymentId, same.DeploymentId);

        Assert.Null(await store.TryGetAsync("item-1", 2));
        var fetched = await store.TryGetAsync("item-1", 1);
        Assert.Equal("dep-1", fetched!.DeploymentId);
        var active = await store.GetActiveForWorkItemAsync("item-1");
        Assert.Equal(1, active!.Iteration);

        // Verdict applies once, from pending only.
        Assert.True(await store.RecordVerdictAsync(
            "item-1", 1, approved: false, "broken", "op",
            new DateTimeOffset(2026, 9, 11, 1, 0, 0, TimeSpan.Zero)));
        Assert.False(await store.RecordVerdictAsync(
            "item-1", 1, approved: true, null, null,
            new DateTimeOffset(2026, 9, 11, 2, 0, 0, TimeSpan.Zero)));
        Assert.False(await store.MarkExpiredAsync(
            "item-1", 1, new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero)));
        var decided = await store.TryGetAsync("item-1", 1);
        Assert.Equal(HumanDeploymentReviewStatus.Rejected, decided!.Status);
        Assert.Equal("broken", decided.Notes);

        // Consume is idempotent; the row leaves the active set.
        await store.MarkConsumedAsync("item-1", 1, new DateTimeOffset(2026, 9, 11, 4, 0, 0, TimeSpan.Zero));
        await store.MarkConsumedAsync("item-1", 1, new DateTimeOffset(2026, 9, 11, 5, 0, 0, TimeSpan.Zero));
        Assert.Null(await store.GetActiveForWorkItemAsync("item-1"));

        // A consumed row can be replaced with a fresh pending review.
        var fresh = await store.GetOrCreatePendingAsync(Row() with { DeploymentId = "dep-2" });
        Assert.Equal("dep-2", fresh.DeploymentId);
        Assert.Equal(HumanDeploymentReviewStatus.Pending, fresh.Status);
    }

    [Fact]
    public async Task Store_ListExpiredPending_ReturnsOnlyUndecidedPastDeadline()
    {
        using var store = new SqliteHumanDeploymentReviewStore(_dbPath);
        var now = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        await store.GetOrCreatePendingAsync(Row("expired-item")
            with { Deadline = now.AddMinutes(-1) });
        await store.GetOrCreatePendingAsync(Row("fresh-item")
            with { Deadline = now.AddMinutes(1) });
        await store.GetOrCreatePendingAsync(Row("decided-item")
            with { Deadline = now.AddMinutes(-1) });
        await store.RecordVerdictAsync("decided-item", 1, true, null, null, now);

        var expired = await store.ListExpiredPendingAsync(now);
        Assert.Equal(["expired-item"], expired.Select(r => r.WorkItemId).ToArray());
    }
}
