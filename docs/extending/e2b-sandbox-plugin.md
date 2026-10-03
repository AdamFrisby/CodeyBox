# E2B sandbox provider plugin

`CodeyBox.E2bSandboxPlugin` contributes a hosted sandbox backend for
[E2B](https://e2b.dev). Sandboxes are created on E2B-run infrastructure
through its REST control plane; exec and file transfer go through each
sandbox's envd daemon.

**Provider kind:** `e2b` — name it from a
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
  is refused for this kind (unplaceable, `enforced-egress`, naming `e2b`).
  The provider additionally refuses a `ProfileName` that ever reaches it.
- There is no service-side network control to push a policy to: guest egress
  follows the E2B account's network configuration, full stop. An operator
  choosing this provider is accepting E2B-default egress, not a CodeyBox
  allowlist.

What the provider does give:

- A fresh microVM per work item, torn down on disposal.
- `SandboxIsolationLevel.DedicatedKernel`: E2B guests are Firecracker
  microVMs (separate guest kernel). This is a real isolation claim about the
  *guest boundary*, still independent of the egress classification.
- Secret hygiene in depth: the long-lived API key (`X-API-KEY`) rides only
  control-plane traffic from the orchestrator process; per-sandbox data-plane
  traffic carries the sandbox-scoped `envdAccessToken` (`X-Access-Token`),
  and secret-bearing exec environments are staged as a guest file and sourced
  — values never enter the command payload the hosted control plane retains.
- Everything — sandbox disk, snapshots, retained sandboxes — lives on E2B
  storage you do not control. Do not send workloads whose content or secrets
  you are unwilling to place on third-party infrastructure.

## Enabling the plugin

The plugin is disabled by default. An operator enables it explicitly:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Enabled": [ "codeybox.e2b-sandbox" ],
      "Allowlist": [ "codeybox.e2b-sandbox" ]
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          {
            "MemberId": "e2b-pool",
            "ProviderKind": "e2b",
            "Capacity": 4,
            "PreferenceScore": 40
          }
        ]
      }
    ]
  }
}
```

and the provider's own switch, under `CodeyBox:Plugins:codeybox.e2b-sandbox`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.e2b-sandbox": {
        "Enabled": true,
        "TemplateId": "base",
        "SandboxTimeoutSeconds": 3600
      }
    }
  }
}
```

## Credentials

The API key comes from the credential chain — a host environment variable —
never from configuration. `ApiKeyEnvVar` names the variable (default
`E2B_API_KEY`); the value is read per call, so rotation propagates without
a restart and the options record carries no secret material. Provision it
through the usual host secret path (vault agent, container secrets). A missing
variable fails `CreateAsync` loudly, naming the variable — never silently
falling back to another provider.

Every endpoint that carries the API key — the configured `ApiBaseUrl` and
every derived per-sandbox envd host — must be `https`. A cleartext `http`
URL is refused unless the dev-only `AllowUnsafeHttp` option is set, and even
then only for loopback hosts: remote `http` URLs are refused unconditionally,
so the API key can never ride a cleartext request to a remote host because of
one operator edit.

## Capacity and placement

`SandboxMember.Capacity` is honored by the member admission gate — an E2B
member never exceeds its cap, and queued acquisitions wait for headroom rather
than overflowing onto the service. The provider creates sandboxes only inside
`CreateAsync` (one service sandbox per acquisition, no hidden extras) and
reports live load through `IActiveSandboxProvider.SnapshotActiveSandboxes`
plus the existing admission wrappers (held permits + `SandboxLiveCounter`),
so least-loaded member selection stays accurate. Configure capacity to match
the team's real E2B quota — the provider cannot see service-side quota
headroom, and quota exhaustion surfaces as a deferred provisioning failure.

## What the provider implements

- **Lifecycle.** `POST /v2/sandboxes` → poll to `running` → envd health check
  → stage mounts → extend timeout. Disposal syncs writable mounts back once,
  then deletes the sandbox. A failed create attempts a best-effort delete;
  when the delete cannot prove removal the deferral names the leak risk.
- **Exec.** Each exec runs synchronously through the envd command gateway: a
  bootstrap shell preamble delivers the environment (base64 `KEY=value`
  exports), `cd`s to the working directory, pipes bounded stdin, then runs
  the argv. Output is captured fully (bounded) per the `ISandbox` contract
  and handed to the pipeline's exec pipe. `MaxStdoutBytes`/`MaxStderrBytes`
  truncate and flag over-cap output; `SandboxResourceLimits.WallClock` bounds
  each exec via the per-exec `timeoutMs`.
- **Cancellation / limits.** Cancelling the exec token aborts the client
  wait; the guest command is bounded by `timeoutMs` but is not guaranteed
  dead on cancel (no server-side kill endpoint exists in the recorded
  contract). `KillActiveExecsAsync` cancels all tracked client waits.
