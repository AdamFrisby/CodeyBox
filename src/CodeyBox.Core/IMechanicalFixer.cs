namespace CodeyBox.Core;

/// <summary>
/// Deterministic, no-model code normalizer that mutates a work tree in place.
/// Mechanical fixers run after work/rework and before audit so cheap formatting
/// or generated-code normalization does not consume LLM audit/rework cycles.
/// </summary>
public interface IMechanicalFixer
{
    /// <summary>Stable config name for this fixer.</summary>
    string Name { get; }

    /// <summary>Implementation kind for logs and diagnostics.</summary>
    string Kind { get; }

    /// <summary>
    /// Apply the deterministic transform to <paramref name="workingDirectory"/>.
    /// Implementations must not call a model and should be idempotent.
    /// </summary>
    Task<MechanicalFixerResult> ApplyAsync(
        ISandbox sandbox,
        string workingDirectory,
        MechanicalFixerContext context,
        CancellationToken ct = default);
}

/// <summary>Registry of all mechanical fixers registered in DI.</summary>
public interface IMechanicalFixerRegistry
{
    IReadOnlyList<IMechanicalFixer> All { get; }
}

/// <summary>Information passed to a mechanical fixer invocation.</summary>
public sealed record MechanicalFixerContext(
    WorkItemId WorkItemId,
    string WorkBranch,
    string BaseBranch,
    int AuditIteration,
    string ProjectId,
    IReadOnlyList<IAuditor> Auditors);

/// <summary>Result from one mechanical fixer invocation.</summary>
public sealed record MechanicalFixerResult(
    bool Changed,
    string? Summary = null,
    string? RawOutput = null);
