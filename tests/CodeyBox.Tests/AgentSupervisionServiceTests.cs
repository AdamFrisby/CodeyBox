using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentSupervisionServiceTests
{
    [Fact]
    public async Task Disabled_DoesNotCreateSessions_AndRejectsInjection()
    {
        var service = new AgentSupervisionService(() => new AgentSupervisionOptions { Enabled = false });

        var scope = await service.TryStartSessionAsync(Start());
        var receipt = await service.EnqueueInjectionAsync(
            "missing",
            new AgentSupervisionInjectionRequest("look at this", "operator"));
        var sessions = await service.ListSessionsAsync();

        Assert.Null(scope);
        Assert.False(receipt.Accepted);
        Assert.Equal("disabled", receipt.Status);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task Enabled_SurfacesSession_QueuesInjection_AndClosesIntakeWhenDrained()
    {
        var notifier = new RecordingSupervisionNotifier();
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 1 },
            notifier);

        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("please add the missing test", "alice"));
        var full = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("second", "alice"));

        Assert.True(receipt.Accepted);
        Assert.Equal("queue_full", full.Status);

        var sessions = await service.ListSessionsAsync();
        Assert.Single(sessions);
        Assert.True(sessions[0].AcceptingInjections);
        Assert.Equal(1, sessions[0].PendingInjections);

        Assert.True(scope.TryBeginNextInjection(out var injection));
        Assert.Equal(receipt.InjectionId, injection.InjectionId);
        await scope.MarkInjectionStartedAsync(injection);
        await scope.MarkInjectionCompletedAsync(injection, new AgentResult(true, "ok", "stdout", null));

        Assert.False(scope.TryBeginNextInjection(out _));
        var afterDrain = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("too late", "alice"));

        Assert.Equal("closed", afterDrain.Status);
        Assert.Contains(notifier.InjectionEvents, e => e.Method == "queued" && e.InjectionId == receipt.InjectionId);
        Assert.Contains(notifier.InjectionEvents, e => e.Method == "started" && e.InjectionId == receipt.InjectionId);
        Assert.Contains(notifier.CompletedInjections, e => e.InjectionId == receipt.InjectionId && e.Success);
    }

    [Fact]
    public async Task StdoutTailAndCommandsAreRedactedAndTruncated()
    {
        var notifier = new RecordingSupervisionNotifier();
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions
            {
                Enabled = true,
                MaxPromptChars = 1024,
                MaxOutputBufferChars = 4096,
            },
            notifier);

        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var fakeGitHubToken = string.Concat("gho_", "ABCdef123456789012345678901234");
        var fakeAnthropicToken = string.Concat(
            "sk-ant-api03-",
            "ABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefAA");
        var callback = scope.WrapStdoutCallback(null);
        callback?.Invoke($"token={fakeGitHubToken} done");
        await scope.PublishCodeyBoxCommandAsync(
            "autonomous",
            $"prompt with {fakeAnthropicToken}",
            injectionId: null);

        var sessions = await service.ListSessionsAsync();
        Assert.DoesNotContain("gho_", sessions[0].OutputTail);
        Assert.Contains("***", sessions[0].OutputTail);
        Assert.Single(notifier.Commands);
        Assert.DoesNotContain("sk-ant-api03", notifier.Commands[0].Prompt);
    }

    private static AgentSupervisionSessionStart Start() =>
        new(
            WorkItemId.New(),
            "project",
            "work",
            1,
            AgentKind.Claude,
            AgentInstanceId: null,
            ModelId: "claude-opus-4-7",
            ReasoningMode: "high",
            SandboxId: "sandbox-1",
            WorkingDirectory: "/work",
            Source: "test");

    private sealed class RecordingSupervisionNotifier : IAgentSupervisionNotifier
    {
        public List<AgentSupervisionSessionSnapshot> Started { get; } = [];
        public List<AgentSupervisionCommandEvent> Commands { get; } = [];
        public List<(string Method, string InjectionId)> InjectionEvents { get; } = [];
        public List<AgentSupervisionInjectionCompletedEvent> CompletedInjections { get; } = [];

        public Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default)
        {
            Started.Add(session);
            return Task.CompletedTask;
        }

        public Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }

        public Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default)
        {
            InjectionEvents.Add(("queued", injection.InjectionId));
            return Task.CompletedTask;
        }

        public Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default)
        {
            InjectionEvents.Add(("started", injection.InjectionId));
            return Task.CompletedTask;
        }

        public Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default)
        {
            CompletedInjections.Add(injection);
            return Task.CompletedTask;
        }
    }
}
