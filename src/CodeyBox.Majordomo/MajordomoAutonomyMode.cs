namespace CodeyBox.Majordomo;

/// <summary>
/// Operator-selected autonomy level for the majordomo. It is configuration
/// (see <see cref="MajordomoOptions"/>), not a constant: the authorization
/// decision takes the current value on every call so a config reload flips
/// the mode without a restart. Both modes are first-class — neither is a
/// degraded version of the other.
/// </summary>
public enum MajordomoAutonomyMode
{
    /// <summary>MUTATE tools execute immediately, subject to blast-radius bounds.</summary>
    Autonomous,

    /// <summary>
    /// MUTATE tools do not execute; each produces a proposal the operator
    /// approves or rejects. READ tools still execute.
    /// </summary>
    Proposed,
}
