# Artifact provenance

CodeyBox can verify executable artifacts **before** they run: plugin bundles
before load, and externally staged tool executables before baseline
provisioning. The policy is operator-owned, opt-in, and disabled by default —
existing installations keep existing behavior until the operator enables it.

Config section: `CodeyBox:ArtifactTrust` (see
[`../reference/configuration.md`](../reference/configuration.md)). Validation
fails fast at host start; values are hot-reloadable and cached verdicts are
keyed by the live policy digest, so a policy edit never reuses a stale result.

## Trust model

- Each entry pins an **immutable content digest** (`sha256:` hex) plus the
  **expected publisher identity and issuer**, and optionally exact
  repository / workflow / source-ref / predicate constraints.
- A valid signature from any other identity is **insufficient**: verification
  uses only key material or flags derived from the policy entry, never from
  artifact-supplied metadata. Artifact sidecars may carry descriptive
  provenance, but they never define the trusted publisher or override policy.
- Verification runs through the official verifier binaries — no reimplemented
  cryptography, no disabled checks, no identity patterns, no trust in mutable
  release labels alone:
  - `cosign verify-blob` ([contract](https://docs.sigstore.dev/cosign/verifying/verify/))
  - `gh attestation verify` ([contract](https://cli.github.com/manual/gh_attestation_verify))
- Under enforcement, missing/incompatible evidence or an unavailable verifier
  produces a **visible blocked outcome** for the affected artifact — it never
  loads silently.
- Signed OS-package-manager installs (`apt`) remain their own explicit
  mechanism (`os-package`): they are never presented as Sigstore verification,
  and they never admit executables through this gate. Evidence availability
  differs by publisher — the policy must only claim evidence the publisher
  actually provides.

## Supported artifact formats and evidence types

| `Evidence` value       | Verifier command | Sidecars (next to the source) | Network |
|------------------------|------------------|-------------------------------|---------|
| `openssl-local`        | platform ECDSA over the provenance statement | `<file>.provenance.json` + `<file>.provenance.sig` (created with `openssl dgst -sha256 -sign`) | never |
| `cosign-local-key`     | `cosign verify-blob --key <pinned-key> --bundle <bundle> -- <file>` | `<file>.provenance.json` + `<file>.sigstore.json` (created with `cosign sign-blob --key <key> --bundle <bundle> --yes <file>`); the pinned key goes in policy `PublicKeyPem`, policy `Issuer` is `local-key`, policy `Publisher` is `key:<fingerprint>` | never |
| `sigstore-bundle`      | `cosign verify-blob --bundle <bundle> --certificate-identity <publisher> --certificate-issuer <issuer> -- <file>` | `<file>.provenance.json` + `<file>.sigstore.json` (Fulcio-issued bundle) | bundle fetch only as configured |
| `github-attestation`   | `gh attestation verify --bundle <local-bundle> --repo <repo> --signer-workflow <workflow> --predicate-type <type> --cert-identity <publisher> --cert-oidc-issuer <issuer> --source-ref <ref> --format json -- <file>` | local bundle `<file>.attestation.json` where the publisher provides one, otherwise controlled online discovery when `AllowNetworkDiscovery` is set | only inside the `gh` process, only when enabled |
| `os-package`           | n/a (apt trust)  | n/a — never admits executables | n/a |

Constraint support differs by evidence kind. `cosign verify-blob`
(`sigstore-bundle` and `cosign-local-key`) attests only the publisher/issuer
(plus the subject digest, re-checked locally): a policy entry using either
kind must not set `Repository`/`Workflow`/`SourceRef`/`PredicateType` —
verification fails closed when any of them is set, because the
artifact-supplied sidecar can never satisfy them. Use `openssl-local`
(statement bytes are the signed object) or `github-attestation` (every
constraint travels as a `gh` flag and the verdict is parsed from `gh`'s
verified output) for repository/workflow/source-ref/predicate constraints.

The provenance statement is a small JSON document:

```json
{
  "digest": "<sha256 hex of the exact artifact bytes>",
  "publisher": "<descriptive publisher label>",
  "issuer": "<descriptive issuer label>",
  "repository": "https://example.invalid/org/repo",
  "workflow": "release.yml",
  "sourceRef": "refs/tags/v1.2.3",
  "predicateType": "https://slsa.dev/provenance/v1"
}
```

Publisher/issuer in the statement are descriptive; the enforced identity
comes from the policy entry (or, for `cosign-local-key`, from the pinned key
itself).

## Enforcement points

1. **Plugin load** — after the existing enablement/allowlist/version gates,
   the bundle (primary assembly plus co-located executable dependencies) is
   staged to a private directory and admitted. Only the staged copy loads;
   a refusal marks every candidate `ProvenanceBlocked` and nothing executes.
2. **Tool provisioning** (Incus, Multipass, OpenStack) — each
   `ExecutableProvisions` source is admitted before fingerprinting/naming,
   and only the admitted staged copy is transferred or consumed. The staged
   bytes are re-hashed at consumption; substitution fails closed. Verified
   identities join the baseline cache fingerprint, so trusted and untrusted
   bakes never share a cache entry.

Verification credentials (request tokens, key material) stay in the
orchestrator process and are never passed to plugin or tool processes.
Downloads use controlled endpoints with size limits, safe extraction, and
cancellation.

## Enabling

```json
{
  "CodeyBox": {
    "ArtifactTrust": {
      "Enabled": true,
      "VerificationTimeoutSeconds": 60,
      "TrustedArtifacts": [
        {
          "ArtifactId": "agent-gateway",
          "Sha256": "<sha256 of the exact executable bytes>",
          "Publisher": "key:<fingerprint of PublicKeyPem>",
          "Issuer": "local-key",
          "Evidence": "cosign-local-key",
          "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n"
        }
      ]
    }
  }
}
```

Compute the key fingerprint with the same function the host uses
(`ArtifactTrustOptions.FingerprintPublicKey`); policy validation rejects
entries whose publisher/issuer do not match the pinned key. Start with one
artifact, confirm admission in the baseline report / plugin statuses, then
extend. Disabling the section restores previous behavior immediately.
