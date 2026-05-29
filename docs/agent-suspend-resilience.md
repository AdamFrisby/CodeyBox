# Agent suspend resilience (R8-resilience)

R8-core freezes multipass VMs on orchestrator shutdown and resumes them on
startup. While the VM is suspended, in-flight TCP connections to LLM
providers are torn down from the peer's perspective. Each agent CLI handles
that freeze window differently.

This document records observed behaviour and the CodeyBox mitigations that
keep work-phase LLM calls dependable without operator intervention.

## Mitigations

| Layer | Mechanism |
|-------|-----------|
| R8-core | `CODEYBOX_AGENT_LOG_FILE` + `.exit` sidecar so `SandboxResumeOnStartupService` can re-tail and adopt the in-VM agent after `multipass start`. |
| R8-resilience (shim) | `CliAgentRunnerBase` re-invokes the agent **once** when stderr matches `AgentFailureClassifier.TransientNetworkPatterns` (or a small set of generic exit codes). See `AgentSuspendResilience` in `CodeyBox.Agents`. |
| Orchestrator | Stranded-item recovery + transient-cancellation auto-retry when adoption times out or the agent exits with a recoverable classification. |

The shim retry is the preferred single point of intervention: it covers all
five built-in CLIs (`claude`, `codex`, `gemini`, `cursor`, `opencode`) without
per-binary wrapper scripts.

## Behaviour matrix

Results below are produced by the automated smoke suite
(`AgentSuspendResilienceMatrixTests`). Each cell is one real VM, one live
low-token LLM call, suspend **1 s** after the call starts, hold for **N**
seconds, then `multipass start`.

| Agent \\ Suspend (s) | 5 | 60 | 120 | 300 |
|----------------------|---|----|-----|-----|
| `claude` | run smoke | run smoke | run smoke | run smoke |
| `codex` | run smoke | run smoke | run smoke | run smoke |
| `gemini` | run smoke | run smoke | run smoke | run smoke |
| `cursor` | run smoke | run smoke | run smoke | run smoke |
| `opencode` | run smoke | run smoke | run smoke | run smoke |

**Outcome legend**

| Outcome | Meaning |
|---------|---------|
| **Completed** | CLI exited 0; response finished (possibly after internal CLI retry). |
| **Recoverable** | Non-zero exit but `AgentFailureClassifier` → `TransientNetwork`; shim retry or orchestrator recovery handles it. |
| **Failed** | Non-recoverable exit or harness timeout — needs investigation or a stronger wrapper. |

Update the table after a CI or local matrix run. The workflow uploads
per-scenario log snippets under the job artifacts directory on the runner.

### Expected behaviour (design target)

With the shim retry enabled, all five agents should show **Completed** or
**Recoverable** for **N ≤ 60 s** without operator action. **N ≤ 300 s** is
the ideal bar; longer freezes increase the chance of provider-side idle
timeouts that only the orchestrator recovery path can salvage.

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
workflow runs the same filter on a self-hosted or manually triggered runner
with secrets configured. It does **not** run on every push to `main` (cost
and duration).

## When to add a stronger wrapper

If an agent shows **Failed** at **N ≤ 60 s** even after the shim retry:

1. Confirm the failure stderr — extend `AgentFailureClassifier.TransientNetworkPatterns` if it is a novel transient shape.
2. If the CLI supports an internal `--retry` flag, add it in that agent's `BuildInvocation` for the work phase only.
3. As a last resort, bundle a per-CLI wrapper in the baseline image or use `LD_PRELOAD` to tune `TCP_USER_TIMEOUT` for that process only.

Do not add stronger wrappers preemptively; the smoke matrix is the source of
truth.
