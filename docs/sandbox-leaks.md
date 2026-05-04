# Sandbox Leak Detection

The orchestrator runs a periodic background sweep (`SandboxLeakReaper`) that
detects — and optionally disposes — Multipass VMs that outlived their work item.
These "leaked" sandboxes accumulate when the orchestrator crashes mid-disposal
and are the primary cause of the host running out of memory and disk over time.

---

## What counts as a leak

A Multipass VM is classified as leaked when **all three** conditions hold:

1. Its name starts with `codeybox-` (the orchestrator's sandbox prefix).
2. The current orchestrator process has **no in-memory record** of having created it.
   After a normal restart, this is initially empty, so the age threshold (below)
   guards against false positives.
3. Its creation timestamp — derived from the staging directory mtime — is **older
   than `LeakAgeThreshold`** (default 30 minutes).

A sandbox that is mid-way through the VM-launch → clone → mount → start sequence
is typically less than 10 minutes old. The 30-minute threshold is a conservative
safety margin: it is unlikely that a legitimately active sandbox would be both
untracked *and* over 30 minutes old.

Sandboxes for which the creation time cannot be determined (staging directory
missing) are **not** declared leaked — the reaper is conservative by design.

### Providers

| Provider | Leaks tracked? | Why |
|---|---|---|
| `multipass` | **Yes** | VMs persist as KVM guests; `multipass list` reports them after a crash |
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
      "AutoDispose": false
    }
  }
}
```

| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enable or disable the sweep entirely |
| `CheckInterval` | `00:15:00` (15 min) | How often to run the leak scan |
| `LeakAgeThreshold` | `00:30:00` (30 min) | Minimum age before a non-active sandbox is declared leaked |
| `AutoDispose` | `false` | When true, automatically purge each detected leak |

### AutoDispose

`AutoDispose` defaults to **false**. The first time you see leaked sandboxes, you
may want to investigate the cause before enabling automatic cleanup. Once you
understand the failure mode, set `AutoDispose: true` to have the reaper call
`multipass delete --purge <name>` on each detected leak.

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

Each event carries `{ name, ageMinutes, diskMb }` in the structured log fields.

---

## API

### `GET /sandboxes/leaked`

Returns the list of sandboxes detected as leaked on the **most recent sweep**.
An empty array means no leaks were detected on the last sweep, not that no leaks
could exist (e.g., the reaper may not have run yet).

```json
[
  {
    "name": "codeybox-a1b2c3d4e5f6",
    "createdAt": "2026-05-04T02:00:00+00:00",
    "ageMinutes": 127.3,
    "diskMb": null
  }
]
```

### `POST /sandboxes/leaked/{name}/dispose`

Operator-triggered dispose of a specific leaked sandbox. Works regardless of the
`AutoDispose` configuration. The `name` parameter **must** start with
`codeybox-` — requests for other names are rejected with `400` to prevent
accidental deletion of non-CodeyBox VMs on the host.

On success: `200 { "disposed": "<name>" }`  
On timeout (5 min): `504`  
On error: `500` with the error message

---

## Manual cleanup

If the reaper is not available (e.g., before upgrading to this version), clean up
leaked VMs manually:

```bash
# List all codeybox-* VMs
multipass list | grep codeybox-

# Delete a specific VM
multipass delete --purge codeybox-a1b2c3d4e5f67

# Delete all codeybox-* VMs at once (use with caution)
multipass list --format=json \
  | jq -r '.list[].name | select(startswith("codeybox-"))' \
  | xargs -r multipass delete --purge
```

Note: `cb-baseline-*` VMs are **not** touched by the reaper — they are baseline
images used to speed up VM cloning and should be preserved.

---

## Expected vs. active sandbox tracking

The reaper determines whether a sandbox is "expected" by checking the current
orchestrator process's **in-memory active set**, which is populated in
`MultipassSandboxProvider.CreateAsync` and cleared in `DisposeAsync`.

**After an orchestrator restart**, the in-memory set starts empty. This means
all pre-existing `codeybox-*` VMs are initially untracked. The age threshold
prevents them from being classified as leaked immediately: only VMs that existed
before the restart AND are older than `LeakAgeThreshold` are declared leaked on
the first sweep.

If a future PR lands the worker-registry feature (tracking sandbox names
alongside work-item IDs in the database), the reaper can be enhanced to use that
as a more durable source of "expected" sandboxes instead of the in-memory set.
Until then, the in-memory approach is the correct and complete implementation.
