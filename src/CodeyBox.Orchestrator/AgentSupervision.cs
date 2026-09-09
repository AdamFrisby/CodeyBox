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
    public int InjectionDrainIdleTimeoutMs { get; set; } = 250;
    public int CompletedSessionRetentionSeconds { get; set; } = 300;
    public int MaxSessions { get; set; } = 512;

    /// <summary>
    /// Hard upper bound enforced regardless of operator config so a permissive
    /// MaxSessions can't turn a listing call into an unbounded memory hog.
    /// </summary>
    public const int MaxSessionsCeiling = 4096;

    /// <summary>How many recent CodeyBox commands to retain on each session for late-join review.</summary>
    public int RetainedCommandsPerSession { get; set; } = 32;

    /// <summary>Default page size for /agent-supervision/sessions.</summary>
    public int DefaultListPageSize { get; set; } = 64;

    /// <summary>Hard cap on the per-request page size (operator overrides clamp to this).</summary>
    public int MaxListPageSize { get; set; } = 256;

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
        if (InjectionDrainIdleTimeoutMs < 0)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:InjectionDrainIdleTimeoutMs must be >= 0");
        if (CompletedSessionRetentionSeconds < 0)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:CompletedSessionRetentionSeconds must be >= 0");
        if (MaxSessions < 1)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:MaxSessions must be >= 1");
        if (MaxSessions > MaxSessionsCeiling)
            throw new InvalidOperationException($"CodeyBox:AgentSupervision:MaxSessions must be <= {MaxSessionsCeiling}");
        if (RetainedCommandsPerSession < 0)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:RetainedCommandsPerSession must be >= 0");
        if (DefaultListPageSize < 1)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:DefaultListPageSize must be >= 1");
        if (MaxListPageSize < DefaultListPageSize)
            throw new InvalidOperationException("CodeyBox:AgentSupervision:MaxListPageSize must be >= DefaultListPageSize");
    }
}

public interface IAgentSupervisionService
{
    bool Enabled { get; }

    Task<IAgentSupervisionSession?> TryStartSessionAsync(
        AgentSupervisionSessionStart start,
        CancellationToken ct = default);

    Task<AgentSupervisionSessionPage> ListSessionsAsync(
        AgentSupervisionListQuery query,
        CancellationToken ct = default);

    Task<AgentSupervisionInjectionReceipt> EnqueueInjectionAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Caller-facing supervision-session contract. Hides the queue-drain internals
/// of <see cref="AgentSupervisionService"/>; callers depend on the behaviour
/// (running pending injections through their own runner-dispatch delegate) not
/// the underlying state machine.
/// </summary>
public interface IAgentSupervisionSession : IAsyncDisposable
{
    string SessionId { get; }
    Action<string>? WrapStdoutCallback(Action<string>? inner);
    Task PublishCodeyBoxCommandAsync(string kind, string prompt, string? injectionId, CancellationToken ct = default);

