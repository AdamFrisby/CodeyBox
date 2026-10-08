# CycloneDX SBOM Evidence, Validation, and Baseline Comparison

Producer-neutral CycloneDX Software Bill of Materials (SBOM) evidence for
CodeyBox audits (CBX-NEXT-043). One integration family: a language-neutral
Core import/validate/diff path plus an optional, explicitly selected
generator adapter (cdxgen) and an optional trusted validator
(cyclonedx-cli). There is deliberately **no** mandatory cdxgen plugin, no
mandatory CycloneDX CLI plugin, no NuGet/`dotnet restore` assumption, and no
single-language assumption anywhere in the Core path.

## What it is not

- **Not a vulnerability scanner.** Successful validation means the inventory
  was well-formed and matches the approved baseline. It never means the
  components are vulnerability-free. Vulnerability verdicts stay with the
  dedicated dependency-vulnerability auditors (Trivy, Grype, OSV-Scanner,
  Dependency-Check, …).
- **Not a baseline promotion mechanism.** This feature compares against an
  operator-approved immutable baseline; promotion (approving a new baseline)
  is an explicit operator-owned step performed outside the auditor: store the
  approved SBOM bytes and pin their SHA-256 digest in configuration.
- **Not part of the external-build framework or the MSBuild task.** It reuses
  the standalone audit artifact/provenance store and the current
  plugin/evidence interfaces (`IAuditor`, `IAuditRunArtifactStore`).

## Standard import path

`SbomCycloneDxImport.Import` (Core, pure, no I/O) accepts CycloneDX JSON and
XML documents from **any** producer/ecosystem — cdxgen, cyclonedx-cli,
cyclonedx-npm, syft, or any other — through the same code:

- Supported spec versions are allowlisted (`1.4`, `1.5`, `1.6` by default);
  anything else is rejected as `sbom.unsupported-spec`.
- Bounds are enforced **before** buffering: document bytes, component count,
  and dependency-edge count each have hot-reloadable caps. Oversized,
  malformed, or truncated evidence is rejected (`sbom.oversized`,
  `sbom.malformed`, `sbom.missing`) — never silently accepted.
- Component identifiers are preserved verbatim: full package URLs (including
  ecosystem qualifiers), bom-refs, and content digests. Identity is the full
  purl (qualifiers included), else the bom-ref; components are **never**
  collapsed by name. Missing identifiers, invalid purls, invalid digests,
  duplicate identities, and ambiguous component relationships (dangling or
  duplicated dependency refs) are rejected with typed issues.
- Producer/tool version, spec version, serial number, and the content digest
  (SHA-256 of the exact bytes) are retained as provenance. Referenced URLs
  are never fetched.

## Validation

Validation is the import path above (the built-in bounded validator), plus an
optional `cyclonedx validate` run when the operator explicitly selects
`ValidatorTool=cyclonedx` with a pinned `ValidatorExpectedVersion`. Both run
bounded inside the audit sandbox with cancellation. Rejected evidence fails
the audit with actionable findings; it never passes.

## Baseline comparison

The `codeybox:sbom-cyclonedx` auditor compares the candidate against the
operator-approved immutable baseline owned by the **same project and
configuration**:

- The baseline is accepted only when its bytes match the operator-pinned
  `BaselineDigest` (SHA-256, exact equality) and, when pinned,
  `BaselineProjectId` / `BaselineConfigDigest`. Repository-controlled
  baseline choice and embedded producer assertions are never trusted without
  this verification.
- The diff defines additions, removals, version changes (same purl
  type/namespace/name/qualifiers, different version), and relationship
  changes (dependency edges added/removed per ref). Every change carries a
  stable finding id (`sbom-…` over change kind + identity key) and an
  actionable detail naming the purl/bom-ref and the from→to versions.
- Explicit policy semantics (`PolicyMode`): `FailOnAnyChange` (default),
  `FailOnAddedOrVersionChanged`, `AdvisoryOnly`.
- Absent baseline, incomplete coverage, and unavailable generation/import
  **never pass**: they fail with explicit findings or report unavailable
  (`AuditUnavailableException`), so a missing gate can never masquerade as a
  clean inventory.

## Generation (optional, default off)

When no existing producer evidence is found, the auditor reuses it first;
generation is a last resort and only when the operator explicitly selects
`Generator=cdxgen` (exact match) with project capabilities in
`EcosystemTags` (e.g. `npm,nuget,maven,native`) and a pinned
`GeneratorExpectedVersion`. The cdxgen adapter invokes the `cdxgen` binary
with a structured argv array inside the sandbox — never a shell string, never
`dotnet restore` — so non-.NET ecosystems generate without any .NET
toolchain. Generation side effects stay in the sandbox under existing
policies.

## Evidence retention

Candidate bytes, the validation report, and the diff report are retained as
machine-readable artifacts (`sbom-candidate.cdx.json`, `sbom-validation.json`,
`sbom-diff.json`) through the existing bounded artifact store with
source/configuration provenance, project access checks, redaction, retention,
and safe allowlisted paths. Exact candidate binding (project, source SHA,
config digest, content digest) is verified before audit/gate consumption.

## Configuration (all default OFF)

```jsonc
// CodeyBox:Plugins:codeybox.sbom-cyclonedx
{
  "Enabled": false,               // master switch (default false)
  "PolicyMode": "FailOnAnyChange",// FailOnAnyChange | FailOnAddedOrVersionChanged | AdvisoryOnly
  "CandidatePath": "",            // explicit worktree-relative candidate; empty = auto-discover bom.json, sbom.json, cyclonedx.json, sbom.cdx.json, .cyclonedx/bom.json, bom.xml, sbom.xml
  "BaselinePath": "",             // operator-owned approved-baseline path (worktree-relative)
  "BaselineDigest": "",           // SHA-256 pin of the exact approved baseline bytes (required with BaselinePath)
  "BaselineProjectId": "",        // project the baseline was approved under (checked when set)
  "BaselineConfigDigest": "",     // config digest the baseline was approved under (checked when set)
  "Generator": "",                // explicitly selected generator: "" (none) or "cdxgen"
  "GeneratorExpectedVersion": "", // pinned cdxgen version (default 11.0.0 when selected)
  "EcosystemTags": "",            // project capabilities, e.g. "npm,nuget,maven,native" (required for generation)
  "ValidatorTool": "",            // "" (built-in only) or "cyclonedx"
  "ValidatorExpectedVersion": "", // pinned cyclonedx-cli version (default 0.24.2 when selected)
  "MaxSbomBytes": 5242880,
  "MaxComponents": 20000,
  "MaxDependencies": 60000,
  "SupportedSpecVersions": "1.4,1.5,1.6",
  "OperationTimeoutSeconds": 120
}
```

Enable the plugin with `Plugins:Enabled: ["codeybox.sbom-cyclonedx"]` and
`Enabled=true` only after approving a baseline. Official references:
<https://github.com/CycloneDX/cyclonedx-cli>,
<https://github.com/cdxgen/cdxgen>.
