# Agent suspend resilience (R8-resilience)

When `Shutdown.SandboxTeardownMode=Suspend` is explicitly selected, R8-core
freezes multipass VMs on orchestrator shutdown and resumes them on startup.
While the VM is suspended, in-flight TCP connections to LLM providers are torn
down from the peer's perspective. Each agent CLI handles that freeze window
differently. The default teardown mode is `Stop`, which avoids the RAM snapshot
and does not preserve in-VM process state.

This document records observed behaviour and the CodeyBox mitigations that
keep work-phase LLM calls dependable without operator intervention. The same
agent-turn recovery also covers non-shutdown infrastructure interruptions,
including an Incus exec path that explicitly reports `ExecutionUnavailable`.

## Mitigations

| Layer | Mechanism |
|-------|-----------|
| R8-core | `CODEYBOX_AGENT_LOG_FILE` + `.exit` sidecar so `SandboxResumeOnStartupService` can re-tail and adopt the in-VM agent after `multipass start`. |
| CLI-native resume | Claude and Codex capture a validated session id from structured output and resume that exact session in a live sandbox with a short continuation prompt. Resume is bounded and hard quota/auth failures are excluded. |
| Durable agent-turn checkpoint | After a remaining quota, transient-network, explicit `ExecutionUnavailable`, or exit-137 failure, CodeyBox commits only the dirty source tree to a content-addressed Git ref and stores the bounded CLI scratchpad as a host-private SQLite BLOB. The exact route can restore both in a new sandbox; Claude/Codex also reuse the exact native id when captured. |
| Retained Incus lease | If an Incus outage prevents the durable checkpoint commands themselves from running, CodeyBox can retain the exact stopped VM under a provider-bound token and private creation manifest. A later pickup converts it to the ordinary immutable checkpoint before any agent is dispatched. |
| Incus interrupted-exec recovery | Incus keeps `/work` on the bounded COW root disk. On an ambiguous/unavailable exec it makes one immediate bounded restart attempt of the exact owned `STOPPED` VM; configured delayed attempts run at subsequent exec boundaries while the sandbox remains poisoned. |
| R8-resilience (shim) | `CliAgentRunnerBase` re-invokes the agent **once** only for unknown failures with a small suspend-related exit-code allowlist. See `AgentSuspendResilience` in `CodeyBox.Agents`. |
| Orchestrator | Stranded-item recovery, transient-cancellation auto-retry, and durable transient-network auto-retry when adoption times out or the agent exits with a recoverable classification such as `AgentFailureClassifier.TransientNetworkPatterns`. |

The durable orchestrator retry is the preferred path for recognised transport
failures because it persists state and applies backoff+jitter across common
provider or network incidents. The shim stays narrow so it does not create a
synchronised second request herd before durable scheduling takes over.

### Durable turn-resume contract

`CodeyBox:PipelineTuning:AgentSessionResumeMaxAttempts` is the hot-reloadable
bound for both same-sandbox CLI-native resumes and later durable checkpoint
re-dispatches. The two counters are independent. Every durable dispatch claims
and increments its attempt atomically inside `PipelineRunner`, so scheduler,
startup, and dead-worker enqueue paths cannot bypass the cap. Setting the value
to `0` disables CLI-native resume and agent-turn re-dispatch; the separate
legacy suspend retry, sandbox-adoption, and Git-only preempt-record paths retain
their own behavior. A durable re-dispatch is pinned initially to the exact
agent instance route, model, reasoning mode, phase, iteration, and prompt
revision recorded by the failed turn. The checkpoint remains valid while that
route is parked in `WaitingForAgentResume`; typed Git/lease evidence also
remains available in `NeedsOperatorInput` and
`AbandonedAfterRecoveryAttempts` for explicit operator recovery.

If class fallback selects a different agent after that exact route fails, the
fallback receives only the checkpointed source tree and a continuation prompt.
The CLI archive is never a Git object and is loaded from host-private SQLite
only for the exact route, so another route receives neither the archive nor the
native session id. Prompt edits and explicit retries from a different phase
invalidate the saved turn so stale instructions are not replayed.

The checkpoint is best-effort recovery evidence, not a success result. If the
scratchpad capture/private write fails, the source commit/push fails, or the
content binding cannot be verified, CodeyBox preserves the original failure.
When the cause is an Incus execution outage and one exact interrupted exec can
be proved, it may instead atomically publish a retained-VM lease, subject to
`MaxRetainedAgentTurnSandboxes` (16 by default). Other failures use the normal
phase retry boundary. SQLite cleanup removes private BLOBs when checkpoint
metadata is cleared and reconciles orphaned or no-longer-referenced rows at
startup.

