using System;

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
/// A lean, execution-focused test case artifact linked to a work item.
///
/// CodeyBox intentionally does NOT model the management taxonomy JobTrack uses
/// (no SurfaceArea, no parent/path/level hierarchy). <see cref="ExecutableArtifactJson"/>
/// and <see cref="ConformanceJson"/> are persisted as opaque JSON strings; the schema for
/// those payloads is owned by their consumers (the E2E executor, the mutation gate), which
/// land as separate items. See <c>docs/test-cases.md</c> for the schema and the JobTrack mapping.
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
