using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentSupervisionServiceTests
{
    private static readonly AgentSupervisionListQuery DefaultListQuery = new();

    [Fact]
    public async Task Disabled_DoesNotCreateSessions_AndRejectsInjection()
    {
        var service = new AgentSupervisionService(() => new AgentSupervisionOptions { Enabled = false });

        var scope = await service.TryStartSessionAsync(Start());
        var receipt = await service.EnqueueInjectionAsync(
            "missing",
            new AgentSupervisionInjectionRequest("look at this", "operator"));
        var page = await service.ListSessionsAsync(DefaultListQuery);

        Assert.Null(scope);
        Assert.False(receipt.Accepted);
        Assert.Equal("disabled", receipt.Status);
        Assert.False(page.Enabled);
        Assert.Empty(page.Sessions);
    }

    [Fact]
    public async Task RunPendingInjectionsAsync_DispatchesQueuedInjectionAsAgentTurn()
    {
        var notifier = new RecordingSupervisionNotifier();
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 4 },
            notifier);

        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("please add the missing test", "alice"));
        Assert.True(receipt.Accepted);

        var dispatched = new List<(string Prompt, string InjectionId)>();
        var initial = new AgentResult(true, "autonomous done", "auto-out", null);

        var merged = await scope.RunPendingInjectionsAsync(initial, async (turn, _) =>
        {
            dispatched.Add((turn.Prompt, turn.Injection.InjectionId));
            await Task.Yield();
            return new AgentResult(true, "injection ok", "inj-out", null);
        });

        var single = Assert.Single(dispatched);
        Assert.Equal(receipt.InjectionId, single.InjectionId);
        Assert.Contains("Live operator instruction", single.Prompt);
        Assert.Contains("please add the missing test", single.Prompt);

        Assert.True(merged.Success);
        // Merged stdout combines autonomous + injection turns.
        Assert.Contains("auto-out", merged.Stdout);
        Assert.Contains("inj-out", merged.Stdout);

        Assert.Contains(notifier.InjectionEvents, e => e.Method == "started" && e.InjectionId == receipt.InjectionId);
        Assert.Contains(notifier.CompletedInjections, e => e.InjectionId == receipt.InjectionId && e.Success);
        // human-injection command persisted on the session for late-joining
        // supervisors to review.
        var page = await service.ListSessionsAsync(DefaultListQuery);
        var session = Assert.Single(page.Sessions);
        Assert.Contains(session.RecentCommands, r => r.Kind == "human-injection" && r.InjectionId == receipt.InjectionId);
    }

    [Fact]
    public async Task RunPendingInjectionsAsync_StopsOnFirstFailedTurn()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 4 });
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        await service.EnqueueInjectionAsync(scope.SessionId, new AgentSupervisionInjectionRequest("first", "alice"));
        await service.EnqueueInjectionAsync(scope.SessionId, new AgentSupervisionInjectionRequest("second", "alice"));

        var seen = 0;
        var merged = await scope.RunPendingInjectionsAsync(new AgentResult(true, "auto", "auto", null), (_, _) =>
        {
            seen++;
            return Task.FromResult(new AgentResult(false, "boom", "stdout", "stderr"));
        });

        Assert.Equal(1, seen);
        Assert.False(merged.Success);
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

        var page = await service.ListSessionsAsync(DefaultListQuery);
        Assert.Single(page.Sessions);
        Assert.True(page.Sessions[0].AcceptingInjections);
        Assert.Equal(1, page.Sessions[0].PendingInjections);

        await scope.RunPendingInjectionsAsync(new AgentResult(true, "ok", null, null), (_, _) =>
            Task.FromResult(new AgentResult(true, "drained", null, null)));

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

        var page = await service.ListSessionsAsync(DefaultListQuery);
        Assert.DoesNotContain("gho_", page.Sessions[0].OutputTail);
        Assert.Contains("***", page.Sessions[0].OutputTail);
        Assert.Single(notifier.Commands);
        Assert.DoesNotContain("sk-ant-api03", notifier.Commands[0].Prompt);

        // Late-join review: persisted command list does not leak the secret either.
        var commands = page.Sessions[0].RecentCommands;
        Assert.Single(commands);
        Assert.Equal("autonomous", commands[0].Kind);
        Assert.DoesNotContain("sk-ant-api03", commands[0].Prompt);
    }

    [Fact]
    public async Task InjectionRejection_RejectsBlankMessage()
    {
        var service = new AgentSupervisionService(() => new AgentSupervisionOptions { Enabled = true });
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("   ", "alice"));

        Assert.False(receipt.Accepted);
        Assert.Equal("invalid", receipt.Status);
    }

    [Fact]
    public async Task InjectionRejection_RejectsOversizedMessage()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, MaxInjectionChars = 128 });
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest(new string('x', 256), "alice"));

        Assert.False(receipt.Accepted);
        Assert.Equal("invalid", receipt.Status);
        Assert.Contains("MaxInjectionChars", receipt.Error);
    }

    [Fact]
    public async Task InjectionAccept_TruncatesOverlongActorString()
    {
        var notifier = new RecordingSupervisionNotifier();
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true },
            notifier);
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var actor = new string('a', 500);
        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("ok", actor));

        Assert.True(receipt.Accepted);
        var evt = Assert.Single(notifier.InjectionEvents, e => e.Method == "queued");
        var queued = notifier.QueuedInjections.Single(q => q.InjectionId == evt.InjectionId);
        Assert.Equal(200, queued.Actor.Length);
    }

    [Fact]
    public async Task TryStartSessionAsync_HonoursMaxSessionsLimit()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, MaxSessions = 1 });

        await using var first = await service.TryStartSessionAsync(Start());
        var second = await service.TryStartSessionAsync(Start());

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task PruneCompleted_DropsExpiredCompletedSessions()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions
            {
                Enabled = true,
                CompletedSessionRetentionSeconds = 0,
            });

        var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");
        await scope.DisposeAsync();

        var page = await service.ListSessionsAsync(DefaultListQuery);
        Assert.Empty(page.Sessions);
    }

    [Fact]
    public async Task NotifierFailures_AreSwallowedAndAuditContinues()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true },
            new ThrowingNotifier());

        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("test", "alice"));

        Assert.True(receipt.Accepted);
    }

    [Fact]
    public async Task ListSessionsAsync_RespectsPaginationAndCaps()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions
            {
                Enabled = true,
                MaxSessions = 8,
                DefaultListPageSize = 2,
                MaxListPageSize = 4,
                RetainedCommandsPerSession = 8,
            });

        var scopes = new List<IAgentSupervisionSession>();
        for (var i = 0; i < 5; i++)
        {
            var scope = await service.TryStartSessionAsync(Start());
            Assert.NotNull(scope);
            scopes.Add(scope!);
            await scope!.PublishCodeyBoxCommandAsync("autonomous", $"prompt-{i}", null);
        }

        try
        {
            var defaultPage = await service.ListSessionsAsync(new AgentSupervisionListQuery());
            Assert.Equal(5, defaultPage.Total);
            Assert.Equal(2, defaultPage.Sessions.Count);

            var bigPage = await service.ListSessionsAsync(new AgentSupervisionListQuery(Take: 99));
            Assert.Equal(4, bigPage.Sessions.Count); // clamped to MaxListPageSize

            var trimmedTail = await service.ListSessionsAsync(
                new AgentSupervisionListQuery(IncludeOutputTail: false, OutputTailMaxChars: 0));
            Assert.All(trimmedTail.Sessions, s => Assert.Equal(string.Empty, s.OutputTail));

            var noCommands = await service.ListSessionsAsync(
                new AgentSupervisionListQuery(RecentCommandsLimit: 0));
            Assert.All(noCommands.Sessions, s => Assert.Empty(s.RecentCommands));
        }
        finally
        {
            foreach (var s in scopes)
                await s.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(nameof(AgentSupervisionOptions.MaxPromptChars), 0)]
    [InlineData(nameof(AgentSupervisionOptions.MaxOutputBufferChars), 0)]
    [InlineData(nameof(AgentSupervisionOptions.MaxInjectionChars), 0)]
    [InlineData(nameof(AgentSupervisionOptions.InjectionQueueCapacity), 0)]
    [InlineData(nameof(AgentSupervisionOptions.CompletedSessionRetentionSeconds), -1)]
    [InlineData(nameof(AgentSupervisionOptions.MaxSessions), 0)]
    [InlineData(nameof(AgentSupervisionOptions.MaxSessions), AgentSupervisionOptions.MaxSessionsCeiling + 1)]
    [InlineData(nameof(AgentSupervisionOptions.RetainedCommandsPerSession), -1)]
    [InlineData(nameof(AgentSupervisionOptions.DefaultListPageSize), 0)]
    public void Validate_RejectsOutOfBoundsField(string field, int badValue)
    {
        var options = new AgentSupervisionOptions();
        switch (field)
        {
            case nameof(AgentSupervisionOptions.MaxPromptChars): options.MaxPromptChars = badValue; break;
            case nameof(AgentSupervisionOptions.MaxOutputBufferChars): options.MaxOutputBufferChars = badValue; break;
            case nameof(AgentSupervisionOptions.MaxInjectionChars): options.MaxInjectionChars = badValue; break;
            case nameof(AgentSupervisionOptions.InjectionQueueCapacity): options.InjectionQueueCapacity = badValue; break;
            case nameof(AgentSupervisionOptions.CompletedSessionRetentionSeconds): options.CompletedSessionRetentionSeconds = badValue; break;
            case nameof(AgentSupervisionOptions.MaxSessions): options.MaxSessions = badValue; break;
            case nameof(AgentSupervisionOptions.RetainedCommandsPerSession): options.RetainedCommandsPerSession = badValue; break;
            case nameof(AgentSupervisionOptions.DefaultListPageSize): options.DefaultListPageSize = badValue; break;
            default: throw new InvalidOperationException($"unknown field {field}");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Validate_RejectsMaxListPageSizeBelowDefault()
    {
        var options = new AgentSupervisionOptions
        {
            DefaultListPageSize = 32,
            MaxListPageSize = 16,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxListPageSize", ex.Message);
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
        public List<AgentSupervisionInjectionEvent> QueuedInjections { get; } = [];
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
            QueuedInjections.Add(injection);
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

    private sealed class ThrowingNotifier : IAgentSupervisionNotifier
    {
        public Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
        public Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default) =>
            throw new InvalidOperationException("nope");
    }
}
