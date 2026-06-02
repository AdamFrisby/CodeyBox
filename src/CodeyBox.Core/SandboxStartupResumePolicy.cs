namespace CodeyBox.Core;

/// <summary>
/// Public policy limits for startup resume configuration. Kept in Core so API
/// validation does not depend on a concrete orchestrator hosted service.
/// </summary>
public static class SandboxStartupResumePolicy
{
    public static readonly TimeSpan DefaultResumeTimeout = SuspendTimeoutPolicy.DefaultFloor;
    public static readonly TimeSpan DefaultAdoptionDeadline = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumResumeTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan MaximumAdoptionDeadline = TimeSpan.FromHours(2);
}
