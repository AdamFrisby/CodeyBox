using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.Tests.E2eAuthoring;

/// <summary>
/// Deterministic stand-in for a cheap-model computer-use agent. Drives the
/// real computer-use bridge with scripted actions so authoring tests never
/// burn frontier coding quota.
/// </summary>
public sealed class ScriptedE2eCuaExplorer : IE2eCuaExplorer
{
    public async Task ExploreAsync(
        ISandbox sandbox,
        IComputerUseExplorationTarget target,
        E2eExplorationPlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(target);
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

            await target.ExecuteAsync(sandbox, request, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Returns scripted computer-use actions turn-by-turn through the model-client
/// seam so tests exercise the real cheap-model explorer loop.
/// </summary>
public sealed class ScriptedComputerUseModelClient : IComputerUseModelClient
{
    private readonly IReadOnlyList<E2eExplorationAction> _actions;
    private int _index;

    public ScriptedComputerUseModelClient(IReadOnlyList<E2eExplorationAction> actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public Task<IReadOnlyList<ComputerUseRequest>> PlanNextActionsAsync(
        ComputerUseModelTurnContext context,
        CancellationToken ct = default)
    {
        if (_index >= _actions.Count)
            return Task.FromResult<IReadOnlyList<ComputerUseRequest>>([]);

        var action = _actions[_index++];
        ComputerUseRequest request = action.Kind switch
        {
            "click" => new ComputerUseRequest { Action = "click", X = action.X ?? 0, Y = action.Y ?? 0 },
            "type" => new ComputerUseRequest { Action = "type", Text = action.Text ?? string.Empty },
            "key" => new ComputerUseRequest { Action = "key", Key = action.Key ?? action.Text },
            "screenshot" => new ComputerUseRequest { Action = "screenshot" },
            _ => throw new NotSupportedException($"Unsupported exploration action '{action.Kind}'."),
        };

        return Task.FromResult<IReadOnlyList<ComputerUseRequest>>([request]);
    }
}
