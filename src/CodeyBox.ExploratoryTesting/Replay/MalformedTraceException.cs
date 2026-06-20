namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Thrown by the engine when a recorded action's shape is internally
/// inconsistent (a recorder bug), as opposed to a sandbox / input-dispatch
/// failure. Surfaces as a precise recording-shape diagnostic on
/// <see cref="ReplayStepResult"/> instead of being conflated with
/// <see cref="ReplayFailureKind.ActionFailed"/>'s "input dispatch failed"
/// wording, so operators can triage a recorder bug without parsing the
/// downstream validator's text.
/// </summary>
internal sealed class MalformedTraceException : Exception
{
    public MalformedTraceException(string message) : base(message)
    {
    }
}
