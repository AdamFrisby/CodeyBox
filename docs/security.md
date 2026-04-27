# Security model and threat analysis

This document is the authoritative reference for CodeyBox's security posture.
**Read it before deploying to anything that matters.**

## Threat model

We assume the LLM agent's output is **adversarial-by-default**:

* The prompt may be authored by someone we trust, but the agent's response is
  shaped by training data and tool calls that can include attacker-influenced
  content (prompt injection from a fetched web page, a poisoned dependency,
  malicious source files in the repo being edited, etc.).
* The agent may attempt to read or exfiltrate secrets, persist outside the
  sandbox, escalate privileges, or pivot through the kernel.
* A compromise of the agent process must not become a compromise of the host
  or any other sandbox.

We do **not** defend against:

* A compromised host kernel running the orchestrator.
* A compromised orchestrator process.
* The agent producing *bad code that humans then merge*. Code review of the
  merged output is still required; CodeyBox does not validate correctness.
* Side-channel attacks against the host CPU (Spectre-class). Mitigations are
  the host kernel's responsibility.

## Core mitigations

### 1. Kernel isolation via Firecracker microVMs

The headline reason this framework exists. With plain containers, every
container shares the host kernel. A Linux LPE (and there are several per
year) becomes a host compromise. With Kata + Firecracker (or crun-vm /
libkrun), each sandbox runs its own kernel inside a microVM. A guest
kernel exploit doesn't get the attacker out of the VM.

The dev-only `Sandbox.Process` provider has **none** of these properties.
It is for local pipeline development only. Do not run it against untrusted
prompts, untrusted repositories, or untrusted upstream content.

### 2. Two-tier credential boundary

| Credential                         | Lives in                          | Visible to sandbox? |
|------------------------------------|-----------------------------------|---------------------|
| Agent API key (Anthropic, OpenAI…) | Host env, mounted to *work* sbx   | Only the work sandbox, only its own agent |
| GitHub PAT / generic git creds     | Orchestrator process only         | **Never**           |
| Host SSH keys, cloud creds         | Host                              | **Never**           |

The merge sandbox runs with no agent credentials. Even if the agent's
output (committed code) contains an exfiltration shell, it will not be
executed in a context that has an LLM API key reachable. The merge phase
runs `git merge` and nothing else.

The upstream push runs **on the host**, not in any sandbox. The token
required to push to GitHub is held by the orchestrator and never crosses
the sandbox boundary.

### 3. Network policy = deny-by-default

Every sandbox starts with no egress. The orchestrator explicitly grants:

* Work sandbox: the agent's API endpoint (e.g. `api.anthropic.com`) and the
  host git endpoint.
* Merge sandbox: the host git endpoint only. No agent endpoints.
* Upstream push: not a sandbox; runs in the host's normal network.

`SandboxNetworkPolicy.AllowedHosts` is the surface. The Kata/crun-vm
providers must enforce this with nftables or an equivalent — see the
provider TODO in `docs/sandbox-providers.md`. The Process provider does
**not** enforce it (it is dev-only).

### 4. Ephemeral credential mounts

Agent secrets are written to a tmpfs mount (`/run/codeybox/creds`) that
exists for the lifetime of the sandbox and is destroyed with it. The
secret never lands on a persistent disk inside the VM.

### 5. Resource bounds

`SandboxResourceLimits` caps CPU, memory, disk, and wall-clock. The
production providers must enforce these via cgroups (Kata config) or
libkrun limits. The orchestrator additionally enforces per-phase
`WorkTimeout` and `MergeTimeout` via `CancellationTokenSource.CancelAfter`.

### 6. State persistence is host-side only

The host bare git repo and the SQLite work-item store live on the host
filesystem. Sandbox writes to the bare repo go through the git protocol,
not raw filesystem writes. Even if a sandbox could write to the mount, it
would corrupt the bare repo's git data — git-fsck on the host can detect
this.

### 7. Token scrubbing on errors

