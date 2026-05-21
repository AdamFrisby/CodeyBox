using System;

namespace CodeyBox.Core;

/// <summary>
/// Stable sandbox leak classification reason codes exposed in webhook details,
/// audit events, and sandbox leak API responses.
/// </summary>
public static class SandboxLeakReasons
{
    public const string UntrackedSandbox = "untracked_sandbox_age_threshold_exceeded";
    public const string UntrackedSandboxMissingCreationMetadata = "untracked_sandbox_missing_creation_metadata";
    public const string ExpiredPreemptRetention = "expired_preempt_retention_age_threshold_exceeded";
}

/// <summary>
/// Public status DTO for a sandbox classified as leaked by the active sandbox
/// provider ownership snapshot.
/// </summary>
public sealed record LeakedSandboxInfo(
    string Name,
    DateTimeOffset CreatedAt,
    TimeSpan Age,
    long? DiskBytes,
    string Reason = SandboxLeakReasons.UntrackedSandbox);
