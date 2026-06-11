using System.Collections.Concurrent;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentSupervisionOptions
{
    public bool Enabled { get; set; }
    public int MaxPromptChars { get; set; } = 16_384;
    public int MaxOutputBufferChars { get; set; } = 128 * 1024;
    public int MaxInjectionChars { get; set; } = 8_192;
    public int InjectionQueueCapacity { get; set; } = 16;
    public int CompletedSessionRetentionSeconds { get; set; } = 300;
    public int MaxSessions { get; set; } = 512;

    public void Validate()
    {
        if (MaxPromptChars < 1024)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:MaxPromptChars must be >= 1024");
        if (MaxOutputBufferChars < 4096)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:MaxOutputBufferChars must be >= 4096");
        if (MaxInjectionChars < 128)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:MaxInjectionChars must be >= 128");
        if (InjectionQueueCapacity < 1)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:InjectionQueueCapacity must be >= 1");
        if (CompletedSessionRetentionSeconds < 0)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:CompletedSessionRetentionSeconds must be >= 0");
        if (MaxSessions < 1)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:MaxSessions must be >= 1");
    }
}

public interface IAgentSupervisionService
{
    bool Enabled { get; }

    Task<AgentSupervisionSessionScope?> TryStartSessionAsync(
        AgentSupervisionSessionStart start,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentSupervisionSessionSnapshot>> ListSessionsAsync(CancellationToken ct = default);

    Task<AgentSupervisionInjectionReceipt> EnqueueInjectionAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        CancellationToken ct = default);
}

public interface IAgentSupervisionNotifier
{
    Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default);
    Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default);
    Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default);
    Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default);
    Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default);
    Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default);
    Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default);
    Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default);
}

public sealed class NullAgentSupervisionNotifier : IAgentSupervisionNotifier
{
    public static NullAgentSupervisionNotifier Instance { get; } = new();
    private NullAgentSupervisionNotifier() { }

    public Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) => Task.CompletedTask;
    public Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) => Task.CompletedTask;
    public Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) => Task.CompletedTask;
    public Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default) => Task.CompletedTask;
    public Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default) => Task.CompletedTask;
    public Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) => Task.CompletedTask;
    public Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) => Task.CompletedTask;
    public Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed record AgentSupervisionSessionStart(
    WorkItemId WorkItemId,
    string ProjectId,
    string Phase,
    int Iteration,
    AgentKind Agent,
    string? AgentInstanceId,
    string? ModelId,
    string? ReasoningMode,
    string SandboxId,
    string WorkingDirectory,
    string Source);

public sealed record AgentSupervisionSessionSnapshot(
    string SessionId,
    string WorkItemId,
    string ProjectId,
    string Phase,
    int Iteration,
    string Agent,
    string? AgentInstanceId,
    string? ModelId,
    string? ReasoningMode,
    string SandboxId,
    string WorkingDirectory,
    string Source,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string State,
    bool AcceptingInjections,
    int PendingInjections,
    string OutputTail);

public sealed record AgentSupervisionCommandEvent(
    string SessionId,
    string WorkItemId,
    string ProjectId,
    string Phase,
    int Iteration,
    string Agent,
    string Kind,
    string? InjectionId,
    DateTimeOffset SentAt,
    string Prompt);

public sealed record AgentSupervisionStdoutEvent(
    string SessionId,
    string WorkItemId,
    string Phase,
    string Agent,
    string Chunk,
    DateTimeOffset ObservedAt);

public sealed record AgentSupervisionInjectionRequest(
    string Message,
    string? Actor = null);

public sealed record AgentSupervisionInjectionReceipt(
    bool Accepted,
    string Status,
    string? InjectionId = null,
    string? Error = null);

public sealed record AgentSupervisionInjectionEvent(
    string SessionId,
    string InjectionId,
    string WorkItemId,
    string ProjectId,
    string Phase,
    int Iteration,
    string Agent,
    string Actor,
    DateTimeOffset SentAt,
    string Message);

public sealed record AgentSupervisionInjectionCompletedEvent(
    string SessionId,
    string InjectionId,
    string WorkItemId,
    string ProjectId,
    string Phase,
    int Iteration,
    string Agent,
    string Actor,
    DateTimeOffset SentAt,
    DateTimeOffset CompletedAt,
    bool Success,
    string Summary);

public sealed class AgentSupervisionService : IAgentSupervisionService
{
    private readonly Func<AgentSupervisionOptions> _optionsAccessor;
    private readonly IAgentSupervisionNotifier _notifier;
    private readonly ILogger<AgentSupervisionService> _log;
    private readonly ConcurrentDictionary<string, AgentSupervisionSessionState> _sessions = new(StringComparer.Ordinal);

