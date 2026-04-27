# Audit phase

The audit phase sits between Work and Merge. It runs registered auditors
against the work-phase output; on failure it hands findings back to the
agent for rework and re-runs auditors until either everything passes or
the iteration cap is hit.

The phase is **opt-in by registration**: a deployment with no `IAuditor`
registered skips the phase entirely, preserving the original 3-phase
pipeline.

## Pipeline shape with audit

```
Work phase             ─→  push <workBranch>
       │
       ▼
Audit + rework loop:
   for iteration 1..MaxIterations:
       Audit sandboxes (capability-grouped)
         │
         ├── pass  → break, proceed to merge
         └── fail  → Rework sandbox: agent fixes → push <workBranch>
                     loop
   if no pass:  AuditFailed (terminal)
       │
       ▼
Merge phase           ─→  push <baseBranch>
       │
       ▼
Upstream push (host)  ─→  push to GitHub etc.
```

The Work + Audit + Merge phases together remain the atomic unit: failure
of any of them marks the work item as `Failed` (or `AuditFailed` for the
specific case of audit not converging). UpstreamPush is still the
post-success replication tier.

### State machine additions

```
WorkComplete ──→ Auditing ──pass──→ AuditPassed ──→ Merging
                    │
                    └─fail──→ Reworking ──→ Auditing  (loop)
                                       │
                                       └─no-changes──→ Failed
                    │
                    └─maxIters──→ AuditFailed (terminal)
```

`AuditFailed` is treated as a terminal failure distinct from `Failed`
so operators can dashboards-filter "agent didn't converge" separately
from "infrastructure broke."

## Auditor capability groups

Each `IAuditor` declares its required capabilities:

```csharp
[Flags] public enum AuditCapabilities
{
    None             = 0,
    AgentCredentials = 1 << 0,
    Network          = 1 << 1,
}
```

The pipeline groups auditors by capability and spawns **one sandbox per
group**. Tool-only auditors (`None`) run in a credential-free, network-
free sandbox; LLM-driven auditors run in a sandbox with the agent
credentials and network egress allowance.

This is a defence-in-depth choice: a buggy or compromised linter cannot
exfiltrate the agent's API key, because the API key is not present in
the sandbox where the linter runs.

### Implication for new auditors

If you write a tool auditor, declare `Required = AuditCapabilities.None`
to keep it in the credential-free sandbox. If your tool genuinely needs
network (downloading CVE feeds, fetching package versions), declare
`AuditCapabilities.Network`. Only declare `AuditCapabilities.AgentCredentials`
if the auditor itself is an LLM call.

## Built-in auditors

### `ShellCommandAuditor` (`CodeyBox.Audit.Shell`)

Runs an arbitrary command inside the audit sandbox. Exit code 0 = pass;
non-zero = fail with stdout/stderr captured as a single Error finding.

```csharp
new ShellCommandAuditor(new ShellCommandAuditorOptions
{
    Name = "golangci-lint",
    Argv = ["golangci-lint", "run", "./..."],
})
```

Use for any tool with the standard "exit 0 = good" contract: linters,
type-checkers, formatters in `--check` mode, SAST scanners that exit
non-zero on findings.

Capability: `None`.

### `LlmReviewAuditor` (`CodeyBox.Audit.Llm`)

Runs an `IAgentRunner` with a review-style prompt. The agent is
instructed to write a JSON verdict to `/audit/result.json`:

```json
{
  "passed": true,
  "findings": [
    { "severity": "error|warning|info",
      "title": "...", "description": "...", "location": "path:line" }
  ]
}
```

If the file is missing or unparsable, the auditor reports a single Error
finding with the parser failure and the truncated agent output. The
pipeline treats that as a normal audit failure and re-runs on the next
iteration (the agent may not yet be reliably producing structured output).

```csharp
new LlmReviewAuditor(new LlmReviewAuditorOptions
{
    Name = "Architecture review",
    Agent = new ClaudeAgentRunner(),
    ReviewFocus =
        "- Loose-coupling violations (concrete types in cross-module signatures)\n" +
        "- Missing input validation at trust boundaries\n" +
        "- Hardcoded secrets or URLs",
})
```

Capability: `AgentCredentials | Network`.

## Rework prompt

When an audit iteration fails, `ReworkPromptBuilder` assembles a prompt
of the form:

```
## Rework requested

Audit iteration N of M found issues with your previous changes. Please
address every error below, then ensure all auditors would pass on a
re-run. Make new commits — do not amend.

### <Auditor name>
- **Error**: <title> (<location>)
  <description>

### <Other auditor>
- **Warning**: <title>
  <description>

## Original task

<original prompt>
```

Findings are grouped by auditor, sorted within a group with errors first
then warnings, then info. The original prompt is appended at the end so
the agent has full context.

## Rework no-changes guard

If the rework agent commits nothing new, the pipeline fails fast with
"Rework agent produced no changes". This prevents an infinite loop where
the agent declines to fix issues but the auditors keep failing the same
way.

Practically this happens when:

* The agent decided the audit was wrong (and didn't push back via the
  prompt).
* The agent's output got truncated.
* The findings were so vague the agent couldn't act on them.

In all cases the work item moves to `Failed`. An operator can inspect
the work branch in the host bare repo, decide what to do, and either
re-queue the work item with a clearer prompt or merge by hand.

## Configuration

`appsettings.json`:

```json
{
  "CodeyBox": {
    "Audit": {
      "MaxIterations": 3,
      "FailingSeverity": "Error",
      "PerIterationTimeoutMinutes": 10,
      "StopOnFirstFailure": false
    }
  }
}
```

* `MaxIterations` — how many audit + rework cycles to attempt before
  giving up. Default 3.
* `FailingSeverity` — findings at or above this severity block the merge.
  Lower-severity findings are still surfaced to the agent on rework.
  Default `Error`.
* `PerIterationTimeoutMinutes` — wall-clock cap on a single audit
  iteration's sandbox. Default 10 minutes.
* `StopOnFirstFailure` — if `true`, stop running auditors as soon as one
  returns a blocking finding. Useful when expensive LLM auditors come
  after cheap linters: no point paying for tokens if a linter already
  failed.

## Adding a new auditor

1. New project (or class), implementing `IAuditor`.
2. Declare `Required` capabilities truthfully — it controls which
   sandbox your code runs in.
3. Register as `IAuditor` in DI:
   ```csharp
   builder.Services.AddSingleton<IAuditor, MyAuditor>();
   ```
4. The `AuditorRegistry` picks it up automatically; no orchestrator
   changes needed.

## Security notes

The audit phase widens the attack surface in two ways:

1. **More sandboxes per work item.** Each iteration spawns one or more
   sandboxes. Resource limits (`SandboxResourceLimits`) and the per-
   iteration timeout cap the blast radius.
2. **The rework prompt contains audit findings** which themselves may
   reflect content from the agent's earlier output. A prompt-injection
   payload in the agent's output could in theory propagate through a
   finding's description and influence rework. Mitigations:
   - The rework sandbox is a fresh VM with the same isolation as the
     work sandbox; nothing about the previous compromise carries across.
   - The rework prompt structure is controlled by
     `ReworkPromptBuilder` — operators should not allow auditors to
     inject raw model-controlled text into the orchestrator log without
     review.

The merge sandbox boundary remains the strongest defence: even after
audit passes, the merge runs in a sandbox with no agent credentials.
The audit phase does not expand what the agent can write — it only
gates whether what the agent already wrote gets merged.
