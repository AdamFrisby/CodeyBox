# CodeyBox: Bitwarden Secrets (`codeybox.bitwarden`)

Lease-shaped credential provider for [Bitwarden Secrets
Manager](https://bitwarden.com/products/secrets-manager/): secret values
from operator-mapped secrets, read with machine-account access tokens
minted via the client-credentials grant. One project, one plugin. **Off
unless an operator enables it.**

## Topology

Machine accounts are the scoping unit and map naturally onto per-project
or per-group credentials: one machine account (or account set) per secret
group, each granted exactly the projects its group needs.

```
workload sandbox ── env (value) ── orchestrator
                                      │  lease handles only
                                      │  (persisted in state DB)
                                      ▼
                               codeybox.bitwarden
                                  │  1. POST {identity}/connect/token
                                  │     (client-credentials, api.secrets)
                                  │  2. GET {api}/secrets-manager/secrets/{id}
                                  ▼
guest ── value ──► upstream      Bitwarden Cloud (or self-hosted)
```

- **Authorisation** stays with the host `SecretLeaseManager`: a grant
  decides whether a group applies. A group without a matching grant is
  never fetched, let alone injected. This provider only resolves mapped
  secrets the manager already approved. A Secrets Manager secret resolves
  *into* a group (the mapping names both), never beside it — groups stay
  the unit of authorisation and no parallel grouping concept is
  introduced.
- **Renewal/revocation/reconciliation** need no new wiring: the manager's
  background sweep calls `RenewAsync` while the phase runs (capped at the
  work item's deadline) and `RevokeAsync` on teardown; the reconciliation
  sweep re-revokes terminal items whose teardown failed. Lease handles are
  self-describing (`bitwarden.s.{var}.{tail}`), so renew/revoke keep
  working after an orchestrator restart.

## Why native HTTPS and not the C# SDK

Bitwarden ships a C# SDK (`bitwarden/sdk-sm`), and this provider was
evaluated against it first. The SDK is **not** taken as a dependency, for
a licence reason, not a technical one:

- The SDK is distributed under the proprietary **Bitwarden SDK License
  Agreement** (v1, 17 March 2023): a limited, non-assignable,
  non-sublicensable licence to build a "Compatible Application" for
  personal/family use or for internal business operations **in connection
  with a paid Bitwarden server product**, with no redistribution,
  sublicensing, or derivative-works rights except as narrowly permitted.
- CodeyBox is MIT-licensed open source. Depending on a
  non-sublicensable, redistribution-restricted SDK would encumber every
  downstream build with Bitwarden's commercial terms — inadvisable for
  this codebase.

So this provider speaks the documented Secrets Manager REST API directly
over HTTPS (token mint + secret get/list), with no subprocess, no CLI, no
output parsing, and no shelling out. If Bitwarden ever re-licenses the
SDK under an MIT-compatible open-source licence, revisiting the native
dependency is reasonable; until then the HTTP path is the deliberate,
documented choice — not a silent fallback.

## Backend setup (Bitwarden side)

1. **Create the organisation** (or use the existing one) and note its
   UUID — that is `OrganizationId`. It is needed for key-based
   resolution; UUID-form mappings work without it.
2. **Create the projects** the deployment needs (for example `Automation`,
   `Development`). Projects are the unit you scope: one project (or
   project set) per secret group. Note each secret's UUID (preferred —
   UUIDs survive renames) or its exact key.
3. **Create the secrets** in each project (for example `PAID_API_KEY`).
   The provider reads the `value` field only.
4. **Create a machine account per secret group**: Organisation →
   Settings → Machine accounts, granting exactly the projects that
   account may read (read-only where possible). A machine account's
   project grants are least-privilege by construction — an account for
   `paid-api` never sees `Development` projects.
5. Note each machine account's **client id** (configuration, not a
   secret) and **client secret** (credential — goes in the host
   credential chain, never in configuration).

For EU tenants use `https://identity.bitwarden.eu` /
`https://api.bitwarden.eu`; for self-hosted servers point both URLs at
the deployment's origins.

## Least-privilege configuration

- **One machine account per secret group** (`paid-api` vs `dev-api`
  never share). Mappings spanning accounts each name their own client id
  and client-secret variable.
- **Read-only project grants.** The provider only ever reads secrets —
  never writes, never administers.
- **Prefer secret UUIDs** (`SecretId`) over keys: keys resolve with an
  exact-match lookup (similarly-named secrets never shadow each other —
  ambiguity fails loudly), but a rename breaks key-based mappings while
  UUIDs survive it.
