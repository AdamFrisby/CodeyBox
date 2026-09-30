# BoxLite sandbox provider plugin

`CodeyBox.BoxLiteSandboxPlugin` contributes an embedded/local microVM backend.
A BoxLite daemon runs on the orchestrator host (or an adjacent host) and boots
one hardware-isolated microVM per work item from an OCI image; exec, file
transfer, snapshots, and lifecycle all go through the daemon's `…/v1` REST API.

**Provider kind:** `boxlite` — name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. The provider instance is constructed once and shared across
every member naming the kind.

## Isolation — read this first

This provider runs locally with hardware isolation (each sandbox is a
microVM with its own guest kernel, `SandboxIsolationLevel.DedicatedKernel`
for workload-trust routing), but it is still a plugin-contributed kind, so
the host classifies it `NotEnforced`, unconditionally. The plugin cannot
promote itself; no option, label, or return value changes the classification.
Promotion above `NotEnforced` is a separate in-tree change under review and
is explicitly out of scope for this plugin.

Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress
  and is refused for this kind (unplaceable, `enforced-egress`, naming
  `boxlite`). The provider additionally refuses a `ProfileName` that ever
  reaches it, rather than silently downgrading to the daemon restriction.
- The daemon restriction that *is* applied — `isolated` for a `Denied`
  policy, `restricted` plus a hostname allowlist otherwise — is **best-effort
  defence in depth only**: guest-side filtering on a VM whose agent runs as
  root is not a substitute for the host-enforced guarantee.

### Network-restriction evidence (for the future promotion decision)

What the provider can show today, and what it cannot:

- The daemon applies the restriction from VM boot (the create request
  carries it) and the provider re-asserts it after setup commands
  (bake-then-lock), so provisioning egress never leaks into the work phase.
- The restriction is expressed per VM in the daemon record and is visible in
  the `network` payload of every create in the recorded-shape fixture.
- What is **not** established: an unbypassable host-kernel drop path. The
  daemon documents its restriction as guest-network-namespace filtering;
  a root agent inside the guest is outside the evaluated threat model, and
  no packet-level test in this tree proves otherwise. Until that evidence
  exists and is reviewed in-tree, the kind stays `NotEnforced`.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.boxlite-sandbox" ],
      "Allowlist": [ "codeybox.boxlite-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "boxlite-pool",
            "ProviderKind": "boxlite",
            "Capacity": 4,
            "PreferenceScore": 60
          }
        ]
      }
    ]
  }
}
```

and the provider's own switch, under `CodeyBox:Plugins:codeybox.boxlite-sandbox`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.boxlite-sandbox": {
        "Enabled": true,
        "DaemonUrl": "https://127.0.0.1:8899",
        "ApiTokenEnvVar": "BOXLITE_API_TOKEN",
        "DefaultImage": "ghcr.io/codeybox/sandbox-base:latest",
        "NamePrefix": "codeybox-"
      }
    }
  }
}
```

All operational values are hot-reloadable options (see
`BoxLiteSandboxOptions`): image defaults, CPU/RAM/disk defaults, timeouts,
poll intervals, exec/file/archive bounds, snapshot prefix, and the
provisioning recheck backoff. Secrets never appear here — see Credentials.

## Host platforms

BoxLite is an embedded/local provider: the daemon runs on the orchestrator
host (or an adjacent host reachable over the network), so unlike the
Linux-only local VM backends it is not gated on the orchestrator OS —
plugin-contributed kinds are supported on every host OS and containment is
bounded by the `NotEnforced` classification instead. The daemon itself ships
builds for Linux, macOS, and Windows hosts; microVM hardware isolation
requires a host with KVM (Linux), Hypervisor.framework (macOS), or
WHPX/Hyper-V (Windows). Where the host cannot provide hardware acceleration
the daemon refuses VM creation, which surfaces as a `service-rejected`
infrastructure deferral — never as a diff verdict.

## Credentials

The API token comes from the credential chain — a host environment variable —
never from configuration. `ApiTokenEnvVar` names the variable (default
`BOXLITE_API_TOKEN`); the value is read per call, so rotation propagates
without a restart and the options record carries no secret material.
Provision it through the usual host secret path (vault agent, container
secrets). A missing variable fails `CreateAsync` loudly, naming the
variable — never silently falling back to another provider.

Every request carrying the token must go over `https`. A cleartext `http`
URL is refused unless the dev-only `AllowUnsafeHttp` option is set, and even
then only for loopback hosts (`localhost` / `127.0.0.1` / `::1`): remote
`http` URLs are refused unconditionally, so the token can never ride a
cleartext request to a remote host because of one operator edit.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — a BoxLite
member never exceeds its cap, and queued acquisitions wait for headroom
rather than overflowing onto the daemon. Live load reaches placement through
the existing admission wrappers (held permits + `SandboxLiveCounter`), so
least-loaded member selection stays accurate. Configure capacity to match the
host's real headroom (RAM/disk for concurrent microVMs) — the provider
cannot see daemon-side quota headroom, and quota exhaustion surfaces as a
deferred provisioning failure.

## What the provider implements

- **Lifecycle.** `POST /v1/vms` → poll to `running` → operator
  `SetupCommands` → re-assert the network restriction (bake-then-lock) →
  stage mounts. Disposal syncs writable mounts back, then deletes the VM
  unless a suspend/retain path preserved it.
- **Exec.** Each exec starts with an argv array plus base64
  environment/stdin and is polled to completion; incremental stdout/stderr
  deltas reach the caller's chunk callbacks as they arrive. Spec environment
  (plus the timing work-item id) merges under per-exec overrides, and
  `EnvironmentVariablesToUnset` removals win deterministically.
