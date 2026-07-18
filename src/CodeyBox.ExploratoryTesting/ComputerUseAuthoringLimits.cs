namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Bounded operational limits for cheap-model computer-use authoring.
/// </summary>
public sealed record ComputerUseAuthoringLimits
{
    public int MaxTurns { get; init; } = 32;
    public int MaxActionsPerTurn { get; init; } = 16;
    public int MaxTotalActions { get; init; } = 128;
    public int MaxTraceEntries { get; init; } = 256;
    public int MaxTraceBytes { get; init; } = 32 * 1024 * 1024;
    public int DisplayWidthPx { get; init; } = 1280;
    public int DisplayHeightPx { get; init; } = 800;
    public int MaxModelResponseBytes { get; init; } = 256 * 1024;
    public int MaxToolUsesPerTurn { get; init; } = 16;

    public IReadOnlyList<string> AllowedOrigins { get; init; } =
    [
        "http://app.local",
        "https://app.local",
    ];
}
