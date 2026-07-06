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
    private readonly int _maxTurns;

    public AnthropicCheapModelCuaExplorer(
        IComputerUseModelClient modelClient,
        string modelId,
        int maxTurns = 32)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        CheapModelAllowlist.EnsureCheap(modelId);
        _modelId = modelId;
        _maxTurns = maxTurns;
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

        var priorActions = new List<ComputerUseRequest>();
        for (var turn = 0; turn < _maxTurns; turn++)
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
                },
                ct).ConfigureAwait(false);

            if (actions.Count == 0)
                break;

            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(action.Action, "screenshot", StringComparison.OrdinalIgnoreCase))
                    continue;

                await target.ExecuteAsync(sandbox, action, ct).ConfigureAwait(false);
                priorActions.Add(action);
            }
        }
    }
}
