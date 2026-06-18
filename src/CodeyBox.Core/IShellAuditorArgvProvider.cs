namespace CodeyBox.Core;

/// <summary>
/// Optional capability mixin for shell-style auditors that expose the argv
/// they invoke. The work-phase prompt builder uses this to advise the agent
/// to run those exact commands itself before committing, pre-empting iter-1
/// mechanical findings (format, lint, build-WaE).
///
/// Shell auditors should implement this; LLM and diff-pattern auditors don't.
/// Other consumers (admin UI, observability) MAY also use it to display the
/// concrete check.
/// </summary>
public interface IShellAuditorArgvProvider
{
    /// <summary>
    /// Argv to invoke. Null/empty means "no concrete command" — implementations
    /// in this case should not implement the interface at all.
    /// </summary>
    IReadOnlyList<string> Argv { get; }

    /// <summary>
    /// Optional command discovery metadata. Language preset wrappers use this so
    /// mechanical fixers can reuse the same command and project-marker discovery
    /// without receiving the full auditor panel.
    /// </summary>
    ShellAuditorCommandMetadata? CommandMetadata => null;
}

public sealed record ShellAuditorCommandMetadata(
    string? Language = null,
    string? MarkerDescription = null,
    string? MarkerScript = null);

public sealed record ShellAuditorCommandDescriptor(
    string Name,
    IReadOnlyList<string> Argv,
    ShellAuditorCommandMetadata? Metadata = null);