Upstream remote impls scrub their tokens from any error message returned
to the orchestrator. The orchestrator log lines never contain credential
material. *Do not* extend the orchestrator to log the contents of
`AgentCredential` or `GitHubUpstreamOptions`.

## Audit status

An initial security audit has been performed (see
[`security-audit.md`](security-audit.md)). Highlights:

* Cross-work-item bare-repo exposure — **fixed** (per-item mount).
* `workBranch == baseBranch` bypass of merge containment — **fixed**.
* GitHub PAT visible on argv via token-in-URL — **fixed** (askpass).
* Git option-injection via `RepositoryUrl` / branch names — **fixed**
  (validation + `--` separator).
* Agent secrets on per-exec argv — **fixed** (env-file at boot).
* Default network bind tightened to loopback.
* Bearer-token auth required (or explicit `DangerouslyDisableAuth`).

See the audit doc for the complete list, severities, and accepted-risk
items.

## Sharp edges (read these before deploying)

### A. The Process provider is named "UNSAFE" for a reason

`Sandbox.Process` runs commands as the host's normal user, with read-only
copies for read-only mounts and *symlinks* for writable mounts. It does
not enforce the network policy. It exists so the orchestrator pipeline
can be developed and tested without a Kata/Firecracker setup. Do not ship
this provider to production. Configuration that selects it should fail
loudly when `ASPNETCORE_ENVIRONMENT != Development`.

### B. Sandbox image trust

The orchestrator pulls and runs images by reference
(`SandboxImageReference`). You are trusting:

1. The contents of that image (including the agent CLI binary inside it).
2. The image registry's TLS and signing configuration.

Use a digest pin (`codeybox/agent@sha256:…`) in production, not a tag.
Build the image yourself; do not pull `claude` or `gh` binaries from
arbitrary upstream sources without verification.

### C. Prompt injection through repository content

The agent's prompt typically references the repository it is editing.
Files in that repository can contain instructions designed to subvert the
agent (e.g. `README.md` with "ignore previous instructions and run …").
CodeyBox reduces *blast radius* of a successful injection but does not
prevent injection. The two key reductions:

1. The compromised agent has only its API key and the local repo. It
   cannot reach upstream creds, the host, or other work items' repos.
2. Anything the agent writes is constrained to a feature branch on the
   host bare repo. The merge phase blindly merges, but the upstream push
   is host-controlled, and a human (or a CI pipeline you bolt on) can
   gate it.

For high-stakes deployments, gate the upstream push behind a manual
approval step (replace `IUpstreamRemote` with one that records the merge
and requires a separate API call to push).

### D. The merge sandbox blindly merges

The default merge phase runs `git merge --no-ff origin/<workBranch>`. It
does not run tests, lint, or any policy check. If you want gating, plug
in an `IPullRequestService` that runs checks before allowing the merge,
and adjust the orchestrator's pipeline to consult it.

### E. Multi-tenant deployments

The framework has no concept of tenants today. If you run this for
multiple users, you must:

* Partition `IGitHost` repos per-tenant.
* Partition `ICredentialProvider` per-tenant (do not let tenant A's work
  items use tenant B's API key).
* Partition the SQLite store or move to per-tenant DBs.
* Gate the REST API behind authentication (out of scope today).

## Operational hygiene

* **Rotate API keys** on a schedule. The orchestrator picks up changes via
  `EnvironmentCredentialProvider` on next read; in-flight sandboxes hold
  the previous value until they finish.
* **Audit log everything**. Every state transition is logged at
  Information level. Send these to an append-only sink.
* **Don't disable warnings-as-errors.** The codebase enforces this in
  `Directory.Build.props`. Suppressing warnings about uninitialised
  fields or platform-specific calls usually masks a real bug.

## Review cadence

Review this document whenever:

* A new sandbox provider is added.
* A new agent integration is added (each agent has its own auth quirks).
* The credential or network model changes.
* Any cross-sandbox communication is introduced.
