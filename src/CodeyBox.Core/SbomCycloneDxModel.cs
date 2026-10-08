namespace CodeyBox.Core;

/// <summary>
/// Language/ecosystem-neutral CycloneDX SBOM model. Core carries no .NET,
/// MSBuild, NuGet, npm, Maven, or Unity-specific fields: toolchain-specific
/// parsing and configuration live in adapters/plugins. Package URLs
/// (purls), bom-refs, and ecosystem qualifiers are preserved verbatim;
/// distinct components are never collapsed by name.
/// </summary>
public sealed record SbomComponentIdentity
{
    /// <summary>Document-local reference (<c>bom-ref</c>). May be absent on some producer output.</summary>
    public string? BomRef { get; init; }

    /// <summary>Package URL (<c>purl</c>), verbatim including qualifiers. May be absent on some producer output.</summary>
    public string? Purl { get; init; }

    /// <summary>CycloneDX component type (for example <c>library</c>, <c>framework</c>, <c>application</c>).</summary>
    public string Type { get; init; } = "library";

    /// <summary>Component group/namespace, when the producer supplies one.</summary>
    public string? Group { get; init; }

    /// <summary>Component name.</summary>
    public required string Name { get; init; }

    /// <summary>Component version, when the producer supplies one.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Stable identity key: the full purl string when present (qualifiers included —
    /// different qualifiers are different components), otherwise the bom-ref,
    /// otherwise type/group/name/version. Exact match only, never substring.
    /// </summary>
    public string IdentityKey =>
        !string.IsNullOrWhiteSpace(Purl) ? "purl:" + Purl.Trim()
        : !string.IsNullOrWhiteSpace(BomRef) ? "ref:" + BomRef.Trim()
        : $"coord:{Type.Trim().ToLowerInvariant()}/{Group?.Trim() ?? string.Empty}/{Name.Trim()}/{Version?.Trim() ?? string.Empty}";
}

/// <summary>One SBOM component with its identity and content digests.</summary>
public sealed record SbomComponent
{
    /// <summary>Component identity (identifiers preserved verbatim).</summary>
    public required SbomComponentIdentity Identity { get; init; }

    /// <summary>Content hashes as algorithm-to-hex maps (for example <c>SHA-256</c>).</summary>
    public IReadOnlyDictionary<string, string> Hashes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>License expressions/ids reported by the producer, verbatim.</summary>
    public IReadOnlyList<string> Licenses { get; init; } = [];

    /// <summary>Producer-reported scope (for example <c>required</c>, <c>optional</c>).</summary>
    public string? Scope { get; init; }

    /// <summary>Publisher reported by the producer, verbatim.</summary>
    public string? Publisher { get; init; }
}

/// <summary>One dependency edge: <c>Ref</c> depends on each entry of <c>DependsOn</c>.</summary>
public sealed record SbomDependency
{
    /// <summary>Source bom-ref.</summary>
    public required string Ref { get; init; }

    /// <summary>Target bom-refs.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>Producer/tool provenance retained from the SBOM document.</summary>
public sealed record SbomProducerInfo
{
    /// <summary>Producer name (for example <c>cdxgen</c>, <c>cyclonedx-npm</c>, <c>syft</c>).</summary>
    public string Producer { get; init; } = string.Empty;

    /// <summary>Tool name when distinguishable from the producer.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Tool version reported by the document (never trusted for policy).</summary>
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>Document serial number, when present.</summary>
    public string? SerialNumber { get; init; }
}

/// <summary>Neutral SBOM document: identifiers, graph, spec version, and provenance.</summary>
public sealed record SbomDocument
{
    /// <summary>CycloneDX spec version (for example <c>1.5</c>).</summary>
    public required string SpecVersion { get; init; }

    /// <summary>Document format (<c>json</c> or <c>xml</c>).</summary>
    public required string Format { get; init; }

    /// <summary>Producer/tool provenance.</summary>
    public SbomProducerInfo Producer { get; init; } = new();

    /// <summary>Components in document order.</summary>
    public IReadOnlyList<SbomComponent> Components { get; init; } = [];

    /// <summary>Dependency graph edges in document order.</summary>
    public IReadOnlyList<SbomDependency> Dependencies { get; init; } = [];

