# microsandbox sandbox provider plugin

`CodeyBox.MicrosandboxPlugin` contributes a local-first microVM backend built on
[microsandbox](https://microsandbox.dev). A microsandbox server runs on the
orchestrator host (default loopback `https://127.0.0.1:5555`) and boots one
hardware-isolated microVM per work item from an OCI image; exec, file transfer,
snapshots, branches, and lifecycle all go through the server's `/v1` REST API.

**Provider kind:** `microsandbox` — name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. The provider instance is constructed once and shared across
every member naming the kind. No core or pipeline code special-cases this kind:
everything it needs goes through the `ISandboxProvider` and capability
abstractions, and an unknown/unavailable kind fails closed naming `microsandbox`
— it is never silently substituted with another provider.

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
  `microsandbox`). The provider additionally refuses a `ProfileName` that ever
  reaches it, rather than silently downgrading to the server restriction.
- The server restriction that *is* applied — `isolated` for a `Denied`
  policy, `restricted` plus a hostname allowlist otherwise — is **best-effort
  defence in depth only**: guest-side filtering on a VM whose agent runs as
  root is not a substitute for the host-enforced guarantee.

### Network-restriction evidence (for the future promotion decision)

What the provider can show today, and what it cannot:

- The server applies the restriction from sandbox boot (the create request
  carries it) and the provider re-asserts it after setup commands
  (bake-then-lock), so provisioning egress never leaks into the work phase.
- The restriction is expressed per sandbox in the server record and is visible
  in the `network` payload of every create in the recorded-shape fixture.
- What is **not** established: an unbypassable host-kernel drop path. The
  restriction is guest-network-namespace filtering; a root agent inside the
  guest is outside the evaluated threat model, and no packet-level test in
  this tree proves otherwise. Until that evidence exists and is reviewed
  in-tree, the kind stays `NotEnforced`.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.microsandbox-sandbox" ],
      "Allowlist": [ "codeybox.microsandbox-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "microsandbox-pool",
            "ProviderKind": "microsandbox",
            "Capacity": 4,
            "PreferenceScore": 60
          }
        ]
      }
    ]
  }
}
```

and the provider's own switch, under `CodeyBox:Plugins:codeybox.microsandbox-sandbox`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.microsandbox-sandbox": {
        "Enabled": true,
        "ServerUrl": "https://127.0.0.1:5555",
        "ApiKeyEnvVar": "MICROSANDBOX_API_KEY",
        "DefaultImage": "microsandbox/base:latest",
        "NamePrefix": "codeybox-"
      }
    }
  }
}
```

All operational values are hot-reloadable options (see
`MicrosandboxSandboxOptions`): image defaults, CPU/RAM/disk defaults, timeouts,
poll intervals, exec/file bounds, snapshot prefix, and the provisioning recheck
backoff. Secrets never appear here — see Credentials.

## Host platforms

microsandbox is a local-first provider: the server runs on the orchestrator
host (default loopback), so unlike the Linux-only local VM backends it is not
gated on the orchestrator OS — plugin-contributed kinds are supported on every
host OS and containment is bounded by the `NotEnforced` classification instead.
The microsandbox server ships for Linux, macOS, and Windows hosts; microVM
hardware isolation requires a host with KVM (Linux), Hypervisor.framework
(macOS), or WHPX/Hyper-V (Windows). Where the host cannot provide hardware
acceleration the server refuses sandbox creation, which surfaces as an
infrastructure deferral — never as a diff verdict.

## Credentials

The API key comes from the credential chain — a host environment variable —
never from configuration. `ApiKeyEnvVar` names the variable (default
`MICROSANDBOX_API_KEY`); the value is read per call, so rotation propagates
without a restart and the options record carries no secret material.
Provision it through the usual host secret path (vault agent, container
secrets). A missing variable fails `CreateAsync` loudly, naming the
variable — never silently falling back to another provider.

