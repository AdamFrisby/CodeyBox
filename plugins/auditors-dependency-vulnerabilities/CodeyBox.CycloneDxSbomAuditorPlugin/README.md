# CodeyBox: CycloneDX SBOM Baseline Comparison Auditor

Producer-neutral CycloneDX SBOM auditor (`codeybox:sbom-cyclonedx`). Imports
existing CycloneDX evidence from any producer/ecosystem (cdxgen,
cyclonedx-cli, cyclonedx-npm, syft — NuGet, npm, Maven, native) through one
shared Core import/validate/diff path, compares the candidate against an
operator-approved immutable baseline, and reports additions, removals, and
version and relationship changes under explicit policy semantics.

**Not a vulnerability scanner.** A pass means the inventory is well-formed
and matches the approved baseline — never that the components are
vulnerability-free.

## Status

Default OFF. The plugin ships no enabled configuration: add
`codeybox.sbom-cyclonedx` to `Plugins:Enabled` and set
`CodeyBox:Plugins:codeybox.sbom-cyclonedx:Enabled=true` with an approved
baseline (`BaselinePath` + `BaselineDigest` pin) to use it. See
[`docs/concepts/sbom-cyclonedx.md`](../../../docs/concepts/sbom-cyclonedx.md)
for the full contract.

## What it reports

- One finding per rejected-evidence issue (`sbom.malformed`,
  `sbom.unsupported-spec`, `sbom.duplicate-identity`,
  `sbom.ambiguous-relationship`, …) — all Error, all blocking.
- One finding per baseline-relative change (added / removed / version-changed
  / relationship-changed) with a stable `sbom-…` finding id and an actionable
  detail naming the purl/bom-ref and from→to versions. Blocking follows
  `PolicyMode` (`FailOnAnyChange` default; `FailOnAddedOrVersionChanged`;
  `AdvisoryOnly`).
- An informational "matches the approved baseline" finding on a clean pass,
  explicitly stating validation is not a vulnerability verdict.
- Absent baseline, incomplete coverage, and unavailable generation/import
  never pass: explicit Error findings or `AuditUnavailableException`
  (missing input, missing/unknown tool, version mismatch, timeout).

## Scope and defaults

- **Candidate:** explicit `CandidatePath` first, then well-known producer
  outputs (`bom.json`, `sbom.json`, `cyclonedx.json`, `sbom.cdx.json`,
  `.cyclonedx/bom.json`, `bom.xml`, `sbom.xml`), then optional generation.
  Existing evidence is reused; generation never runs when evidence is found.
- **Baseline:** accepted only when its bytes match the operator-pinned
  `BaselineDigest` (and pinned project/config when set). Promotion is an
  explicit operator-owned step (update the file and its pin); this auditor
  never promotes.
- **Generation (optional):** only when `Generator=cdxgen` (exact) with
  `EcosystemTags` capabilities and a pinned version. Runs `cdxgen` with a
  structured argv array in the sandbox — no shell strings, no
  `dotnet restore`, no .NET toolchain requirement for other ecosystems.
- **Validation (optional):** `ValidatorTool=cyclonedx` adds a bounded
  `cyclonedx validate` run; otherwise the built-in bounded Core validator is
  the only validator.
- No network egress is declared; no referenced URL is ever fetched; secrets
  in evidence text are redacted from logs and reports.