    /// <summary>
    /// Drains queued human injections by formatting each as a follow-up
    /// agent prompt and delegating dispatch to <paramref name="runTurnAsync"/>.
    /// The delegate is responsible for any prompt preprocessing AND for
    /// routing through the session-capable runner abstraction
    /// (<see cref="ISessionAgentRunner"/>) so injections preserve
    /// resumable-session and thinking-block invariants. The session lifecycle
    /// events (started/completed, audit-log entries, notifier callbacks) are
    /// managed inside this method.
    /// </summary>
    Task<AgentResult> RunPendingInjectionsAsync(
        AgentResult current,
        Func<AgentSupervisionInjectionTurn, CancellationToken, Task<AgentResult>> runTurnAsync,
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
    string OutputTail,
    IReadOnlyList<AgentSupervisionCommandRecord> RecentCommands);

/// <summary>Persisted CodeyBox command record exposed to late-joining supervisors.</summary>
public sealed record AgentSupervisionCommandRecord(
    string Kind,
    string? InjectionId,
    DateTimeOffset SentAt,
    string Prompt);

public sealed record AgentSupervisionListQuery(
    int? Skip = null,
    int? Take = null,
    bool IncludeOutputTail = true,
    int? OutputTailMaxChars = null,
    int? RecentCommandsLimit = null);

public sealed record AgentSupervisionSessionPage(
    bool Enabled,
    int Total,
    int Skip,
    int Take,
    IReadOnlyList<AgentSupervisionSessionSnapshot> Sessions);

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

/// <summary>
/// Payload supplied to the injection-turn delegate. Carries both the queued
/// injection record and the pre-formatted prompt so callers can apply their
/// own prompt-preprocessor chain before dispatching the turn.
/// </summary>
public sealed record AgentSupervisionInjectionTurn(
    AgentSupervisionInjection Injection,
    string Prompt);

public sealed class AgentSupervisionService : IAgentSupervisionService
{
    private readonly Func<AgentSupervisionOptions> _optionsAccessor;
    private readonly IAgentSupervisionNotifier _notifier;
    private readonly ILogger<AgentSupervisionService> _log;
    // Audit events flow through this Serilog logger rather than the static global
    // so a concurrent reassignment of Serilog.Log.Logger cannot silently reroute
    // them. Captured at construction from the process-global logger when none is
    // injected.
    private readonly Serilog.ILogger _auditLogger;
    private readonly ConcurrentDictionary<string, AgentSupervisionSessionState> _sessions = new(StringComparer.Ordinal);

    public AgentSupervisionService(
        Func<AgentSupervisionOptions> optionsAccessor,
        IAgentSupervisionNotifier? notifier = null,
        ILogger<AgentSupervisionService>? log = null,
        Serilog.ILogger? auditLogger = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _notifier = notifier ?? NullAgentSupervisionNotifier.Instance;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentSupervisionService>.Instance;
        _auditLogger = auditLogger ?? Serilog.Log.Logger;
    }

    public bool Enabled => CurrentOptions().Enabled;

    public async Task<IAgentSupervisionSession?> TryStartSessionAsync(
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
        var state = new AgentSupervisionSessionState(sessionId, start, options.MaxOutputBufferChars, options.RetainedCommandsPerSession);
        _sessions[sessionId] = state;
        await SafeNotifyAsync(n => n.SessionStartedAsync(BuildSnapshot(state, options), ct)).ConfigureAwait(false);
        return new AgentSupervisionSessionScope(this, state);
    }

    public Task<AgentSupervisionSessionPage> ListSessionsAsync(
        AgentSupervisionListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var options = CurrentOptions();
        if (!options.Enabled)
            return Task.FromResult(new AgentSupervisionSessionPage(false, 0, 0, 0, []));

        PruneCompleted(options);

        var defaultTake = Math.Max(1, options.DefaultListPageSize);
        var maxTake = Math.Max(defaultTake, options.MaxListPageSize);
        var skip = Math.Max(0, query.Skip ?? 0);
        var take = query.Take is int t && t > 0 ? Math.Min(t, maxTake) : defaultTake;
        var tailLimit = query.IncludeOutputTail
            ? (query.OutputTailMaxChars is int max && max >= 0 ? Math.Min(max, options.MaxOutputBufferChars) : options.MaxOutputBufferChars)
            : 0;
        var commandsLimit = query.RecentCommandsLimit is int rc && rc >= 0
            ? Math.Min(rc, options.RetainedCommandsPerSession)
            : options.RetainedCommandsPerSession;

        var ordered = _sessions.Values
            .OrderByDescending(s => s.StartedAt)
            .ToList();
        var page = ordered
            .Skip(skip)
            .Take(take)
            .Select(s => BuildSnapshot(s, options, tailLimit, commandsLimit))
            .ToList();
        return Task.FromResult(new AgentSupervisionSessionPage(true, ordered.Count, skip, page.Count, page));
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
            state.PendingInjectionSignal.TrySetResult();
        }

        AuditLog.AgentSupervisionInjectionQueued(
            state.Start.WorkItemId,
            sessionId,
            state.Start.Phase,
            state.Start.Agent,
            actor,
            injection.InjectionId,
            message,
            _auditLogger);

