using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Configuration for <see cref="CheapModelCuaAuthor"/>. The model id is
/// metadata for audit trails — authoring must use a cheap model, never a
/// frontier coding agent.
/// </summary>
public sealed record CheapModelCuaAuthorOptions
{
    /// <summary>Default Haiku-class model used for computer-use authoring.</summary>
    public string ModelId { get; init; } = "claude-haiku-4-5-20251001";
}

/// <summary>
/// Orchestrates a cheap-model computer-use exploration session and emits a
/// deterministic <see cref="E2eReplayArtifact"/>. The explorer implementation
/// is injected so tests can substitute a scripted driver that does not call a
/// live model API.
/// </summary>
public sealed class CheapModelCuaAuthor
{
    private readonly CheapModelCuaAuthorOptions _options;
    private readonly TimeProvider _timeProvider;

    public CheapModelCuaAuthor(
        CheapModelCuaAuthorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new CheapModelCuaAuthorOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ModelId => _options.ModelId;

    public async Task<E2eAuthoringResult> ExploreAndEmitAsync(
        AppUnderTestSession session,
        IE2eCuaExplorer explorer,
        E2eExplorationPlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(explorer);
        ArgumentNullException.ThrowIfNull(plan);

        var recorder = new RecordingComputerUseBridge(
            session.ComputerUse,
            _timeProvider,
            new RecordingComputerUseBridgeOptions { Modality = plan.Modality });

        recorder.SetMetadata(
            targetName: plan.TargetName,
            entryUrl: plan.EntryUrl ?? session.EntryUrl,
            readinessScreenshotPng: session.ReadinessScreenshotPng);

        await explorer.ExploreAsync(session.Sandbox, recorder, plan, ct).ConfigureAwait(false);
        recorder.EndTrace();

        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
            recorder.Trace,
            plan.Assertions,
            plan.EmitOptions);

        return new E2eAuthoringResult(recorder.Trace, artifact, _options.ModelId);
    }
}

public sealed record E2eAuthoringResult(
    SessionTrace Trace,
    E2eReplayArtifact Artifact,
    string AuthorModelId);

/// <summary>High-level capability the cheap-model CUA is asked to exercise.</summary>
public sealed record E2eExplorationPlan
{
    public required string TargetName { get; init; }
    public string? EntryUrl { get; init; }
    public string Modality { get; init; } = "web-graphical";
    public required IReadOnlyList<E2eExplorationAction> Actions { get; init; }
    public required IReadOnlyList<E2eReplayAssertion> Assertions { get; init; }
    public E2eReplayEmitOptions? EmitOptions { get; init; }
}

public sealed record E2eExplorationAction
{
    public required string Kind { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public string? Text { get; init; }
    public string? Key { get; init; }
}

public interface IE2eCuaExplorer
{
    Task ExploreAsync(
        ISandbox sandbox,
        RecordingComputerUseBridge recorder,
        E2eExplorationPlan plan,
        CancellationToken ct = default);
}

/// <summary>
/// Deterministic stand-in for a cheap-model computer-use agent. Drives the
/// real computer-use bridge with scripted actions so authoring tests never
/// burn frontier coding quota.
/// </summary>
public sealed class ScriptedE2eCuaExplorer : IE2eCuaExplorer
{
    public async Task ExploreAsync(
        ISandbox sandbox,
        RecordingComputerUseBridge recorder,
        E2eExplorationPlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            var request = action.Kind switch
            {
                "click" => new ComputerUseRequest { Action = "click", X = action.X ?? 0, Y = action.Y ?? 0 },
                "type" => new ComputerUseRequest { Action = "type", Text = action.Text ?? string.Empty },
                "key" => new ComputerUseRequest { Action = "key", Key = action.Key ?? action.Text },
                "screenshot" => new ComputerUseRequest { Action = "screenshot" },
                _ => throw new NotSupportedException($"Unsupported exploration action '{action.Kind}'."),
            };

            await recorder.ExecuteAsync(sandbox, request, ct).ConfigureAwait(false);
        }
    }
}
