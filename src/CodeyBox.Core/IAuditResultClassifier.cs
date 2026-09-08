namespace CodeyBox.Core;

/// <summary>
/// Refines a non-zero command exit into a richer <see cref="AuditResult"/> when
/// the raw exit code alone is uninformative (e.g. a <c>dotnet test</c> run that
/// exits 1 but whose stdout distinguishes genuine test failures from an
/// unrunnable environment). Returning <c>null</c> means "no refinement — use the
/// generic command-failure result".
///
/// A classifier may instead throw <see cref="AuditUnavailableException"/> when
/// the output proves the runner never executed against the code (refused
/// arguments, missing target, missing tool). That is an infrastructure fault:
/// it must surface as infrastructure, never as a code finding, so the pipeline
/// routes it to operator attention without consuming rework iterations.
///
/// This abstraction lives in Core (not in the shell auditor assembly) so that
/// first-class auditor types such as <c>ITestRunnerAuditor</c> can declare their
/// classifier as a member without depending on the shell runner.
/// </summary>
public interface IAuditResultClassifier
{
    /// <exception cref="AuditUnavailableException">
    /// Thrown when the command output proves the check could not run at all
    /// (runner-invocation or environment fault). Callers must let this
    /// propagate to the pipeline's infrastructure path rather than converting
    /// it into findings.
    /// </exception>
    AuditResult? ClassifyFailedCommand(AuditResultClassificationContext context);
}

/// <summary>
/// Inputs a <see cref="IAuditResultClassifier"/> inspects to decide whether a
/// failed command should produce a refined result.
/// </summary>
public sealed record AuditResultClassificationContext(
    string AuditorName,
    IReadOnlyList<string> Argv,
    SandboxExecResult Result,
    string CombinedOutput,
    AuditFinding CommandFinding);
