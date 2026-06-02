using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Host-side policy limits for startup sandbox resume configuration.
/// </summary>
public static class SandboxStartupResumePolicy
{
    public static readonly TimeSpan DefaultResumeTimeout = SuspendTimeoutPolicy.DefaultFloor;
    public static readonly TimeSpan DefaultAdoptionDeadline = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumResumeTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan MaximumAdoptionDeadline = TimeSpan.FromHours(2);
}
