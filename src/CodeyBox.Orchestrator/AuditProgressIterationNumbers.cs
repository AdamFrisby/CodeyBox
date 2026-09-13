namespace CodeyBox.Orchestrator;

internal static class AuditProgressIterationNumbers
{
    // Work-phase events nominally have no iteration number ("work runs once").
    // Aligning them with audit iteration 1 keeps audit-progress attempt lookups
    // paired with the work that produced the first audit input.
    public const int WorkPhase = 1;

    // Delegation turns are orthogonal to the audit iteration ladder (they run
    // before it, at most once per explicit trigger). Zero keeps delegation
    // iteration events and agent-log paths distinct from every 1-based audit
    // iteration, and no audit-progress dispatch row is ever recorded for it.
    public const int DelegationPhase = 0;
}
