namespace CodeyBox.Orchestrator;

internal static class AuditProgressIterationNumbers
{
    // Work-phase events nominally have no iteration number ("work runs once").
    // Aligning them with audit iteration 1 keeps audit-progress attempt lookups
    // paired with the work that produced the first audit input.
    public const int WorkPhase = 1;
}
