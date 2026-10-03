# Modal sandbox provider plugin

`CodeyBox.ModalSandboxPlugin` contributes a hosted sandbox backend for
[Modal Sandboxes](https://modal.com/docs/guide/sandboxes): cloud execution
with custom images, filesystem snapshots, high concurrency, and streaming
execution. Sandboxes are created on Modal-run infrastructure through its
control-plane API; exec output streams back over incremental polls that feed
the existing stdout broadcast.

**Provider kind:** `modal` — name it from a `SandboxClass` member's
`ProviderKind` and placement selects it like any built-in backend. The
provider instance is constructed once and shared across every member naming
the kind.

## Isolation — read this before enabling

This is a **hosted** provider: the guest runs on infrastructure CodeyBox does
not control. The egress guarantee CodeyBox gives for local VMs — nftables
allowlist drops enforced in a kernel CodeyBox owns — **cannot apply here**.

The host therefore classifies this kind `NotEnforced`, unconditionally. The
plugin cannot promote itself; no option, label, or return value changes the
classification. Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress
  and is refused for this kind (unplaceable, `enforced-egress`, naming
  `modal`). The provider additionally refuses a `ProfileName` that ever
  reaches it, and the guest's workload-trust routing treats the boundary as
  shared-kernel (see below).
- `AllowedHosts`, when set, is passed to the service as egress *intent*
  alongside the create metadata. That is best-effort defence in depth only —
  service-side controls on hardware you do not own are not a substitute for
  the host-enforced guarantee, and the provider documents the gap instead of
  claiming parity with a local VM.

What the provider does and does not give:

- A fresh sandbox per work item, torn down (terminated) on disposal; a
  preserve path snapshots the filesystem first so the disk survives
  service-side as a named snapshot (running processes do not — the pipeline
  replays from its checkpoint).
- The guest boundary is reported as `SandboxIsolationLevel.SharedKernel` for
  workload-trust routing: Modal schedules sandboxes as containers on shared
  workers, so untrusted workloads in production still pass the normal
  workload-trust gate. This is a conservative claim about the *guest
  boundary*, still independent of the egress classification.
- Everything — sandbox disk, snapshots, staged files — lives on Modal storage
  you do not control. Do not send workloads whose content or secrets you are
  unwilling to place on third-party infrastructure.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.modal" ],
      "Allowlist": [ "codeybox.modal" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "modal-pool",
            "ProviderKind": "modal",
            "Capacity": 64,
            "PreferenceScore": 40
          }
        ]
      }
    ]
  }
}
```

and the provider's own switch, under `CodeyBox:Plugins:codeybox.modal`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.modal": {
        "Enabled": true,
        "AppName": "codeybox",
        "ImageRef": "registry.example.com/codeybox-agent:latest",
        "NamePrefix": "codeybox-",
        "TokenIdEnvironmentVariable": "MODAL_TOKEN_ID",
        "TokenSecretEnvironmentVariable": "MODAL_TOKEN_SECRET"
      }
    }
  }
}
```

High member capacity is the point of this backend: `Capacity` may legitimately
be far above any local provider's. Size it to the account's real Modal quota —
the provider cannot see service-side headroom, and exhaustion surfaces as a
deferred provisioning failure, not a silent queue.

All options are hot-reloadable (re-read on every create):

| Key | Default | Notes |
|-----|---------|-------|
| `ApiBaseUrl` | `https://api.modal.com` | Absolute https required; http only with `AllowUnsafeHttp` on loopback. |
| `TokenIdEnvironmentVariable` / `TokenSecretEnvironmentVariable` | `MODAL_TOKEN_ID` / `MODAL_TOKEN_SECRET` | Names only — values come from the process environment (credential chain). |
| `AppName` | `codeybox` | Modal app sandboxes are created under. |
| `ImageRef` | _(none)_ | Custom image (registry ref or Modal image id) with the agent toolchain baked in. Null = service default image. |
| `SnapshotId` | _(none)_ | Filesystem snapshot new sandboxes restore from. Wins over `ImageRef` when both are set. |
| `NamePrefix` | `codeybox-` | Sandbox name prefix; also the ownership marker for inventory sweeps. |
| `CpuCount` / `MemoryMiB` / `IdleTimeoutSeconds` | `2` / `4096` / `600` | Per-sandbox service-side scheduling hints; spec limits override CPU/memory when set. |
| `WaitForRunningTimeout` | 5 min | How long create waits for `running`. |
| `ExecPollInterval` / `ApiTimeout` | 1 s / 30 s | Poll cadence and per-request timeout. |
| `MaxExecOutputBytes` | 64 MiB | Host-memory backstop per exec stream. |
| `MaxStageFileBytes` / `MaxStageTotalBytes` / `MaxStageFileCount` | 48 MiB / 256 MiB / 5000 | Mount-staging bounds, enforced before buffering. |
| `MaxReadBackBytes` | 48 MiB | Per-file teardown read-back bound. |
| `MaxCommandBytes` / `MaxEnvironmentBytes` / `MaxStdinBytes` | 512 KiB / 256 KiB / 1 MiB | Exec payload bounds. |
| `MaxListPages` | `10` | Inventory sweep page bound. |
| `AllowPersistentTmpfsDowngrade` | `false` | Downgrade non-secret tmpfs mounts to persistent guest dirs (refused until set). |
| `AllowUnsafeHttp` | `false` | Test-only escape hatch for local mock servers; loopback only. |

