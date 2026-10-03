# CodeyBox Blaxel perpetual-sandbox provider plugin

Sandbox provider plugin (`kind: blaxel`) that runs CodeyBox work items in
[Blaxel sandboxes](https://docs.blaxel.ai/Sandboxes/Overview.md): hosted
microVMs created on demand over the Blaxel control-plane REST API, with
automatic scale-to-zero standby that preserves memory, processes, and
filesystem, and resume in milliseconds. One project, one plugin, contributed
through the plugin trust model. **Off unless an operator enables it.**

## Containment posture — read this first

This is a **hosted** provider: the guest runs on infrastructure CodeyBox does
not control. The egress guarantee is enforced with nftables on a host
CodeyBox owns, which cannot apply here. The host therefore classifies every
plugin kind `NotEnforced`, and this plugin cannot promote itself — any
deployment requiring enforced egress refuses this provider with a reason.

What isolation this provider **does** give:

- Each work item gets a fresh cloud microVM (separate guest kernel, no
  shared filesystem with the orchestrator host or with other sandboxes).
- Secrets travel as process environment inside the guest; the Blaxel API key
  itself never enters the guest (it stays in the orchestrator process
  environment and the `x-blaxel-authorization` header). Exec environments are
  delivered natively via `ProcessRequest.env` — values are never interpolated
  into the command string the platform retains with the execution record.
- Standby snapshots preserve the whole guest: memory, running processes, and
  filesystem. Resume wakes the same guest (not a fresh boot), and every
  resume is verified with an exec probe — a guest that does not answer fails
  the resume as infrastructure instead of serving broken work.

What it **does not** give:

- No host-enforced egress filtering. Blaxel offers account-level proxy /
  domain-filtering and firewall rulesets, but those are workspace
  configuration, not CodeyBox network profiles: the provider refuses any
  sandbox naming a network profile, and placement never routes profiled work
  here. An operator choosing this provider for unprofiled work is accepting
  Blaxel-default egress, not a CodeyBox allowlist. There is deliberately no
  knob mapping profiles to Blaxel proxy rules — that mapping would be a
  plugin-side claim about containment, and containment is host-owned.
- No tmpfs semantics. Credential tmpfs mounts are refused outright (use
  environment variables); other tmpfs mounts need
  `AllowPersistentTmpfsDowngrade` and become persistent guest directories
  that survive in snapshots.
- No graphical desktop. Blaxel microVMs expose no display server.
- No baseline bake, disk guard, cache seeding, or port publishing through
  this provider. Blaxel offers related platform features (custom template
  images, volumes, preview URLs), but this provider does not implement them
  and therefore does not declare them — placement refuses work requiring
  capabilities it does not declare.

## What an operator must configure

1. A Blaxel account, workspace, and API key: `BL_API_KEY` and `BL_WORKSPACE`
   in the orchestrator process environment — never in a configuration file.
   Only the variable *names* are configured (see the knob reference).
2. Allowlist **and** enable the plugin (both gates must pass; changing
   `Enabled` requires a host restart):

```json
{
  "CodeyBox": {
    "Plugins": {
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "Allowlist": ["codeybox.blaxel"],
      "Enabled": ["codeybox.blaxel"]
    },
    "Plugins:codeybox.blaxel": {
      "Image": "blaxel/blaxel-base:latest",
      "MemoryMb": 8192
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          { "MemberId": "blaxel-main", "ProviderKind": "blaxel", "Capacity": 4 }
        ]
      }
    ]
  }
}
```

3. Acknowledge shared-kernel risk is **not** required (sandboxes are
   dedicated microVMs), but untrusted workloads in production still pass the
   normal workload-trust gate.

### Full knob reference (`CodeyBox:Plugins:codeybox.blaxel`)

| Key | Default | Meaning |
|---|---|---|
| `ApiBaseUrl` | `https://api.blaxel.ai/v0` | Blaxel control-plane base URL (https required; http only with `AllowUnsafeHttp`). |
| `ApiKeyEnvironmentVariable` | `BL_API_KEY` | Env var holding the API key. Only the *name* is configured; the value is read from the process environment at use time. |
| `WorkspaceEnvironmentVariable` | `BL_WORKSPACE` | Env var holding the workspace name. Same chain rule as the key. |
| `NamePrefix` | `codeybox-` | Sandbox name prefix (letter-led, `[a-z0-9-]`, ≤26 chars; names cap at 49). Also used to recognise owned sandboxes in inventory sweeps alongside the `codeybox-managed` label. |
| `Image` | `blaxel/base-image:latest` | Sandbox image new sandboxes boot from. |
| `MemoryMb` | `8192` | Memory in MB (512…65536; CPU = memory / 2048). |
| `Region` | unset | Region (e.g. `us-pdx-1`); unset means the platform picks the closest. |
| `Ttl` | `48h` | Max-age after which Blaxel deletes the sandbox — bounds snapshot-storage cost for sandboxes a failed teardown leaves behind. |
| `WaitForRunningTimeoutSeconds` | `300` | Bound on waiting for `RUNNING`/`DEPLOYED` after create, and on resume. |
| `ExecPollIntervalSeconds` | `1` | Process-status poll cadence. |
| `ApiTimeoutSeconds` | `30` | Per-request HTTP timeout. |
| `SuspendWaitTimeoutSeconds` | `180` | Bound on waiting for a busy sandbox to settle into `STANDBY` after suspend. |
| `MaxExecOutputBytes` | `67108864` | Captured stdout/stderr cap per exec (host memory backstop). |
| `MaxStageFileBytes` / `MaxStageTotalBytes` / `MaxStageFileCount` | `48MiB` / `256MiB` / `5000` | Bounds for staging host mounts into the sandbox. |
| `MaxReadBackBytes` | `48MiB` | Per-file cap for teardown sync-back. |
| `MaxCommandBytes` / `MaxEnvironmentBytes` / `MaxStdinBytes` | `512KiB` / `256KiB` / `1MiB` | Per-exec payload bounds. |
| `MaxListPages` | `10` | Inventory sweep bound. |
| `AllowPersistentTmpfsDowngrade` | `false` | Downgrade non-secret tmpfs mounts to persistent guest dirs. |
| `AllowUnsafeHttp` | `false` | Test-only: allow http for loopback-local mocks (never for remote hosts). |

## Repository setup

The pipeline's existing behaviour is preferred: the pipeline clones and
prepares the work tree with its own exec calls, and the provider only
materialises what the spec mounts describe. Host-path mounts are staged into
the sandbox file-by-file through the process API (bounded; oversized or
missing sources fail closed), writable host mounts are synced back once at
teardown, and the working directory is created as a plain guest directory.
Bake a custom template image only to pre-install the toolchain, never to
substitute the checkout.

## What it costs

Every `CreateAsync` provisions a billable cloud microVM. Idle sandboxes stop
accruing compute but keep a standby snapshot (storage cost until deleted);
`Ttl` bounds how long a leaked sandbox can bill storage. Teardown deletes the
sandbox; deleting a sandbox with no attached volumes gives zero data
retention. Size work to `MemoryMb` deliberately — CPU scales with memory.

## API contract

- Control plane `https://api.blaxel.ai/v0`: `POST /sandboxes`
  (`{metadata: {name, labels}, spec: {runtime: {image, memory, envs, ttl},
  region}}`), `GET /sandboxes/{name}` (`state: RUNNING|STANDBY`, `status:
  DEPLOYED|…`, `metadata.url`), `GET /sandboxes` (cursor-paginated
  `{data, meta}` or bare array), `DELETE /sandboxes/{name}`. Auth on every
  request: `x-blaxel-authorization: Bearer <key>` plus `x-blaxel-workspace:
  <workspace>`.
- Data plane at the sandbox's `metadata.url` (same headers): `POST /process`
  (`{command, workingDir, waitForCompletion: false, name, env}`),
  `GET /process/{id}`, `GET /process/{id}/logs` (`{logs, stdout, stderr}`),
  `DELETE /process/{id}/kill`. The endpoint URL is service output and is
  validated before use (absolute https; http only for loopback-local mocks):
  a non-conforming URL fails the operation as infrastructure, never
  redirects credentials and workload code elsewhere.
- Exec output is bounded twice: each data-plane poll response is streamed
  through a byte ceiling derived from `MaxExecOutputBytes` (plus 1 MiB of
  JSON-envelope slack), and accumulation caps apply per stream. A guest that
  overruns the response ceiling has its process killed and gets a
  limit-flagged result instead of OOMing the host.
- Suspend has no explicit endpoint: the platform moves idle sandboxes to
  `STANDBY` automatically (≈15s after activity drains). `SuspendAsync` waits
  for that state (bounded); resume is any data-plane request followed by a
  `RUNNING` wait and a nonce exec probe that must echo exactly.
- Recorded shapes live in `tests/CodeyBox.Tests/Fixtures/blaxel/`; the live
  counterpart is `BlaxelIntegrationTests` (gated, see below).

## What it cannot do

- Enforced egress (see containment posture): profiled work is refused, never
  silently downgraded.
- Baseline bakes, disk-guard accounting, cache seeding, and port publishing:
  not implemented, not declared, refused by placement when requested.
- Explicit standby control: suspend timing is the platform's, so
  `SuspendAsync` can take ~15s+ on a busy sandbox and fails (as
  infrastructure, retryable) if the sandbox never settles within
  `SuspendWaitTimeoutSeconds`.
- `POST /sandboxes/{name}/archive` is deliberately unused: archives keep the
  filesystem only, not memory/processes, so archiving is not suspend.

## Live verification

`BlaxelIntegrationTests` runs the full lifecycle against the real service —
create, exec, file round-trip, standby suspend, resume with a working guest,
file survival across the suspend/resume cycle, delete. It cannot run in CI
(it needs a funded `BL_API_KEY` + `BL_WORKSPACE`, provisions billable cloud
VMs, and needs egress to `api.blaxel.ai`), so the recorded-shape fixtures pin
the contract on every run instead:

```sh
CODEYBOX_RUN_BLAXEL_INTEGRATION=1 BL_API_KEY=… BL_WORKSPACE=… \
  dotnet test --filter "FullyQualifiedName~BlaxelIntegrationTests"
```

Tagged `requires_blaxel` so CI profiles skip it like the multipass VM tests.
