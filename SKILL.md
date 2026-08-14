---
name: codeybox-operations
description: Install, configure, validate, operate, and troubleshoot CodeyBox. Use when an agent needs to choose a sandbox provider, deploy the orchestrator or Admin dashboard, register repositories, configure GitHub delivery, inspect work items and queues, or prove an end-to-end run.
---

# CodeyBox Operations

Use this skill for deployment and operations. Use `AGENTS.md` when changing
CodeyBox source, and follow the linked files in `docs/` for full schemas.

## Run a deployment preflight

Before changing a production installation, inspect the host and ask about
unresolved policy choices. Do not silently omit optional components or choose
production defaults on the user's behalf.

Ask explicitly: **"Do you want the CodeyBox Admin UI installed?"** Do not infer
the answer from an API-only deployment. If yes, confirm its authentication and
any exception to the default co-hosted ingress topology.

Confirm:

- sandbox provider and storage pool;
- whether to deploy Admin, its authentication, and any requested ingress exception;
- whether provider baselines should be disabled, baked lazily, or prewarmed;
- required network profiles and which phases may access the internet;
- repository credentials, upstream PR delivery, and auto-merge policy;
- persistent state, backups, log retention, and failure notifications.

Record non-secret choices in version-controlled configuration. Keep tokens and
generated credentials in protected environment files or a secret store.

### Co-host the Admin dashboard by default

Use one public CodeyBox origin by default: serve Admin at the origin root and
proxy the orchestrator API at `/api/` through the same TLS terminator or
tunnel. Keep the API listener loopback-only, and update every caller
(including JobTrack) to use the `/api` base URL. This provides one canonical
operator URL and one edge-authentication policy.

Do not create a separate public Admin hostname unless the user explicitly
requests independent ingress or authentication. A separately packaged Admin
process may still run locally; “co-hosted” describes the public origin and
reverse-proxy boundary, not a requirement for a shared executable.

Before cutover, identify every API consumer, preserve WebSocket forwarding,
remove the separate public ingress, and validate unauthenticated Admin,
authenticated Admin, API, and caller access through the canonical origin.

### Choose the sandbox provider explicitly

- Recommend `incus` for persistent, high-throughput headless deployments.
  Require Incus 6.3+ and an existing ZFS or Btrfs pool so baseline clones use
  copy-on-write storage. Confirm the service identity can administer Incus.
  Never create, reformat, or destroy storage without explicit approval.
- Offer `multipass` for the simplest setup or graphical sandboxes. Explain that
  its baseline clones copy full VM images, increasing latency, disk use, and
  SSD writes across repeated work and audit passes.
- Use `multipass-remote` when VM execution belongs on another host.
- Use Bubblewrap only when shared-kernel isolation is explicitly acceptable.
- Use `process` only for trusted development or constrained CI; it provides no
  isolation.

Provider baselines are independent. Ask which network-profile baselines to
prewarm; do not assume Incus inherits Multipass provisioning. Read
`docs/sandbox-providers.md` and `docs/host-firewall.md`.

## Define installation completeness

A healthy orchestrator alone is not a complete operator-facing deployment.
Inventory and explicitly deploy or omit:

- orchestrator and Admin dashboard;
- authenticated API access, protected secrets, and secure ingress;
- persistent database, Git, log, and sandbox storage;
- startup supervision and restart behavior;
- operator-visible queued, running, parked, and failed work.

The normal public layout is `https://codeybox.example/` for Admin and
`https://codeybox.example/api/` for the orchestrator API. A distinct public
Admin hostname is an explicit exception, not the default.

An operator must be able to tell what is running, what needs attention, why it
failed, and how to recover it. Read `docs/security.md`, `docs/operations.md`,
and the Admin documentation before finalizing the inventory.

## Build and configure

Install the .NET 10 SDK, Git, the chosen sandbox provider, and at least one
authenticated agent CLI.

```bash
dotnet build CodeyBox.slnx
```

The repository handles an inherited unwritable NuGet home through
`Directory.NuGetHomeHeal.targets` and `scripts/nuget-home-heal.sh`. Prefer
fixing ownership in a reusable baseline when the bad ownership originates
there.

Put project configuration in JSON and point `CODEYBOX_EXTRA_CONFIG` at it:

