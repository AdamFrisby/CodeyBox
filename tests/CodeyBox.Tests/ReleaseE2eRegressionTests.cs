using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Comprehensive test suite for E2E Regression testing as a release deep-audit dimension:
/// <list type="bullet">
///   <item>ITestCaseStore.ListByProjectAsync filtering by project.</item>
///   <item>IReleaseStore.SaveE2eReplayResultsAsync and ListE2eReplayResultsAsync persistence.</item>
///   <item>E2eRegressionDeepAuditor: selection modes ("all", "capability-filtered"), execution, throttling, error reporting.</item>
///   <item>ReleaseService deep-audit loop: regression-pass-merges, regression-fail-blocks, remediation convergence, gate disabled behavior, per-release config overrides.</item>
///   <item>GET /releases/{id}/e2e-results API endpoint.</item>
/// </list>
/// </summary>
[Collection("Background service timing")]
public sealed class ReleaseE2eRegressionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-e2e-reg-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly SqliteTestCaseStore _testCaseStore;
    private readonly SqliteE2eRunStore _runStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();

    public ReleaseE2eRegressionTests()
    {
        _releaseStore = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
        _testCaseStore = new SqliteTestCaseStore(_dbPath);
        _runStore = new SqliteE2eRunStore(_dbPath);
    }

    public void Dispose()
    {
        _runStore.Dispose();
        _testCaseStore.Dispose();
        _workItemStore.Dispose();
        _releaseStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── 1. Store persistence tests ──────────────────────────────────────────

    [Fact]
    public async Task TestCaseStore_ListByProjectAsync_FiltersByProject()
    {
        var projA = new ProjectId("proj-alpha");
        var projB = new ProjectId("proj-beta");

        var itemA = new WorkItem { Id = WorkItemId.New(), ProjectId = projA, Title = "A", Prompt = "p" };
        var itemB = new WorkItem { Id = WorkItemId.New(), ProjectId = projB, Title = "B", Prompt = "p" };
        await _workItemStore.CreateAsync(itemA);
        await _workItemStore.CreateAsync(itemB);

        var tcA1 = new TestCase { Id = "tc-a1", Name = "A1", Description = "d", SourceWorkItemId = itemA.Id.ToString() };
        var tcA2 = new TestCase { Id = "tc-a2", Name = "A2", Description = "d", SourceWorkItemId = itemA.Id.ToString() };
        var tcB1 = new TestCase { Id = "tc-b1", Name = "B1", Description = "d", SourceWorkItemId = itemB.Id.ToString() };
        await _testCaseStore.CreateAsync(tcA1);
        await _testCaseStore.CreateAsync(tcA2);
        await _testCaseStore.CreateAsync(tcB1);

        var casesA = new List<TestCase>();
        await foreach (var tc in _testCaseStore.ListByProjectAsync(projA))
            casesA.Add(tc);

        var casesB = new List<TestCase>();
        await foreach (var tc in _testCaseStore.ListByProjectAsync(projB))
            casesB.Add(tc);

        var casesC = new List<TestCase>();
        await foreach (var tc in _testCaseStore.ListByProjectAsync(new ProjectId("proj-gamma")))
            casesC.Add(tc);

        Assert.Equal(2, casesA.Count);
        Assert.All(casesA, c => Assert.Equal(projA, c.ProjectId));
        Assert.Contains(casesA, c => c.Id == "tc-a1");
        Assert.Contains(casesA, c => c.Id == "tc-a2");

        Assert.Single(casesB);
        Assert.Equal("tc-b1", casesB[0].Id);
        Assert.Equal(projB, casesB[0].ProjectId);

        Assert.Empty(casesC);
    }

    [Fact]
    public async Task ReleaseStore_SaveAndListE2eReplayResults_RoundTrips()
    {
        var releaseId = ReleaseId.New();
        var now = DateTimeOffset.UtcNow;

        var r1 = new ReleaseE2eReplayResult
        {
            ReleaseId = releaseId,
            Iteration = 1,
            TestCaseId = "tc-1",
            TestCaseName = "Login test",
            Label = "auth",
            Passed = true,
            Status = E2eRunStatus.Passed,
            ResultJson = "{\"passed\":true}",
            DurationMs = 150,
            CreatedAt = now,
        };
        var r2 = new ReleaseE2eReplayResult
        {
            ReleaseId = releaseId,
            Iteration = 1,
            TestCaseId = "tc-2",
            TestCaseName = "Checkout test",
            Label = "checkout",
            Passed = false,
            Status = E2eRunStatus.Failed,
            ResultJson = "{\"passed\":false,\"failureKind\":\"AssertionFailed\"}",
            DurationMs = 250,
            FailureKind = "AssertionFailed",
            Summary = "Payment button not clickable",
            CreatedAt = now,
        };
        var r3 = new ReleaseE2eReplayResult
        {
            ReleaseId = releaseId,
            Iteration = 2,
            TestCaseId = "tc-2",
            TestCaseName = "Checkout test",
            Label = "checkout",
            Passed = true,
            Status = E2eRunStatus.Passed,
            ResultJson = "{\"passed\":true}",
            DurationMs = 180,
            CreatedAt = now.AddMinutes(5),
        };

        await _releaseStore.SaveE2eReplayResultsAsync(releaseId, 1, [r1, r2]);
        await _releaseStore.SaveE2eReplayResultsAsync(releaseId, 2, [r3]);

        var all = await _releaseStore.ListE2eReplayResultsAsync(releaseId);
        Assert.Equal(3, all.Count);

        var iter1 = await _releaseStore.ListE2eReplayResultsAsync(releaseId, iteration: 1);
        Assert.Equal(2, iter1.Count);
        Assert.Contains(iter1, r => r.TestCaseId == "tc-1" && r.Passed);
        Assert.Contains(iter1, r => r.TestCaseId == "tc-2" && !r.Passed && r.FailureKind == "AssertionFailed");

        var iter2 = await _releaseStore.ListE2eReplayResultsAsync(releaseId, iteration: 2);
        Assert.Single(iter2);
        Assert.Equal("tc-2", iter2[0].TestCaseId);
        Assert.True(iter2[0].Passed);

        var iter3 = await _releaseStore.ListE2eReplayResultsAsync(releaseId, iteration: 3);
        Assert.Empty(iter3);
    }

    // ── 2. E2eRegressionDeepAuditor unit tests ────────────────────────────────

    [Fact]
    public async Task Auditor_WhenNoCommittedE2eCases_PassesImmediatelyWithNoFindings()
    {
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: true);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore);

        var ctx = new DeepAuditContext(
            ReleaseId: ReleaseId.New(),
            ProjectId: new ProjectId("empty-proj"),
            BranchName: "release/v1",
            Iteration: 1);

        var res = await auditor.RunAsync(new AlwaysSucceedSandbox(), "/work/repo", ctx);

        Assert.True(res.Passed);
        Assert.Empty(res.Findings);
    }

    [Fact]
    public async Task Auditor_SelectionAll_RunsAllCommittedE2eCases()
    {
        var projectId = new ProjectId("reg-proj");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: true);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var tc1 = await SeedTestCaseAsync(projectId, "tc-1", "Auth", "auth", AutomationKind.E2eReplay, MakeValidArtifact("auth"));
        var tc2 = await SeedTestCaseAsync(projectId, "tc-2", "Cart", "cart", AutomationKind.E2eReplay, MakeValidArtifact("cart"));
        // Unit test case: must be ignored by E2E regression auditor
        await SeedTestCaseAsync(projectId, "tc-unit", "Unit", "unit", AutomationKind.Unit, MakeValidArtifact("unit"));
        // E2E case without committed artifact: must be ignored
        await SeedTestCaseAsync(projectId, "tc-uncommitted", "Draft", "draft", AutomationKind.E2eReplay, artifactJson: null);

        var releaseId = ReleaseId.New();
        var ctx = new DeepAuditContext(
            ReleaseId: releaseId,
            ProjectId: projectId,
            BranchName: "release/v1",
            Iteration: 1,
            E2eRegressionConfig: new ReleaseE2eRegressionConfig { Enabled = true, Selection = "all" });

        var res = await auditor.RunAsync(new AlwaysSucceedSandbox(), "/work/repo", ctx);

        Assert.True(res.Passed);
        Assert.Empty(res.Findings);

        var saved = await _releaseStore.ListE2eReplayResultsAsync(releaseId);
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, r => r.TestCaseId == "tc-1" && r.Passed);
        Assert.Contains(saved, r => r.TestCaseId == "tc-2" && r.Passed);

        // Verify test cases were updated with last run status
        var updatedTc1 = await _testCaseStore.GetAsync("tc-1");
        Assert.True(updatedTc1!.LastRunPassed);
        Assert.NotNull(updatedTc1.LastRunAt);
    }

    [Fact]
    public async Task Auditor_SelectionCapabilityFiltered_MatchesOnlySpecifiedLabels()
    {
        var projectId = new ProjectId("filter-proj");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var executedNames = new List<string>();
        var runtime = new TestE2eReplayRuntime(artifact =>
        {
            lock (executedNames) { if (artifact.Name is not null) executedNames.Add(artifact.Name); }
            return Task.FromResult(new E2eRunResult { Passed = true });
        });
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore);

        await SeedTestCaseAsync(projectId, "tc-auth", "Auth", "auth", AutomationKind.E2eReplay, MakeValidArtifact("auth-flow"));
        await SeedTestCaseAsync(projectId, "tc-billing", "Billing", "billing", AutomationKind.E2eReplay, MakeValidArtifact("billing-flow"));
        await SeedTestCaseAsync(projectId, "tc-admin", "Admin", "admin", AutomationKind.E2eReplay, MakeValidArtifact("admin-flow"));

        var releaseId = ReleaseId.New();
        var ctx = new DeepAuditContext(
            ReleaseId: releaseId,
            ProjectId: projectId,
            BranchName: "release/v1",
            Iteration: 1,
            E2eRegressionConfig: new ReleaseE2eRegressionConfig
            {
                Enabled = true,
                Selection = "capability-filtered",
                Capabilities = ["Auth", "admin"],
            });

        var res = await auditor.RunAsync(new AlwaysSucceedSandbox(), "/work/repo", ctx);

        Assert.True(res.Passed);
        Assert.Equal(2, executedNames.Count);
        Assert.Contains("auth-flow", executedNames);
        Assert.Contains("admin-flow", executedNames);
        Assert.DoesNotContain("billing-flow", executedNames);

        var saved = await _releaseStore.ListE2eReplayResultsAsync(releaseId);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public async Task Auditor_ReplayFails_EmitsBlockingErrorFinding()
    {
        var projectId = new ProjectId("fail-proj");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(artifact =>
        {
            if (artifact.Name == "bad-flow")
            {
                return Task.FromResult(new E2eRunResult
                {
                    Passed = false,
                    Summary = "Step 2 timed out waiting for #submit",
                    FailureKind = "SelectorTimeout",
                });
            }
            return Task.FromResult(new E2eRunResult { Passed = true });
        });
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore);

        await SeedTestCaseAsync(projectId, "tc-pass", "Good", "good", AutomationKind.E2eReplay, MakeValidArtifact("good-flow"));
        await SeedTestCaseAsync(projectId, "tc-fail", "Bad", "bad", AutomationKind.E2eReplay, MakeValidArtifact("bad-flow"));

        var releaseId = ReleaseId.New();
        var ctx = new DeepAuditContext(
            ReleaseId: releaseId,
            ProjectId: projectId,
            BranchName: "release/v1",
            Iteration: 1);

        var res = await auditor.RunAsync(new AlwaysSucceedSandbox(), "/work/repo", ctx);

        Assert.False(res.Passed);
        Assert.Single(res.Findings);
        var finding = res.Findings[0];
        Assert.Equal("e2e-regression", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("Bad failed", finding.Title);
        Assert.Contains("SelectorTimeout", finding.Description);
        Assert.Equal("tc-fail", finding.Location);

        var saved = await _releaseStore.ListE2eReplayResultsAsync(releaseId);
        Assert.Equal(2, saved.Count);
        var failResult = saved.Single(r => r.TestCaseId == "tc-fail");
        Assert.False(failResult.Passed);
        Assert.Equal("SelectorTimeout", failResult.FailureKind);
    }

    [Fact]
    public async Task Auditor_InvalidArtifact_EmitsErrorFinding()
    {
        var projectId = new ProjectId("invalid-proj");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: true);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore);

        await SeedTestCaseAsync(projectId, "tc-broken", "Broken JSON", "broken", AutomationKind.E2eReplay, "not valid json {");

        var releaseId = ReleaseId.New();
        var ctx = new DeepAuditContext(
            ReleaseId: releaseId,
            ProjectId: projectId,
            BranchName: "release/v1",
            Iteration: 1);

        var res = await auditor.RunAsync(new AlwaysSucceedSandbox(), "/work/repo", ctx);

        Assert.False(res.Passed);
        Assert.Single(res.Findings);
        Assert.Equal(AuditSeverity.Error, res.Findings[0].Severity);

        var saved = await _releaseStore.ListE2eReplayResultsAsync(releaseId);
        Assert.Single(saved);
        Assert.False(saved[0].Passed);
        Assert.Equal(E2eRunStatus.Error, saved[0].Status);
    }

    [Fact]
    public async Task Auditor_ConcurrencyThrottled_RespectsPoolMaxConcurrent()
    {
        var projectId = new ProjectId("throttle-proj");
        var pool = new TestE2eExecutionPool(maxConcurrent: 2);
        var runtime = new TestE2eReplayRuntime(async _ =>
        {
            await Task.Delay(40);
            return new E2eRunResult { Passed = true };
        });
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore);

        for (var i = 1; i <= 6; i++)
        {
            await SeedTestCaseAsync(projectId, $"tc-{i}", $"Test {i}", "t", AutomationKind.E2eReplay, MakeValidArtifact($"flow-{i}"));
        }

        var releaseId = ReleaseId.New();
        var ctx = new DeepAuditContext(
            ReleaseId: releaseId,
            ProjectId: projectId,
            BranchName: "release/v1",
            Iteration: 1);

        var res = await auditor.RunAsync(new AlwaysSucceedSandbox(), "/work/repo", ctx);

        Assert.True(res.Passed);
        Assert.True(pool.PeakInFlight <= 2, $"Expected peak in-flight <= 2, was {pool.PeakInFlight}");
    }

    // ── 3. ReleaseService integration tests ──────────────────────────────────

    [Fact]
    public async Task ReleaseAudit_E2eRegressionEnabled_ReplaysPass_TransitionsToReleased()
    {
        var projectId = new ProjectId("release-e2e-pass");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: true);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "E2E Pass Project",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                E2eRegression = new ReleaseE2eRegressionConfig { Enabled = true },
                DeepAuditMaxIterations = 2,
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var queue = new AutoCompleteTaskQueue(_workItemStore);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            taskQueue: queue,
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost());

        await SeedTestCaseAsync(projectId, "tc-1", "Pass 1", "p1", AutomationKind.E2eReplay, MakeValidArtifact("p1"));
        await SeedTestCaseAsync(projectId, "tc-2", "Pass 2", "p2", AutomationKind.E2eReplay, MakeValidArtifact("p2"));

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, projectId: projectId.Value, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done, projectId);
        await _workItemStore.CreateAsync(item);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id, s => s is ReleaseState.Released or ReleaseState.Failed);
        Assert.Equal(ReleaseState.Released, final);

        var results = await _releaseStore.ListE2eReplayResultsAsync(rel.Id);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Passed));
    }

    [Fact]
    public async Task ReleaseAudit_E2eRegressionEnabled_ReplaysFail_BlocksMergeAndConvergesOnRemediation()
    {
        var projectId = new ProjectId("release-e2e-converge");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var callCount = 0;
        var runtime = new TestE2eReplayRuntime(_ =>
        {
            var call = Interlocked.Increment(ref callCount);
            // Fail on iteration 1, pass on iteration 2
            return Task.FromResult(new E2eRunResult
            {
                Passed = call > 1,
                Summary = call > 1 ? "Fixed" : "Regression detected",
                FailureKind = call > 1 ? null : "AssertionFailed",
            });
        });
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "E2E Converge Project",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                E2eRegression = new ReleaseE2eRegressionConfig { Enabled = true },
                DeepAuditMaxIterations = 3,
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var queue = new AutoCompleteTaskQueue(_workItemStore);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            taskQueue: queue,
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost());

        await SeedTestCaseAsync(projectId, "tc-1", "Flaky Flow", "flaky", AutomationKind.E2eReplay, MakeValidArtifact("flaky"));

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, projectId: projectId.Value, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done, projectId);
        await _workItemStore.CreateAsync(item);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id, s => s is ReleaseState.Released or ReleaseState.Failed);
        Assert.Equal(ReleaseState.Released, final);

        // Assert audit iterations recorded
        var iterations = await _releaseStore.ListAuditIterationsAsync(rel.Id);
        Assert.Equal(2, iterations.Count);
        Assert.Equal(1, iterations[0].BlockingFindings);
        Assert.Equal(0, iterations[1].BlockingFindings);

        // Assert remediation work item was dispatched
        Assert.NotNull(iterations[0].RemediationWorkItemId);
        Assert.Contains(_webhooks.Events, e => e.Event == "release.deep_audit_remediation_dispatched");

        // Assert results stored for both iterations
        var iter1Results = await _releaseStore.ListE2eReplayResultsAsync(rel.Id, iteration: 1);
        Assert.Single(iter1Results);
        Assert.False(iter1Results[0].Passed);

        var iter2Results = await _releaseStore.ListE2eReplayResultsAsync(rel.Id, iteration: 2);
        Assert.Single(iter2Results);
        Assert.True(iter2Results[0].Passed);
    }

    [Fact]
    public async Task ReleaseAudit_E2eRegressionEnabled_ReplaysContinueFailing_FailsReleaseAtMaxIterations()
    {
        var projectId = new ProjectId("release-e2e-fail");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: false);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "E2E Fail Project",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                E2eRegression = new ReleaseE2eRegressionConfig { Enabled = true },
                DeepAuditMaxIterations = 2,
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var queue = new AutoCompleteTaskQueue(_workItemStore);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            taskQueue: queue,
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost());

        await SeedTestCaseAsync(projectId, "tc-broken", "Broken Flow", "broken", AutomationKind.E2eReplay, MakeValidArtifact("broken"));

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, projectId: projectId.Value, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done, projectId);
        await _workItemStore.CreateAsync(item);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id, s => s is ReleaseState.Released or ReleaseState.Failed);
        Assert.Equal(ReleaseState.Failed, final);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.NotNull(refreshed!.FailedReason);
        Assert.Contains("deep audit did not converge", refreshed.FailedReason);
    }

    [Fact]
    public async Task ReleaseAudit_E2eRegressionDisabled_GateSkipped_TransitionsToReleased()
    {
        var projectId = new ProjectId("release-e2e-disabled");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: false); // Would fail if executed!
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "Disabled Project",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                E2eRegression = new ReleaseE2eRegressionConfig { Enabled = false },
                DeepAuditors = [], // None configured
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            gitHost: new DeepAuditTestGitHost());

        await SeedTestCaseAsync(projectId, "tc-bad", "Bad", "b", AutomationKind.E2eReplay, MakeValidArtifact("bad"));

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, projectId: projectId.Value, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done, projectId);
        await _workItemStore.CreateAsync(item);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id, s => s is ReleaseState.Released or ReleaseState.Failed);
        Assert.Equal(ReleaseState.Released, final);

        // No E2E results should have been recorded because gate was disabled
        var results = await _releaseStore.ListE2eReplayResultsAsync(rel.Id);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ReleaseAudit_PerReleaseOverride_EnablesGate()
    {
        var projectId = new ProjectId("override-enable");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: false);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "Project",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                E2eRegression = new ReleaseE2eRegressionConfig { Enabled = false }, // Disabled on project
                DeepAuditMaxIterations = 1,
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var queue = new AutoCompleteTaskQueue(_workItemStore);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            taskQueue: queue,
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost());

        await SeedTestCaseAsync(projectId, "tc-f", "Failing", "f", AutomationKind.E2eReplay, MakeValidArtifact("f"));

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, projectId: projectId.Value, branchName: "release/v1.0") with
        {
            ConfigJson = "{\"e2eRegression\": {\"enabled\": true}}", // Enabled via override
        };
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done, projectId);
        await _workItemStore.CreateAsync(item);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id, s => s is ReleaseState.Released or ReleaseState.Failed);
        Assert.Equal(ReleaseState.Failed, final);
    }

    [Fact]
    public async Task ReleaseAudit_PerReleaseOverride_DisablesGate()
    {
        var projectId = new ProjectId("override-disable");
        var pool = new TestE2eExecutionPool(maxConcurrent: 4);
        var runtime = new TestE2eReplayRuntime(defaultPassed: false);
        var auditor = new E2eRegressionDeepAuditor(_testCaseStore, pool, runtime, _releaseStore, _runStore);

        var project = new Project
        {
            Id = projectId,
            DisplayName = "Project",
            RepositoryUrl = "file:///tmp/noop",
            ReleaseConfig = new ProjectReleaseConfig
            {
                Enabled = true,
                E2eRegression = new ReleaseE2eRegressionConfig { Enabled = true }, // Enabled on project
                DeepAuditors = ["e2e-regression"],
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            gitHost: new DeepAuditTestGitHost());

        await SeedTestCaseAsync(projectId, "tc-f", "Failing", "f", AutomationKind.E2eReplay, MakeValidArtifact("f"));

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, projectId: projectId.Value, branchName: "release/v1.0") with
        {
            ConfigJson = "{\"e2eRegression\": {\"enabled\": false}}", // Disabled via override
        };
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done, projectId);
        await _workItemStore.CreateAsync(item);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id, s => s is ReleaseState.Released or ReleaseState.Failed);
        Assert.Equal(ReleaseState.Released, final);

        var results = await _releaseStore.ListE2eReplayResultsAsync(rel.Id);
        Assert.Empty(results);
    }

    // ── 4. API endpoint tests ────────────────────────────────────────────────

    [Fact]
    public async Task GetE2eResults_UnknownRelease_Returns404()
    {
        using var factory = new ReleaseE2eApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/releases/{ReleaseId.New()}/e2e-results");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetE2eResults_InvalidReleaseId_Returns400()
    {
        using var factory = new ReleaseE2eApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/releases/invalid-guid/e2e-results");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetE2eResults_ReturnsSavedResults_AndFiltersByIteration()
    {
        using var factory = new ReleaseE2eApiFactory();
        using var client = factory.CreateClient();

        var relId = ReleaseId.New();
        var release = new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = "rel-1.0",
            State = ReleaseState.InReview,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await factory.ReleaseStore.CreateAsync(release);

        await factory.ReleaseStore.SaveE2eReplayResultsAsync(relId, 1,
        [
            new ReleaseE2eReplayResult
            {
                ReleaseId = relId,
                Iteration = 1,
                TestCaseId = "tc-1",
                TestCaseName = "Test 1",
                Label = "auth",
                Passed = false,
                Status = E2eRunStatus.Failed,
                FailureKind = "AssertionFailed",
                Summary = "failed assertion",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        ]);

        await factory.ReleaseStore.SaveE2eReplayResultsAsync(relId, 2,
        [
            new ReleaseE2eReplayResult
            {
                ReleaseId = relId,
                Iteration = 2,
                TestCaseId = "tc-1",
                TestCaseName = "Test 1",
                Label = "auth",
                Passed = true,
                Status = E2eRunStatus.Passed,
                CreatedAt = DateTimeOffset.UtcNow,
            }
        ]);

        // GET all results
        var allResp = await client.GetAsync($"/releases/{relId}/e2e-results");
        Assert.Equal(HttpStatusCode.OK, allResp.StatusCode);
        var allList = await allResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(allList);
        Assert.Equal(2, allList.Count);

        // GET iteration 1
        var iter1Resp = await client.GetAsync($"/releases/{relId}/e2e-results?iteration=1");
        Assert.Equal(HttpStatusCode.OK, iter1Resp.StatusCode);
        var iter1List = await iter1Resp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(iter1List);
        Assert.Single(iter1List);
        Assert.False(iter1List[0].GetProperty("passed").GetBoolean());
        Assert.Equal("AssertionFailed", iter1List[0].GetProperty("failureKind").GetString());

        // GET iteration 2
        var iter2Resp = await client.GetAsync($"/releases/{relId}/e2e-results?iteration=2");
        Assert.Equal(HttpStatusCode.OK, iter2Resp.StatusCode);
        var iter2List = await iter2Resp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(iter2List);
        Assert.Single(iter2List);
        Assert.True(iter2List[0].GetProperty("passed").GetBoolean());
    }

    // ── Helper methods ───────────────────────────────────────────────────────

    private async Task<string> SeedTestCaseAsync(
        ProjectId projectId,
        string id,
        string name,
        string? label,
        AutomationKind automationKind,
        string? artifactJson)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = $"Fixture item for {name}",
            Prompt = "n/a",
        };
        await _workItemStore.CreateAsync(item);

        var tc = new TestCase
        {
            Id = id,
            Name = name,
            Description = $"Desc for {name}",
            Label = label,
            SourceWorkItemId = item.Id.ToString(),
            AutomationKind = automationKind,
            ExecutableArtifactJson = artifactJson,
        };
        await _testCaseStore.CreateAsync(tc);
        return id;
    }

    private static string MakeValidArtifact(string name)
        => JsonSerializer.Serialize(new E2eReplayArtifact
        {
            Name = name,
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
            Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#root" }],
        });

    private static WorkItem MakeWorkItem(ReleaseId releaseId, WorkItemState state, ProjectId? projectId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = projectId ?? new ProjectId("test-project"),
        Title = "Release content item",
        Prompt = "test prompt",
        ReleaseId = releaseId,
        State = state,
    };

    private async Task<ReleaseState> PollUntilAsync(
        ReleaseId id,
        Func<ReleaseState, bool> predicate,
        int timeoutSeconds = 5)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var r = await _releaseStore.GetAsync(id);
            if (r is not null && predicate(r.State))
                return r.State;
            await Task.Delay(20);
        }
        var final = await _releaseStore.GetAsync(id);
        return final?.State ?? ReleaseState.Open;
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class TestE2eExecutionPool : IE2eExecutionPool
    {
        private readonly int _maxConcurrent;
        private int _inFlight;
        public int PeakInFlight { get; private set; }

        public TestE2eExecutionPool(int maxConcurrent = 10)
        {
            _maxConcurrent = maxConcurrent;
        }

        public string Name => "test-pool";
        public int MaxConcurrent => _maxConcurrent;
        public int InFlight => Volatile.Read(ref _inFlight);

        public Task<IE2eExecutionSlot> LeaseAsync(CancellationToken ct = default)
        {
            var cur = Interlocked.Increment(ref _inFlight);
            lock (this)
            {
                if (cur > PeakInFlight) PeakInFlight = cur;
            }
            return Task.FromResult<IE2eExecutionSlot>(new TestSlot(this));
        }

        private sealed class TestSlot : IE2eExecutionSlot
        {
            private readonly TestE2eExecutionPool _pool;
            private int _disposed;

            public TestSlot(TestE2eExecutionPool pool)
            {
                _pool = pool;
            }

            public ISandbox Sandbox { get; } = new AlwaysSucceedSandbox();
            public string SandboxId => "slot-sandbox";

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref _pool._inFlight);
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestE2eReplayRuntime : IE2eReplayRuntime
    {
        private readonly Func<E2eReplayArtifact, Task<E2eRunResult>> _handler;

        public TestE2eReplayRuntime(Func<E2eReplayArtifact, Task<E2eRunResult>> handler)
        {
            _handler = handler;
        }

        public TestE2eReplayRuntime(bool defaultPassed = true)
        {
            _handler = artifact => Task.FromResult(new E2eRunResult
            {
                Passed = defaultPassed,
                Summary = defaultPassed ? "All steps passed" : "Assertion failed",
                FailureKind = defaultPassed ? null : "AssertionFailed",
            });
        }

        public Task<E2eRunResult> ExecuteAsync(E2eReplayArtifact artifact, ISandbox sandbox, CancellationToken ct = default)
            => _handler(artifact);
    }

    private sealed class ReleaseE2eApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"cb-e2e-api-{Guid.NewGuid():N}.db");

        public SqliteReleaseStore ReleaseStore { get; }
        public SqliteWorkItemStore WorkItemStore { get; }

        public ReleaseE2eApiFactory()
        {
            ReleaseStore = new SqliteReleaseStore(_dbPath);
            WorkItemStore = new SqliteWorkItemStore(_dbPath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.RemoveAll<IReleaseStore>();
                services.AddSingleton<IReleaseStore>(ReleaseStore);

                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(WorkItemStore);

                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                    new Project
                    {
                        Id = new ProjectId("test-project"),
                        DisplayName = "Test Project",
                        RepositoryUrl = "https://github.com/test/repo",
                        ReleaseConfig = new ProjectReleaseConfig { Enabled = true },
                    }));
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WorkItemStore.Dispose();
                ReleaseStore.Dispose();
                try { File.Delete(_dbPath); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
