# Sandbox Leak Detection

The orchestrator runs a periodic background sweep (`SandboxLeakReaper`) that
detects — and optionally disposes — persistent managed sandboxes that outlived
their work item. These "leaked" sandboxes accumulate when the orchestrator
crashes mid-disposal and can exhaust host or remote-provider memory and disk.

---

## What counts as a leak

A provider-owned sandbox is classified as leaked when these conditions hold:

1. A managed lifecycle provider reports it as an ordinary provider-owned
   sandbox. Each provider enforces its own ownership metadata and configured
   name prefix; baseline images are inventoried separately.
2. The current orchestrator process does **not** report it as actively owned by
   a work item. After a normal restart, active in-memory ownership is initially
   empty, so the age threshold below guards against false positives. Durable
   suspended-sandbox mappings are also exempt while startup recovery owns them.
3. Its creation timestamp — derived from provider metadata or provider-owned
   staging metadata where available — is **older than
   `LeakAgeThreshold`** (default 30 minutes), or its creation timestamp cannot be
   determined.

A sandbox that is mid-way through the VM-launch → clone → mount → start sequence
is typically less than 10 minutes old. The 30-minute threshold is a conservative
safety margin: it is unlikely that a legitimately active sandbox would be both
untracked *and* over 30 minutes old.

Sandboxes for which the creation time still cannot be determined are declared
leaked once they are untracked by the current provider snapshot. Their age is
reported from the threshold boundary and their reason is
`untracked_sandbox_missing_creation_metadata`, so operators can distinguish
missing metadata from an ordinary age-threshold leak.

### Providers

| Provider | Leaks tracked? | Why |
|---|---|---|
| `multipass` | **Yes** | VMs persist as KVM guests; `multipass list` reports them after a crash |
| `incus` | **Yes** | VMs and provider-owned staging persist; the dedicated Incus project reports them after a crash |
| `multipass-remote` | **Yes** | Remote VMs persist and are inventoried through the remote lifecycle provider |
| `sprites` | **Yes** | Remote sprites have persistent service identities and lifecycle inventory |
| `bubblewrap` | No | Processes exit when the orchestrator dies; no persistent identity to track |
| `process` | No | Dev-only; no persistent lifecycle |

---

## Configuration

All options are under `CodeyBox:SandboxLeak` in `appsettings.json`.

```json
{
  "CodeyBox": {
    "SandboxLeak": {
      "Enabled": true,
      "CheckInterval": "00:15:00",
      "LeakAgeThreshold": "00:30:00",
      "AutoDispose": true
    }
  }
}
```

| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enable or disable the sweep entirely |
| `CheckInterval` | `00:15:00` (15 min) | How often to run the leak scan |
| `LeakAgeThreshold` | `00:30:00` (30 min) | Minimum age before a non-active sandbox is declared leaked |
| `AutoDispose` | `true` | When true, automatically purge each detected leak |

### AutoDispose

`AutoDispose` defaults to **true** because stale persistent sandboxes keep
consuming provider resources after their phase has ended. Set
`AutoDispose: false` for detection-only operation on a diagnostic host.

Each auto-dispose runs with a 5-minute per-sandbox timeout and is best-effort:
one failed disposal never blocks the rest of the sweep.

---

## Audit events

The reaper emits the following audit-tier events (filtered to the audit-only log
and any configured webhook endpoints):

| Event | When emitted |
|---|---|
| `sandbox.leak_detected` | A leaked sandbox was found (detection-only or before auto-dispose) |
| `sandbox.leak_disposed` | A leaked sandbox was successfully disposed |
| `sandbox.leak_dispose_failed` | Disposal of a leaked sandbox failed |

Each event carries `{ name, ageMinutes, diskMb, reason }` in the structured log
fields. The `reason` is a stable classification code such as
`untracked_sandbox_age_threshold_exceeded` or
`untracked_sandbox_missing_creation_metadata`.

---

## API

### `GET /sandboxes/leaked`

Returns the list of sandboxes detected as leaked on the **most recent sweep**
and not yet successfully disposed. An empty array means no pending leaked
sandboxes remain from the last sweep; with `AutoDispose=true`, stale VMs may
have been detected and already purged.

```json
[
  {
    "name": "codeybox-a1b2c3d4e5f6",
    "createdAt": "2026-05-04T02:00:00+00:00",
    "ageMinutes": 127.3,
    "diskMb": null,
    "reason": "untracked_sandbox_age_threshold_exceeded",
    "providerId": "incus"
  }
]
```

### `GET /admin/sandbox-leaks`

Returns an operator summary for sandboxes detected as leaked and not yet
successfully disposed by the latest sweep.

```json
{
  "count": 1,
  "agesMinutes": [127.3],
  "leaks": [
    {
      "name": "codeybox-a1b2c3d4e5f6",
      "createdAt": "2026-05-04T02:00:00+00:00",
      "ageMinutes": 127.3,
      "diskMb": null,
      "reason": "untracked_sandbox_age_threshold_exceeded",
      "providerId": "incus"
    }
  ]
}
```

### `POST /sandboxes/leaked/{name}/dispose`

Operator-triggered dispose of a specific leaked sandbox. Works regardless of
the `AutoDispose` configuration. The sandbox must be present in the latest leak
snapshot, and the owning provider re-verifies ownership beside its destructive
operation. If more than one provider reports the same name, first read
`providerId` from `GET /sandboxes/leaked`, then pass it as the exact
`?providerId=...` query value. A name-only request is rejected as ambiguous.

- On success: `200 { "disposed": "<name>" }`
- On unknown name (not in latest leaked list): `404`
- On duplicate name without an exact `providerId`: `409`
- On invalid `providerId`: `400`
- On timeout (5 min): `504`
- On error: `500` with a generic message; provider diagnostics remain server-side

---

## Manual cleanup

If the reaper is unavailable, inspect the selected provider directly. Confirm
the resource's CodeyBox ownership before deleting one exact instance; do not
bulk-delete by a default prefix when prefixes or projects are configurable.

```bash
# Local Multipass inventory and one exact delete
multipass list
multipass delete --purge codeybox-a1b2c3d4e5f67

# Incus inventory in the configured dedicated project and one exact delete
incus --project codeybox list
incus --project codeybox delete codeybox-a1b2c3d4e5f67 --force
```

Baseline instances are **not** touched by the sandbox leak reaper. They have a
separate content-addressed inventory and baseline sweep; preserve them during
manual sandbox cleanup unless intentionally invalidating the bake cache.

---

## Expected vs. active sandbox tracking

The reaper determines whether a sandbox is expected from the active-ownership
snapshot supplied by its managed lifecycle provider. VM providers register
ownership before exposing a new instance and clear it after disposal. During a
Multipass/Incus cutover, lifecycle inventory retains the concrete backend ID so
duplicate names can be routed without broadcasting a destructive operation.

**After an orchestrator restart**, the in-memory set starts empty. This means
pre-existing ordinary instances are initially untracked unless a durable
suspended-sandbox mapping reserves them for startup recovery. The age threshold
prevents other prior-process instances from being classified as leaked
immediately: only instances older than `LeakAgeThreshold` are declared leaked
on the first sweep.