A spec's `BaselineImageRef` pin is honored as the restore snapshot for that
create (it wins over the configured `SnapshotId`); when no pin is set the
create falls back to `SnapshotId`, then `ImageRef`. Per-spec CPU/memory are
advisory scheduling hints; disk limits have no service knob and are ignored.

## Credentials

The token id and secret come from the credential chain — host environment
variables — never from configuration files. `TokenIdEnvironmentVariable` and
`TokenSecretEnvironmentVariable` name the variables (defaults
`MODAL_TOKEN_ID` / `MODAL_TOKEN_SECRET`); the values are resolved at use time,
so rotation propagates without a restart and the options record carries no
secret material. Provision them through the usual host secret path (vault
agent, container secrets). A missing variable fails `CreateAsync` loudly,
naming the variable — never silently falling back to another provider — and
no request URL, body, or log line carries the secret (the token travels in
`X-Modal-Token-*` headers).

The control-plane URL must be `https`. A cleartext `http` URL is refused
unless the test-only `AllowUnsafeHttp` option is set, and even then only for
loopback hosts (`localhost` / `127.0.0.1` / `::1`): remote `http` URLs are
refused unconditionally, so the API secret can never ride a cleartext request
to a remote host because of one operator edit.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — a Modal
member never exceeds its cap, and queued acquisitions wait for headroom rather
than overflowing onto the service. Live load reaches placement through the
existing admission wrappers (held permits + `SandboxLiveCounter`), so
least-loaded member selection stays accurate across members sharing the kind.

## What the provider implements

- **Lifecycle.** `POST /v1/sandboxes` → poll to `running` → stage mounts.
  Disposal syncs writable mounts back once, then terminates — unless a
  preserve path ran first, in which case disposal is a no-op. A cancelled
  create terminates best-effort and proves removal; when removal cannot be
  proven the deferral names the leak risk.
- **Exec with streamed output.** Each exec dispatches a shell command that
  delivers the environment (base64-encoded values, so no quoting edge case
  can break out of the preamble), pipes bounded stdin, `cd`s to the working
  directory, then runs the argv. Output is polled incrementally
  (`stdout_after`/`stderr_after` offsets) and each delta feeds the exec's
  chunk callbacks — the existing stdout broadcast, not a parallel mechanism.
  `MaxStdoutBytes`/`MaxStderrBytes` (or the retained tail for
  `StreamOutputWithoutKill`) bound host memory; `KillOnOutputLimit` kills the
  remote exec when a bound trips. `SandboxResourceLimits.WallClock` bounds
  each exec (exit `124`, not infra). Secret-bearing execs stage the merged
  environment as a sourceable file and source-then-delete it, so values never
  enter host-visible command argv; a staging failure fails the exec as
  infrastructure rather than falling back to inline transport.
- **Cancellation.** Cancelling the exec token kills the remote exec and
  propagates the cancellation (never converted into a deferral).
- **Files.** `WriteFileAsync`/`ReadFileAsync` move UTF-8 text over the files
  endpoints with guest-path validation (absolute, bounded, no escapes) and
  byte bounds enforced before buffering. Host mounts are staged in at create
  (bounded file count/total/single-file size, no symlinks or devices).
  Writable host mounts sync back at `SyncStateToHostAsync` and once more at
  disposal: guest files are listed with a bounded `find`, validated per entry
  (containment in the mount, count/size budgets), and applied atomically
  (staging dir → swap for directories, temp-file rename for single files).
  There is no tmpfs: non-secret tmpfs mounts are refused until
  `AllowPersistentTmpfsDowngrade` explicitly downgrades them; credential
  mounts are always refused (see below).