    /// <summary>SHA-256 hex of the exact imported bytes.</summary>
    public required string ContentDigest { get; init; }

    /// <summary>True when the producer declares the inventory complete; false/unknown means partial coverage.</summary>
    public bool InventoryComplete { get; init; } = true;
}

/// <summary>Exact candidate binding required before audit/gate consumption.</summary>
public sealed record SbomCandidateBinding
{
    /// <summary>Owning project id.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Resolved immutable source SHA the candidate was produced from.</summary>
    public required string ResolvedSha { get; init; }

    /// <summary>Config digest frozen at audit time.</summary>
    public required string ConfigDigest { get; init; }

    /// <summary>SHA-256 hex of the exact candidate SBOM bytes.</summary>
    public required string ContentDigest { get; init; }
}

/// <summary>Operator-approved immutable baseline owned by the same project/configuration.</summary>
public sealed record SbomBaseline
{
    /// <summary>Owning project id.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Config digest the baseline was approved under.</summary>
    public required string ConfigDigest { get; init; }

    /// <summary>SHA-256 hex of the exact baseline SBOM bytes.</summary>
    public required string ContentDigest { get; init; }

    /// <summary>Baseline document snapshot.</summary>
    public required SbomDocument Document { get; init; }

    /// <summary>Operator identity that approved the baseline.</summary>
    public string ApprovedBy { get; init; } = string.Empty;
}

/// <summary>One baseline-relative change.</summary>
public sealed record SbomChange
{
    /// <summary>Change kind.</summary>
    public required SbomChangeKind Kind { get; init; }

    /// <summary>Stable identity key of the affected component.</summary>
    public required string IdentityKey { get; init; }

    /// <summary>Human-actionable detail (includes purl/bom-ref and from→to versions).</summary>
    public required string Detail { get; init; }

    /// <summary>Stable finding id (<c>sbom-…</c>).</summary>
    public required string FindingId { get; init; }
}

/// <summary>Baseline-relative change kinds.</summary>
public enum SbomChangeKind
{
    Unknown = 0,
    Added = 1,
    Removed = 2,
    VersionChanged = 3,
    RelationshipChanged = 4,
}

/// <summary>Baseline-relative diff: additions, removals, version and relationship changes.</summary>
public sealed record SbomDiff
{
    /// <summary>All changes in stable order (kind, then identity key).</summary>
    public IReadOnlyList<SbomChange> Changes { get; init; } = [];

    /// <summary>True when the diff is empty.</summary>
    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>Explicit policy semantics for baseline comparison.</summary>
public enum SbomPolicyMode
{
    /// <summary>Any addition, removal, version, or relationship change fails.</summary>
    FailOnAnyChange = 0,
    /// <summary>Only additions and version changes fail; removals and pure relationship changes are advisory.</summary>
    FailOnAddedOrVersionChanged = 1,
    /// <summary>All changes are advisory; the audit passes with informational findings.</summary>
    AdvisoryOnly = 2,
}

/// <summary>One validation rejection with a machine-readable code.</summary>
public sealed record SbomValidationIssue
{
    /// <summary>Stable code (for example <c>sbom.malformed</c>).</summary>
    public required string Code { get; init; }

    /// <summary>Human-actionable message.</summary>
    public required string Message { get; init; }
}

/// <summary>Import + validation outcome. Success carries the document; failure carries issues.</summary>
public sealed record SbomImportResult
{
    /// <summary>True when the document imported and validated.</summary>
    public bool Ok { get; init; }

    /// <summary>Imported document (null on failure).</summary>
    public SbomDocument? Document { get; init; }

    /// <summary>Validation issues (empty on success).</summary>
    public IReadOnlyList<SbomValidationIssue> Issues { get; init; } = [];

    /// <summary>Success result.</summary>
    public static SbomImportResult Success(SbomDocument document) => new() { Ok = true, Document = document };

    /// <summary>Failure result.</summary>
    public static SbomImportResult Failure(params SbomValidationIssue[] issues) =>
        new() { Ok = false, Issues = issues };

    /// <summary>Failure result.</summary>
    public static SbomImportResult Failure(IReadOnlyList<SbomValidationIssue> issues) =>
        new() { Ok = false, Issues = issues };
}