Every request carrying the key must go over `https`. A cleartext `http`
URL is refused unless the dev-only `AllowUnsafeHttp` option is set, and even
then only for loopback hosts (`localhost` / `127.0.0.1` / `::1`): remote
`http` URLs are refused unconditionally, so the key can never ride a
cleartext request to a remote host because of one operator edit.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — a
microsandbox member never exceeds its cap, and queued acquisitions wait for
headroom rather than overflowing onto the server. Live load reaches placement
through the existing admission wrappers (held permits + `SandboxLiveCounter`),
so least-loaded member selection stays accurate. Configure capacity to match
the host's real headroom (RAM/disk for concurrent microVMs) — the provider
cannot see server-side quota headroom, and quota exhaustion surfaces as a
deferred provisioning failure.

## What the provider implements

- **Lifecycle.** `POST /v1/sandboxes` → poll to `running` → operator
  `SetupCommands` → re-assert the network restriction (bake-then-lock) →
  stage mounts. Disposal syncs writable file mounts back, then deletes the
  sandbox unless a suspend/retain path preserved it.
- **Exec.** Each exec starts with an argv array plus environment/stdin and is
  polled to completion; incremental stdout/stderr deltas reach the caller's
  chunk callbacks as they arrive. Spec environment (plus the timing work-item
  id) merges under per-exec overrides, and
  `EnvironmentVariablesToUnset` removals win deterministically.
- **Cancellation / limits.** Cancelling the exec token kills the guest
  process and surfaces `OperationCanceledException`.
  `MaxStdoutBytes`/`MaxStderrBytes` bound captured output;
  `KillOnOutputLimit` kills the guest process when a bound trips.
  `SandboxResourceLimits.WallClock` bounds each exec (exit 124 on timeout).
  CPU/memory/disk map to the sandbox record's `cpu`/`memoryMib`/`diskGib`.
- **Files.** Single files move through `PUT/GET /v1/sandboxes/{name}/files`
  (bounded base64, absolute contained paths only, canonicalize-then-contain
  on both sides). Host file mounts are staged in at create; writable file
  mounts sync back atomically (staging temp file + move) on
  `SyncStateToHostAsync` and once more at disposal. Tmpfs mounts without a
  host source become plain guest directories — they are **not** tmpfs-backed;
  do not place secrets that require memory-only storage on them.
- **Snapshots / baselines.** `baseline-bake` is implemented via server
  snapshots: `EnsureBaselineImageAsync` bakes a scratch sandbox
  (`codeybox-*`, deleted afterward) and snapshots it as
  `codeybox-baseline-<profile>-<flavor>`; later specs pin that ref.
  `ListBaselineImagesAsync` / `DisposeBaselineImageAsync` manage the
  prefix-scoped snapshot inventory.
- **Suspend/resume, retain/adopt, reconcile.** `suspend-resume` is declared:
  `SuspendAsync` pauses (the sandbox keeps its disk server-side),
  `ResumeSandboxAsync` resumes/starts paused/stopped sandboxes on host
  startup, `RetainForInfrastructureRecoveryAsync` stops the sandbox and
  stamps a SHA-256 recovery-token hash label (the token itself never leaves
  the lease), and `ReconcileStuckSandboxesAsync` deletes managed sandboxes
  in transitional states that no live orchestrator mapping claims.
  Adopted-agent completion tails the conventional agent log plus its `.exit`
  marker.
- **Teardown.** `teardown` is declared: `StopAndPreserveAsync` stops without
  deleting, `DisablePreserveOnDispose` reverts to delete-on-dispose.

## Live branching and the existing sandbox concepts

Live branching is what makes this backend valuable for contract testing: one
work tree forks cheaply into parallel variants. It maps onto existing
concepts with no new pipeline abstraction:

- A branch (`POST /v1/sandboxes/{name}/branch`,
  `MicrosandboxSandboxProvider.BranchSandboxAsync`) is a **copy-on-write
  fork** that becomes an ordinary `ISandbox` handle with its own managed
  name, its own active-tracking entry, and the same teardown path as a
  created sandbox. It consumes one admission permit for its lifetime, exactly
  like a create.
