# CodeyBox: 1Password Secrets (`codeybox.onepassword`)

Lease-shaped credential provider for [1Password](https://1password.com):
item-field retrieval from operator-mapped vaults through the self-hosted
**Connect server** (Connect token) and through the **`op` CLI** under a
**service-account token**. One project, one plugin. **Off unless an
operator enables it.**

## Topology

Connect is a self-hosted sync service the deployment talks to — this is
not a hosted API call. The operator deploys Connect inside their own
infrastructure; the orchestrator reaches it over the local network, and
the service-account path shells to the `op` CLI on the host (no shell,
argv array only).

```
                  self-hosted (operator infrastructure)
                 ┌─────────────────────────────────────┐
workload sandbox │  Connect server (:8080)             │
── env (value) ──┼─► orchestrator                      │
                 │     │  lease handles only           │
                 │     │  (persisted in state DB)      │
                 │     ▼                               │
                 │  codeybox.onepassword               │
                 │    ├─ Connect: Bearer token ──► GET │
                 │    │   /v1/vaults/{vault}/items/…   │
                 │    └─ service account: op read ──►  │
                 │        op://vault/item/field        │
                 │        (child env carries token)    │
                 └─────────────────────────────────────┘
guest ── value ──► upstream

1Password.com (SaaS, control plane only):
  vault/item administration, Connect token create/revoke/expire,
  service-account create (grants are immutable — see below).
```

- **Connect mode (default).** The sandbox receives the value as a normal
  time-bound lease. Renewal re-fetches; revocation is local invalidation
  plus the teardown scrub of the per-exec environment (Connect items
  expose no server-side lease to delete); teardown scrubs the per-exec
  environment regardless.
- **Service-account mode** (`UseServiceAccount: true`). The provider runs
  `op read --no-newline "op://vault/item/field"` with the service-account
  token in the child's environment only. Same lease semantics as Connect
  mode; the transport is the CLI, not HTTP.
- **Authorisation** stays with the host `SecretLeaseManager`: a grant
  decides whether a group applies. A group without a matching grant is
  never fetched, let alone injected. This provider only resolves mapped
  secrets the manager already approved. A vault resolves *into* a group
  (the mapping names both), never beside it — groups stay the unit of
  authorisation and no parallel grouping concept is introduced.
- **Renewal/revocation/reconciliation** need no new wiring: the manager's
  background sweep calls `RenewAsync` while the phase runs (capped at the
  work item's deadline) and `RevokeAsync` on teardown; the reconciliation
  sweep re-revokes terminal items whose teardown failed. Lease handles are
  self-describing (`onepassword.{c|o}.{var}.{tail}`), so renew/revoke keep
  working after an orchestrator restart.

## Backend setup (1Password side)

1. **Deploy a Connect server** in your infrastructure (the documented
   `op-connect-api` + `op-connect-sync` containers backed by a credentials
   file for a machine account that can read the vaults below). Note its
   internal origin (for example `http://op-connect:8080`) — that is
   `ServerUrl`. The deployment talks to Connect; only token
   administration touches 1Password.com.
2. **Create the vaults** the deployment needs (for example `Automation`,
   `Development`). Vault-scoped access is the natural mapping onto secret
   groups: one vault (or vault set) per group.
3. **Create the items** in each vault (for example a `Paid API` item of
   category `API_CREDENTIAL`). The provider reads one field per mapping —
   label first, then field id — defaulting to `password`, the conventional
   label 1Password assigns the concealed credential.
4. **Create a Connect token per vault (or vault set)**: 1Password.com →
   Developer → your Connect server → New Token, granting exactly the
   vaults that token may read (read-only where possible). A token's vault
   list is immutable: to change it, revoke the token and create a new one.
   Tokens also support expiry — set one.
5. For the service-account path, **create a service account** with access
   to exactly the vaults its mappings need (read-only). Its vault grants
   are likewise immutable. Install the `op` CLI (≥ 2.18.0) on the host and
   point `OpBinaryPath` at it when it is not on `PATH`.

## Connect tokens vs service accounts

Both are bearer-style credentials whose *values* live only in the host
credential chain (configuration holds only variable names), but they
differ in what they can reach and how they are managed:

|                            | Connect token | Service account |
| -------------------------- | ------------- | --------------- |
| Talks to                   | Your Connect server (`ServerUrl`) | `op` CLI on the host (`OpBinaryPath`) |
| Scoping                    | Vault list granted to the token, per Connect server; immutable, revocable, expirable on 1Password.com | Vaults granted to the account on 1Password.com; immutable (create a new account to change) |
| Network                    | Orchestrator → Connect over your LAN (https unless loopback) | Host-local CLI; CLI → 1Password.com over https |
| Rotation                   | Revoke + create a new token, update the host env var; renewal re-fetches so rotation propagates within one lease window | Same: new account, update the host env var |
| Cannot reach               | Vaults outside its grant (403); other Connect servers | Personal/Private/Employee/default-Shared vaults; the Kubernetes Operator path |

A mapping uses exactly one of them: `UseServiceAccount: true` selects the
CLI path (per-mapping `TokenEnvVar` is rejected there — the token always
comes from `ServiceAccountTokenEnvVar`); otherwise the Connect path
applies, with per-mapping `TokenEnvVar` selecting a vault-scoped token.

## Least-privilege configuration

- **One Connect token per vault (or vault set)** (`Automation` vs
  `Development` never share). Mappings spanning vaults each name their own
  token via mapping-level `TokenEnvVar`.
- **Read-only grants.** Both paths need only item-read on their vaults —
  never write, never admin, never personal vaults.
- **Prefer vault UUIDs** (`VaultId`/`ItemId`) over names where the mapping
  is long-lived: names resolve with an exact-match lookup (similarly-named
  vaults never shadow each other — ambiguity fails loudly), but a rename
  breaks name-based mappings while UUIDs survive it.
- **Provider credentials from the host credential chain only**: the
  `OP_CONNECT_TOKEN` / `OP_SERVICE_ACCOUNT_TOKEN` values live in host
  environment (vault agent, systemd credentials, container secrets).
  Configuration holds only the variable *names*. Never put values in
  `appsettings.json`.
- **Network**: the orchestrator needs egress to the Connect server only
  (plus 1Password.com for the `op` CLI path). Prefer `https` for Connect
  (plain `http` is rejected for non-loopback servers).
- **Prefer short lease windows**: the minted value's exposure after a
  missed revocation is bounded by rotation + the client-side TTL, so keep
  `StaticLeaseTtlMinutes` short for high-value groups.

## Enablement

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["plugins/credentials/CodeyBox.OnePasswordPlugin/bin/Release/net10.0/CodeyBox.OnePasswordPlugin.dll"],
      "Allowlist": ["codeybox.onepassword"],
      "codeybox.onepassword": {
        "Enabled": true,
        "ServerUrl": "http://op-connect:8080",
        "ConnectTokenEnvVar": "OP_CONNECT_TOKEN_AUTOMATION",
        "ServiceAccountTokenEnvVar": "OP_SERVICE_ACCOUNT_TOKEN",
        "OpBinaryPath": "op",
        "StaticLeaseTtlMinutes": 20,
        "Mappings": [
          { "SandboxEnvVar": "PAID_API_TOKEN", "Group": "paid-api",
            "VaultId": "ftz4pm2xxwmwrsd7rjqn7grzfz", "ItemTitle": "Paid API", "Field": "password" },
          { "SandboxEnvVar": "DEV_TOKEN", "Group": "dev-api",
            "VaultName": "Development", "ItemId": "k9p4exampleitem00000002",
            "TokenEnvVar": "OP_CONNECT_TOKEN_DEV" },
          { "SandboxEnvVar": "SEARCH_API_TOKEN", "Group": "paid-api",
            "VaultName": "Automation", "ItemTitle": "Search API",
            "UseServiceAccount": true }
        ],
      }
    }
  }
}
```

Notes: mappings are the unit of service — a secret with no mapping is
never fetched. Vault resolution (`VaultId` xor `VaultName`) and item
resolution (`ItemId` xor `ItemTitle`) are exact-match; `Field` defaults to
`password`. All knobs are re-read per issue/renew/revoke
(hot-reloadable); secrets are never read from configuration.

## Honest lease statement

|                        | Connect (server token) | Service account (`op` CLI) |
| ---------------------- | ---------------------- | -------------------------- |
| Lease identity         | Client handle (`onepassword.c.…`) | Client handle (`onepassword.o.…`) |
| Renewal                | Re-fetch (rotation propagates ≤ 1 window) | Re-run `op read` (rotation propagates ≤ 1 window) |
| Revocation             | Local invalidation + teardown scrub (no server call exists) | Local invalidation + teardown scrub (no server call exists) |
| Restart-safe           | Yes (mapping re-resolves, value re-fetched) | Yes (mapping re-resolves, value re-read) |

Neither transport offers a server-side lease: there is no per-read token
to revoke and no expiry handed out per fetch, so revocation is
client-side by construction. A lease revoked in this process refuses
renewal loudly (the provider tracks revocations in memory); after a
restart the handle re-resolves its mapping and renews again — the
documented trade-off of holding no server lease. Token expiry and
revocation happen on the 1Password side (Connect token expiry/revoke,
service-account replacement); an expired credential surfaces as
unauthorised infrastructure on the next fetch, never as a diff verdict.

## Failure classification

Backend unreachable (including `op` timeouts), unauthorised (401/403),
rate-limited (429), throttled (503 + retry signal), server errors (5xx),
and malformed success bodies are **infrastructure** (`IsInfrastructure`,
`FailureKindForWorkItem = infrastructure`) — never a verdict on the work
item's diff. The manager already treats every lease failure this way: the
item runs without the secret, renewals retry on the next sweep,
revocations retry up to `MaxRevocationAttempts` and then park loudly.
Missing credentials, absent vaults/items/fields (404), ambiguous names,
bad mappings, and a missing `op` binary are **configuration** faults
(still loud, still never a diff verdict). Backend **redirects (3xx) are
never followed** — the HTTP client is built with redirects disabled and a
3xx fails closed as infrastructure — so a redirect target can never
receive a bearer token. Nothing — logs, lease records, exceptions — ever
carries a value or token; lease ids are the auditable unit.

## Contract gaps

Things the lease contract cannot express that this backend does:

1. **Revocation kind.** `RevokeAsync` success always means local
   invalidation here (no server call exists in either transport). The
   report cannot distinguish it from a server-side delete; the
   distinction lives in this README.
2. **Lease-id routing.** Renew/revoke receive only the handle, so routing
   (sandbox var → mapping, transport kind) is embedded in the handle
   format `onepassword.{c|o}.{var}.{tail}`. The `UseServiceAccount` rule
   lives here, not in core.
3. **Name resolution.** Vault/item names resolve to UUIDs with exact-match
   lookups on the Connect path but pass through verbatim on the CLI path;
   ambiguity fails loudly on the former and is the CLI's verdict on the
   latter. The contract sees only leases.
4. **Field selection.** Which field of an item holds the credential is an
   operator-declared string (`Field`, label-then-id), not a typed
   contract; a renamed field surfaces as a missing-field fault, not a
   mapping fault.
5. **Transport failure shapes.** An `op` failure arrives as an exit code
   plus truncated stderr rather than an HTTP status; the mapping from
   that shape onto `Unauthorized`/`NotFound`/`BackendError` lives here.

## Tests

`tests/CodeyBox.Tests/OnePassword/OnePasswordPluginTests.cs`
(recorded shapes in `tests/CodeyBox.Tests/Fixtures/onepassword/`,
transcribed from the live Connect API reference): granted-resolves /
ungranted-never-fetched (asserting zero HTTP *and* zero CLI invocations),
renewal across a 240-minute phase, teardown revocation verified by
refused renewal plus the store state, sweep-after-failed-teardown,
log-leak, failure classification (HTTP and CLI), redirect refusal
(verified over loopback with a recording sink), name resolution
(exact-match, ambiguity fails loudly), service-account read/renew/revoke,
options validation.

**Live test** (`Live_Fetch_Against_Real_Instance`, opt-in): no live
instance runs in CI because the suite must stay deterministic with no
network dependence. To run it, deploy Connect locally (`docker run -d -p
8080:8080 1password/connect-api:latest`, configured per the setup
section) and export:

```
OP_CONNECT_LIVE_URL=http://127.0.0.1:8080
OP_CONNECT_LIVE_TOKEN=<connect-token-with-vault-access>
OP_CONNECT_LIVE_VAULT=<vault-uuid>
OP_CONNECT_LIVE_ITEM=<item-uuid>
OP_CONNECT_LIVE_FIELD=password
OP_CONNECT_LIVE_VALUE=<expected-value>
```

then `dotnet test --filter FullyQualifiedName~OnePassword`.
