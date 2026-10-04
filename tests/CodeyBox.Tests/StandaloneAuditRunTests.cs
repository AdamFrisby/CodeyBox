using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CodeyBox.Audit;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Standalone audit-run acceptance: selection validation, durable lifecycle,
/// idempotent replay, per-auditor/aggregate truthfulness, cancellation,
/// recovery, artifact bounds, cross-project denial, real tool execution
/// through the public service path, and proof that no coding-agent,
/// commit, merge, or push occurs.
/// </summary>
public sealed class StandaloneAuditRunTests
{
    private const string ProjectA = "audit-proj-a";
    private const string ProjectB = "audit-proj-b";
    private const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // ── Pure validation ──────────────────────────────────────────────

    [Fact]
    public void Selection_RequiresExactlyOneMechanism()
    {
        Assert.NotNull(AuditRunValidation.ValidateSelection(new AuditRunSelection()));
        Assert.NotNull(AuditRunValidation.ValidateSelection(new AuditRunSelection
        {
            AuditorIds = ["a"],
            Profile = "p",
        }));
        Assert.Null(AuditRunValidation.ValidateSelection(new AuditRunSelection { AuditorIds = ["a"] }));
        Assert.Null(AuditRunValidation.ValidateSelection(new AuditRunSelection { Profile = "p" }));
    }

    [Fact]
    public void Selection_RejectsDuplicatesAndOverflow()
    {
        Assert.NotNull(AuditRunValidation.ValidateSelection(new AuditRunSelection { AuditorIds = ["a", "a"] }));
        Assert.NotNull(AuditRunValidation.ValidateSelection(new AuditRunSelection
        {
            AuditorIds = Enumerable.Range(0, AuditRunValidation.MaxAuditorsPerRun + 1).Select(i => $"a{i}").ToList(),
        }));
    }

    [Fact]
    public void Ref_RejectsInjectionShapes()
    {
        Assert.Null(AuditRunValidation.ValidateRef(Sha1, "ref"));
        Assert.NotNull(AuditRunValidation.ValidateRef(null, "ref"));
        Assert.NotNull(AuditRunValidation.ValidateRef("main; rm -rf /", "ref"));
        Assert.NotNull(AuditRunValidation.ValidateRef("../../etc/passwd", "ref"));
        Assert.NotNull(AuditRunValidation.ValidateRef("$(evil)", "ref"));
    }

    [Fact]
    public async Task ShaResolver_AcceptsOnlyExplicitSha()
    {
        var resolver = new ExplicitShaAuditRefResolver();
        var ok = await resolver.ResolveAsync("p", Sha1);
        Assert.True(ok.Ok);
        Assert.Equal(Sha1, ok.Sha);
        Assert.False((await resolver.ResolveAsync("p", "main")).Ok);
        Assert.False((await resolver.ResolveAsync("p", "HEAD~1")).Ok);
        Assert.False((await resolver.ResolveAsync("p", "../escape")).Ok);
    }

    [Fact]
    public void Classify_RejectsLlmAndAgentCredentialsWithReasons()
    {
        var llm = new FakeAuditor("review:llm", "llm");
        var (s1, r1, d1) = AuditRunValidation.ClassifyAuditor(llm, false, false, true);
        Assert.False(s1);
        Assert.Equal(AuditRunUnsupportedReason.LlmAuditor, r1);
        Assert.Contains("review:llm", d1);

        var creds = new FakeAuditor("needs-creds", "shell", AuditCapabilities.AgentCredentials);
        var (s2, r2, _) = AuditRunValidation.ClassifyAuditor(creds, false, false, true);
        Assert.False(s2);
        Assert.Equal(AuditRunUnsupportedReason.AgentCredentialsRequired, r2);

        var diff = new FakeAuditor("diff:patterns", "diff-pattern");
        var (s3, r3, _) = AuditRunValidation.ClassifyAuditor(diff, true, false, true);
        Assert.False(s3);
        Assert.Equal(AuditRunUnsupportedReason.MissingExplicitDiffBase, r3);
        var (s4, _, _) = AuditRunValidation.ClassifyAuditor(diff, true, true, true);
        Assert.True(s4);

        var tool = new FakeAuditor("tool:lint", "shell");
        var (s5, _, _) = AuditRunValidation.ClassifyAuditor(tool, false, false, true);
        Assert.True(s5);
        var (s6, r6, _) = AuditRunValidation.ClassifyAuditor(tool, false, false, false);
        Assert.False(s6);
        Assert.Equal(AuditRunUnsupportedReason.DisabledAuditor, r6);
    }