```json
{
  "CodeyBox": {
    "SandboxProvider": "incus",
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "https://github.com/owner/my-app.git",
        "BaseBranch": "main",
        "Agent": "codex"
      }
    ]
  }
}
```

Use credential-free repository URLs and protected environment variables for
secrets. See `docs/projects.md` and `docs/configuration.md`.

For development:

```bash
export CODEYBOX_API_KEY=replace-with-a-secret
export CODEYBOX_EXTRA_CONFIG=/path/to/codeybox.json
dotnet run --project src/CodeyBox.Api
```

Production should use immutable published artifacts, a dedicated service
identity, a supervisor such as systemd, and protected TLS ingress.

## Register a repository and upstream

1. Resolve the canonical URL and default branch with `git ls-remote --symref`.
2. Verify the service identity can read it without credentials in the URL.
3. Add one narrowly scoped project, preserving existing audit, network, budget,
   and merge policy.
4. For PR delivery, configure the GitHub upstream and a secret-backed token
   environment variable. Do not infer auto-merge authorization.
5. Validate and install JSON atomically, then confirm reload in logs and
   `GET /projects/{id}`.
6. Verify GitHub identifies the token as the intended account and reports the
   required repository permission.

Host Git credential helpers can override `GIT_ASKPASS` unless authenticated
subprocesses clear inherited helpers. If GitHub reports an unexpected user,
check credential-helper precedence as well as the URL and token.

## Operate through authenticated interfaces

Prefer the typed CLI:

```bash
dotnet run --project tools/CodeyBox.Cli -- queue ls
dotnet run --project tools/CodeyBox.Cli -- queue show WORK_ITEM_ID
dotnet run --project tools/CodeyBox.Cli -- queue watch WORK_ITEM_ID
```

For direct API calls, load the token inside a protected subshell:

```bash
sudo -u SERVICE_USER bash -lc '
  set -a
  source /path/to/codeybox.env
  curl -fsS \
    -H "authorization: Bearer $CODEYBOX_API_KEY" \
    http://127.0.0.1:5036/workitems |
  jq
'
```

Never print or persist tokens. Useful reads include:

- `GET /projects` and `/projects/{id}`;
- `GET /workitems` and `/workitems/{id}`;
- `GET /workitems/{id}/timeline`, `/agent-history`, `/diff`, and
  `/stdout-tail`;
- `GET /workers/status`, `/queue/status`, and project budget usage.

Inspect current state before retrying, cancelling, resuming, or recovering.
Use mutation endpoints only when authorized.

## Understand workspaces

Each item gets an isolated bare repository under `GitRootDirectory`. Its
sandbox normally receives `/repo`, `/work`, and a `codeybox/<short-id>` branch.
Verify results in that repository and the API, not a developer checkout.

With `Upstream.Kind=noop`, work remains local. With GitHub upstream enabled, a
local commit is not delivery: verify the remote branch and PR URL.

## Validate end to end

1. Confirm orchestrator and Admin health, worker capacity, queue state, project
   visibility, repository access, credentials, and sandbox inventory.
2. Open Admin through production ingress and verify live data.
3. Dispatch a disposable one-file task through the intended caller.
4. Observe work, audit, merge, upstream push, and `Done`.
5. Verify the exact diff and commit, remote branch, and PR URL.
6. Verify the caller reconciles to terminal success.
7. Confirm transient sandboxes were disposed.
8. Exercise or safely simulate failure and confirm Admin shows an actionable
   error, logs or timeline, and recovery control.
9. Restart services when proportionate and verify persistence and ingress.

Do not report full-stack success when any required phase is failed, parked,
unobserved, or undelivered. Report execution, upstream delivery, caller
reconciliation, dashboard visibility, and failure recovery separately.

## Recover deliberately

- `WaitingForQuotaReset`: wait for reset or add capacity.
- transient/no-change agent failure: inspect history and retry after it clears.
- provisioning failure flood: stop dispatch and validate the baseline and
  cloud-init with a disposable sandbox.
- `AuditFailed`: inspect findings and requeue only after fixing the cause.
- upstream failure: preserve the local merge, correct credentials or remote
  policy, and retry the bounded upstream phase.

Judge health by advancing state and timestamps, not only completed-item count.
See `docs/operations.md` and `docs/recovery.md`.