- **Provider credentials from the host credential chain only**: the
  `BITWARDEN_CLIENT_SECRET_*` values live in host environment (vault
  agent, systemd credentials, container secrets). Configuration holds
  only the variable *names* (plus the non-secret client ids). Never put
  values in `appsettings.json`.
- **Network**: the orchestrator needs egress to the identity and API
  origins only. Prefer `https` (plain `http` is rejected for
  non-loopback hosts).
- **Prefer short lease windows**: the minted value's exposure after a
  missed revocation is bounded by rotation + the client-side TTL, so keep
  `StaticLeaseTtlMinutes` short for high-value groups.

## Honest lease statement

Secrets Manager secrets are **static values**: there is no server-side
lease to delete and the client-credentials flow exposes **no
token-revocation endpoint**. The lease this provider issues is therefore
a client-side validity window over a genuinely short-lived access token:

- **Identity**: the lease id (`bitwarden.s.{var}.{tail}`) carries names
  only — safe to log, persist, and reconcile.
- **Renewal**: re-authenticates (reusing the cached token while it is
  still safely valid) and re-fetches, so an edit or rotation propagates
  within one window. The minted expiry never exceeds the access token's
  own server lifetime minus skew — a secret is never cached beyond its
  lease or expiry.
- **Revocation**: drops the cached access token (a sibling lease
  re-authenticates transparently on its next renewal), records local
  invalidation so the revoked handle refuses renewal in this process
  (verified, not assumed), and relies on the orchestrator's teardown
  scrub of the per-exec environment. Always succeeds; idempotent.
- **Residual exposure**: a minted access token stays bearer-valid until
  its own short server expiry (typically 60 minutes, often less in
  practice). That bounds the worst case after a missed revocation, and
  short `StaticLeaseTtlMinutes` values shrink it further. The trade-off
  is memory-only by design: after a restart a revoked handle re-resolves
  its mapping rather than remembering the revocation.

## Enablement

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["plugins/credentials/CodeyBox.BitwardenPlugin/bin/Release/net10.0/CodeyBox.BitwardenPlugin.dll"],
      "Allowlist": ["codeybox.bitwarden"],
      "codeybox.bitwarden": {
        "Enabled": true,
        "ApiUrl": "https://api.bitwarden.com",
        "IdentityUrl": "https://identity.bitwarden.com",
        "ClientId": "your-machine-account-client-id",
        "ClientSecretEnvVar": "BITWARDEN_CLIENT_SECRET_AUTOMATION",
        "OrganizationId": "your-organisation-uuid",
        "StaticLeaseTtlMinutes": 20,
        "Mappings": [
          { "SandboxEnvVar": "PAID_API_TOKEN", "Group": "paid-api",
            "SecretId": "2c4c8a1e-3f5b-4a6c-9d7e-8f0a1b2c3d4e" },
          { "SandboxEnvVar": "DEV_TOKEN", "Group": "dev-api",
            "SecretKey": "DEV_TOKEN", "ProjectId": "9d7e8f0a-1b2c-4d5e-8f0a-1b2c3d4e5f6a",
            "ClientId": "dev-machine-account-client-id",
            "ClientSecretEnvVar": "BITWARDEN_CLIENT_SECRET_DEV" }
        ]
      }
    }
  }
}
```

## Contract gaps

None known: the lease-shaped contract (`Issue` / `Renew` / `Revoke`
plus the reconciliation sweep over persisted lease handles) expresses
everything this backend offers. Brokered/dynamic secrets do not exist in
Secrets Manager, so this provider always issues classic values
(`Brokered=false`).

## Tests

`tests/CodeyBox.Tests/Bitwarden/BitwardenPluginTests.cs` verifies the
contract with HTTP faked at the transport and the real manager/store/sweep
wiring; recorded payload shapes live in
`tests/CodeyBox.Tests/Fixtures/bitwarden/`, transcribed from the live
Secrets Manager API shapes. The one live test runs only when
`BW_SM_LIVE_*` env is set (`BW_SM_LIVE_IDENTITY_URL`,
`BW_SM_LIVE_API_URL`, `BW_SM_LIVE_CLIENT_ID`,
`BW_SM_LIVE_CLIENT_SECRET`, `BW_SM_LIVE_SECRET_ID`,
`BW_SM_LIVE_VALUE`), so offline runs rely on the recorded shapes plus
this README's statement of why no live instance is required in CI.
