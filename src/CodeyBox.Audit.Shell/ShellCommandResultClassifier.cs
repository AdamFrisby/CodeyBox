using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

public interface IShellCommandResultClassifier
{
    AuditResult? ClassifyFailedCommand(ShellCommandResultContext context);
}

public sealed record ShellCommandResultContext(
    string AuditorName,
    IReadOnlyList<string> Argv,
    SandboxExecResult Result,
    string CombinedOutput,
    AuditFinding CommandFinding);
