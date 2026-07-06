using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Plans the next computer-use actions for an exploration turn. Production
/// wiring uses <see cref="AnthropicComputerUseModelClient"/>; tests inject a
/// deterministic stand-in.
/// </summary>
public interface IComputerUseModelClient
{
    Task<IReadOnlyList<ComputerUseRequest>> PlanNextActionsAsync(
        ComputerUseModelTurnContext context,
        CancellationToken ct = default);
}

/// <summary>One cheap-model turn: screenshot plus exploration metadata.</summary>
public sealed record ComputerUseModelTurnContext
{
    public required string ModelId { get; init; }
    public required E2eExplorationPlan Plan { get; init; }
    public required int TurnIndex { get; init; }
    public byte[]? ScreenshotPng { get; init; }
    public IReadOnlyList<ComputerUseRequest> PriorActions { get; init; } = [];
}