    public AgentSupervisionService(
        Func<AgentSupervisionOptions> optionsAccessor,
        IAgentSupervisionNotifier? notifier = null,
        ILogger<AgentSupervisionService>? log = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _notifier = notifier ?? NullAgentSupervisionNotifier.Instance;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentSupervisionService>.Instance;
    }

    public bool Enabled => CurrentOptions().Enabled;

    public async Task<AgentSupervisionSessionScope?> TryStartSessionAsync(
        AgentSupervisionSessionStart start,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var options = CurrentOptions();
        if (!options.Enabled)
            return null;

        PruneCompleted(options);
        if (_sessions.Count >= options.MaxSessions)
        {
            _log.LogWarning(
                "Agent supervision session limit reached ({MaxSessions}); session for work item {WorkItemId} phase {Phase} will not be supervised",
                options.MaxSessions,
                start.WorkItemId,
                start.Phase);
            return null;
        }

        var sessionId = "ags-" + Guid.NewGuid().ToString("N");
        var state = new AgentSupervisionSessionState(sessionId, start, options.MaxOutputBufferChars);
        _sessions[sessionId] = state;
        await SafeNotifyAsync(n => n.SessionStartedAsync(BuildSnapshot(state), ct)).ConfigureAwait(false);
        return new AgentSupervisionSessionScope(this, state);
    }

    public Task<IReadOnlyList<AgentSupervisionSessionSnapshot>> ListSessionsAsync(CancellationToken ct = default)
    {
        var options = CurrentOptions();
        if (!options.Enabled)
            return Task.FromResult<IReadOnlyList<AgentSupervisionSessionSnapshot>>([]);

        PruneCompleted(options);
        var sessions = _sessions.Values
            .OrderByDescending(s => s.StartedAt)
            .Select(BuildSnapshot)
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentSupervisionSessionSnapshot>>(sessions);
    }