- **Snapshots.** `teardown` is declared: `StopAndPreserveAsync` snapshots the
  filesystem service-side and then terminates the sandbox (the snapshot, not
  running processes, is what survives); `DisablePreserveOnDispose` reverts to
  terminate-on-dispose. Restoring happens at create time via `SnapshotId` /
  `BaselineImageRef`, not via a resume path.

## Failure classification

Service-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class (`unreachable`, `unauthorised`, `throttled`,
  `quota-exhausted`, `server-error`, …) and a recheck interval —
  `Retry-After` is honored for 429s; auth/quota classes wait longer so
  operators can fix credentials or capacity.
- Mid-exec transport or observation failures return `ExecutionUnavailable`
  (infra), not a non-zero exit that could be mistaken for a work failure. A
  completed exec that yields no observable exit code is likewise unavailable,
  not a pass.
- Non-secret staging defects (missing mount source, over-budget payloads) are
  deterministic errors, not deferrals — they are provisioning defects in the
  request, not service outages.

## What it does not implement

- **Baseline bake.** `baseline-bake` is not declared: Modal has no bake
  primitive in this provider — the toolchain arrives via the operator-baked
  `ImageRef` / `SnapshotId`, and placement refuses bake work rather than
  failing deep inside a phase.
- **Suspend/resume.** Not declared: Modal offers filesystem snapshots, not
  RAM checkpoints; preserve is snapshot-and-terminate and there is no resume
  path to adopt. Recovery leases are explicitly refused (never silently
  substituted with a fresh sandbox).
- **Disk guard / cache seeding / port publishing.** Not implemented; not
  declared.
- **File-backed agent credentials.** The sandbox implements
  `IRejectsFileBackedAgentCredentials`: the guest disk (and its snapshots)
  persists on third-party infrastructure, so credential files would outlive
  the work item. Agents needing file-materialised credentials cannot run on
  this kind — route them to an enforced provider.
- **Graphical flavor.** Refused: Modal sandboxes expose no display server.

## Operator notes / costs

- Every live sandbox burns Modal quota (compute on the account);
  `IdleTimeoutSeconds` bounds the cost of idle sandboxes and snapshots add
  snapshot storage. A crashed host can orphan running sandboxes — sweep
  `codeybox-*` names in the Modal dashboard.
- Bake the agent CLIs into `ImageRef` (see `docs/concepts/agents.md`: a kind
  without its binary on the image fails every dispatch with exit 127).
  Per-sandbox provisioning is intentionally absent — slow setup belongs in the
  image, not in the create path.
- Unprofiled work only: any named network profile is refused at placement and
  again at create. `AllowedHosts` is recorded intent, not enforcement.

## Verification without a live service (and the live gate)

A live test needs funded `MODAL_TOKEN_ID` / `MODAL_TOKEN_SECRET`, creates
billable cloud sandboxes, and requires egress to api.modal.com — none
available in CI. In its place:

- `FakeModalHandler` (`tests/CodeyBox.Tests/FakeModalHandler.cs`) mirrors the
  recorded request/response shapes the client parses and backs every
  provider test in `ModalSandboxProviderTests.cs` (lifecycle, streaming,
  cancellation, limits, secrets, preserve, sync-back, capabilities,
  classification, capacity, credentials).
- `tests/CodeyBox.Tests/Fixtures/modal/*.json` plus `ModalRecordedShapeTests`
  pin the wire shapes field-by-field so contract drift fails loudly in tests,
  not silently inside a phase.
- `ModalIntegrationTests` runs the real create/exec/file/snapshot/preserve
  flow when `CODEYBOX_RUN_MODAL_INTEGRATION=1` with both credential variables
  set (tagged `requires_modal`, skipped otherwise).

Two drift risks worth flagging for operators: Modal's control plane is
SDK-driven and offers no stable public REST sandbox contract, so the endpoint
shapes and the `X-Modal-Token-*` header mapping are transcribed from SDK/CLI
traffic at the time of writing — re-validate them against a live run before
trusting this provider in production, and treat a shape change as a provider
bug, not a pipeline bug.
