using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Copies the provider-specific Claude session config into the orchestrator's
/// provider-neutral session dispatch options. Kept separate from Program.cs so
/// startup and hot-reload assignment semantics are directly testable.
/// </summary>
internal static class AgentSessionDispatchOptionsBinder
{
    public static void Apply(AgentSessionDispatchOptions target, ClaudeSessionOptions? src)
    {
        target.Enabled = src?.Enabled ?? false;
        target.PreemptiveSelfReviewEnabled = src?.PreemptiveSelfReview?.Enabled ?? false;
    }
}
