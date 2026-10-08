using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Core;

/// <summary>
/// Producer-neutral baseline comparison, explicit policy semantics, exact
/// candidate binding, redaction, and bounded evidence retention for CycloneDX
/// SBOM evidence. Pure core: no filesystem, network, or process access.
/// A successful validation never means "no vulnerabilities" — it means the
/// inventory was well-formed; vulnerability scanning is a separate concern.
/// </summary>
public static class SbomCycloneDxDiff
{
    /// <summary>
    /// Compares a candidate against an operator-approved immutable baseline.
    /// Identity is the full purl (qualifiers included) or bom-ref; components
    /// are never collapsed by name. Version changes are detected across purls
    /// that share type/namespace/name/qualifiers but differ in version.
    /// </summary>
    public static SbomDiff Compare(SbomDocument baseline, SbomDocument candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var baseByKey = baseline.Components.ToDictionary(c => c.Identity.IdentityKey, StringComparer.Ordinal);
        var candidateByKey = candidate.Components.ToDictionary(c => c.Identity.IdentityKey, StringComparer.Ordinal);
        var changes = new List<SbomChange>();

        foreach (var component in candidate.Components)
        {
            ct.ThrowIfCancellationRequested();
            var key = component.Identity.IdentityKey;
            if (baseByKey.TryGetValue(key, out var before))
            {
                if (!VersionsEqual(before.Identity.Version, component.Identity.Version))
                {
                    changes.Add(Change(SbomChangeKind.VersionChanged, key,
                        $"Version changed for '{Display(component)}': '{Safe(before.Identity.Version) ?? "(none)"}' → '{Safe(component.Identity.Version) ?? "(none)"}'."));
                }
            }
            else if (TryFindVersionPredecessor(component, baseByKey, out var predecessor))
            {
                changes.Add(Change(SbomChangeKind.VersionChanged, key,
                    $"Version changed for '{DisplayName(component)}': '{Safe(predecessor!.Identity.Version) ?? "(none)"}' → '{Safe(component.Identity.Version) ?? "(none)"}' (identity '{SbomCycloneDxImport.SanitizeToken(key)}'; was '{SbomCycloneDxImport.SanitizeToken(predecessor.Identity.IdentityKey)}')."));
            }
            else
            {
                changes.Add(Change(SbomChangeKind.Added, key,
                    $"Component added: '{Display(component)}'."));
            }
        }

        var candidateKeys = new HashSet<string>(candidateByKey.Keys, StringComparer.Ordinal);
        foreach (var component in baseline.Components)
        {
            ct.ThrowIfCancellationRequested();
            var key = component.Identity.IdentityKey;
            if (candidateKeys.Contains(key))
                continue;
            if (HasVersionSuccessor(component, candidateByKey))
                continue;
            changes.Add(Change(SbomChangeKind.Removed, key,
                $"Component removed: '{Display(component)}'."));
        }

        foreach (var edgeChange in CompareRelationships(baseline, candidate, ct))
            changes.Add(edgeChange);

        return new SbomDiff
        {
            Changes = changes
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.IdentityKey, StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>
    /// Verifies exact candidate binding: the candidate must belong to the same
    /// project/configuration as the baseline and its content digest must match
    /// the claimed bytes. Returns an error string, or null when bound.
    /// </summary>
    public static string? VerifyBinding(SbomBaseline baseline, SbomCandidateBinding candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(baseline.ProjectId, candidate.ProjectId, StringComparison.Ordinal))
            return $"Baseline project '{SbomCycloneDxImport.SanitizeToken(baseline.ProjectId)}' does not own candidate project '{SbomCycloneDxImport.SanitizeToken(candidate.ProjectId)}'.";
        if (!string.Equals(baseline.ConfigDigest, candidate.ConfigDigest, StringComparison.Ordinal))
            return "Baseline config digest does not match the candidate configuration; re-approval under the current configuration is required.";
        if (!string.Equals(baseline.Document.ContentDigest, baseline.ContentDigest, StringComparison.Ordinal))
            return "Baseline content digest does not match the baseline document bytes.";
        return null;
    }

    /// <summary>
    /// Applies explicit policy semantics. Missing baselines, incomplete coverage,
    /// and unavailable evidence never pass: they yield an infrastructure-style
    /// result the caller must surface as unavailable, never as success.
    /// </summary>
    public static SbomPolicyVerdict Evaluate(
        SbomDiff diff,
        SbomPolicyMode mode,
        bool inventoryComplete)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (!inventoryComplete)
        {
            return new SbomPolicyVerdict(
                false,
                [Finding("sbom-partial-inventory", AuditSeverity.Error,
                    "SBOM inventory incomplete",
                    "The candidate inventory is partial (missing identifiers or truncated evidence). A partial inventory never passes; complete the evidence and re-run.")],
                true);
        }
        var findings = new List<AuditFinding>();
        foreach (var change in diff.Changes)
        {
            var blocking = change.Kind switch
            {
                SbomChangeKind.Added => mode is SbomPolicyMode.FailOnAnyChange or SbomPolicyMode.FailOnAddedOrVersionChanged,
                SbomChangeKind.VersionChanged => mode is SbomPolicyMode.FailOnAnyChange or SbomPolicyMode.FailOnAddedOrVersionChanged,
                SbomChangeKind.Removed => mode == SbomPolicyMode.FailOnAnyChange,
                SbomChangeKind.RelationshipChanged => mode == SbomPolicyMode.FailOnAnyChange,
                _ => true,
            };
            findings.Add(Finding(
                change.FindingId,
                blocking ? AuditSeverity.Error : AuditSeverity.Info,
                SbomTitle(change),
                change.Detail + (blocking ? " Update the approved baseline through the explicit operator-owned promotion step, or remove the unexpected component." : " Advisory under the current policy mode.")));
        }
        var passed = mode == SbomPolicyMode.AdvisoryOnly || findings.All(f => f.Severity != AuditSeverity.Error);
        return new SbomPolicyVerdict(passed, findings, false);
    }

