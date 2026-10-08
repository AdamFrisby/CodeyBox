# Hetzner Cloud sandbox provider (`codeybox.hetzner-sandbox`, kind `hetzner`)

First-party hosted-server sandbox backend. The OpenStack remote-VM provider
is the concrete analogue — same lifecycle shape, SSH data plane, and
label-scoped leak cleanup, adapted to the Hetzner Cloud API
(`https://docs.hetzner.cloud/reference/cloud`).

Status: **disabled by default**. No `SandboxClass` member is added and the
provider is not enabled — enabling is a future operator action (see
`docs/extending/hetzner-sandbox-plugin.md`, "Operator prerequisites").

## Layout

- `HetznerSandboxProvider.cs` — `ISandboxProvider` + `IPluginInitializer` +
  `IActiveSandboxProvider`. Owns provisioning (pins → key → firewall →
  server → floating IP → running → SSH → stage), labeled-set deletion,
  request-label reconciliation, and the ownership-scoped orphan sweep.
  Declares only `teardown`. No baseline surface at all.
- `HetznerApiClient.cs` — typed Hetzner Cloud v1 client (servers, server
  types, images, locations, SSH keys, firewalls, floating IPs). Bearer auth,
  streamed response bounds, page-bounded listings, client-side re-checks of
  every server-side filter. No provider logic.
- `HetznerSandbox.cs` — live handle (exec/stage/sync/dispose). Sync-back
  runs before cloud cleanup and before the handle is marked disposed
  (two-flag disposal: `_disposeStarted` refuses new execs, `_disposed` is
  set last).
- `HetznerSandboxOptions.cs` / `HetznerCredentialChain.cs` — hot-reloadable
  knobs + host-only token resolution (`HCLOUD_TOKEN`, never in config).
- `HetznerCloudInit.cs` — pure cloud-config renderer (pinned ed25519 host
  key, tmpfs mounts). Unit-testable without a cloud.
- `HetznerSshKeys.cs` — `ssh-keygen` key seams, DNS seam, and the
  `OpenSshCliTransport` factory with strict host keys.
- `HetznerFirewallPolicy.cs` — pure firewall-rule planner (defence in
  depth; kind stays `NotEnforced`).
- `HetznerSandboxSmoke.cs` — real end-to-end smoke flow (opt-in, needs a
  funded project; never runs in CI).

## Invariants for future edits

- The token stays on the host: never in user-data, logs, prompts, or config.
- Name prefixes gate candidacy; only exact ownership labels authorize
  deletion. Other owners' resources are never touched.
- Ambiguous creates reconcile by request label before any resubmit — the API
  has no idempotency key.
- Every bound (bytes, pages, items, retries, waits, rules, ports) is
  enforced before buffering, and every wait is bounded and cancellable.
- `HetznerSandboxOptions` defaults are the single source of truth for knobs;
  `HetznerClientLimits.Default*` mirrors them for the client.
