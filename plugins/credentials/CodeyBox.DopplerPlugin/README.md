# CodeyBox: Doppler Secrets (`codeybox.doppler`)

Lease-shaped credential provider for [Doppler](https://www.doppler.com):
secret retrieval from Doppler projects/configs through restricted service
tokens (static path) and short-lived service-account identity tokens
(lease-shaped path). One project, one plugin. **Off unless an operator
enables it.**

## Topology

```
workload sandbox ── env (value) ── orchestrator
                                      │  lease handles only
                                      │  (persisted in state DB)
                                      ▼
                               codeybox.doppler
                                  ├─ static: restricted service token ──► guest env (time-bound)
                                  └─ identity: OIDC ──► short-lived token ──► guest env (server expiry)
                                                      │
guest ── value ──► upstream                          ▼
                                         POST /v3/auth/revoke on teardown
```

- **Static mode (default).** The sandbox receives the value as a normal
  time-bound lease. Renewal re-fetches; revocation is local invalidation
  plus the teardown scrub of the per-exec environment (Doppler static
  secrets expose no server-side lease to delete); teardown scrubs the
  per-exec environment regardless.
- **Identity mode.** The provider exchanges a fresh OIDC token for a
  short-lived service-account identity token (`POST /v3/auth/oidc`), fetches
  with it, and revokes it explicitly (`POST /v3/auth/revoke`) on teardown.
  The lease expiry is the server's `expires_at`, not a client guess.
- **Authorisation** stays with the host `SecretLeaseManager`: a grant
  decides whether a group applies. A group without a matching grant is
  never fetched, let alone injected. This provider only resolves mapped
  secrets the manager already approved. A Doppler config resolves *into* a
  group (the mapping names both), never beside it — groups stay the unit
  of authorisation and no parallel grouping concept is introduced.
- **Renewal/revocation/reconciliation** need no new wiring: the manager's
  background sweep calls `RenewAsync` while the phase runs (capped at the
  work item's deadline) and `RevokeAsync` on teardown; the reconciliation
  sweep re-revokes terminal items whose teardown failed. Lease handles are
  self-describing (`doppler.{s|i}.{var}.{tail}`), so renew/revoke keep
  working after an orchestrator restart.

## Backend setup (Doppler side)

1. Create a **project** (e.g. `acme`) and a **config** per environment
   (e.g. `prd`, `dev`). Configs are the unit you map: one mapping names
   one project/config/secret triple and resolves it into one group.
2. Create the secrets in each config (e.g. `PAID_API_KEY`). The provider
   reads the `computed` value (references resolved), never `raw`.
3. Create a **restricted service token** per project/config that needs
   static access: Project → Config → Access → Generate Service Token,
   scoped to that config with **read-only** access. One token per config;
   tokens never span projects.
4. For the identity path, create a **service account** with a role limited
   to the projects/configs it needs (read-only), then add an **identity**
   (Settings → Service Account Identities) trusting your OIDC provider
   (e.g. GitHub Actions `aud`/`sub` claim rules). Note the identity id —
   it is configuration, not a secret.
5. Arrange for a fresh **OIDC token** to reach the host on every run (your
   CI system's OIDC endpoint, staged into the environment by a host helper
   before the orchestrator starts the phase). The provider reads it from
   the configured env var at each exchange and never stores it.

Self-hosting for evaluation is not applicable (Doppler is SaaS); for
offline evaluation, run the recorded-shape test suite — no network needed.

## Least-privilege configuration

- **One restricted service token per project/config** (`prd` vs `dev`
  never share). A token is bound to exactly one config: mappings spanning
  configs each name their own token via mapping-level `TokenEnvVar`.
- **Read-only tokens and roles.** The static path needs only secret-read
  on its config. The identity path needs only secret-read on its projects
  plus the `auth/oidc` exchange for its own identity — never personal or
  CLI tokens (those are account-wide) and never write.
- **Prefer the identity path where available** (Team/Enterprise plans):
  short-lived tokens with server-side expiry and explicit revocation beat
  long-lived service tokens. Per-mapping `TokenEnvVar` overrides always
  force the static path for that mapping, so you can mix: identity by
  default, a pinned restricted token for the one config the service
  account cannot see.
- **Provider credentials from the host credential chain only**: the
  `DOPPLER_TOKEN` / OIDC-token values live in host environment (vault
  agent, systemd credentials, container secrets). Configuration holds only
  the variable *names*. Never put values in `appsettings.json`.
- **Network**: the orchestrator needs egress to `https://api.doppler.com`
  only. Prefer `https` (plain `http` is rejected for non-loopback APIs).
- **Prefer short identity lifetimes**: the minted token's server expiry
  bounds exposure when a revocation cannot run (e.g. orchestrator restart
  drops the memory-only token — see below).

## Enablement

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["plugins/credentials/CodeyBox.DopplerPlugin/bin/Release/net10.0/CodeyBox.DopplerPlugin.dll"],
      "Allowlist": ["codeybox.doppler"],
      "codeybox.doppler": {
        "Enabled": true,
        "ApiUrl": "https://api.doppler.com",
        "DefaultProject": "acme",
        "DefaultConfig": "prd",
        "ServiceTokenEnvVar": "DOPPLER_TOKEN_PRD",
        "StaticLeaseTtlMinutes": 20,
        "IdentityId": "00000000-0000-0000-0000-000000000000",
        "OidcTokenEnvVar": "CODEYBOX_DOPPLER_OIDC_TOKEN",
        "Mappings": [
          { "SandboxEnvVar": "PAID_API_TOKEN", "Group": "paid-api", "SecretName": "PAID_API_KEY" },
          { "SandboxEnvVar": "DEV_TOKEN", "Group": "dev-api", "SecretName": "DEV_KEY",
            "Project": "acme", "Config": "dev", "TokenEnvVar": "DOPPLER_TOKEN_DEV" }
        ],
      }
    }
  }
}
```

Notes: mappings are the unit of service — a secret with no mapping is
never fetched. `SecretName` defaults to `SandboxEnvVar`; `Project`/`Config`
default to `DefaultProject`/`DefaultConfig`; `TokenEnvVar` defaults to
`ServiceTokenEnvVar` and forces the static path for that mapping. Static
and identity modes mix freely per mapping. All knobs are re-read per
issue/renew/revoke (hot-reloadable); secrets are never read from
configuration.

## Honest lease statement

|                        | Static (service token) | Identity (service account) |
| ---------------------- | ---------------------- | -------------------------- |
| Lease identity         | Client handle (`doppler.s.…`) | Client handle (`doppler.i.…`); the server token lives only in host memory, never in the handle |
| Renewal                | Re-fetch (rotation propagates ≤ 1 window) | Re-fetch while the minted token is valid; past its lifetime a fresh OIDC exchange is required |
| Revocation             | Local invalidation + teardown scrub (no server call exists) | **Server revoke** (`POST /v3/auth/revoke`; 4xx = already gone = success) |
| Restart-safe           | Yes (mapping re-resolves, value re-fetched) | Partially: renewal re-exchanges when a fresh OIDC token is available; revocation after a restart **fails loudly** — the memory-only token is unaddressable, so the sweep retries then parks the lease visibly while the short server-side expiry bounds exposure |

The OIDC token feeding the identity path is itself short-lived (minutes):
renewal across a phase longer than the OIDC token's lifetime fails as
infrastructure and the sweep keeps retrying — loud, never a diff verdict.
Stage a fresh OIDC token per phase when using long phases with identity
leases, or accept static leases for those groups.

## Failure classification

Backend unreachable, unauthorised (401/403), rate-limited (429), throttled
(503 + retry signal), server errors (5xx), and malformed success bodies are
**infrastructure** (`IsInfrastructure`, `FailureKindForWorkItem =
infrastructure`) — never a verdict on the work item's diff. The manager
already treats every lease failure this way: the item runs without the
secret, renewals retry on the next sweep, revocations retry up to
`MaxRevocationAttempts` and then park loudly. Missing credentials, absent
projects/configs/secrets (404), and bad mappings are **configuration**
faults (still loud, still never a diff verdict). Backend **redirects (3xx)
are never followed** — the HTTP client is built with redirects disabled
and a 3xx fails closed as infrastructure — so a redirect target can never
receive a bearer token. Nothing — logs, lease records, exceptions — ever
carries a value or token; lease ids are the auditable unit.

## Contract gaps

Things the lease contract cannot express that this backend does:

1. **Revocation kind.** `RevokeAsync` success means different things:
   server-side token revoke (identity) vs local invalidation (static). The
   report cannot distinguish them; the distinction lives in this README
   and the lease-id prefix (`i` vs `s`).
2. **Lease-id routing.** Renew/revoke receive only the handle, so routing
   (sandbox var → mapping, resolved project/config/secret) is embedded in
   the handle format `doppler.{s|i}.{var}.{tail}`. The minted identity
   token is deliberately *not* embedded (handles are logged and
   persisted); it lives only in host memory, which is why post-restart
   identity revocation fails loudly instead of silently succeeding.
3. **Mixed credential modes.** Whether a mapping uses the identity
   exchange or a static token is an operator choice per mapping
   (`TokenEnvVar` override forces static); the contract sees only
   leases. The `UseIdentity` rule lives here, not in core.
4. **Computed vs raw.** Doppler resolves `${…}` references server-side;
   the provider always takes `computed`. The contract has no notion of
   reference expansion, so a secret whose references break at fetch time
   surfaces as a backend value fault, not a mapping fault.

## Tests

`tests/CodeyBox.Tests/Doppler/DopplerPluginTests.cs` (recorded shapes in
`tests/CodeyBox.Tests/Fixtures/doppler/`, transcribed from the live
OpenAPI + docs): granted-resolves/ungranted-never-fetched, renewal across
a 240-minute phase, teardown revocation verified against the fake
backend's server state, sweep-after-failed-teardown, log-leak, failure
classification, identity exchange/renew/revoke, redirect refusal (verified
over loopback with a recording sink), static-only unchanged, options
validation.

**Live test** (`Live_Fetch_Against_Real_Instance`, opt-in): no live
instance runs in CI because the suite must stay deterministic with no
network dependence. To run it, export:

```
DOPPLER_LIVE_TOKEN=dp.st.prd.xxxxxx
DOPPLER_LIVE_PROJECT=acme DOPPLER_LIVE_CONFIG=prd
DOPPLER_LIVE_SECRET=PAID_API_KEY
```

then `dotnet test --filter FullyQualifiedName~Doppler`.
