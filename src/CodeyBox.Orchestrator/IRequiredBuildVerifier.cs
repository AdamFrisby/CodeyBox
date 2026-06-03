using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hard build gate used before work completion and audit pass. Implementations
/// decide whether a branch needs the gate, materialise the branch into an
/// isolated execution environment, and run the required build command.
/// Lives in the orchestrator layer because the contract carries sandbox
/// policy fields (network profile, baseline image) that are infrastructure
/// concerns; Core must not depend on those.
/// </summary>
public interface IRequiredBuildVerifier
{
    Task<RequiredBuildProbeResult> ProbeAsync(RequiredBuildProbeRequest request, CancellationToken ct);

    Task<RequiredBuildVerificationResult> VerifyAsync(RequiredBuildVerificationRequest request, CancellationToken ct);
}

public static class RequiredBuildGateIdentity
{
    public const string AuditorName = "process:required-build";
    public const string DisplayCommand = "dotnet build";
}

public sealed record RequiredBuildProbeRequest
{
    public required WorkItemId WorkItemId { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string RepositoryId { get; init; }
    public string? BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
}

public enum RequiredBuildProbeStatus
{
    NotApplicable = 0,
    Applies = 1,
    Unavailable = 2,
}

public sealed record RequiredBuildProbeResult(
    RequiredBuildProbeStatus Status,
    string? Reason = null)
{
    public static RequiredBuildProbeResult NotApplicable { get; } =
        new(RequiredBuildProbeStatus.NotApplicable);

    public static RequiredBuildProbeResult Applies { get; } =
        new(RequiredBuildProbeStatus.Applies);

    public static RequiredBuildProbeResult Unavailable(string reason) =>
        new(RequiredBuildProbeStatus.Unavailable, reason);
}

public sealed record RequiredBuildVerificationRequest
{
    public required WorkItemId WorkItemId { get; init; }
    /// <summary>Project the work item belongs to; available for logging only.</summary>
    public required ProjectId ProjectId { get; init; }
    public required string RepositoryId { get; init; }
    public string? BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
    public required string Phase { get; init; }
    public int? Iteration { get; init; }
    /// <summary>
    /// Pre-resolved sandbox/build policy. The orchestrator resolves
    /// project-aware fields (audit-tool network profile, baseline image)
    /// before crossing this boundary so the verifier contract does not
    /// expose the full <see cref="Project"/> aggregate to implementations.
    /// </summary>
    public required RequiredBuildSandboxPolicy SandboxPolicy { get; init; }
}

/// <summary>
/// Minimum sandbox/build inputs a required-build verifier needs from the
/// orchestrator. Resolved once by the orchestrator from the work item /
/// project so verifier implementations do not pull in unrelated project
/// configuration.
/// </summary>
public sealed record RequiredBuildSandboxPolicy
{
    /// <summary>Network profile to apply to the audit-tool sandbox, or null for the default profile.</summary>
    public string? NetworkProfile { get; init; }
    /// <summary>Pre-resolved baseline image ref, or null if not applicable.</summary>
    public string? BaselineImageRef { get; init; }
}

public enum RequiredBuildVerificationStatus
{
    Skipped = 0,
    Passed = 1,
    Failed = 2,
    Unavailable = 3,
}

public sealed record RequiredBuildVerificationResult(
    RequiredBuildVerificationStatus Status,
    int ExitCode,
    string Output,
    string? Reason = null)
{
    public static RequiredBuildVerificationResult Skipped { get; } =
        new(RequiredBuildVerificationStatus.Skipped, 0, string.Empty);

    public static RequiredBuildVerificationResult Passed(int exitCode, string output) =>
        new(RequiredBuildVerificationStatus.Passed, exitCode, output);

    public static RequiredBuildVerificationResult Failed(int exitCode, string output) =>
        new(RequiredBuildVerificationStatus.Failed, exitCode, output);

    public static RequiredBuildVerificationResult Unavailable(string reason, int exitCode = 0, string output = "") =>
        new(RequiredBuildVerificationStatus.Unavailable, exitCode, output, reason);
}