    /// <summary>Serializes a diff to stable machine-readable JSON for artifact retention.</summary>
    public static string SerializeDiff(SbomDiff diff, SbomDocument candidate, SbomBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(candidate);
        var payload = new
        {
            candidate.SpecVersion,
            candidate.Format,
            candidate.ContentDigest,
            candidate.Producer.Producer,
            candidate.Producer.ToolName,
            candidate.Producer.ToolVersion,
            baselineId = baseline?.ContentDigest,
            changes = diff.Changes.Select(c => new
            {
                kind = c.Kind.ToString(),
                identity = c.IdentityKey,
                findingId = c.FindingId,
                detail = c.Detail,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Redacts secret-looking values from SBOM-derived text (logs, excerpts).
    /// SBOM documents are machine evidence: bearer tokens and secret-looking
    /// assignments are never logged verbatim. Residual control characters are
    /// stripped per line so redacted text can never carry raw terminal escapes.
    /// </summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var lines = value.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            if (lower.Contains("bearer ") || lower.Contains("api_key") || lower.Contains("apikey")
                || lower.Contains("secret") || lower.Contains("password") || lower.Contains("token="))
                lines[i] = "[redacted]";
            else
                lines[i] = SbomCycloneDxImport.SanitizeToken(lines[i], int.MaxValue);
        }
        return string.Join('\n', lines);
    }

    /// <summary>Safe artifact names for the bounded artifact store (no repository-controlled segments).</summary>
    public static class ArtifactNames
    {
        /// <summary>Candidate SBOM bytes.</summary>
        public const string Candidate = "sbom-candidate.cdx.json";
        /// <summary>Validation report.</summary>
        public const string Validation = "sbom-validation.json";
        /// <summary>Baseline-relative diff.</summary>
        public const string Diff = "sbom-diff.json";

        /// <summary>True when <paramref name="name"/> is one of the known SBOM artifact names (exact match).</summary>
        public static bool IsKnown(string name) =>
            string.Equals(name, Candidate, StringComparison.Ordinal)
            || string.Equals(name, Validation, StringComparison.Ordinal)
            || string.Equals(name, Diff, StringComparison.Ordinal);
    }

    private static IReadOnlyList<SbomChange> CompareRelationships(SbomDocument baseline, SbomDocument candidate, CancellationToken ct)
    {
        var baseEdges = EdgeMap(baseline.Dependencies);
        var candidateEdges = EdgeMap(candidate.Dependencies);
        var refs = new HashSet<string>(baseEdges.Keys, StringComparer.Ordinal);
        refs.UnionWith(candidateEdges.Keys);
        var changes = new List<SbomChange>();
        foreach (var depRef in refs.OrderBy(r => r, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            baseEdges.TryGetValue(depRef, out var before);
            candidateEdges.TryGetValue(depRef, out var after);
            before ??= new HashSet<string>(StringComparer.Ordinal);
            after ??= new HashSet<string>(StringComparer.Ordinal);
            if (before.SetEquals(after))
                continue;
            var added = after.Except(before, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var removed = before.Except(after, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var detail = new StringBuilder($"Dependency relationships changed for '{SbomCycloneDxImport.SanitizeToken(depRef)}':");
            if (added.Count > 0)
                detail.Append($" added [{string.Join(", ", added.Select(SbomCycloneDxImport.SanitizeToken))}];");
            if (removed.Count > 0)
                detail.Append($" removed [{string.Join(", ", removed.Select(SbomCycloneDxImport.SanitizeToken))}];");
            changes.Add(Change(SbomChangeKind.RelationshipChanged, depRef, detail.ToString().Trim()));
        }
        return changes;
    }

    private static Dictionary<string, HashSet<string>> EdgeMap(IReadOnlyList<SbomDependency> edges)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!map.TryGetValue(edge.Ref, out var set))
                map[edge.Ref] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in edge.DependsOn)
                set.Add(target);
        }
        return map;
    }