    public async Task<AgentSupervisionInjectionReceipt> EnqueueInjectionAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        CancellationToken ct = default)
    {
        if (!CurrentOptions().Enabled)
            return new AgentSupervisionInjectionReceipt(false, "disabled", Error: "agent supervision is disabled");
        if (string.IsNullOrWhiteSpace(sessionId))
            return new AgentSupervisionInjectionReceipt(false, "invalid", Error: "sessionId is required");
        if (request is null)
            return new AgentSupervisionInjectionReceipt(false, "invalid", Error: "request is required");
        if (!_sessions.TryGetValue(sessionId, out var state))
            return new AgentSupervisionInjectionReceipt(false, "not_found", Error: "session not found");

        var options = CurrentOptions();
        var message = request.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return new AgentSupervisionInjectionReceipt(false, "invalid", Error: "message is required");
        if (message.Length > options.MaxInjectionChars)
        {
            return new AgentSupervisionInjectionReceipt(
                false,
                "invalid",
                Error: $"message exceeds CodeyBox:AgentSupervision:MaxInjectionChars ({options.MaxInjectionChars})");
        }

        var actor = string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor!.Trim();
        if (actor.Length > 200)
            actor = actor[..200];
        var injection = new AgentSupervisionInjection(
            InjectionId: "agi-" + Guid.NewGuid().ToString("N"),
            Actor: actor,
            Message: message,
            SentAt: DateTimeOffset.UtcNow);

        lock (state.Sync)
        {
            if (!state.AcceptingInjections)
                return new AgentSupervisionInjectionReceipt(false, "closed", Error: "session is no longer accepting injections");
            if (state.PendingCount >= options.InjectionQueueCapacity)
                return new AgentSupervisionInjectionReceipt(false, "queue_full", Error: "injection queue is full");

            state.PendingInjections.Enqueue(injection);
            state.PendingCount++;
        }

        AuditLog.AgentSupervisionInjectionQueued(
            state.Start.WorkItemId,
            sessionId,
            state.Start.Phase,
            state.Start.Agent,
            actor,
            injection.InjectionId,
            message);

        await SafeNotifyAsync(n => n.InjectionQueuedAsync(BuildInjectionEvent(state, injection, options), ct)).ConfigureAwait(false);
        await SafeNotifyAsync(n => n.SessionUpdatedAsync(BuildSnapshot(state), ct)).ConfigureAwait(false);
        return new AgentSupervisionInjectionReceipt(true, "accepted", injection.InjectionId);
    }

    internal async Task PublishCommandAsync(
        AgentSupervisionSessionState state,
        string kind,
        string prompt,
        string? injectionId,
        CancellationToken ct)
    {
        var options = CurrentOptions();
        var redacted = Truncate(RawChunkRedactor.Redact(prompt), options.MaxPromptChars);
        var evt = new AgentSupervisionCommandEvent(
            state.SessionId,
            state.Start.WorkItemId.ToString(),
            state.Start.ProjectId,
            state.Start.Phase,
            state.Start.Iteration,
            state.Start.Agent.Value,
            kind,
            injectionId,
            DateTimeOffset.UtcNow,
            redacted);
        await SafeNotifyAsync(n => n.CodeyBoxCommandAsync(evt, ct)).ConfigureAwait(false);
    }

    internal void PublishStdoutChunk(AgentSupervisionSessionState state, string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        var redacted = RawChunkRedactor.Redact(chunk);
        state.AppendOutput(redacted);
        var evt = new AgentSupervisionStdoutEvent(
            state.SessionId,
            state.Start.WorkItemId.ToString(),
            state.Start.Phase,
            state.Start.Agent.Value,
            redacted,
            DateTimeOffset.UtcNow);
        _ = SafeNotifyAsync(n => n.StdoutChunkAsync(evt, CancellationToken.None));
    }

    internal bool TryBeginNextInjection(
        AgentSupervisionSessionState state,
        out AgentSupervisionInjection injection)
    {
        lock (state.Sync)
        {
            if (state.PendingInjections.TryDequeue(out injection!))
            {
                state.PendingCount--;
                return true;
            }

            state.AcceptingInjections = false;
            injection = null!;
            return false;
        }
    }

    internal async Task MarkInjectionStartedAsync(
        AgentSupervisionSessionState state,
        AgentSupervisionInjection injection,
        CancellationToken ct)
    {
        AuditLog.AgentSupervisionInjectionStarted(
            state.Start.WorkItemId,
            state.SessionId,
            state.Start.Phase,
            state.Start.Agent,
            injection.Actor,
            injection.InjectionId);
        await SafeNotifyAsync(n => n.InjectionStartedAsync(BuildInjectionEvent(state, injection, CurrentOptions()), ct))
            .ConfigureAwait(false);
        await SafeNotifyAsync(n => n.SessionUpdatedAsync(BuildSnapshot(state), ct)).ConfigureAwait(false);
    }

    internal async Task MarkInjectionCompletedAsync(
        AgentSupervisionSessionState state,
        AgentSupervisionInjection injection,
        AgentResult result,
        CancellationToken ct)
    {
        var options = CurrentOptions();
        var summary = Truncate(RawChunkRedactor.Redact(result.Summary ?? ""), options.MaxPromptChars);
        AuditLog.AgentSupervisionInjectionCompleted(
            state.Start.WorkItemId,
            state.SessionId,
            state.Start.Phase,
            state.Start.Agent,
            injection.Actor,
            injection.InjectionId,
            result.Success,
            summary);
        var evt = new AgentSupervisionInjectionCompletedEvent(
            state.SessionId,
            injection.InjectionId,
            state.Start.WorkItemId.ToString(),
            state.Start.ProjectId,
            state.Start.Phase,
            state.Start.Iteration,
            state.Start.Agent.Value,
            injection.Actor,
            injection.SentAt,
            DateTimeOffset.UtcNow,
            result.Success,
            summary);
        await SafeNotifyAsync(n => n.InjectionCompletedAsync(evt, ct)).ConfigureAwait(false);
        await SafeNotifyAsync(n => n.SessionUpdatedAsync(BuildSnapshot(state), ct)).ConfigureAwait(false);
    }

    internal async ValueTask CompleteSessionAsync(AgentSupervisionSessionState state)
    {
        lock (state.Sync)
        {
            if (state.CompletedAt is not null)
                return;
            state.AcceptingInjections = false;
            state.CompletedAt = DateTimeOffset.UtcNow;
        }

        await SafeNotifyAsync(n => n.SessionCompletedAsync(BuildSnapshot(state), CancellationToken.None))
            .ConfigureAwait(false);
    }

    internal static string BuildHumanInjectionPrompt(AgentSupervisionInjection injection) =>
        "## Live operator instruction\n\n" +
        "A human operator connected to CodeyBox's supervision channel sent this additional instruction for the same live sandbox/session. " +
        "Treat it as a normal follow-up turn. Preserve the repository requirements and existing CodeyBox prompt constraints.\n\n" +
        $"<codeybox-human-instruction id=\"{injection.InjectionId}\" actor=\"{EscapeAttribute(injection.Actor)}\" sentAt=\"{injection.SentAt:O}\">\n" +
        injection.Message +
        "\n</codeybox-human-instruction>\n";

    private AgentSupervisionOptions CurrentOptions()
    {
        var options = _optionsAccessor() ?? new AgentSupervisionOptions();
        options.Validate();
        return options;
    }

    private void PruneCompleted(AgentSupervisionOptions options)
    {
        if (options.CompletedSessionRetentionSeconds == 0)
        {
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.CompletedAt is not null)
                    _sessions.TryRemove(kvp.Key, out _);
            }
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-options.CompletedSessionRetentionSeconds);
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.CompletedAt is { } completedAt && completedAt < cutoff)
                _sessions.TryRemove(kvp.Key, out _);
        }
    }

    private AgentSupervisionSessionSnapshot BuildSnapshot(AgentSupervisionSessionState state)
    {
        lock (state.Sync)
        {
            return new AgentSupervisionSessionSnapshot(
                state.SessionId,
                state.Start.WorkItemId.ToString(),
                state.Start.ProjectId,
                state.Start.Phase,
                state.Start.Iteration,
                state.Start.Agent.Value,
                state.Start.AgentInstanceId,
                state.Start.ModelId,
                state.Start.ReasoningMode,
                state.Start.SandboxId,
                state.Start.WorkingDirectory,
                state.Start.Source,
                state.StartedAt,
                state.CompletedAt,
                state.CompletedAt is null ? "running" : "completed",
                state.AcceptingInjections,
                state.PendingCount,
                state.GetOutputTail());
        }
    }

    private static AgentSupervisionInjectionEvent BuildInjectionEvent(
        AgentSupervisionSessionState state,
        AgentSupervisionInjection injection,
        AgentSupervisionOptions options)
    {
        var message = Truncate(RawChunkRedactor.Redact(injection.Message), options.MaxPromptChars);
        return new AgentSupervisionInjectionEvent(
            state.SessionId,
            injection.InjectionId,
            state.Start.WorkItemId.ToString(),
            state.Start.ProjectId,
            state.Start.Phase,
            state.Start.Iteration,
            state.Start.Agent.Value,
            injection.Actor,
            injection.SentAt,
            message);
    }

    private async Task SafeNotifyAsync(Func<IAgentSupervisionNotifier, Task> call)
    {
        try
        {
            await call(_notifier).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Agent supervision notification failed");
        }
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;
        return value[..maxChars] + $"... [{value.Length - maxChars} chars truncated]";
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal);
}

