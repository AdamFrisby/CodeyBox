# Daytona sandbox provider plugin

`CodeyBox.DaytonaSandboxPlugin` contributes a hosted sandbox backend for
[Daytona](https://www.daytona.io). Sandboxes are created on Daytona-run
infrastructure through its REST control plane; exec and file transfer go
through each sandbox's toolbox daemon over the toolbox proxy.

**Provider kind:** `daytona` — name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. The provider instance is constructed once and shared across
every member naming the kind.

## Isolation — read this before enabling

This is a **hosted** provider: the guest runs on infrastructure CodeyBox does
not control. The egress guarantee CodeyBox gives for local VMs — nftables
allowlist drops enforced in a kernel CodeyBox owns — **cannot apply here**.

The host therefore classifies this kind `NotEnforced`, unconditionally. The
plugin cannot promote itself; no option, label, or return value changes the
classification. Consequences, enforced by placement:

- Any acquisition that names a **network profile** requires enforced egress and
  is refused for this kind (unplaceable, `enforced-egress`, naming `daytona`).
  The provider additionally refuses a `ProfileName` that ever reaches it.
- An empty `AllowedHosts` policy is pushed to the sandbox as Daytona's
  `networkBlockAll`; a non-empty set becomes a service-side `domainAllowList`.
  Both are **best-effort defence in depth only** — service-side controls on
  hardware you do not own are not a substitute for the host-enforced
  guarantee.

What the provider does give:

- A fresh sandbox per work item, torn down on disposal.
- `sandboxClass` selects Daytona's isolation class: the default is
  container-based (shared runner kernel — `SandboxIsolationLevel.SharedKernel`);
  setting `SandboxClass=linux-vm` requests Daytona's VM-isolated class and the
  provider reports `DedicatedKernel` for workload-trust routing. This is a
  real isolation claim about the *guest boundary*, still independent of the
  egress classification.
- Everything — sandbox disk, snapshots, retained sandboxes — lives on Daytona
  storage you do not control. Do not send workloads whose content or secrets
  you are unwilling to place on third-party infrastructure.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.daytona-sandbox" ],
      "Allowlist": [ "codeybox.daytona-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "daytona-pool",
            "ProviderKind": "daytona",
            "Capacity": 4,
            "PreferenceScore": 40
          }
        ]
      }
    ]
  }
}
```

and the provider's own switch, under `CodeyBox:Plugins:codeybox.daytona-sandbox`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.daytona-sandbox": {
        "Enabled": true,
        "ApiUrl": "https://app.daytona.io/api/",
        "ToolboxProxyUrl": "https://proxy.app.daytona.io/toolbox/",
        "ApiKeyEnvVar": "DAYTONA_API_KEY",
        "OrganizationId": "org_abc",
        "DefaultSnapshot": "codeybox-base",
        "NamePrefix": "codeybox-"
      }
    }
  }
}
```

## Credentials

The API key comes from the credential chain — a host environment variable —
never from configuration. `ApiKeyEnvVar` names the variable (default
`DAYTONA_API_KEY`); the value is read per call, so rotation propagates without
a restart and the options record carries no secret material. Provision it
through the usual host secret path (vault agent, container secrets). A missing
variable fails `CreateAsync` loudly, naming the variable — never silently
falling back to another provider.

`OrganizationId` is optional and non-secret; it is sent as the
`X-Daytona-Organization-ID` header on every API call.

Every endpoint that carries the API key — the configured `ApiUrl` /
`ToolboxProxyUrl` and any per-sandbox `toolboxProxyUrl` the service returns —
must be `https`. A cleartext `http` URL is refused unless the dev-only
`AllowUnsafeHttp` option is set, and even then only for loopback hosts
(`localhost` / `127.0.0.1` / `::1`): remote `http` URLs are refused
unconditionally, so the API key can never ride a cleartext request to a
remote host because of one operator edit.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — a Daytona
member never exceeds its cap, and queued acquisitions wait for headroom rather
than overflowing onto the service. Live load reaches placement through the
existing admission wrappers (held permits + `SandboxLiveCounter`), so
least-loaded member selection stays accurate. Configure capacity to match the
organization's real Daytona quota — the provider cannot see service-side quota
headroom, and quota exhaustion surfaces as a deferred provisioning failure.

## What the provider implements

- **Lifecycle.** `POST /api/sandbox` → poll to `started` → operator
  `SetupCommands` → apply egress intent → stage mounts. Disposal deletes the
  sandbox unless a suspend/retain path preserved it. `AutoDeleteInterval` is
  set as a service-side safety net for sandboxes orphaned by a crashed host
  (set to 0 to disable — you take over leak management).
- **Exec.** Each exec runs in a fresh toolbox session: a bootstrap shell
  script delivers the environment (base64 `KEY=value` lines) and stdin via
  byte-counted `dd` reads, `cd`s to the working directory, then runs the argv.
  Output streams back over the follow-WebSocket (`…/logs?follow=true`),
  demuxed on the daemon's `01 01 01`/`02 02 02` channel prefixes. Daemons
  without WS follow fall back to polling the command record plus a one-shot
  logs snapshot.
