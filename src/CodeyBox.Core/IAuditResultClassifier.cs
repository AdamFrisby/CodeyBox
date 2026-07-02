namespace CodeyBox.Core;

/// <summary>
/// Refines a non-zero command exit into a richer <see cref="AuditResult"/> when
/// the raw exit code alone is uninformative (e.g. a <c>dotnet test</c> run that
/// exits 1 but whose stdout distinguishes genuine test failures from an
/// unrunnable environment). Returning <c>null</c> means "no refinement — use the
/// generic command-failure result".
///
/// This abstraction lives in Core (not in the shell auditor assembly) so that
/// first-class auditor types such as <c>ITestRunnerAuditor</c> can declare their
/// classifier as a member without depending on the shell runner.
/// </summary>
public interface IAuditResultClassifier
{
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