public sealed class AgentSupervisionSessionScope : IAsyncDisposable
{
    private readonly AgentSupervisionService _owner;
    private readonly AgentSupervisionSessionState _state;
    private bool _disposed;

    internal AgentSupervisionSessionScope(AgentSupervisionService owner, AgentSupervisionSessionState state)
    {
        _owner = owner;
        _state = state;
    }

    public string SessionId => _state.SessionId;

    public Action<string>? WrapStdoutCallback(Action<string>? inner)
    {
        return chunk =>
        {
            _owner.PublishStdoutChunk(_state, chunk);
            inner?.Invoke(chunk);
        };
    }

    public Task PublishCodeyBoxCommandAsync(string kind, string prompt, string? injectionId, CancellationToken ct = default) =>
        _owner.PublishCommandAsync(_state, kind, prompt, injectionId, ct);

    public bool TryBeginNextInjection(out AgentSupervisionInjection injection) =>
        _owner.TryBeginNextInjection(_state, out injection);

    public Task MarkInjectionStartedAsync(AgentSupervisionInjection injection, CancellationToken ct = default) =>
        _owner.MarkInjectionStartedAsync(_state, injection, ct);

    public Task MarkInjectionCompletedAsync(AgentSupervisionInjection injection, AgentResult result, CancellationToken ct = default) =>
        _owner.MarkInjectionCompletedAsync(_state, injection, result, ct);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        return _owner.CompleteSessionAsync(_state);
    }
}

public sealed record AgentSupervisionInjection(
    string InjectionId,
    string Actor,
    string Message,
    DateTimeOffset SentAt);

internal sealed class AgentSupervisionSessionState
{
    private readonly int _maxOutputChars;
    private readonly string _sessionId;
    private readonly StringBuilder _output = new();

    public AgentSupervisionSessionState(string sessionId, AgentSupervisionSessionStart start, int maxOutputChars)
    {
        _sessionId = sessionId;
        Start = start;
        _maxOutputChars = maxOutputChars;
    }

    public object Sync { get; } = new();
    public string SessionId => _sessionId;
    public AgentSupervisionSessionStart Start { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool AcceptingInjections { get; set; } = true;
    public Queue<AgentSupervisionInjection> PendingInjections { get; } = new();
    public int PendingCount { get; set; }

    public void AppendOutput(string chunk)
    {
        lock (Sync)
        {
            _output.Append(chunk);
            if (_output.Length <= _maxOutputChars)
                return;
            _output.Remove(0, _output.Length - _maxOutputChars);
        }
    }

    public string GetOutputTail()
    {
        lock (Sync)
        {
            return _output.ToString();
        }
    }
}
