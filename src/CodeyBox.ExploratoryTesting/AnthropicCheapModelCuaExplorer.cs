using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Cheap-model explorer that consults an <see cref="IComputerUseModelClient"/>
/// each turn and drives the real computer-use bridge.
/// </summary>
public sealed class AnthropicCheapModelCuaExplorer : IE2eCuaExplorer
{
    private readonly IComputerUseModelClient _modelClient;
    private readonly string _modelId;
    private readonly ComputerUseAuthoringLimits _limits;

    public AnthropicCheapModelCuaExplorer(
        IComputerUseModelClient modelClient,
        string modelId,
        ComputerUseAuthoringLimits? limits = null)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        CheapModelAllowlist.EnsureCheap(modelId);
        _modelId = modelId;
        _limits = limits ?? new ComputerUseAuthoringLimits();
    }

    public async Task ExploreAsync(
        ISandbox sandbox,
        IComputerUseExplorationTarget target,
        E2eExplorationPlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);

        ComputerUseAuthoringActionPolicy.EnsurePlanAllowed(plan, _limits);

        var priorActions = new List<ComputerUseRequest>();
        var totalExecutedActions = 0;

        for (var turn = 0; turn < _limits.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var screenshot = await target.ExecuteAsync(
                sandbox,
                new ComputerUseRequest { Action = "screenshot" },
                ct).ConfigureAwait(false);

            var actions = await _modelClient.PlanNextActionsAsync(
                new ComputerUseModelTurnContext
                {
                    ModelId = _modelId,
                    Plan = plan,
                    TurnIndex = turn,
                    ScreenshotPng = screenshot.ScreenshotPng,
                    PriorActions = priorActions,
                    MaxResponseBytes = _limits.MaxModelResponseBytes,
                    MaxToolUses = _limits.MaxActionsPerTurn,
                },
                ct).ConfigureAwait(false);

            if (actions.Count == 0)
                break;

            if (actions.Count > _limits.MaxActionsPerTurn)
            {
                throw new InvalidOperationException(
                    $"Model returned {actions.Count} actions; the per-turn cap is {_limits.MaxActionsPerTurn}.");
            }

            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(action.Action, "screenshot", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (totalExecutedActions >= _limits.MaxTotalActions)
                {
                    throw new InvalidOperationException(
                        $"Authoring action cap of {_limits.MaxTotalActions} total actions was exceeded.");
                }

                ComputerUseAuthoringActionPolicy.EnsureActionAllowed(action, _limits);
                await target.ExecuteAsync(sandbox, action, ct).ConfigureAwait(false);
                priorActions.Add(action);
                totalExecutedActions++;
            }
        }
    }
}