- **Cancellation / limits.** Cancelling the exec token kills the guest
  process (DELETE the exec) and surfaces `OperationCanceledException`.
  `MaxStdoutBytes`/`MaxStderrBytes` bound captured output;
  `KillOnOutputLimit` kills the guest process when a bound trips.
  `SandboxResourceLimits.WallClock` bounds each exec (exit 124 on timeout).
  CPU/memory/disk map to the VM record's `cpu`/`memoryMib`/`diskGib`.
- **Files.** Single files move through `PUT/GET /v1/vms/{id}/files`
  (bounded, absolute contained paths only). Directory mounts are staged in
  as validated tar.gz archives and synced back the same way: the returned
  archive is validated (entry count, expanded size, no links, path
  containment) and applied atomically (extract to staging dir → swap →
  remove old). Tmpfs mounts without a host source become plain guest
  directories — they are **not** tmpfs-backed; do not place secrets that
  require memory-only storage on them.
- **Persistence.** VM disks survive stop/pause/resume when `PersistentDisks`
  is set (the default); disposal always deletes the disk. Snapshots provide
  the durable baseline path below.
- **Snapshots / baselines.** `baseline-bake` is implemented via daemon
  snapshots: `EnsureBaselineImageAsync` bakes a scratch VM
  (`codeybox-bake-*`, deleted afterward) and snapshots it as
  `codeybox-baseline-<content-hash>`; `BaselineImageRef` on a spec creates
  directly from that snapshot. `ListBaselineImagesAsync` /
  `DisposeBaselineImageAsync` manage the prefix-scoped snapshot inventory.
- **Suspend/resume, retain/adopt, reconcile.** `suspend-resume` is declared:
  `SuspendAsync` pauses (the VM keeps its disk daemon-side),
  `ResumeSandboxAsync` resumes/starts paused/stopped VMs on host startup,
  `RetainForInfrastructureRecoveryAsync` stops the VM and stamps a SHA-256
  recovery-token hash label (the token itself never leaves the lease), and
  `ReconcileStuckSandboxesAsync` deletes managed VMs in stopped or wedged
  states that no live orchestrator mapping claims. Adopted-agent completion
  tails the conventional agent log plus its `.exit` marker.
- **Teardown.** `teardown` is declared: `StopAndPreserveAsync` stops without
  deleting, `DisablePreserveOnDispose` reverts to delete-on-dispose.

## Failure classification

Daemon-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class (`unreachable`, `unauthorized`, `quota-exhausted`,
  `throttled`, `service-rejected`, `server-error`) and a recheck interval —
  `Retry-After` honored for 429s; auth/quota classes wait longer so operators
  can fix credentials or capacity.
- Mid-exec transport failures return `ExecutionUnavailable` (infra), not a
  non-zero exit that could be mistaken for a work failure.
- Guest-influenced response bodies (exec snapshots, file/archive reads,
  adopt-log tails) are capped on the wire at the matching option-derived
  ceiling before the client buffers them; a body past the ceiling throws as
  infrastructure (the guest process is killed on the exec path), and daemon
  error bodies are truncated before they reach exception messages. Host
  memory per read stays O(cap) no matter how much the guest wrote.
- Setup-command non-zero exits are deterministic `InvalidOperationException`s —
  they are provisioning defects in the configured commands, not daemon
  outages.
- A failed create attempts a best-effort delete; when the delete cannot prove
  removal the deferral names the leak risk.

## What it costs

- **Host resources, not a service bill.** Each live VM pins its RAM plus a
  sparse disk image on the daemon host; size `SandboxMember.Capacity`
  against real headroom (a 4 GiB default VM × capacity 4 ≈ 16 GiB RAM).
- **OCI pulls.** First boot of an image pulls it to the daemon host;
  subsequent boots are copy-on-write. `DefaultImage` should name an image
  the daemon has pulled; baseline snapshots avoid re-provisioning per item.
- **No per-minute meter.** The daemon is operator-run infrastructure; there
  is no third-party usage charge. The cost of orphaned VMs is host disk/RAM
  until the leak reaper or `ReconcileStuckSandboxesAsync` collects them.

## What it does not implement

- **Port publishing.** `port-publishing` is not declared: the daemon's port
  forwarding needs an async lease handshake the synchronous
  `ISandboxPortPublisher`/`IRoutableSandbox` contract cannot express.
- **Disk guard.** The daemon reports no per-VM disk usage CodeyBox can
  enforce; `disk-guard` is not declared.
- **Cache seeding / detached batch.** Not implemented; not declared.
- **Graphical flavor.** Headless only; `Graphical` specs are refused.
- **Named network profiles.** Refused at placement and again in the
  provider — see Isolation above.

## Recorded-shape fixture

A real-service integration test cannot run in this suite — it needs a
BoxLite daemon binary, host virtualization (KVM/Hypervisor.framework/WHPX),
and OCI pull network — so `BoxLiteSandboxProviderTests` drives a
`FakeBoxLiteServer` that mirrors the daemon surface the provider uses (VM
lifecycle, exec start/poll/kill, files, archives, labels, network,
snapshots) with the response shapes in `BoxLiteApiClient` ("Wire shapes").
Run the suite against a real daemon by pointing `DaemonUrl` at it, setting
`ApiTokenEnvVar`, and enabling the plugin; the fake's shapes are the contract
to re-verify if the daemon API drifts.
