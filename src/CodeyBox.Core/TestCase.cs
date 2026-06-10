using System;
using System.Collections.Generic;

namespace CodeyBox.Core;

/// <summary>
/// Specifies the type of automation execution used for a test case.
/// </summary>
public enum AutomationKind
{
    Manual,
    Unit,
    Integration,
    E2eReplay
}

/// <summary>
/// A step inside an e2e-replay executable artifact.
/// </summary>
public sealed record E2eReplayStep
{
    public required string Action { get; init; }
    public required string Selector { get; init; }
    public string? Value { get; init; }
}

/// <summary>
/// An assertion inside an e2e-replay executable artifact.
/// </summary>
public sealed record E2eReplayAssertion
{
    public required string Type { get; init; }
    public required string Selector { get; init; }
    public string? ExpectedValue { get; init; }
}

/// <summary>
/// The structured executable artifact payload for E2E replays.
/// </summary>
public sealed record E2eReplayArtifact
{
    public required IReadOnlyList<E2eReplayStep> Steps { get; init; }
    public required IReadOnlyList<E2eReplayAssertion> Assertions { get; init; }
}

/// <summary>
/// The conformance conditions determining whether a test case meets coverage expectations.
/// </summary>
public sealed record ConformanceCondition
{
    public string? BrokenBranch { get; init; }
    public string? ExpectedOutcome { get; init; }
}

/// <summary>
/// A lean, execution-focused test case artifact linked to a work item.
/// </summary>
public sealed record TestCase
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string SourceWorkItemId { get; init; }

    // Audit / archived fields
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsArchived { get; init; }

    // Automation metadata
    public AutomationKind? AutomationKind { get; init; }
    public string? ExecutableArtifactJson { get; init; }
    public string? ConformanceJson { get; init; }

    // Flat capability/area label
    public string? Label { get; init; }

    // Execution results
    public bool? LastRunPassed { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastRunResult { get; init; }
}
