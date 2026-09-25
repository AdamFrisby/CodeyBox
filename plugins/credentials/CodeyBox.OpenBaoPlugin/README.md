# CodeyBox: OpenBao Secrets (`codeybox.openbao`)

Lease-shaped credential provider for [OpenBao](https://openbao.org): static
KV reads plus genuine server-side dynamic leases — issue, renew while the
phase runs, explicit revoke at teardown — from one OpenBao cluster. One
project, one plugin. **Off unless an operator enables it.**

## Topology

```
workload sandbox ── env (value) ── orchestrator
                                      │  lease handles only
                                      │  (persisted in state DB)
                                      ▼
                               codeybox.openbao
                                  ├─ static:  GET /v1/secret/data/…      ──► guest env (time-bound)
                                  └─ dynamic: GET /v1/database/creds/…   ──► guest env (server lease)
                                                        │
                                      POST /v1/sys/leases/renew  (sweep)
                                      POST /v1/sys/leases/revoke (teardown, sync)
```

- **Static mode (KV v1/v2).** The sandbox receives the value as a normal
  time-bound lease. Renewal re-fetches so rotation propagates within one
  window; revocation is local invalidation plus the teardown scrub of the
  per-exec environment (KV secrets expose no server-side lease to delete).
- **Dynamic mode.** The mapping names a credential endpoint
  (`database/creds/<role>`, `aws/creds/<role>`, …) whose response carries a
  real `lease_id`. The lease identity is the server id itself; renewal is
  `sys/leases/renew`; revocation is `sys/leases/revoke` with `sync=true`,
  so the call returns only once the credential genuinely stopped working.
- **Authorisation** stays with the host `SecretLeaseManager`: a grant
  decides whether a group applies. A group without a matching grant is
  never fetched, let alone injected. This provider only resolves mapped
  secrets the manager already approved.
- **Renewal/revocation/reconciliation** need no new wiring: the manager's
  background sweep calls `RenewAsync` while the phase runs (capped at the
  work item's deadline) and `RevokeAsync` on teardown; the reconciliation
  sweep re-revokes terminal items whose teardown failed. Lease handles are
  self-describing (`openbao.{s|d}.{var}.{tail}`), so renew/revoke keep
  working after an orchestrator restart — for dynamic leases the tail *is*
  the server lease id, so revocation works even if the mapping was deleted.
  Revocation is also not gated on `Enabled`: disabling the plugin (a
  natural response to a suspect backend) must not strand already-issued
  leases until their server TTL.

## Backend setup (OpenBao side)

1. Run an OpenBao server reachable from the orchestrator
   (`bao server -dev` or `docker run -p 8200:8200 openbao/openbao` for
   evaluation; a real deployment with TLS for anything else).
2. Create an **AppRole** for the plugin:
   `bao auth enable approle`, then `bao write auth/approle/role/codeybox
   token_ttl=20m token_max_ttl=2h policies=codeybox-secrets`. Read out the
   `role_id` and issue a `secret_id`. (A ready-made token from a trusted
   provisioner also works — see `TokenEnvVar` — but AppRole keeps the
   provider credential itself short-lived.)
3. Attach a **policy** covering exactly the mapped paths (see below).
4. Create the secrets:
   - **Static:** `bao secrets enable -path=secret kv-v2`, then
     `bao kv put secret/myapp password=…`. Map it with
     `SecretPath: "secret/data/myapp"`, `KvVersion: 2`,
     `DataField: "password"`.
   - **Dynamic:** `bao secrets enable database`, configure a connection
     and a role (`database/roles/readonly`) whose `default_ttl` is at or
     below your phase length and whose `max_ttl` is above it. Map it with
     `DynamicPath: "database/creds/readonly"`, `DataField: "password"`.
     Any read-style (GET) creds endpoint works — `aws/creds/…`,
     `pki/certs/…` is *not* read-style and is out of scope.

## Least-privilege configuration

- **One AppRole per environment** (`prod` vs `dev` never share), bound to
  a policy that allows only:
  - `read` on each mapped `SecretPath`/`DynamicPath`
    (e.g. `path "secret/data/myapp" { capabilities = ["read"] }`,
    `path "database/creds/readonly" { capabilities = ["read"] }`)
  - `update` on `sys/leases/renew` and `sys/leases/revoke`
    (`path "sys/leases/*" { capabilities = ["update"] }`)
  - Nothing else. No `sys/*` admin, no other mounts, no `sudo`.
- **Provider credentials from the host credential chain only**: the role
  ID / secret ID (or token) values live in host environment variables
  provisioned by a vault agent, systemd credentials, or container secrets.
  Configuration holds only the variable *names*. Never put values in
  `appsettings.json`.
- **Network**: the orchestrator needs egress to the OpenBao address only.
  Plain `http` is rejected for non-loopback addresses — use `https`
  everywhere outside local development.
- Keep `RevokeSync` at its default `true`: revocation returns after the
  credential is dead, so a "revoked" lease record is verified, not queued.

## Enablement

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["plugins/credentials/CodeyBox.OpenBaoPlugin/bin/Release/net10.0/CodeyBox.OpenBaoPlugin.dll"],
      "Allowlist": ["codeybox.openbao"],
      "codeybox.openbao": {
        "Enabled": true,
        "Address": "https://bao.internal.example.com:8200",
        "AppRoleIdEnvVar": "OPENBAO_ROLE_ID",
        "AppRoleSecretIdEnvVar": "OPENBAO_SECRET_ID",
        "AuthMount": "approle",
        "RenewIncrementSeconds": 1200,
        "StaticLeaseTtlMinutes": 20,
        "Mappings": [
          { "SandboxEnvVar": "PAID_API_TOKEN", "Group": "paid-api",
            "SecretPath": "secret/data/myapp", "KvVersion": 2, "DataField": "password" },
          { "SandboxEnvVar": "DB_PASSWORD", "Group": "paid-api",
            "DynamicPath": "database/creds/readonly", "DataField": "password" }
        ]
      }
    }
  }
}
```

Notes: mappings are the unit of service — a secret with no mapping is
never fetched. Exactly one of `SecretPath` / `DynamicPath` per mapping;
`DataField` always required. All knobs are re-read per issue/renew/revoke
(hot-reloadable); secrets are never read from configuration.

## Honest lease statement

|                        | Static KV secrets | Dynamic-engine credentials |
| ---------------------- | ----------------- | -------------------------- |
| Lease identity         | Client handle embedding sandbox var (`openbao.s.…`) | **Server lease id** (`openbao.d.{var}.{server-lease-id}`) |
| Renewal                | Re-fetch (rotation propagates ≤ 1 window) | **`sys/leases/renew`**, new `lease_duration` |
| Revocation             | Local invalidation + teardown scrub (no server call exists) | **`sys/leases/revoke` with `sync=true`** — verified, not queued (404 = already gone = success) |
| Restart-safe           | Yes (mapping re-resolves) | Yes (server lease id embedded in handle; no mapping needed) |

Not supported, declared rather than faked:

- **Write-style issue endpoints** (e.g. `pki/issue/<role>` needs a POST
  body): only GET-readable credential paths are mapped.
- **Periodic/renewable provider tokens**: a direct `TokenEnvVar` token is
  trusted as-is until the backend rejects it; the AppRole path is the
  recommended least-privilege setup and re-logins on expiry.
- **Brokered injection**: this plugin issues values only; there is no
  loopback proxy mode.
- **Namespaces**: OpenBao has none; a Vault Enterprise deployment behind
  the same API would need the header added (not implemented).

## Failure classification

Backend unreachable, unauthorised (401/403), rate-limited (429), throttled
(503 + retry signal), server errors (5xx), and malformed success bodies are
**infrastructure** (`IsInfrastructure`, `FailureKindForWorkItem =
infrastructure`) — never a verdict on the work item's diff. The manager
already treats every lease failure this way: the item runs without the
secret, renewals retry on the next sweep, revocations retry up to
`MaxRevocationAttempts` and then park loudly. Missing provider
credentials, absent secrets (404), and bad mappings are **configuration**
faults (still loud, still never a diff verdict). Backend **redirects (3xx)
are never followed** — the HTTP client is built with redirects disabled
and a 3xx fails closed as infrastructure, so a redirect target can never
receive a token. Nothing — logs, lease records, exceptions — ever carries
a value, token, or secret ID; lease ids are the auditable unit.

## Contract gaps

Things the lease contract cannot express that OpenBao does:

1. **Lease-id routing.** Renew/revoke receive only the handle, so routing
   (sandbox var → mapping, server lease id) is embedded in the handle
   format `openbao.{s|d}.{var}.{tail}`. For dynamic leases the tail is the
   verbatim server lease id (`database/creds/readonly/…`); slashes ride
   the tail segment, but a server lease id containing `.` cannot
   round-trip and is refused at issue as an invalid response.
2. **Renewal increment.** `RenewAsync` takes only the lease id, so the
   `increment` asked of `sys/leases/renew` comes from the
   `RenewIncrementSeconds` knob rather than the phase's remaining time.
   The server clamps to the role's max TTL, so this is a policy ask, not a
   correctness issue.
3. **Renewable flag.** `renewable: false` on issue is logged but still
   issued; a renewal attempt then fails server-side (400) and surfaces
   through the sweep rather than being pre-computed at issue.
4. **Revocation kind.** `RevokeAsync` success means different things:
   verified server revoke (dynamic) vs local invalidation (static). The
   report cannot distinguish them; the distinction lives in this README
   and the lease-id kind letter (`d` vs `s`).

## Tests

`tests/CodeyBox.Tests/OpenBao/OpenBaoPluginTests.cs` (recorded shapes in
`tests/CodeyBox.Tests/Fixtures/openbao/`, captured from the live OpenBao
API docs): granted-resolves/ungranted-never-fetched, dynamic renewal
across a 240-minute phase, teardown revocation verified against the fake
backend's server state (including a post-revoke renew the server rejects),
sweep-after-failed-teardown, log-leak, failure classification, redirect
refusal, restart-safe renew/revoke from the handle alone, static-only
unchanged, options validation.

**Live test** (`Live_Issue_Renew_Revoke_Against_Real_Instance`, opt-in):
no live instance runs in CI because the suite must stay deterministic
with no network dependence. To run it, start a dev or test OpenBao with a
database engine (or any read-style creds endpoint) and export:

```
OPENBAO_LIVE_ADDR=http://127.0.0.1:8200
OPENBAO_LIVE_TOKEN=<token>            # or OPENBAO_LIVE_ROLE_ID + OPENBAO_LIVE_SECRET_ID
OPENBAO_LIVE_DYNAMIC_PATH=database/creds/readonly
OPENBAO_LIVE_FIELD=password
OPENBAO_LIVE_STATIC_PATH=secret/data/myapp   # optional
```

then `dotnet test --filter FullyQualifiedName~OpenBao`. The test issues,
renews, revokes, and then asserts the server rejects a further renew of
the dead lease — the credential genuinely stopped working.