    private static bool TryFindVersionPredecessor(
        SbomComponent component,
        Dictionary<string, SbomComponent> baseByKey,
        out SbomComponent? predecessor)
    {
        predecessor = null;
        if (!TrySplitPurl(component.Identity.Purl, out var type, out var name, out _, out var qualifiers))
            return false;
        foreach (var before in baseByKey.Values)
        {
            if (!TrySplitPurl(before.Identity.Purl, out var beforeType, out var beforeName, out _, out var beforeQualifiers))
                continue;
            if (string.Equals(type, beforeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, beforeName, StringComparison.Ordinal)
                && string.Equals(qualifiers, beforeQualifiers, StringComparison.Ordinal)
                && !VersionsEqual(before.Identity.Version, component.Identity.Version))
            {
                predecessor = before;
                return true;
            }
        }
        return false;
    }

    private static bool HasVersionSuccessor(SbomComponent baseline, Dictionary<string, SbomComponent> candidateByKey)
    {
        if (!TrySplitPurl(baseline.Identity.Purl, out var type, out var name, out _, out var qualifiers))
            return false;
        foreach (var after in candidateByKey.Values)
        {
            if (!TrySplitPurl(after.Identity.Purl, out var afterType, out var afterName, out _, out var afterQualifiers))
                continue;
            if (string.Equals(type, afterType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, afterName, StringComparison.Ordinal)
                && string.Equals(qualifiers, afterQualifiers, StringComparison.Ordinal)
                && !VersionsEqual(baseline.Identity.Version, after.Identity.Version))
                return true;
        }
        return false;
    }