        await SafeNotifyAsync(n => n.InjectionQueuedAsync(BuildInjectionEvent(state, injection, options), ct)).ConfigureAwait(false);
        await SafeNotifyAsync(n => n.SessionUpdatedAsync(BuildSnapshot(state, options), ct)).ConfigureAwait(false);
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
        var sentAt = DateTimeOffset.UtcNow;
        state.AppendCommand(new AgentSupervisionCommandRecord(kind, injectionId, sentAt, redacted));
        var evt = new AgentSupervisionCommandEvent(
            state.SessionId,
            state.Start.WorkItemId.ToString(),
            state.Start.ProjectId,
            state.Start.Phase,
            state.Start.Iteration,
            state.Start.Agent.Value,
            kind,
            injectionId,
            sentAt,
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

    /// <summary>
    /// Drain queued injections, format the prompt, and delegate dispatch to
    /// <paramref name="runTurnAsync"/>. Stops on the first failed turn or
    /// cancellation. The caller's delegate is the integration point for prompt
    /// preprocessing and session-aware runner routing.
    /// </summary>
    internal async Task<AgentResult> RunPendingInjectionsAsync(
        AgentSupervisionSessionState state,
        AgentResult current,
        Func<AgentSupervisionInjectionTurn, CancellationToken, Task<AgentResult>> runTurnAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runTurnAsync);
        var result = current;
        while (true)
        {
            if (!TryBeginNextInjection(state, out var injection))
            {
                if (await WaitForPendingInjectionAsync(state, ct).ConfigureAwait(false))
                    continue;
                if (TryCloseInjectionQueueIfEmpty(state))
                    break;
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var prompt = BuildHumanInjectionPrompt(injection);
            await MarkInjectionStartedAsync(state, injection, ct).ConfigureAwait(false);
            await PublishCommandAsync(state, "human-injection", prompt, injection.InjectionId, ct).ConfigureAwait(false);
            AgentResult turn;
            try
            {
                turn = await runTurnAsync(new AgentSupervisionInjectionTurn(injection, prompt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                turn = new AgentResult(
                    false,
                    $"injection threw {ex.GetType().Name}: {ex.Message}",
                    Stdout: null,
                    Stderr: ex.ToString());
                await MarkInjectionCompletedAsync(state, injection, turn, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            await MarkInjectionCompletedAsync(state, injection, turn, ct).ConfigureAwait(false);
            result = MergeTurnResult(result, turn);
            if (!turn.Success)
                break;
        }

        return result;
    }

    private bool TryBeginNextInjection(
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

            injection = null!;
            return false;
        }
    }

    private async Task<bool> WaitForPendingInjectionAsync(
        AgentSupervisionSessionState state,
        CancellationToken ct)
    {
        var timeoutMs = CurrentOptions().InjectionDrainIdleTimeoutMs;
        if (timeoutMs <= 0)
            return false;

        Task waitTask;
        lock (state.Sync)
        {
            if (state.PendingCount > 0)
                return true;
            if (!state.AcceptingInjections)
                return false;

            state.PendingInjectionSignal = AgentSupervisionSessionState.CreatePendingInjectionSignal();
            waitTask = state.PendingInjectionSignal.Task;
        }

        try
        {
            await waitTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static bool TryCloseInjectionQueueIfEmpty(AgentSupervisionSessionState state)
    {
        lock (state.Sync)
        {
            if (state.PendingCount > 0)
                return false;
            state.AcceptingInjections = false;
            return true;
        }
    }

    private async Task MarkInjectionStartedAsync(
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
            injection.InjectionId,
            _auditLogger);
        var options = CurrentOptions();
        await SafeNotifyAsync(n => n.InjectionStartedAsync(BuildInjectionEvent(state, injection, options), ct))
            .ConfigureAwait(false);
        await SafeNotifyAsync(n => n.SessionUpdatedAsync(BuildSnapshot(state, options), ct)).ConfigureAwait(false);
    }

    private async Task MarkInjectionCompletedAsync(
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
            summary,
            _auditLogger);
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
        await SafeNotifyAsync(n => n.SessionUpdatedAsync(BuildSnapshot(state, options), ct)).ConfigureAwait(false);
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

        await SafeNotifyAsync(n => n.SessionCompletedAsync(BuildSnapshot(state, CurrentOptions()), CancellationToken.None))
            .ConfigureAwait(false);
    }

    internal static string BuildHumanInjectionPrompt(AgentSupervisionInjection injection) =>
        "## Live operator instruction\n\n" +
        "A human operator connected to CodeyBox's supervision channel sent this additional instruction for the same live sandbox/session. " +
        "Treat it as a normal follow-up turn. Preserve the repository requirements and existing CodeyBox prompt constraints.\n\n" +
        $"<codeybox-human-instruction id=\"{injection.InjectionId}\" actor=\"{EscapeAttribute(injection.Actor)}\" sentAt=\"{injection.SentAt:O}\">\n" +
        injection.Message +
        "\n</codeybox-human-instruction>\n";

    internal static AgentResult MergeTurnResult(AgentResult previous, AgentResult latest) =>
        latest with
        {
            Stdout = CombineAgentText(previous.Stdout, latest.Stdout),
            Stderr = CombineAgentText(previous.Stderr, latest.Stderr),
        };

    private static string? CombineAgentText(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))
            return second;
        if (string.IsNullOrEmpty(second))
            return first;
        return first.EndsWith('\n') ? first + second : first + "\n" + second;
    }

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

    private AgentSupervisionSessionSnapshot BuildSnapshot(
        AgentSupervisionSessionState state,
        AgentSupervisionOptions options,
        int? outputTailMaxChars = null,
        int? recentCommandsLimit = null)
    {
        lock (state.Sync)
        {
            var tail = state.GetOutputTail();
            if (outputTailMaxChars is int limit && tail.Length > limit)
                tail = limit == 0 ? string.Empty : tail[^limit..];

            var commands = state.GetRecentCommandsSnapshot();
            var cmdLimit = recentCommandsLimit ?? options.RetainedCommandsPerSession;
            if (commands.Count > cmdLimit)
                commands = commands.GetRange(commands.Count - cmdLimit, cmdLimit);

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
                tail,
                commands);
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

internal sealed class AgentSupervisionSessionScope : IAgentSupervisionSession
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

    public Task<AgentResult> RunPendingInjectionsAsync(
        AgentResult current,
        Func<AgentSupervisionInjectionTurn, CancellationToken, Task<AgentResult>> runTurnAsync,
        CancellationToken ct = default) =>
        _owner.RunPendingInjectionsAsync(_state, current, runTurnAsync, ct);

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
    private readonly int _retainedCommands;
    private readonly string _sessionId;
    private readonly StringBuilder _output = new();
    private readonly Queue<AgentSupervisionCommandRecord> _commands = new();

    public AgentSupervisionSessionState(
        string sessionId,
        AgentSupervisionSessionStart start,
        int maxOutputChars,
        int retainedCommands)
    {
        _sessionId = sessionId;
        Start = start;
        _maxOutputChars = maxOutputChars;
        _retainedCommands = retainedCommands;
    }

    public object Sync { get; } = new();
    public string SessionId => _sessionId;
    public AgentSupervisionSessionStart Start { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool AcceptingInjections { get; set; } = true;
    public Queue<AgentSupervisionInjection> PendingInjections { get; } = new();
    public int PendingCount { get; set; }
    public TaskCompletionSource PendingInjectionSignal { get; set; } = CreatePendingInjectionSignal();

    public static TaskCompletionSource CreatePendingInjectionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    public void AppendCommand(AgentSupervisionCommandRecord record)
    {
        lock (Sync)
        {
            if (_retainedCommands == 0)
                return;
            _commands.Enqueue(record);
            while (_commands.Count > _retainedCommands)
                _commands.Dequeue();
        }
    }

    public List<AgentSupervisionCommandRecord> GetRecentCommandsSnapshot()
    {
        lock (Sync)
        {
            return _commands.ToList();
        }
    }
}
