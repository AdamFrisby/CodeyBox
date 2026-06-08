using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class CrossAgentHandoffPromptPreprocessorTests
{
    [Fact]
    public async Task InjectsBrief_WhenCurrentAgentDiffersFromPriorEntry()
    {
        var workItemId = WorkItemId.New();
        var involvement = new StubInvolvementStore(
        [
            Entry(workItemId, AgentKind.Claude, "work"),
            Entry(workItemId, AgentKind.Claude, "audit:security"),
        ]);
        var builder = new RecordingBriefBuilder("Claude tried X and pushed commits A,B. Test foo still failing.");
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement,
            builder);

        var result = await preprocessor.ProcessAsync(NewContext(workItemId, AgentKind.Codex), "next prompt");

        Assert.Contains("## Cross-agent handoff", result);
        Assert.Contains("previously handled by **claude**", result);
        Assert.Contains("now being routed to **codex**", result);
        Assert.Contains("Claude tried X and pushed commits A,B. Test foo still failing.", result);
        Assert.Contains("--- END HANDOFF BRIEF ---", result);
        Assert.Contains("next prompt", result);

        var call = Assert.Single(builder.Calls);
        Assert.Equal(AgentKind.Claude, call.PriorAgent);
        Assert.Equal(AgentKind.Codex, call.Ctx.AgentKind);
    }

    [Fact]
    public async Task NoOp_WhenPriorEntryIsSameAgentKind()
    {
        var workItemId = WorkItemId.New();
        var involvement = new StubInvolvementStore(
        [
            Entry(workItemId, AgentKind.Codex, "work"),
        ]);
        var builder = new RecordingBriefBuilder("should not be requested");
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement,
            builder);

        var result = await preprocessor.ProcessAsync(NewContext(workItemId, AgentKind.Codex), "untouched");

        Assert.Equal("untouched", result);
        Assert.Empty(builder.Calls);
    }

    [Fact]
    public async Task NoOp_OnFirstInvocation()
    {
        var workItemId = WorkItemId.New();
        var involvement = new StubInvolvementStore([]);
        var builder = new RecordingBriefBuilder("should not be requested");
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement,
            builder);

        var result = await preprocessor.ProcessAsync(NewContext(workItemId, AgentKind.Claude), "untouched");

        Assert.Equal("untouched", result);
        Assert.Empty(builder.Calls);
    }

    [Fact]
    public async Task PicksMostRecentDifferingAgent_WhenHistoryMixesKinds()
    {
        // History (oldest first): claude work, codex audit, claude rework, gemini fallback now.
        // The most-recent-different entry relative to the gemini invocation is the
        // claude rework — the brief builder should be told prior=Claude.
        var workItemId = WorkItemId.New();
        var involvement = new StubInvolvementStore(
        [
            Entry(workItemId, AgentKind.Claude, "work"),
            Entry(workItemId, AgentKind.Codex, "audit:security"),
            Entry(workItemId, AgentKind.Claude, "rework"),
        ]);
        var builder = new RecordingBriefBuilder("brief content");
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement,
            builder);

        await preprocessor.ProcessAsync(NewContext(workItemId, AgentKind.Gemini), "prompt");

        var call = Assert.Single(builder.Calls);
        Assert.Equal(AgentKind.Claude, call.PriorAgent);
    }

    [Fact]
    public async Task NoOp_WhenBuilderReturnsNullOrWhitespace()
    {
        var workItemId = WorkItemId.New();
        var involvement = new StubInvolvementStore([Entry(workItemId, AgentKind.Claude, "work")]);
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement,
            new RecordingBriefBuilder(null));

        var result = await preprocessor.ProcessAsync(NewContext(workItemId, AgentKind.Codex), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task NoOp_WhenInvolvementStoreNotWired()
    {
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement: null,
            briefBuilder: new RecordingBriefBuilder("never called"));

        var result = await preprocessor.ProcessAsync(NewContext(WorkItemId.New(), AgentKind.Codex), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task NoOp_WhenBriefBuilderNotWired()
    {
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement: new StubInvolvementStore([]),
            briefBuilder: null);

        var result = await preprocessor.ProcessAsync(NewContext(WorkItemId.New(), AgentKind.Codex), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task NoOp_WhenInvolvementStoreThrows()
    {
        var preprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            new ThrowingInvolvementStore(),
            new RecordingBriefBuilder("never called"));

        var result = await preprocessor.ProcessAsync(NewContext(WorkItemId.New(), AgentKind.Codex), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task PreprocessorFiresThroughChain_OrderRespectedAfterProjectRulesAndAttachments()
    {
        var workItemId = WorkItemId.New();
        var attachments = new StubAttachmentSourceForChain(
        [
            new WorkItemAttachment("/work/.codeybox/attachments/spec.md", "spec.md", "text/markdown", "spec"),
        ]);
        var involvement = new StubInvolvementStore([Entry(workItemId, AgentKind.Claude, "work")]);
        var builder = new RecordingBriefBuilder("handoff text");

        var attachmentPreprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            attachments);
        var handoffPreprocessor = new CrossAgentHandoffPromptPreprocessor(
            NullLogger<CrossAgentHandoffPromptPreprocessor>.Instance,
            involvement,
            builder);

        var chain = new AgentPromptPreprocessorChain([attachmentPreprocessor, handoffPreprocessor]);

        var ctx = NewContext(workItemId, AgentKind.Codex);
        var result = await chain.ProcessAsync(ctx, "next prompt");

        // The attachment manifest is injected first (lower Order), and the
        // handoff brief wraps the entire prior result (higher Order) so it
        // appears above the manifest in the final prompt.
        var handoffIdx = result.IndexOf("## Cross-agent handoff", StringComparison.Ordinal);
        var attachmentIdx = result.IndexOf("## Attachments", StringComparison.Ordinal);
        var promptIdx = result.IndexOf("next prompt", StringComparison.Ordinal);
        Assert.True(handoffIdx >= 0);
        Assert.True(attachmentIdx > handoffIdx);
        Assert.True(promptIdx > attachmentIdx);
    }

    private static PromptContext NewContext(WorkItemId itemId, AgentKind currentAgent) =>
        new(
            itemId,
            currentAgent,
            AgentPromptPhase.Rework,
            2,
            NewProject(),
            new NoopSandbox(),
            "/work");

    private static Project NewProject() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.invalid/repo.git",
    };

    private static AgentInvolvement Entry(WorkItemId workItemId, AgentKind kind, string phase) => new(
        Id: Guid.NewGuid(),
        WorkItemId: workItemId,
        AgentKind: kind,
        ModelId: null,
        Phase: phase,
        StartedAt: DateTimeOffset.UtcNow,
        EndedAt: DateTimeOffset.UtcNow,
        Iteration: null,
        Outcome: "success");

    private sealed class StubInvolvementStore(IReadOnlyList<AgentInvolvement> entries) : IAgentInvolvementStore
    {
        public Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        {
            _ = workItemId;
            _ = ct;
            return Task.FromResult(entries);
        }
    }

    private sealed class ThrowingInvolvementStore : IAgentInvolvementStore
    {
        public Task RecordStartAsync(AgentInvolvement entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task FinalizeAsync(Guid id, DateTimeOffset endedAt, string outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentInvolvement>> ListByWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        {
            _ = workItemId;
            _ = ct;
            throw new InvalidOperationException("store offline");
        }
    }

    private sealed class RecordingBriefBuilder(string? brief) : ICrossAgentHandoffBriefBuilder
    {
        public List<(PromptContext Ctx, AgentKind PriorAgent)> Calls { get; } = [];

        public Task<string?> BuildAsync(PromptContext ctx, AgentKind priorAgent, CancellationToken ct = default)
        {
            _ = ct;
            Calls.Add((ctx, priorAgent));
            return Task.FromResult(brief);
        }
    }

    private sealed class StubAttachmentSourceForChain(IReadOnlyList<WorkItemAttachment> attachments) : IWorkItemAttachmentSource
    {
        public Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default)
        {
            _ = itemId;
            _ = ct;
            return Task.FromResult(attachments);
        }
    }

    private sealed class NoopSandbox : ISandbox
    {
        public string Id => "noop";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = exec;
            _ = ct;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }
    }
}