- **Files.** Host mounts are staged in at create (bounded file count and
  bytes). Writable host mounts are synced back by `SyncStateToHostAsync` and
  once more at disposal, with guest/host path containment validated both
  ways. E2B has no tmpfs: credential tmpfs mounts are refused outright (use
  environment variables); other tmpfs mounts need
  `AllowPersistentTmpfsDowngrade` and become persistent guest directories.
- **Persistence.** `suspend-resume` is declared: `SuspendAsync` pauses the
  sandbox service-side (disk persists, processes do not survive — the
  pipeline replays from its checkpoint), `ResumeSandboxAsync` reconnects
  paused sandboxes on host startup, and `ExtendTimeoutAsync` keeps long runs
  alive. `CreateSnapshotAsync` mints a named snapshot (artifacts) that
  survives the sandbox.
- **Preview URLs.** `port-publishing` is declared so placement can require
  it, but it is never enabled by default: `EnablePreviewUrls` (default
  false) plus a non-empty `AllowedPreviewPorts` gates actual minting, and
  `PublishPort` refuses anything else. A published port returns
  `https://{port}-{sandboxId}.{domain}/` — a public-internet URL. Treat it
  as a deliberate exposure, not a default.
- **Teardown.** `teardown` is declared: `StopAndPreserveAsync` pauses without
  deleting, `DisablePreserveOnDispose` reverts to delete-on-dispose.

## Failure classification

Service-side refusals are never reported as verdicts on a work item's diff:

- Create/provisioning failures map to `SandboxProvisioningDeferredException`
  with an error class (`unreachable`, `unauthorised`, `quota-exhausted`,
  `throttled`, `server-error`, …) and a recheck interval — 2 minutes for
  throttling, 5 minutes for auth/quota so operators can fix credentials or
  capacity.
- Mid-exec transport failures throw `SandboxExecutionUnavailableException`
  (infra), not a non-zero exit that could be mistaken for a work failure.
- A non-zero guest exit code is an ordinary exec result — never a service
  failure.

## What it does not implement

- **File-backed agent credentials.** The sandbox implements
  `IRejectsFileBackedAgentCredentials`: without a tmpfs the guest root fs is
  service-side persistent storage, so credential files would outlive the work
  on infrastructure CodeyBox does not own. Agents needing file-materialised
  credentials (Claude subscriptions, Cursor, opencode, …) cannot run on this
  kind — route them to an enforced provider.
- **Baseline bake, cache seeding, disk guard.** Not implemented; not
  declared. Placement refuses work requiring them.
- **Detached batch launch.** Not implemented; execs stay attached.
- **tmpfs.** Refused for credential mounts; downgraded to a persistent
  directory for others only with `AllowPersistentTmpfsDowngrade`.
- **Graphical sandboxes.** No display/VNC; refused.

## Operator notes / costs

- Every live sandbox burns E2B quota (vCPU-seconds and memory on the
  account); `SandboxTimeoutSeconds` bounds the idle tail, and the service
  auto-pauses on expiry. Snapshots add snapshot storage until deleted.
- `TemplateId` selects the base image; bake the toolchain into a custom
  template for speed rather than provisioning per sandbox.
- `SandboxDomain` must be one of `e2b.app`, `e2b.dev`, `e2b.pro`
  (per-sandbox hosts are `{port}-{sandboxId}.{domain}`); `EnvdPort`
  defaults to `49983`.

## Recorded-shape fixture (no live integration test in CI)

A real-service test needs an E2B API key, funded quota, and outbound
network — none available in CI. In its place, `FakeE2bHandler` in
`E2bSandboxProviderTests.cs` mirrors the request/response shapes verified
against the E2B JS SDK (`e2b@2.52.0`: `POST /v2/sandboxes`,
`GET /sandboxes/{id}`, `GET /v2/sandboxes`, `DELETE /sandboxes/{id}`,
`POST /sandboxes/{id}/pause`, `POST /v2/sandboxes/{id}/connect`,
`POST /sandboxes/{id}/timeout`, `POST /sandboxes/{id}/snapshots`;
envd `GET /health`, `GET /files`, `POST /files` with the `X-API-KEY` /
`X-Access-Token` headers; preview host `{port}-{sandboxId}.{domain}`).
Stale-format risks worth flagging for operators:

- the envd synchronous `POST /commands` envelope (`command`/`envs`/`cwd`/
  `timeoutMs` in, `exitCode`/`stdout`/`stderr` out) is this plugin's recorded
  mapping — the SDK drives commands over Connect-RPC, which the plugin does
  not reimplement;
- the sandbox list envelope accepts both a bare array and
  `{"sandboxes": [...]}`;
- the sandbox lifecycle field accepts both `state` and `status`.

Run the live test before trusting a version upgrade:
`CODEYBOX_RUN_E2B_INTEGRATION=1 E2B_API_KEY=… dotnet test --filter
"FullyQualifiedName~E2bIntegrationTests"`.
