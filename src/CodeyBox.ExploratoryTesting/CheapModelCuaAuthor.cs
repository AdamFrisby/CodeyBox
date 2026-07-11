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

    public ComputerUseAuthoringLimits AuthoringLimits { get; init; } = new();
}

/// <summary>
/// Orchestrates a cheap-model computer-use exploration session and emits a
/// deterministic <see cref="E2eReplayArtifact"/>.
/// </summary>
public sealed class CheapModelCuaAuthor
{
    private readonly CheapModelCuaAuthorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IComputerUseModelClient? _defaultModelClient;
    private readonly IE2eReauthoringHook _reauthoringHook;

    public CheapModelCuaAuthor(
        CheapModelCuaAuthorOptions? options = null,
        TimeProvider? timeProvider = null,
        IComputerUseModelClient? defaultModelClient = null,
        IE2eReauthoringHook? reauthoringHook = null)
    {
        _options = options ?? new CheapModelCuaAuthorOptions();
        CheapModelAllowlist.EnsureCheap(_options.ModelId);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _defaultModelClient = defaultModelClient;
        _reauthoringHook = reauthoringHook ?? NullE2eReauthoringHook.Instance;
    }

    public string ModelId => _options.ModelId;

    public IE2eReauthoringHook ReauthoringHook => _reauthoringHook;

    public async Task<E2eAuthoringResult> ExploreAndEmitAsync(
        AppUnderTestSession session,
        E2eExplorationPlan plan,
        IE2eCuaExplorer? explorer = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);

        explorer ??= CreateDefaultExplorer();
        var recorder = CreateRecorder(session, plan);
        ComputerUseAuthoringActionPolicy.EnsurePlanAllowed(plan, _options.AuthoringLimits);
        await explorer.ExploreAsync(session.Sandbox, recorder, plan, ct).ConfigureAwait(false);
        recorder.EndTrace();

        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
            recorder.Trace,
            plan.Assertions,
            plan.EmitOptions);

        return new E2eAuthoringResult(recorder.Trace, artifact, _options.ModelId);
    }

    public Task<bool> TryReauthorAfterReplayFailureAsync(
        AppUnderTestSession session,
        E2eExplorationPlan plan,
        E2eRunResult failedReplay,
        CancellationToken ct = default)
        => _reauthoringHook.TryReauthorAsync(session, plan, failedReplay, ct);

    private RecordingComputerUseBridge CreateRecorder(AppUnderTestSession session, E2eExplorationPlan plan)
    {
        var limits = _options.AuthoringLimits;
        var recorder = new RecordingComputerUseBridge(
            session.ComputerUse,
            _timeProvider,
            new RecordingComputerUseBridgeOptions
            {
                Modality = plan.Modality,
                MaxTraceEntries = limits.MaxTraceEntries,
                MaxTraceBytes = limits.MaxTraceBytes,
            });

        recorder.SetMetadata(
            targetName: plan.TargetName,
            entryUrl: plan.EntryUrl ?? session.EntryUrl,
            readinessScreenshotPng: session.ReadinessScreenshotPng);
        return recorder;
    }

    private IE2eCuaExplorer CreateDefaultExplorer()
    {
        if (_defaultModelClient is null)
            throw new InvalidOperationException("No IE2eCuaExplorer was supplied and no default IComputerUseModelClient is configured.");

        return new AnthropicCheapModelCuaExplorer(
            _defaultModelClient,
            _options.ModelId,
            _options.AuthoringLimits);
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
    /// <summary>Scripted actions for test explorers only; production explorers plan turns via the model client.</summary>
    public IReadOnlyList<E2eExplorationAction> Actions { get; init; } = [];
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
        IComputerUseExplorationTarget target,
        E2eExplorationPlan plan,
        CancellationToken ct = default);
}