    [Fact]
    public void Options_DisabledByDefault()
    {
        Assert.False(new AuditRunOptions().Enabled);
        Assert.True(AuditRunOptions.IsValid(new AuditRunOptions()));
        var invalid = new AuditRunOptions { MaxConcurrentRuns = 0 };
        Assert.False(AuditRunOptions.IsValid(invalid));
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    [Fact]
    public void Lifecycle_TerminalStatesAreSticky()
    {
        foreach (var terminal in new[] { AuditRunState.Passed, AuditRunState.Findings, AuditRunState.Cancelled, AuditRunState.Failed })
        {
            Assert.True(AuditRunLifecycle.IsTerminal(terminal));
            Assert.False(AuditRunLifecycle.CanTransition(terminal, AuditRunState.Running));
            Assert.False(AuditRunLifecycle.CanTransition(terminal, AuditRunState.Cancelled));
        }
        Assert.True(AuditRunLifecycle.CanTransition(AuditRunState.Queued, AuditRunState.Provisioning));
        Assert.True(AuditRunLifecycle.CanTransition(AuditRunState.Provisioning, AuditRunState.Running));
        Assert.True(AuditRunLifecycle.CanTransition(AuditRunState.Running, AuditRunState.Collecting));
        Assert.True(AuditRunLifecycle.CanTransition(AuditRunState.Collecting, AuditRunState.Passed));
        Assert.True(AuditRunLifecycle.CanTransition(AuditRunState.Queued, AuditRunState.Cancelled));
        Assert.False(AuditRunLifecycle.CanTransition(AuditRunState.Queued, AuditRunState.Running));
    }

    [Theory]
    [InlineData(new[] { AuditRunAuditorOutcome.Pass }, true, AuditRunAggregateOutcome.Pass)]
    [InlineData(new[] { AuditRunAuditorOutcome.Pass, AuditRunAuditorOutcome.Fail }, true, AuditRunAggregateOutcome.Findings)]
    [InlineData(new[] { AuditRunAuditorOutcome.Fail, AuditRunAuditorOutcome.Error }, true, AuditRunAggregateOutcome.InfrastructureFailure)]
    [InlineData(new[] { AuditRunAuditorOutcome.Unavailable }, true, AuditRunAggregateOutcome.MissingTool)]
    [InlineData(new[] { AuditRunAuditorOutcome.Unsupported }, true, AuditRunAggregateOutcome.Unsupported)]
    [InlineData(new[] { AuditRunAuditorOutcome.Pass, AuditRunAuditorOutcome.Unsupported }, true, AuditRunAggregateOutcome.Unsupported)]
    [InlineData(new[] { AuditRunAuditorOutcome.Cancelled }, true, AuditRunAggregateOutcome.Cancelled)]
    [InlineData(new[] { AuditRunAuditorOutcome.TimedOut }, true, AuditRunAggregateOutcome.InfrastructureFailure)]
    [InlineData(new[] { AuditRunAuditorOutcome.NotRun }, true, AuditRunAggregateOutcome.Unsupported)]
    public void Aggregate_RemainsTruthful(AuditRunAuditorOutcome[] outcomes, bool evidence, AuditRunAggregateOutcome expected)
    {
        var now = DateTimeOffset.UtcNow;
        var results = outcomes.Select((o, i) => new AuditRunAuditorResult
        {
            AuditorName = $"a{i}",
            AuditorKind = "shell",
            Outcome = o,
            StartedAt = now,
            EndedAt = now,
            EvidenceSufficient = evidence,
        }).ToList();
        Assert.Equal(expected, AuditRunLifecycle.Aggregate(results));
    }

    [Fact]
    public void Aggregate_EmptyOrEvidenceGapNeverPasses()
    {
        Assert.Equal(AuditRunAggregateOutcome.InfrastructureFailure, AuditRunLifecycle.Aggregate([]));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(
            AuditRunAggregateOutcome.InfrastructureFailure,
            AuditRunLifecycle.Aggregate([new AuditRunAuditorResult
            {
                AuditorName = "a", AuditorKind = "shell", Outcome = AuditRunAuditorOutcome.Pass,
                StartedAt = now, EndedAt = now, EvidenceSufficient = false,
            }]));
    }

    [Fact]
    public void Redaction_RemovesSecretsFromLogs()
    {
        var redacted = AuditRunValidation.Redact("ok line\napi_key=supersecret\nBearer abc123\nclean");
        Assert.Contains("ok line", redacted);
        Assert.DoesNotContain("supersecret", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.Equal("[...truncated]", AuditRunValidation.TruncateBounded(new string('x', 100), 10)[10..]);
    }

    // ── Service: create validation ───────────────────────────────────

    [Fact]
    public async Task Create_DisabledRejects()
    {
        var harness = TestHarness.WithOptions(new AuditRunOptions { Enabled = false });
        var outcome = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["t"] },
        });
        Assert.Null(outcome.Run);
        Assert.Contains("disabled", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_RejectsUnknownProjectAuditorProfileRefAndRepo()
    {
        var harness = TestHarness.Create();
        async Task<string?> Reject(AuditRunCreateRequest req) =>
            (await harness.Service.CreateAsync(req)).Error;

        Assert.Contains("Unknown project", await Reject(new AuditRunCreateRequest
        {
            ProjectId = "nope", Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        }));
        Assert.Contains("Unknown auditor", await Reject(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["nope"] },
        }));
        Assert.Contains("Unknown audit profile", await Reject(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { Profile = "nope" },
        }));
        Assert.Contains("Invalid ref", await Reject(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = "main", Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        }));
        Assert.Contains("repository", await Reject(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
            RepositoryOverride = "https://evil.example/other",
        }));
        Assert.Contains("either explicit", await Reject(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection(),
        }));
    }

    [Fact]
    public async Task Create_RepositoryOverrideMatchingProjectPolicyAccepted()
    {
        var harness = TestHarness.Create();
        var outcome = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1,
            Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
            RepositoryOverride = "https://example.com/repo-a.git",
        });
        Assert.NotNull(outcome.Run);
        var terminal = await harness.WaitForTerminalAsync(outcome.Run.Id);
        Assert.Equal(AuditRunState.Passed, terminal.State);
    }

    [Fact]
    public async Task Create_LlmSelectionRecordedAsUnsupportedNeverProvisioned()
    {
        var harness = TestHarness.Create(extraAuditors: [new FakeAuditor("review:llm", "llm")]);
        var outcome = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["review:llm"] },
        });
        Assert.NotNull(outcome.Run);
        var terminal = await harness.WaitForTerminalAsync(outcome.Run.Id);
        Assert.Equal(AuditRunState.Failed, terminal.State);
        Assert.Equal(AuditRunAggregateOutcome.Unsupported, terminal.AggregateOutcome);
        var member = Assert.Single(terminal.AuditorResults);
        Assert.Equal(AuditRunAuditorOutcome.Unsupported, member.Outcome);
        Assert.Equal(AuditRunUnsupportedReason.LlmAuditor, member.UnsupportedReason);
        Assert.NotNull(member.UnsupportedDetail);
        Assert.Equal(0, harness.Executor.Invocations);
    }

    [Fact]
    public async Task Create_DiffAuditorWithoutBaseIsUnsupportedWithBaseRuns()
    {
        var harness = TestHarness.Create(extraAuditors: [new FakeAuditor("diff:patterns", "diff-pattern")]);
        var noBase = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["diff:patterns"] },
        });
        Assert.NotNull(noBase.Run);
        var t1 = await harness.WaitForTerminalAsync(noBase.Run.Id);
        Assert.Equal(AuditRunAggregateOutcome.Unsupported, t1.AggregateOutcome);

        var withBase = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, BaseRef = Sha2,
            Selection = new AuditRunSelection { AuditorIds = ["diff:patterns"] },
        });
        Assert.NotNull(withBase.Run);
        Assert.Equal(Sha1, withBase.Run.Provenance.ResolvedSha);
        Assert.Equal(Sha2, withBase.Run.Provenance.ResolvedBaseSha);
        var t2 = await harness.WaitForTerminalAsync(withBase.Run.Id);
        Assert.Equal(AuditRunState.Passed, t2.State);
    }

    [Fact]
    public async Task Create_IdempotentReplayReturnsSameRunConflictOnBodyMismatch()
    {
        var harness = TestHarness.Create();
        AuditRunCreateRequest Req(string murmur) => new()
        {
            ProjectId = ProjectA, Ref = Sha1,
            Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
            IdempotencyKey = "fixed-key-1",
            BaseRef = murmur,
        };
        var first = await harness.Service.CreateAsync(Req(null!));
        Assert.NotNull(first.Run);
        Assert.False(first.Replay);
        var replay = await harness.Service.CreateAsync(Req(null!));
        Assert.True(replay.Replay);
        Assert.Equal(first.Run.Id, replay.Run!.Id);
        var clash = await harness.Service.CreateAsync(Req(Sha2));
        Assert.Null(clash.Run);
        Assert.True(clash.Conflict);
    }

    [Fact]
    public async Task Get_ScopedReadDeniesCrossProject()
    {
        var harness = TestHarness.Create();
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        Assert.NotNull(created.Run);
        Assert.NotNull(await harness.Service.GetAsync(created.Run.Id, ProjectA));
        Assert.Null(await harness.Service.GetAsync(created.Run.Id, ProjectB));
        var listed = await harness.Service.ListAsync(ProjectB, 10);
        Assert.DoesNotContain(listed, r => r.Id == created.Run.Id);
    }

    [Fact]
    public async Task Service_NeverCouplesToCodingAgents()
    {
        var fields = typeof(StandaloneAuditService).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, f =>
            f.FieldType.Name.Contains("AgentRunner") || f.FieldType.Name.Contains("AgentExecutor"));
        var ctors = typeof(StandaloneAuditService).GetConstructors();
        Assert.DoesNotContain(ctors.SelectMany(c => c.GetParameters()), p =>
            p.ParameterType.Name.Contains("IAgentRunner"));
    }

    // ── Execution outcomes ───────────────────────────────────────────

    [Fact]
    public async Task Execute_PassAndFindingsWithEvidenceAndDigests()
    {
        var harness = TestHarness.Create();
        harness.Executor.Script["tool:lint"] = _ => Task.FromResult(
            new StandaloneAuditorExecution(true, [], "lint clean", 0, "lint 1.2.3", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        Assert.Equal(AuditRunState.Passed, terminal.State);
        Assert.Equal(AuditRunAggregateOutcome.Pass, terminal.AggregateOutcome);
        var result = Assert.Single(terminal.AuditorResults);
        Assert.Equal("lint 1.2.3", result.ToolVersion);
        Assert.True(result.EvidenceSufficient);
        Assert.NotNull(result.LogArtifactDigest);
        var artifact = Assert.Single(terminal.Artifacts);
        Assert.Equal(result.LogArtifactDigest, artifact.ContentDigest);
        var blob = await harness.Artifacts.GetAsync(terminal.Id, artifact.Name);
        Assert.NotNull(blob);
        Assert.Equal("lint clean", Encoding.UTF8.GetString(blob.Value.Content));
        Assert.Equal(Sha1, terminal.Provenance.ResolvedSha);
        Assert.NotEmpty(terminal.Provenance.ConfigDigest);
    }

    [Fact]
    public async Task Execute_FailingToolYieldsFindingsAndPreservesThem()
    {
        var harness = TestHarness.Create();
        harness.Executor.Script["tool:lint"] = _ => Task.FromResult(
            new StandaloneAuditorExecution(false,
                [new AuditFinding("tool:lint", AuditSeverity.Error, "bad line", "line is bad", "a.cs")],
                "lint output", 1, "lint 1.2.3", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        Assert.Equal(AuditRunState.Findings, terminal.State);
        Assert.Equal(AuditRunAggregateOutcome.Findings, terminal.AggregateOutcome);
        var finding = Assert.Single(Assert.Single(terminal.AuditorResults).Findings);
        Assert.Equal("Error", finding.Severity);
        Assert.Equal(["a.cs"], finding.Files);
    }

    [Fact]
    public async Task Execute_MissingToolFailsVisiblyNeverPasses()
    {
        var harness = TestHarness.Create(failWorkspace: "No standalone-audit workspace provider is configured.");
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        Assert.Equal(AuditRunState.Failed, terminal.State);
        Assert.Equal(AuditRunAggregateOutcome.InfrastructureFailure, terminal.AggregateOutcome);
        Assert.Contains("provisioning", terminal.FailureDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_UnavailableAuditorMapsToMissingTool()
    {
        var harness = TestHarness.Create();
        harness.Executor.Script["tool:lint"] = _ => throw new AuditUnavailableException("could-not-verify: lint not installed");
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        Assert.Equal(AuditRunAggregateOutcome.MissingTool, terminal.AggregateOutcome);
        Assert.Equal(AuditRunAuditorOutcome.Unavailable, Assert.Single(terminal.AuditorResults).Outcome);
    }

    [Fact]
    public async Task Execute_TimeoutPreservesPartialWithoutPromotingToPass()
    {
        var harness = TestHarness.WithOptions(new AuditRunOptions { Enabled = true, PerAuditorTimeoutSeconds = 1 });
        harness.Executor.Script["tool:lint"] = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new StandaloneAuditorExecution(true, [], "late", 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        };
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id, TimeSpan.FromSeconds(30));
        Assert.Equal(AuditRunAuditorOutcome.TimedOut, Assert.Single(terminal.AuditorResults).Outcome);
        Assert.Equal(AuditRunAggregateOutcome.InfrastructureFailure, terminal.AggregateOutcome);
        Assert.NotEqual(AuditRunState.Passed, terminal.State);
    }

    [Fact]
    public async Task Execute_OversizedArtifactWarnsWithoutStoring()
    {
        var harness = TestHarness.WithOptions(new AuditRunOptions { Enabled = true, MaxArtifactBytes = 10 });
        harness.Executor.Script["tool:lint"] = _ => Task.FromResult(
            new StandaloneAuditorExecution(false,
                [new AuditFinding("tool:lint", AuditSeverity.Error, "bad", "bad", null)],
                new string('x', 100), 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        Assert.Equal(AuditRunAggregateOutcome.Findings, terminal.AggregateOutcome);
        Assert.Empty(terminal.Artifacts);
        Assert.Contains(terminal.AuditorResults[0].Findings, f => f.Title == "Artifact oversized");
    }

    [Fact]
    public async Task Execute_RedactsAndBoundsLogs()
    {
        var harness = TestHarness.WithOptions(new AuditRunOptions { Enabled = true, MaxRawOutputChars = 16 });
        harness.Executor.Script["tool:lint"] = _ => Task.FromResult(
            new StandaloneAuditorExecution(true, [], "api_key=hunter2-very-long-secret-value", 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        var excerpt = Assert.Single(terminal.AuditorResults).LogExcerpt;
        Assert.NotNull(excerpt);
        Assert.DoesNotContain("hunter2", excerpt);
    }

    [Fact]
    public async Task ArtifactStore_EnforcesCapAtSink()
    {
        var store = new InMemoryAuditRunArtifactStore(maxBytesPerArtifact: 4);
        await Assert.ThrowsAsync<AuditRunArtifactTooLargeException>(() =>
            store.PutAsync("r", "a.log", "text/plain", new byte[5]));
    }

    [Fact]
    public async Task Cancel_TerminatesAndPreservesCompletedFindings()
    {
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = TestHarness.Create();
        harness.Executor.Script["tool:lint"] = async _ =>
        {
            await gate.Task;
            return new StandaloneAuditorExecution(true, [], "done", 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        };
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        await harness.WaitForStateAsync(created.Run!.Id, AuditRunState.Collecting);
        var (ok, error) = await harness.Service.CancelAsync(created.Run.Id);
        Assert.True(ok, error);
        gate.TrySetResult(null);
        var terminal = await harness.WaitForTerminalAsync(created.Run.Id);
        Assert.Equal(AuditRunState.Cancelled, terminal.State);
        Assert.Equal(AuditRunAggregateOutcome.Cancelled, terminal.AggregateOutcome);
    }

    [Fact]
    public async Task Cancel_TerminalRunRejected()
    {
        var harness = TestHarness.Create();
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        await harness.WaitForTerminalAsync(created.Run!.Id);
        var (ok, _) = await harness.Service.CancelAsync(created.Run.Id);
        Assert.False(ok);
    }

    [Fact]
    public async Task Recovery_MarksInterruptedWithoutRerun()
    {
        var harness = TestHarness.Create();
        var now = DateTimeOffset.UtcNow;
        var stuck = new AuditRunRecord
        {
            Id = "stuck-run",
            ProjectId = ProjectA,
            State = AuditRunState.Running,
            AggregateOutcome = AuditRunAggregateOutcome.Unknown,
            Provenance = new AuditRunProvenance
            {
                ProjectId = ProjectA, RequestedRef = Sha1, ResolvedSha = Sha1,
                ResolvedAuditorIds = ["tool:lint"], ConfigDigest = "abc", CreatedAt = now,
            },
            CreatedAt = now,
            UpdatedAt = now,
        };
        await harness.Runs.CreateAsync(stuck);
        var count = await harness.Service.ReconcileInterruptedAsync();
        Assert.Equal(1, count);
        var after = await harness.Runs.GetAsync("stuck-run");
        Assert.NotNull(after);
        Assert.Equal(AuditRunState.Failed, after.State);
        Assert.Contains("interrupted", after.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Executor.Invocations);
    }

    [Fact]
    public async Task Capacity_LimitedQueueRejectsOverflow()
    {
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = TestHarness.WithOptions(new AuditRunOptions { Enabled = true, MaxQueuedRuns = 1 });
        harness.Executor.Script["tool:lint"] = async _ =>
        {
            await gate.Task;
            return new StandaloneAuditorExecution(true, [], "done", 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        };
        var first = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        Assert.NotNull(first.Run);
        await harness.WaitForStateAsync(first.Run.Id, AuditRunState.Collecting);
        var second = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha2, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        Assert.Null(second.Run);
        Assert.Contains("capacity", second.Error, StringComparison.OrdinalIgnoreCase);
        gate.TrySetResult(null);
        await harness.WaitForTerminalAsync(first.Run.Id);
    }

    [Fact]
    public async Task TeardownFailure_DoesNotBlockTerminalResult()
    {
        var harness = TestHarness.Create(failTeardown: true);
        var created = await harness.Service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["tool:lint"] },
        });
        var terminal = await harness.WaitForTerminalAsync(created.Run!.Id);
        Assert.Equal(AuditRunState.Passed, terminal.State);
        Assert.True(harness.Workspaces.TeardownAttempts > 0);
    }

    // ── Real tool execution through the public path ──────────────────

    [Fact]
    public async Task RealTool_ViolationFindingsThenPassAfterFix()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "standalone-audit");
        Assert.True(Directory.Exists(fixtureRoot), "fixture missing");
        using var scratch = new TempDir();
        var workDir = Path.Combine(scratch.Path, "tree");
        CopyDir(fixtureRoot, workDir);

        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "standalone:fixture-grep",
            Argv = ["sh", "-c", "grep -rq \"STANDALONE_AUDIT_VIOLATION\" . && echo VIOLATION-FOUND; ! grep -rq \"STANDALONE_AUDIT_VIOLATION\" ."],
        });
        var runs = new InMemoryAuditRunStore();
        var artifacts = new InMemoryAuditRunArtifactStore();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId(ProjectA),
            DisplayName = "Audit",
            RepositoryUrl = "https://example.com/repo-a.git",
        });
        var options = new AuditRunOptions { Enabled = true };
        var registry = new AuditorRegistry([auditor]);
        var harnessDir = workDir;
        var workspaces = new ScriptWorkspaceFactory(dir => new StandaloneAuditWorkspace(
            harnessDir, "process", BaselineIdentity: null,
            Sandbox: new RealProcessSandbox(harnessDir),
            TeardownAsync: () => ValueTask.CompletedTask));
        var service = new StandaloneAuditService(
            runs, artifacts, registry, projects,
            new ExplicitShaAuditRefResolver(), workspaces,
            new DirectSandboxAuditExecutor(),
            new StubOptionsMonitor<AuditRunOptions>(options),
            NullLogger<StandaloneAuditService>.Instance);

        var bad = await service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["standalone:fixture-grep"] },
        });
        Assert.NotNull(bad.Run);
        var badTerminal = await WaitForTerminalAsync(runs, bad.Run.Id);
        Assert.Equal(AuditRunState.Findings, badTerminal.State);
        Assert.Equal(AuditRunAggregateOutcome.Findings, badTerminal.AggregateOutcome);
        var badResult = Assert.Single(badTerminal.AuditorResults);
        Assert.Equal(AuditRunAuditorOutcome.Fail, badResult.Outcome);
        Assert.NotEqual(0, badResult.ExitCode);
        Assert.True(badResult.DurationMs >= 0);
        Assert.NotNull(badResult.LogArtifactDigest);
        var blob = await artifacts.GetAsync(badTerminal.Id, $"auditor-{auditor.Name}.log");
        Assert.NotNull(blob);
        Assert.NotEmpty(blob.Value.Content);

        foreach (var file in Directory.GetFiles(workDir, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            await File.WriteAllTextAsync(file, text.Replace("STANDALONE_AUDIT_VIOLATION", "STANDALONE_AUDIT_FIXED"));
        }

        var good = await service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha2, Selection = new AuditRunSelection { AuditorIds = ["standalone:fixture-grep"] },
        });
        Assert.NotNull(good.Run);
        Assert.NotEqual(bad.Run.Id, good.Run.Id);
        var goodTerminal = await WaitForTerminalAsync(runs, good.Run.Id);
        Assert.Equal(AuditRunState.Passed, goodTerminal.State);
        Assert.Equal(AuditRunAggregateOutcome.Pass, goodTerminal.AggregateOutcome);
        Assert.Equal(AuditRunAuditorOutcome.Pass, Assert.Single(goodTerminal.AuditorResults).Outcome);
        Assert.NotEqual(
            badTerminal.AuditorResults[0].LogArtifactDigest,
            goodTerminal.AuditorResults[0].LogArtifactDigest);
    }

    [Fact]
    public async Task RealTool_MissingExecutableFailsVisibly()
    {
        using var scratch = new TempDir();
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "standalone:missing-tool",
            Argv = ["codeybox-definitely-missing-tool-xyz", "--version"],
        }).WithRequiredToolAvailability();
        var runs = new InMemoryAuditRunStore();
        var service = new StandaloneAuditService(
            runs, new InMemoryAuditRunArtifactStore(), new AuditorRegistry([auditor]),
            new InMemoryProjectRepository(new Project
            {
                Id = new ProjectId(ProjectA), DisplayName = "Audit", RepositoryUrl = "https://example.com/repo-a.git",
            }),
            new ExplicitShaAuditRefResolver(),
            new ScriptWorkspaceFactory(_ => new StandaloneAuditWorkspace(
                scratch.Path, "process", null, new RealProcessSandbox(scratch.Path))),
            new DirectSandboxAuditExecutor(),
            new StubOptionsMonitor<AuditRunOptions>(new AuditRunOptions { Enabled = true }),
            NullLogger<StandaloneAuditService>.Instance);
        var created = await service.CreateAsync(new AuditRunCreateRequest
        {
            ProjectId = ProjectA, Ref = Sha1, Selection = new AuditRunSelection { AuditorIds = ["standalone:missing-tool"] },
        });
        var terminal = await WaitForTerminalAsync(runs, created.Run!.Id);
        Assert.Equal(AuditRunAggregateOutcome.MissingTool, terminal.AggregateOutcome);
        Assert.NotEqual(AuditRunState.Passed, terminal.State);
    }

    [Fact]
    public async Task SqliteStore_RoundTripsRunsWithIdempotencyAndCas()
    {
        using var scratch = new TempDir();
        using var store = new SqliteAuditRunStore(Path.Combine(scratch.Path, "state.db"));
        var now = DateTimeOffset.UtcNow;
        var run = new AuditRunRecord
        {
            Id = "run-1",
            ProjectId = ProjectA,
            State = AuditRunState.Queued,
            AggregateOutcome = AuditRunAggregateOutcome.Unknown,
            Provenance = new AuditRunProvenance
            {
                ProjectId = ProjectA, RequestedRef = Sha1, ResolvedSha = Sha1,
                ResolvedAuditorIds = ["tool:lint"], ConfigDigest = "cfg", CreatedAt = now,
            },
            AuditorResults = [new AuditRunAuditorResult
            {
                AuditorName = "tool:lint", AuditorKind = "shell", Outcome = AuditRunAuditorOutcome.Pass,
                Findings = [new AuditReportFinding("f-1", "Error", "t", "m", ["a.cs"], [])],
                StartedAt = now, EndedAt = now, EvidenceSufficient = true,
            }],
            IdempotencyKey = "k",
            IdempotencyBodyHash = "h",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.CreateAsync(run);
        var loaded = await store.GetAsync("run-1");
        Assert.NotNull(loaded);
        Assert.Equal(AuditRunState.Queued, loaded.State);
        Assert.Equal("cfg", loaded.Provenance.ConfigDigest);
        Assert.Equal(["a.cs"], Assert.Single(Assert.Single(loaded.AuditorResults).Findings).Files);
        Assert.NotNull(await store.GetByIdempotencyAsync(ProjectA, "k", "h"));
        Assert.NotNull(await store.GetByIdempotencyKeyAsync(ProjectA, "k"));
        Assert.Null(await store.GetByIdempotencyAsync(ProjectA, "k", "other"));
        Assert.Equal(1, await store.CountActiveAsync());

        var moved = loaded with { State = AuditRunState.Provisioning, UpdatedAt = DateTimeOffset.UtcNow };
        Assert.True(await store.TryUpdateStateAsync("run-1", AuditRunState.Queued, moved));
        Assert.False(await store.TryUpdateStateAsync("run-1", AuditRunState.Queued, moved));
        Assert.Equal(0, await store.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task FileArtifactStore_RoundTripsWithCapAndTraversalSafety()
    {
        using var scratch = new TempDir();
        var store = new FileAuditRunArtifactStore(Path.Combine(scratch.Path, "art"), 64);
        await store.PutAsync("run-1", "auditor-a.log", "text/plain", Encoding.UTF8.GetBytes("hello"));
        var blob = await store.GetAsync("run-1", "auditor-a.log");
        Assert.NotNull(blob);
        Assert.Equal("hello", Encoding.UTF8.GetString(blob.Value.Content));
        Assert.Contains("auditor-a.log", await store.ListAsync("run-1"));
        await Assert.ThrowsAsync<AuditRunArtifactTooLargeException>(() =>
            store.PutAsync("run-1", "big.log", "text/plain", new byte[65]));
        Assert.Null(await store.GetAsync("run-1", "nope"));
        Assert.Null(await store.GetAsync("run-1", "../escape"));
        await store.DeleteRunAsync("run-1");
        Assert.Empty(await store.ListAsync("run-1"));
    }

    // ── REST wiring ──────────────────────────────────────────────────

    [Fact]
    public async Task Api_CreateGetReportsLogsArtifactsCancelRoundTrip()
    {
        using var factory = new AuditRunApiFactory();
        using var client = factory.CreateClient();
        var create = await client.PostAsJsonAsync("/audit-runs", new
        {
            project = ProjectA,
            @ref = Sha1,
            auditors = new[] { "tool:lint" },
            idempotencyKey = "api-key-1",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createdDoc = await create.Content.ReadFromJsonAsync<JsonDocument>();
        var id = createdDoc!.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        var replay = await client.PostAsJsonAsync("/audit-runs", new
        {
            project = ProjectA,
            @ref = Sha1,
            auditors = new[] { "tool:lint" },
            idempotencyKey = "api-key-1",
        });
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var replayedDoc = await replay.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(id, replayedDoc!.RootElement.GetProperty("id").GetString());

        var clash = await client.PostAsJsonAsync("/audit-runs", new
        {
            project = ProjectA,
            @ref = Sha2,
            auditors = new[] { "tool:lint" },
            idempotencyKey = "api-key-1",
        });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        var get = await client.GetAsync($"/audit-runs/{id}?project={ProjectA}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var denied = await client.GetAsync($"/audit-runs/{id}?project={ProjectB}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var reports = await client.GetAsync($"/audit-runs/{id}/reports?project={ProjectA}");
        Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
        var logs = await client.GetAsync($"/audit-runs/{id}/logs?project={ProjectA}");
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        var artifacts = await client.GetAsync($"/audit-runs/{id}/artifacts?project={ProjectA}");
        Assert.Equal(HttpStatusCode.OK, artifacts.StatusCode);

        var cancel = await client.PostAsync($"/audit-runs/{id}/cancel", null);
        Assert.True(cancel.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Api_UnknownAuditorRejectedAndDisabledBlocksCreate()
    {
        using var factory = new AuditRunApiFactory();
        using var client = factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/audit-runs", new
        {
            project = ProjectA,
            @ref = Sha1,
            auditors = new[] { "nope" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var symbolic = await client.PostAsJsonAsync("/audit-runs", new
        {
            project = ProjectA,
            @ref = "main",
            auditors = new[] { "tool:lint" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, symbolic.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void CopyDir(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    private static async Task<AuditRunRecord> WaitForTerminalAsync(IAuditRunStore runs, string id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var run = await runs.GetAsync(id);
            if (run is not null && AuditRunLifecycle.IsTerminal(run.State))
                return run;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Run {id} did not reach a terminal state in time.");
    }

    private sealed class FakeAuditor(
        string name,
        string kind,
        AuditCapabilities required = AuditCapabilities.None,
        Func<AuditResult>? result = null) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public AuditCapabilities Required { get; } = required;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default) =>
            Task.FromResult(result?.Invoke() ?? new AuditResult(true, []));
    }

    private sealed class ScriptExecutor : IStandaloneAuditExecutor
    {
        public readonly Dictionary<string, Func<CancellationToken, Task<StandaloneAuditorExecution>>> Script = new(StringComparer.Ordinal);
        public int Invocations;
        public Task<StandaloneAuditorExecution> ExecuteAsync(
            IAuditor auditor, StandaloneAuditWorkspace workspace, AuditContext context, TimeSpan timeout, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Invocations);
            if (Script.TryGetValue(auditor.Name, out var fn))
                return fn(ct);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new StandaloneAuditorExecution(true, [], $"{auditor.Name} ok", 0, null, now, now));
        }
    }

    private sealed class ScriptWorkspaceFactory : IStandaloneAuditWorkspaceFactory
    {
        private readonly Func<string, StandaloneAuditWorkspace> _build;
        private readonly string? _failure;
        private readonly bool _failTeardown;
        public int TeardownAttempts;

        public ScriptWorkspaceFactory(
            Func<string, StandaloneAuditWorkspace> build = null!,
            string? failure = null,
            bool failTeardown = false)
        {
            _build = build ?? (workDir => new StandaloneAuditWorkspace(workDir, "test", null));
            _failure = failure;
            _failTeardown = failTeardown;
        }

        public Task<StandaloneAuditWorkspace> PrepareAsync(AuditRunRecord run, CancellationToken ct = default)
        {
            if (_failure is not null)
                throw new AuditUnavailableException(_failure);
            var dir = Path.Combine(Path.GetTempPath(), "audit-run-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var ws = _build(dir);
            if (_failTeardown)
                return Task.FromResult(ws with { TeardownAsync = () =>
                {
                    Interlocked.Increment(ref TeardownAttempts);
                    throw new InvalidOperationException("cleanup failed");
                } });
            return Task.FromResult(ws with
            {
                TeardownAsync = () =>
                {
                    Interlocked.Increment(ref TeardownAttempts);
                    try { Directory.Delete(dir, true); } catch { }
                    return ValueTask.CompletedTask;
                },
            });
        }
    }

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private readonly T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private sealed class TestHarness
    {
        public StandaloneAuditService Service { get; }
        public InMemoryAuditRunStore Runs { get; } = new();
        public InMemoryAuditRunArtifactStore Artifacts { get; } = new();
        public ScriptExecutor Executor { get; } = new();
        public ScriptWorkspaceFactory Workspaces { get; }

        private TestHarness(AuditRunOptions options, string? failWorkspace, bool failTeardown, IAuditor[] extra)
        {
            var auditors = new List<IAuditor> { new FakeAuditor("tool:lint", "shell") };
            auditors.AddRange(extra);
            Workspaces = new ScriptWorkspaceFactory(failure: failWorkspace, failTeardown: failTeardown);
            Service = new StandaloneAuditService(
                Runs, Artifacts, new AuditorRegistry(auditors),
                new InMemoryProjectRepository(
                    new Project { Id = new ProjectId(ProjectA), DisplayName = "A", RepositoryUrl = "https://example.com/repo-a.git" },
                    new Project { Id = new ProjectId(ProjectB), DisplayName = "B", RepositoryUrl = "https://example.com/repo-b.git" }),
                new ExplicitShaAuditRefResolver(), Workspaces, Executor,
                new StubOptionsMonitor<AuditRunOptions>(options),
                NullLogger<StandaloneAuditService>.Instance);
        }

        public static TestHarness Create(
            IAuditor[]? extraAuditors = null, string? failWorkspace = null, bool failTeardown = false) =>
            new(new AuditRunOptions { Enabled = true }, failWorkspace, failTeardown, extraAuditors ?? []);

        public static TestHarness WithOptions(AuditRunOptions options) =>
            new(options, null, false, []);

        public async Task<AuditRunRecord> WaitForTerminalAsync(string id, TimeSpan? timeout = null) =>
            await StandaloneAuditRunTests.WaitForTerminalAsync(Runs, id, timeout);

        public async Task<AuditRunRecord> WaitForStateAsync(string id, AuditRunState state, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
            while (DateTime.UtcNow < deadline)
            {
                var run = await Runs.GetAsync(id);
                if (run is not null && (run.State == state || AuditRunLifecycle.IsTerminal(run.State)))
                    return run;
                await Task.Delay(50);
            }
            throw new TimeoutException($"Run {id} did not reach {state} in time.");
        }
    }

    /// <summary>
    /// Test sandbox that really executes the tool process: proving main tool
    /// invocation (exit code, stdout, timing) rather than a mocked result.
    /// </summary>
    private sealed class RealProcessSandbox(string workDir) : ISandbox
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exec.Argv[0],
                WorkingDirectory = exec.WorkingDirectory ?? workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = exec.Stdin is not null,
                UseShellExecute = false,
            };
            foreach (var arg in exec.Argv.Skip(1))
                psi.ArgumentList.Add(arg);
            if (exec.ExtraEnvironment is not null)
                foreach (var (k, v) in exec.ExtraEnvironment)
                    psi.Environment[k] = v;
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("process start failed");
            if (exec.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(exec.Stdin);
                process.StandardInput.Close();
            }
            using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SandboxExecResult(process.ExitCode, stdout, stderr);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "standalone-audit-test", Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    private sealed class AuditRunApiFactory : WebApplicationFactory<Program>
    {
        private readonly TestScratch _scratch = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _scratch.DbPath,
                    ["CodeyBox:GitHubAppStorePath"] = _scratch.Dir("github-apps"),
                    ["CodeyBox:GitRootDirectory"] = _scratch.Dir("git"),
                    ["CodeyBox:AuditLog:Path"] = _scratch.Dir("log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = _scratch.Dir("audit.json"),
                    ["CodeyBox:AuditRuns:Enabled"] = "true",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                var runs = new InMemoryAuditRunStore();
                var artifacts = new InMemoryAuditRunArtifactStore();
                services.RemoveAll<IAuditRunStore>();
                services.AddSingleton<IAuditRunStore>(runs);
                services.RemoveAll<IAuditRunArtifactStore>();
                services.AddSingleton<IAuditRunArtifactStore>(artifacts);
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                    new Project { Id = new ProjectId(ProjectA), DisplayName = "A", RepositoryUrl = "https://example.com/repo-a.git" },
                    new Project { Id = new ProjectId(ProjectB), DisplayName = "B", RepositoryUrl = "https://example.com/repo-b.git" }));
                services.RemoveAll<IStandaloneAuditWorkspaceFactory>();
                services.AddSingleton<IStandaloneAuditWorkspaceFactory>(new ApiWorkspaceFactory());
                services.RemoveAll<IStandaloneAuditExecutor>();
                services.AddSingleton<IStandaloneAuditExecutor>(new ScriptExecutor());
                services.AddSingleton<IAuditor>(new ApiLintAuditor());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _scratch.Dispose();
            base.Dispose(disposing);
        }

        private sealed class TestScratch : IDisposable
        {
            private readonly string _root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "audit-run-api", Guid.NewGuid().ToString("N"));
            public string DbPath => System.IO.Path.Combine(_root, "state.db");
            public string Dir(string name)
            {
                var p = System.IO.Path.Combine(_root, name);
                Directory.CreateDirectory(p);
                return p;
            }
            public void Dispose()
            {
                try { Directory.Delete(_root, true); } catch { }
            }
        }

        private sealed class ApiWorkspaceFactory : IStandaloneAuditWorkspaceFactory
        {
            public Task<StandaloneAuditWorkspace> PrepareAsync(AuditRunRecord run, CancellationToken ct = default)
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audit-run-api-ws", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                return Task.FromResult(new StandaloneAuditWorkspace(dir, "test", null));
            }
        }

        private sealed class ApiLintAuditor : IAuditor
        {
            public string Name => "tool:lint";
            public string Kind => "shell";
            public AuditCapabilities Required => AuditCapabilities.None;
            public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default) =>
                Task.FromResult(new AuditResult(true, []));
        }
    }
}
