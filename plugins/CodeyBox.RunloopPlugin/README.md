# CodeyBox Runloop Devboxes provider plugin

Sandbox provider plugin (`kind: runloop`) that runs CodeyBox work items in
[Runloop Devboxes](https://docs.runloop.ai/docs/devboxes/overview): hosted
Linux VMs created on demand over the Runloop REST API, with disk snapshots
and suspend/resume. One project, one plugin, contributed through the plugin
trust model. **Off unless an operator enables it.**

## Containment posture — read this first

This is a **hosted** provider: the guest runs on infrastructure CodeyBox does
not control. The egress guarantee is enforced with nftables on a host
CodeyBox owns, which cannot apply here. The host therefore classifies every
plugin kind `NotEnforced`, and this plugin cannot promote itself — any
deployment requiring enforced egress refuses this provider with a reason.

What isolation this provider **does** give:

- Each work item gets a fresh cloud VM (separate guest kernel) with no shared
  filesystem with the orchestrator host or with other devboxes.
- Secrets travel as process environment inside the guest; the Runloop API
  token itself never enters the guest (it stays in the orchestrator process
  environment and the `Authorization` header).

What it **does not** give:

- No host-enforced egress filtering. Guest egress follows the Runloop
  account's network configuration, not the CodeyBox network profile. The
  provider refuses any sandbox naming a network profile, and placement never
  routes profiled work here — but an operator choosing this provider for
  unprofiled work is accepting Runloop-default egress, not a CodeyBox
  allowlist.
- No tmpfs semantics. Credential tmpfs mounts are refused outright (use
  environment variables); other tmpfs mounts need `AllowPersistentTmpfsDowngrade`
  and become persistent guest directories that survive in snapshots.
- Suspend is disk-only: running processes do not survive suspend/resume and
  must be restarted (the pipeline replays from its checkpoint, so this is
  expected, not data loss).

## What an operator must configure

1. A Runloop account and API key (`RUNLOOP_API_KEY` in the orchestrator
   process environment — never in a configuration file).
2. Allowlist **and** enable the plugin (both gates must pass; changing
   `Enabled` requires a host restart):

```json
{
  "CodeyBox": {
    "Plugins": {
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "Allowlist": ["codeybox.runloop"],
      "Enabled": ["codeybox.runloop"]
    },
    "Plugins:codeybox.runloop": {
      "ResourceSize": "MEDIUM",
      "KeepAliveSeconds": 3600
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          { "MemberId": "runloop-main", "ProviderKind": "runloop", "Capacity": 4 }
        ]
      }
    ]
  }
}
```

3. Acknowledge shared-kernel risk is **not** required (devboxes are
   dedicated-kernel VMs), but untrusted workloads in production still pass
   the normal workload-trust gate.

### Full knob reference (`CodeyBox:Plugins:codeybox.runloop`)

| Key | Default | Meaning |
|---|---|---|
| `ApiBaseUrl` | `https://api.runloop.ai` | Runloop REST base URL (https required; http only with `AllowUnsafeHttp`). |
| `TokenEnvironmentVariable` | `RUNLOOP_API_KEY` | Env var holding the bearer token. Only the *name* is configured; the value is read from the process environment at use time. |
| `NamePrefix` | `codeybox-` | Devbox name prefix (letter-led, `[a-z0-9-]`, ≤48 chars). |
| `BlueprintId` / `BlueprintName` | unset | Bake the toolchain into new devboxes (mutually exclusive with each other; `SnapshotId` likewise). |
| `SnapshotId` | unset | Restore new devboxes from a disk snapshot. |
| `ResourceSize` | `MEDIUM` | `X_SMALL`…`XX_LARGE`, or `CUSTOM_SIZE` with `CustomCpuCores`/`CustomMemoryGiB`/`CustomDiskGiB`. Per-spec CPU/memory/disk limits are advisory only. |
| `KeepAliveSeconds` | `3600` | Idle keep-alive requested at create (60…172800). |
| `WaitForRunningTimeoutSeconds` | `300` | Bound on waiting for `running` after create/resume. |
| `ExecPollIntervalSeconds` | `1` | Execution-status poll cadence. |
| `ApiTimeoutSeconds` | `30` | Per-request HTTP timeout. |
| `MaxExecOutputBytes` | `67108864` | Captured stdout/stderr cap per exec (host memory backstop). |
| `MaxStageFileBytes` / `MaxStageTotalBytes` / `MaxStageFileCount` | `48MiB` / `256MiB` / `5000` | Bounds for staging host mounts into the devbox. |
| `MaxReadBackBytes` | `48MiB` | Per-file cap for teardown sync-back. |
| `MaxCommandBytes` / `MaxEnvironmentBytes` / `MaxStdinBytes` | `512KiB` / `256KiB` / `1MiB` | Per-exec payload bounds. |
| `MaxListPages` | `10` | Inventory sweep bound. |
| `AllowPersistentTmpfsDowngrade` | `false` | Downgrade non-secret tmpfs mounts to persistent guest dirs. |
| `AllowUnsafeHttp` | `false` | Test-only: allow http API base for local mocks. |

## Repository setup

The pipeline's existing behaviour is preferred: the pipeline clones and
prepares the work tree with its own exec calls, and the provider only
materialises what the spec mounts describe. Host-path mounts are staged into
the devbox file-by-file (bounded; oversized or missing sources fail closed),
writable host mounts are synced back once at teardown, and `/work` is created
as a plain guest directory. The provider's own repo primitives (Runloop code
mounts, blueprint-baked checkouts) are **not** used, because a divergent
checkout would be a works-here-fails-there risk; bake a blueprint or snapshot
only to pre-install the toolchain, never to substitute the checkout.

## What it costs

Every `CreateAsync` provisions a billable cloud VM; `SuspendAsync` stops
compute but retains the disk snapshot (storage cost until shutdown);
snapshots persist and bill until deleted; shutdown releases compute but any
snapshots taken remain until deleted via the Runloop dashboard/API. Size work
with `ResourceSize`, `Capacity`, and prompt snapshot hygiene accordingly.

## What it cannot do

- Enforced-egress network profiles (refused at placement and at create).
- Graphical sandboxes (no display/VNC; refused).
- Baseline bake, cache seeding, disk guard, port publishing (not declared;
  placement refuses work requiring them).
- Live RAM across suspend (disk-only; processes restart).
- Large-file flows beyond the staging bounds above (fails closed with the
  limit named).
- Adopting a running agent process after resume (`WaitForAdoptedAgentCompletionAsync`
  reports nothing to adopt; the pipeline replays from its checkpoint).