Retained-VM adoption is fenced twice: an uncounted database preparation claim
elects one pipeline, and a private host lock elects one provider process. Incus
then verifies the lease token, creation manifest, VM identity, sandbox
specification, guest identity, topology, inode-pinned host sources, and guest
links before recovering it. Failure leaves the VM and lease intact and releases
the preparation claim. Success captures the dirty tree and private CLI state,
atomically replaces the lease with the immutable checkpoint, deletes the VM,
and automatically enqueues the normal resumed turn. The agent never runs in
the mutable retained VM. A missing/deleted retained VM or lost root disk cannot
be recovered in place; after conversion, exact-route recovery in a replacement
sandbox requires both the pushed source ref and its matching host-private
archive.

## Behaviour matrix

Results below are produced by the automated smoke suite
(`AgentSuspendResilienceMatrixTests`). Each cell is one real VM, one live
low-token LLM call, suspend **1 s** after the call starts, hold for **N**
seconds, then `multipass start`.

| Agent \\ Suspend (s) | 5 | 60 | 120 | 300 |
|----------------------|---|----|-----|-----|
| `claude` | Not measured | Not measured | Not measured | Not measured |
| `codex` | Not measured | Not measured | Not measured | Not measured |
| `gemini` | Not measured | Not measured | Not measured | Not measured |
| `cursor` | Not measured | Not measured | Not measured | Not measured |
| `opencode` | Not measured | Not measured | Not measured | Not measured |

Cells are updated from the `agent-suspend-resilience` workflow artifacts after a
successful matrix run on a multipass-capable host (see **Running the smoke
matrix** below). Until then, treat the table as a template — not evidence of
≤60s survival.

**Outcome legend**

| Outcome | Meaning |
|---------|---------|
| **Completed** | CLI exited 0; response finished (possibly after internal CLI retry). |
| **Recoverable** | Non-zero exit but `AgentFailureClassifier` → `TransientNetwork` or an unknown suspend exit-code shape; orchestrator recovery or the narrow shim handles it. |
| **Failed** | Non-recoverable exit or harness timeout — needs investigation or a stronger wrapper. |

Update the table after a CI or local matrix run. The workflow uploads
per-scenario log snippets under the job artifacts directory on the runner.

### Expected behaviour (design target)

With the shim plus durable orchestrator recovery enabled, all five agents should
show **Completed** or **Recoverable** for **N ≤ 60 s** without operator action.
**N ≤ 300 s** is the ideal bar; longer freezes increase the chance of
provider-side idle timeouts that only the orchestrator recovery path can
salvage.

## Running the smoke matrix

Prerequisites:

- R8-core merged and multipass available on the host.
- Agent CLIs baked via `CodeyBox:MultipassExtraRuncmd` (see
  [`baseline-bake-examples.md`](baseline-bake-examples.md#agent-clis)).
- Host credentials for every agent under test (see
  [`agents.md`](agents.md#built-in-agents)).

```bash
export CODEYBOX_RUN_AGENT_SUSPEND_SMOKE=1
export CODEYBOX_CLAUDE_API_KEY=…
export CODEYBOX_CODEX_API_KEY=…
export CODEYBOX_GEMINI_API_KEY=…
export CODEYBOX_CURSOR_AUTH_FILE=~/.cursor/credentials.json   # or CODEYBOX_CURSOR_AUTH_JSON
export CODEYBOX_OPENCODE_AUTH_FILE=…                          # path to opencode auth.json

dotnet test tests/CodeyBox.Tests/CodeyBox.Tests.csproj \
  --filter "Category=AgentSuspendResilience"
```

Each scenario provisions a VM (slow). The full 5×4 matrix typically takes
**several hours** on a cold host because of baseline bake and long suspend
windows.

## CI

The [`agent-suspend-resilience`](../.github/workflows/agent-suspend-resilience.yml)
workflow runs the same filter on a **self-hosted runner labeled `multipass`**
(with KVM and agent credentials). It does **not** run on every push to `main`
(cost and duration). Register such a runner before enabling the schedule.

Set the `CODEYBOX_ACP_BRIDGE_VERIFY_VM` repository variable to an already-baked
CodeyBox baseline VM. The workflow clones that baseline into a disposable
verifier VM before running `scripts/publish-acp-bridge.sh`, so ACP bridge
runtime verification does not depend on hidden runner-local VM state or mutate
the stopped baseline directly. Manual dispatch intentionally uses the same
repository variable rather than accepting a VM-name override, because the job
runs with provider credentials on a self-hosted Multipass runner.

## When to add a stronger wrapper

If an agent shows **Failed** at **N ≤ 60 s** even after durable recovery:

1. Confirm the failure stderr — add the novel transient shape to `CodeyBox:TransientNetworkFailurePatterns` if it should be treated as retryable.
2. If the CLI supports an internal `--retry` flag, add it in that agent's `BuildInvocation` for the work phase only.
3. As a last resort, bundle a per-CLI wrapper in the baseline image or use `LD_PRELOAD` to tune `TCP_USER_TIMEOUT` for that process only.

Do not add stronger wrappers preemptively; the smoke matrix is the source of
truth.