    private static bool VersionsEqual(string? left, string? right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static string Display(SbomComponent component) =>
        // WHY: name/version/group/type are repository-controlled; display copies
        // are sanitized (controls stripped, capped) so change detail and finding
        // descriptions can never carry raw terminal/log control sequences.
        // IdentityKey itself stays exact — StableFindingId hashes it.
        !string.IsNullOrWhiteSpace(component.Identity.Purl)
            ? SbomCycloneDxImport.SanitizeToken(component.Identity.Purl)
            : string.IsNullOrWhiteSpace(component.Identity.Version)
                ? $"{SbomCycloneDxImport.SanitizeToken(component.Identity.Name)} (ref '{SbomCycloneDxImport.SanitizeToken(component.Identity.BomRef)}')"
                : $"{SbomCycloneDxImport.SanitizeToken(component.Identity.Name)}@{SbomCycloneDxImport.SanitizeToken(component.Identity.Version)} (ref '{SbomCycloneDxImport.SanitizeToken(component.Identity.BomRef)}')";

    private static string DisplayName(SbomComponent component) =>
        !string.IsNullOrWhiteSpace(component.Identity.Purl) && TrySplitPurl(component.Identity.Purl, out _, out var name, out _, out _)
            ? "pkg:" + SbomCycloneDxImport.SanitizeToken(name)
            : SbomCycloneDxImport.SanitizeToken(component.Identity.Name);

    private static string? Safe(string? value) =>
        value is null ? null : SbomCycloneDxImport.SanitizeToken(value);

    /// <summary>
    /// Splits a purl into type, full name (namespace + name), version, and qualifiers.
    /// Returns false when the purl is absent or structurally invalid.
    /// </summary>
    public static bool TrySplitPurl(string? purl, out string type, out string name, out string version, out string qualifiers)
    {
        type = string.Empty;
        name = string.Empty;
        version = string.Empty;
        qualifiers = string.Empty;
        if (!SbomCycloneDxImport.IsValidPurl(purl))
            return false;
        var remainder = purl!.Trim().Substring("pkg:".Length);
        var slash = remainder.IndexOf('/');
        if (slash < 0)
            return false;
        type = remainder[..slash].Trim().ToLowerInvariant();
        var path = remainder[(slash + 1)..];
        var qualifierIndex = path.IndexOf('?');
        if (qualifierIndex >= 0)
        {
            qualifiers = path[(qualifierIndex + 1)..].Split('#', 2)[0];
            path = path[..qualifierIndex];
        }
        else
        {
            var hashIndex = path.IndexOf('#');
            if (hashIndex >= 0)
                path = path[..hashIndex];
        }
        var versionIndex = path.IndexOf('@');
        if (versionIndex >= 0)
        {
            version = path[(versionIndex + 1)..].Trim();
            name = path[..versionIndex].Trim().Trim('/');
        }
        else
        {
            name = path.Trim().Trim('/');
        }
        return name.Length > 0;
    }

    private static SbomChange Change(SbomChangeKind kind, string identityKey, string detail) =>
        new()
        {
            Kind = kind,
            IdentityKey = identityKey,
            Detail = detail,
            FindingId = StableFindingId(kind, identityKey),
        };

    /// <summary>Stable finding identity: <c>sbom-&lt;8 hex&gt;</c> over kind and identity key.</summary>
    public static string StableFindingId(SbomChangeKind kind, string identityKey)
    {
        var input = $"sbom-cyclonedx baseline-diff\0{kind}\0{identityKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "sbom-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string SbomTitle(SbomChange change) => change.Kind switch
    {
        SbomChangeKind.Added => "SBOM component added",
        SbomChangeKind.Removed => "SBOM component removed",
        SbomChangeKind.VersionChanged => "SBOM component version changed",
        SbomChangeKind.RelationshipChanged => "SBOM dependency relationships changed",
        _ => "SBOM baseline change",
    };

    private static AuditFinding Finding(string ruleId, AuditSeverity severity, string title, string description) =>
        new("codeybox:sbom-cyclonedx", severity, title, description, ruleId);
}

/// <summary>Policy evaluation outcome.</summary>
public sealed record SbomPolicyVerdict(
    bool Passed,
    IReadOnlyList<AuditFinding> Findings,
    bool EvidenceInsufficient);
