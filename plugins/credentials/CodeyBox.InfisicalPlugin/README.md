# CodeyBox: Infisical Secrets (`codeybox.infisical`)

Lease-shaped credential provider for [Infisical](https://infisical.com):
ordinary secret retrieval (static secrets and dynamic-secret leases) plus
Agent Proxy brokering (a loopback injecting proxy), kept separable so an
operator can use either. One project, one plugin. **Off unless an operator
enables it.**

## Topology

```
workload sandbox ── env (value or endpoint URL) ── orchestrator
                                                      │  lease handles only
                                                      │  (persisted in state DB)
                                                      ▼
                                              codeybox.infisical
                                                ├─ direct: value ──► guest env
                                                └─ brokered: value held proxy-side
                                                      ▲                    │
guest ── request ──► http://127.0.0.1:P/v1/proxy/{lease} ──► upstream (+ credential)
   (never sees value)            ▲ loopback only
```

- **Direct mode.** The sandbox receives the value as a normal time-bound
  lease. Renewal re-fetches; revocation invalidates locally (static) or
  deletes the server lease (dynamic); teardown scrubs the per-exec
  environment regardless.
- **Brokered mode.** The sandbox receives only an endpoint URL (its host is
  folded into the sandbox egress allowlist through the existing broker
  channel — no core changes). The guest sends its own request to the
  endpoint; the proxy attaches the credential server-side and returns the
  upstream response. The value never enters the guest environment, files,
  or logs.
- **Authorisation** stays with the host `SecretLeaseManager`: a grant
  decides whether a group applies. A group without a matching grant is
  never fetched, let alone injected. This provider only resolves mapped
  secrets the manager already approved.
- **Renewal/revocation/reconciliation** need no new wiring: the manager's
  background sweep calls `RenewAsync` while the phase runs (capped at the
  work item's deadline) and `RevokeAsync` on teardown; the reconciliation
  sweep re-revokes terminal items whose teardown failed. Lease handles are
  self-describing, so renew/revoke keep working after an orchestrator
  restart.

## Backend setup (Infisical side)

1. Create a **machine identity** with universal auth (client ID + secret).
2. Give it a project role scoped to **one project, the environments it
   needs** (e.g. `prod`), and — for static secrets — the secret path(s)
   (e.g. `/`) with **read-only** access. No write, no admin, no other
   projects.
3. Create the secrets:
   - **Static:** ordinary secrets under the chosen path (e.g. key
     `PAID_API_KEY`). Enable rotation where the service supports it;
     renewal re-fetches, so rotation propagates within one lease window
     (default 20 min).
   - **Dynamic:** a dynamic secret (SQL, Mongo, Redis, …) in the project
     with a default TTL at or below your phase length and a max TTL above
     it. Note the credential field inside the lease `data` (e.g.
     `password`) — the mapping's `DataField` selects it.
4. For brokered mappings, note the **upstream origin** the credential is
   used against (scheme + host); the broker refuses to forward anywhere
   else.

Self-hosting for evaluation: `docker run -p 8080:8080
infisical/infisical` (see [self-hosting docs](https://infisical.com/docs/self-hosting/overview/introduction)),
then create the identity/project/secrets above in the local UI.

## Least-privilege configuration

- **One identity per environment** (`prod` vs `dev` never share).
- **Read-only project role**, limited to the mapped paths. The identity
  needs exactly these endpoints: `POST /api/v1/auth/universal-auth/login`,
  `GET /api/v3/secrets/raw/{key}`, and — only for dynamic mappings —
  `POST/DELETE /api/v1/dynamic-secrets/leases*`.
- **Provider credentials from the host credential chain only**: the
  `INFISICAL_CLIENT_ID` / `INFISICAL_CLIENT_SECRET` values live in host
  environment (vault agent, systemd credentials, container secrets).
  Configuration holds only the variable *names*. Never put values in
  `appsettings.json`.
- **Network**: the orchestrator needs egress to the Infisical site only.
  Prefer `https` (plain `http` is rejected for non-loopback sites).
- **Broker**: binds loopback-only by default. Guests in VMs without shared
  loopback need `BrokerBindHost` on the host-gateway interface plus
  `BrokerAdvertiseHost` set to the same address — never `0.0.0.0` without
  a firewall. Upstream origins should be `https`. **Pin `BrokerBindPort`
  to a fixed port in any brokered deployment**: broker endpoint URLs embed
  the port, so only a pinned port keeps previously issued endpoints working
  across an orchestrator restart (an ephemeral port is fine for direct-only
  use).
- **Broker path allowlist**: set `BrokerAllowedPaths` to the exact
  upstream paths the workload needs (exact match, never substring); empty
  allows any path under the upstream origin.

## Enablement

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["plugins/credentials/CodeyBox.InfisicalPlugin/bin/Release/net10.0/CodeyBox.InfisicalPlugin.dll"],
      "Allowlist": ["codeybox.infisical"],
      "codeybox.infisical": {
        "Enabled": true,
        "SiteUrl": "https://app.infisical.com",
        "WorkspaceId": "<infisical-project-id>",
        "ProjectSlug": "acme",
        "Environment": "prod",
        "StaticLeaseTtlMinutes": 20,
        "BrokerEnabled": true,
        "Mappings": [
          { "SandboxEnvVar": "PAID_API_TOKEN", "Group": "paid-api", "SecretKey": "PAID_API_KEY" },
          { "SandboxEnvVar": "DB_PASSWORD", "Group": "paid-api", "DynamicSecretName": "ci-database", "DataField": "password", "Ttl": "1h" },
          { "SandboxEnvVar": "SEARCH_API_TOKEN", "Group": "paid-api", "SecretKey": "SEARCH_KEY",
            "Brokered": true, "BrokerUpstreamBaseUrl": "https://api.example.com",
            "BrokerAllowedPaths": ["/v1/query"] }
        ],
      }
    }
  }
}
```

Notes: mappings are the unit of service — a secret with no mapping is
never fetched. `Brokered` mappings additionally need
`BrokerUpstreamBaseUrl` (+ optional `BrokerInjectHeader`/`Scheme`,
default `Authorization: Bearer`). Retrieval and brokering are separable:
use direct mappings alone, brokered mappings alone, or both. All knobs
are re-read per issue/renew/revoke (hot-reloadable); secrets are never
read from configuration.

## Honest lease statement

|                        | Static secrets | Dynamic-secret leases |
| ---------------------- | -------------- | --------------------- |
| Lease identity         | Client handle embedding sandbox var (`infisical.s.…`) | **Server lease uuid** (`infisical.d.…`) |
| Renewal                | Re-fetch (rotation propagates ≤ 1 window) | **Server renew**, new `expireAt` |
| Revocation             | Local invalidation + teardown scrub (no server call exists) | **Server delete** (404 = already gone = success) |
| Restart-safe           | Yes (mapping re-resolves) | Yes (uuid embedded in handle) |

Broker entries live in host memory only and are never persisted (the
lease store carries identity, never values). After an orchestrator
restart, static brokered endpoints are re-registered on the next renew;
dynamic brokered endpoints stay revoked until re-provisioning because a
dynamic value cannot be re-read — the server lease itself still renews.
This is logged loudly and is the documented trade-off of holding values
only in memory.

## Failure classification

Backend unreachable, unauthorised (401/403), rate-limited (429), throttled
(503 + retry signal), server errors (5xx), and malformed success bodies are
**infrastructure** (`IsInfrastructure`, `FailureKindForWorkItem =
infrastructure`) — never a verdict on the work item's diff. The manager
already treats every lease failure this way: the item runs without the
secret, renewals retry on the next sweep, revocations retry up to
`MaxRevocationAttempts` and then park loudly. Missing credentials, absent
secrets/leases, and bad mappings are **configuration** faults (still loud,
still never a diff verdict). Backend or upstream **redirects (3xx) are
never followed** — the HTTP clients are built with redirects disabled, a
3xx from the backend fails closed as infrastructure, and the broker passes
an upstream 3xx back to the guest untouched — so a redirect target can
never receive a token, client secret, or brokered credential. Nothing — logs, lease records, exceptions,
endpoint URLs — ever carries a value or token; lease ids are the auditable
unit.

## Contract gaps

Things the lease contract cannot express that this backend does:

1. **Revocation kind.** `RevokeAsync` success means different things:
   server-side delete (dynamic) vs local invalidation (static). The report
   cannot distinguish them; the distinction lives in this README and the
   lease-id prefix (`d` vs `s`).
2. **Lease-id routing.** Renew/revoke receive only the handle, so routing
   (sandbox var → mapping, server uuid) is embedded in the handle format
   `infisical.{s|S|d|D}.{var}.{tail}` (upper case = brokered). Bounded at
   64-char variable names by validation.
3. **Bearer-URL endpoints.** Broker auth is the unguessable lease id in
   the path plus the loopback bind; the contract has no separate
   authenticator field, so the endpoint URL itself is bearer-equivalent
   (auto-expiring, never written to files).
4. **Dynamic data shapes.** Lease `data` varies by dynamic-secret type;
   field selection is an operator-declared string (`DataField`), not a
   typed contract.
5. **No endpoint refresh on renew.** A renewed lease keeps its original
   endpoint URL (renewal returns only an expiry), so brokered deployments
   must pin `BrokerBindPort` — with an ephemeral port, a post-restart
   broker binds elsewhere and previously issued endpoints go dark until
   re-provisioning.

## Tests

`tests/CodeyBox.Tests/Infisical/InfisicalPluginTests.cs` (recorded shapes
in `tests/CodeyBox.Tests/Fixtures/infisical/`, captured from the live
OpenAPI + docs): granted-resolves/ungranted-never-fetched, renewal across
a 240-minute phase, teardown revocation verified against the fake
backend's server state, sweep-after-failed-teardown, log-leak, failure
classification, broker proxying without value exposure, redirect refusal
(API and broker, verified over loopback with a recording sink), static-only
unchanged, options validation.

**Live test** (`Live_Fetch_Against_Real_Instance`, opt-in): no live
instance runs in CI because the suite must stay deterministic with no
network dependence. To run it, point at a self-hosted instance and export:

```
INFISICAL_LIVE_SITE=http://127.0.0.1:8080
INFISICAL_LIVE_CLIENT_ID=… INFISICAL_LIVE_CLIENT_SECRET=…
INFISICAL_LIVE_WORKSPACE=<project-id> INFISICAL_LIVE_ENV=dev
INFISICAL_LIVE_KEY=<static-secret-key>
```

then `dotnet test --filter FullyQualifiedName~Infisical`.