- It is **not** a baseline image: baselines are named, reusable, content
  snapshots (`codeybox-baseline-*`); a branch is a live, writable twin that
  diverges immediately.
- It is **not** suspend/resume: the parent keeps running; both sides execute
  concurrently.
- It is **not** a second placement member: branches never change the catalog.
  Capacity planning must account for branch fan-out (N variants ≈ N permits)
  against the same member caps.

What does not map: the pipeline has no "variant set" phase — callers that
want parallel variants branch explicitly and dispose each handle. If a
first-class variant concept is ever needed, that is a pipeline change to
propose separately, not something this plugin invents.

## Failure classification

Server-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class (`unreachable`, `unauthorized`, `quota-exhausted`,
  `throttled`, `server-error`, `conflict`, `not-found`, `unexpected`) and a
  recheck interval, so the orchestrator requeues instead of failing the item.
  Quota-shaped 402/403/429 bodies (matching `quota`/`capacity`/`too many`)
  classify as `quota-exhausted`, distinguishing "the service said no" from
  "the work failed".
- Mid-exec transport failures return `ExecutionUnavailable` (infra), not a
  non-zero exit that could be mistaken for a work failure.
- Guest-influenced response bodies (exec polls, file reads, adopt-log tails)
  are capped on the wire at the matching option-derived ceiling before the
  client buffers them; a body past the ceiling throws as infrastructure, and
  server error bodies are truncated before they reach exception messages.
  Host memory per read stays O(cap) no matter how much the guest wrote.
- Setup-command non-zero exits are deterministic `InvalidOperationException`s —
  they are provisioning defects in the configured commands, not server
  outages.
- A failed create attempts a best-effort delete; when the delete cannot prove
  removal the deferral names the leak risk.

## What it costs

- **Host resources, not a service bill.** Each live sandbox pins its RAM plus
  a sparse disk image on the server host; size `SandboxMember.Capacity`
  against real headroom (a 4 GiB default sandbox × capacity 4 ≈ 16 GiB RAM).
  Branches share unmodified pages copy-on-write until they diverge.
- **OCI pulls.** First boot of an image pulls it to the server host;
  subsequent boots and branches are copy-on-write. `DefaultImage` should name
  an image the server has pulled; baseline snapshots avoid re-provisioning
  per item.
- **No per-minute meter.** The server is operator-run infrastructure; there
  is no third-party usage charge. The cost of orphaned sandboxes is host
  disk/RAM until the leak reaper or `ReconcileStuckSandboxesAsync` collects
  them.

## What it does not implement

- **Enforced egress.** Named network profiles are refused. The per-sandbox
  `isolated`/`restricted` server restriction is defence in depth, not the
  host nftables guarantee.
- **Disk guard.** The server reports no per-sandbox disk usage CodeyBox can
  enforce; `disk-guard` is not declared and placement will not route
  disk-guarded work here.
- **Cache seeding.** Not implemented; `cache-seeding` is not declared.
- **Port publishing.** The server exposes no host-port lease handshake the
  synchronous port-publisher contract can express; `port-publishing` is not
  declared.
- **Graphical flavor.** Only `Headless` sandboxes are supported; graphical
  specs throw `NotSupportedException`.

## Recorded-shape fixture

No live microsandbox server exists in CI (it needs a server binary plus host
virtualization: KVM / Hypervisor.framework / WHPX), so
`MicrosandboxSandboxProviderTests` drives the provider through a
`FakeMicrosandboxServer` (`HttpMessageHandler`) that mirrors the `/v1`
surface the client drives — sandbox lifecycle, exec start/poll/kill, files,
labels, network, snapshots, branch — with the request/response shapes the
client documents in its "Wire shapes" section. A test asserts the create
request shape field-for-field, so wire drift fails loudly. An operator with a
real server can promote the fixture by pointing the test options at it; until
then the fixture plus this document are the integration evidence.