- **Cancellation / limits.** Cancelling the exec token deletes the session
  (killing the remote command). `MaxStdoutBytes`/`MaxStderrBytes` bound
  streamed output; `KillOnOutputLimit` kills the remote command when a bound
  trips. `SandboxResourceLimits.WallClock` bounds each exec. CPU/memory/disk
  map to the sandbox record's `cpu`/`memory`/`disk` (GiB, rounded up).
- **Files.** Host mounts are staged in at create (tar.gz/base64 over the exec
  input channel). Writable host mounts are synced back by
  `SyncStateToHostAsync` and once more at disposal — the extracted archive is
  validated (entry count, expanded size, no links, path containment) and
  applied atomically (extract to staging dir → swap → remove old). Daytona has
  no tmpfs; non-credential tmpfs requests degrade to a persistent directory
  with a warning.
- **Snapshots / baselines.** `baseline-bake` is implemented via Daytona
  snapshots: `EnsureBaselineImageAsync` bakes a scratch sandbox
  (`codeybox-bake-*`, deleted afterward) and snapshots it as
  `codeybox-baseline-<content-hash>`; `BaselineImageRef` on a spec creates
  directly from that snapshot. `ListBaselineImagesAsync`/`DisposeBaselineImageAsync`
  manage the prefix-scoped snapshot inventory.
- **Suspend/resume, retain/adopt, reconcile.** `suspend-resume` is declared:
  `SuspendAsync` pauses (Daytona pause keeps the sandbox service-side),
  `ResumeSandboxAsync` starts paused/stopped sandboxes on host startup,
  `RetainForInfrastructureRecoveryAsync` stops the sandbox and stamps a
  SHA-256 recovery-token hash label (the token itself never leaves the lease),
  and `ReconcileStuckSandboxesAsync` deletes managed sandboxes in suspended or
  wedged states that no live orchestrator mapping claims.
- **Teardown.** `teardown` is declared: `StopAndPreserveAsync` stops without
  deleting, `DisablePreserveOnDispose` reverts to delete-on-dispose.

## Failure classification

Service-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class (`unreachable`, `unauthorized`, `quota-exhausted`,
  `throttled`, `service-rejected`, `server-error`) and a recheck interval —
  `Retry-After` honored for 429s; auth/quota classes wait longer so operators
  can fix credentials or capacity.
- Mid-exec transport failures return `ExecutionUnavailable` (infra), not a
  non-zero exit that could be mistaken for a work failure.
- Setup-command non-zero exits are deterministic `InvalidOperationException`s —
  they are provisioning defects in the configured commands, not service
  outages.
- A failed create attempts a best-effort delete; when the delete cannot prove
  removal the deferral names the leak risk.

## What it does not implement

- **Port publishing.** `port-publishing` is not declared: Daytona's preview
  URL flow requires minting an access token (`POST …/preview-access`), which
  is async — the synchronous `ISandboxPortPublisher`/`IRoutableSandbox`
  contract cannot express that handshake honestly.
- **File-backed agent credentials.** The sandbox implements
  `IRejectsFileBackedAgentCredentials`: without a tmpfs the guest root fs is
  service-side persistent storage, so credential files would outlive the work
  on infrastructure CodeyBox does not own. Agents needing file-materialised
  credentials (Claude subscriptions, Cursor, opencode, …) cannot run on this
  kind — route them to an enforced provider.
- **Disk guard.** Daytona reports no per-sandbox disk usage CodeyBox can
  enforce; `disk-guard` is not declared.
- **Cache seeding / detached batch.** Not implemented; not declared.
- **tmpfs.** Downgraded to a persistent directory for non-credential mounts;
  refused for credential mounts.

## Operator notes / costs

- Every live sandbox burns Daytona quota (CPU/memory/disk on the account).
  Baseline snapshots add snapshot storage. `AutoStopInterval`/`AutoDeleteInterval`
  bound the cost of crashes/leaks.
- `Target` (region) is optional; unset = service default.
- `AutoStopIntervalMinutes` should usually stay unset/null: a service-stopped
  sandbox mid-run reads as an execution outage to the progress watchdogs.
- `SetupCommands` run once per create before the egress lock — this is where
  an operator installs agent CLIs etc. For speed, prefer
  `BaselineSourceImage`+`SetupCommands` baked once into a baseline snapshot
  (which `baseline-bake` then reuses) over per-sandbox provisioning.

## Recorded-shape fixture (no live integration test)

A real-service test needs a Daytona API key, organization quota, and outbound
network — none available in CI. In its place, `FakeDaytonaServer` in
`DaytonaSandboxProviderTests.cs` mirrors the recorded request/response shapes
from Daytona's OpenAPI spec (`/api/sandbox`, `/api/snapshots`,
`…/process/session`, `…/command/{id}/logs`, `…/files/*`) and the fake sockets
replay the toolbox log-stream framing (`01 01 01`/`02 02 02` channel
prefixes). Two stale-format risks worth flagging for operators: the `exec`
endpoint response field (`cmdId`) and the session-input body field (`data`)
are taken from Daytona's SDK, not from a published stable contract.
