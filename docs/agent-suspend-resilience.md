# Agent suspend resilience (R8-resilience)

When `Shutdown.SandboxTeardownMode=Suspend` is explicitly selected, R8-core
freezes multipass VMs on orchestrator shutdown and resumes them on startup.
While the VM is suspended, in-flight TCP connections to LLM providers are torn
down from the peer's perspective. Each agent CLI handles that freeze window
differently. The default teardown mode is `Stop`, which avoids the RAM snapshot
and does not preserve in-VM process state.

This document records observed behaviour and the CodeyBox mitigations that
keep work-phase LLM calls dependable without operator intervention.

## Mitigations

| Layer | Mechanism |
|-------|-----------|
| R8-core | `CODEYBOX_AGENT_LOG_FILE` + `.exit` sidecar so `SandboxResumeOnStartupService` can re-tail and adopt the in-VM agent after `multipass start`. |
| R8-resilience (shim) | `CliAgentRunnerBase` re-invokes the agent **once** only for unknown failures with a small suspend-related exit-code allowlist. See `AgentSuspendResilience` in `CodeyBox.Agents`. |
| Orchestrator | Stranded-item recovery, transient-cancellation auto-retry, and durable transient-network auto-retry when adoption times out or the agent exits with a recoverable classification such as `AgentFailureClassifier.TransientNetworkPatterns`. |

The durable orchestrator retry is the preferred path for recognised transport
failures because it persists state and applies backoff+jitter across common
provider or network incidents. The shim stays narrow so it does not create a
synchronised second request herd before durable scheduling takes over.

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

## When to add a stronger wrapper

If an agent shows **Failed** at **N ≤ 60 s** even after durable recovery:

1. Confirm the failure stderr — add the novel transient shape to `CodeyBox:TransientNetworkFailurePatterns` if it should be treated as retryable.
2. If the CLI supports an internal `--retry` flag, add it in that agent's `BuildInvocation` for the work phase only.
3. As a last resort, bundle a per-CLI wrapper in the baseline image or use `LD_PRELOAD` to tune `TCP_USER_TIMEOUT` for that process only.

Do not add stronger wrappers preemptively; the smoke matrix is the source of
truth.
