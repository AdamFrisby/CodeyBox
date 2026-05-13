using CodeyBox.Core;
using CodeyBox.Audit.Presets;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Audit-loop integration tests using a scripted auditor.
///   - audit passes first iteration → straight to merge → Done
///   - audit fails then passes after rework → Done
///   - audit fails max iterations → AuditFailed (terminal)
///   - rework agent makes no changes → fail fast (Failed)
///   - no auditors registered → audit phase is a no-op
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AuditPipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;
    public AuditPipelineIntegrationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-audit-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task AuditPasses_FirstIteration_ReachesDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsThenPassesAfterRework_ReachesDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsAllIterations_ReachesAuditFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], maxAuditIterations: 3);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v3"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("did not pass after 3 iterations", final.LastError);
    }

    [Fact]
    public async Task ReworkProducesNoChanges_FailsFast()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "fix me", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "same-content"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "same-content"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoAuditorsRegistered_SkipsPhaseEntirely()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed); // no auditors
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task ProjectDefaultUatProfile_AuditLogRecordsOnlyUatAuditors()
    {
        var sink = new TestSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
            using var tp = TestSupport.BuildPipeline(
                _workspace,
                seed,
                projectAudit: new ProjectAudit
                {
                    Profile = AuditProfilePresets.Uat,
                    Profiles = AuditProfilePresets.CreateBuiltIns(),
                },
                presetCatalogOverride: new UatIntegrationCatalog());
            tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

            var item = NewItem();
            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var final = await tp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, final!.State);

            var auditorRuns = sink.Events
                .Where(e => GetScalar<string>(e, "EventName") == "auditor.run")
                .Select(e => GetScalar<string>(e, "AuditorName") ?? string.Empty)
                .ToArray();

            Assert.Equal(
                [
                    "csharp:format-check",
                    "csharp:build-WaE",
                    "csharp:test-pass",
                    "security:gitleaks",
                    "security:semgrep",
                    "security:llm-review",
                    "cheating:deterministic-patterns",
                ],
                auditorRuns);

            Assert.DoesNotContain("completeness:llm-review", auditorRuns);
            Assert.DoesNotContain("cheating:llm-review", auditorRuns);

            var profileEvent = Assert.Single(sink.Events,
                e => GetScalar<string>(e, "EventName") == "audit.profile_selected");
            Assert.Equal(AuditProfilePresets.Uat, GetScalar<string>(profileEvent, "AuditProfile"));
            Assert.Equal(auditorRuns, GetStringSequence(profileEvent, "AuditorNames"));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostShutdownDuringAudit_StopsAfterAuditorDrains(bool blockingFinding)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new DrainingAuditor(blockingFinding);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        using var hostShutdown = new CancellationTokenSource();
        var run = Task.Run(() => tp.Pipeline.RunAsync(item, CancellationToken.None, hostShutdown.Token));

        await auditor.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await hostShutdown.CancelAsync();
        auditor.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Auditing, final!.State);
        Assert.Empty(tp.Agent.WorkPlan);
    }

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class ScriptedAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        public ScriptedAuditor(IEnumerable<AuditOutcome> plan) { _plan = new Queue<AuditOutcome>(plan); }
        public string Name => "Scripted";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class DrainingAuditor : IAuditor
    {
        private readonly bool _blockingFinding;

        public DrainingAuditor(bool blockingFinding) => _blockingFinding = blockingFinding;

        public string Name => "Draining";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            var findings = _blockingFinding
                ? (IReadOnlyList<AuditFinding>)[new AuditFinding("Draining", AuditSeverity.Error, "needs fix", "x")]
                : [];
            return new AuditResult(!_blockingFinding, findings);
        }
    }

    private sealed class UatIntegrationCatalog : IPresetCatalog
    {
        public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx)
            => name.Equals("csharp", StringComparison.OrdinalIgnoreCase)
                ? [
                    new PassingAuditor("csharp:format-check"),
                    new PassingAuditor("csharp:build-WaE"),
                    new PassingAuditor("csharp:test-pass"),
                ]
                : [];

        public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx)
            => name.ToLowerInvariant() switch
            {
                "security" =>
                [
                    new PassingAuditor("security:gitleaks"),
                    new PassingAuditor("security:semgrep"),
                    new PassingAuditor("security:llm-review"),
                ],
                "cheating" =>
                [
                    new PassingAuditor("cheating:deterministic-patterns"),
                    new PassingAuditor("cheating:llm-review"),
                ],
                _ => [],
            };

        public IReadOnlyList<string> KnownLanguages => ["csharp"];
        public IReadOnlyList<string> KnownAuditTypes => ["security", "cheating"];
        public string LlmPromptFrameTemplate => "{{reviewFocus}}\n{{resultFile}}";
    }

    private sealed class PassingAuditor(string name) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }

    private static IReadOnlyList<string> GetStringSequence(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not SequenceValue seq)
            return [];
        return seq.Elements
            .OfType<ScalarValue>()
            .Select(v => v.Value?.ToString() ?? string.Empty)
            .ToArray();
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "audit test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };
}
