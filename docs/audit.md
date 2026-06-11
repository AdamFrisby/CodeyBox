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
    Graphical        = 1 << 2,
}
```

The pipeline groups auditors by capability and spawns **one sandbox per
group**. Tool-only auditors (`None`) run in a credential-free, network-
free sandbox; LLM-driven auditors run in a sandbox with the agent
credentials and network egress allowance. Auditors that call graphical
sandbox APIs such as screenshots or synthesized input must declare
`Graphical`.

This is a defence-in-depth choice: a buggy or compromised linter cannot
exfiltrate the agent's API key, because the API key is not present in
the sandbox where the linter runs.

### Implication for new auditors

If you write a tool auditor, declare `Required = AuditCapabilities.None`
to keep it in the credential-free sandbox. If your tool genuinely needs
network (downloading CVE feeds, fetching package versions), declare
`AuditCapabilities.Network`. Only declare `AuditCapabilities.AgentCredentials`
if the auditor itself is an LLM call. Declare `AuditCapabilities.Graphical`
when the auditor requires a desktop sandbox.

## Built-in auditors

### `ShellCommandAuditor` (`CodeyBox.Audit.Shell`)

Runs an arbitrary command inside the audit sandbox. Exit code 0 = pass;
non-zero = fail with stdout/stderr captured as a single Error finding.

Configured via YAML in `auditors` list:

```yaml
auditors:
  - name: "golangci-lint"
    argv: ["golangci-lint", "run", "./..."]
```

Use for any tool with the standard "exit 0 = good" contract: linters,
type-checkers, formatters in `--check` mode, SAST scanners that exit
non-zero on findings.

Capability: `None`.

### `process:build-script`

Runs `./build.sh` from the work-branch repository root in the credential-free
audit-tool sandbox. The project owns the script contents and decides whether it
compiles only or compiles plus tests.

Behavior:

- `build.sh` absent: skipped by default with no findings.
- `build.sh` absent and `Audit.BuildScriptRequired=true`: blocking
  `build.sh missing` finding.
- `build.sh` exits `0`: pass, with stdout/stderr captured in the audit report.
- `build.sh` exits non-zero: blocking `build failed` finding with captured
  stdout/stderr attached.
- `build.sh` cannot execute, exits `126`/`127`, or exceeds
  `CodeyBox:BuildScriptAudit:TimeoutSeconds`: the work item fails as
  infrastructure (`could-not-verify`), not as a code finding, and audit does
  not pass by default.

Capability: `None`.

### Built-in audit-type presets

CodeyBox ships these audit-type presets as YAML resources (see `docs/audit-types.md`):

| Preset         | Components                                                              |
|----------------|-------------------------------------------------------------------------|
| `security`     | gitleaks + semgrep + comprehensive LLM review focus (ASVS 5.0 + Top 10 + LLM-specific). |
| `architecture` | LLM review focus for coupling, layering, leaking internals.                  |
| `quality`      | LLM review focus for dead code, magic numbers, naming, error handling.       |
| `completeness` | LLM review focus for TODOs, missing tests, half-finished impls.              |
| `cheating`     | Deterministic diff-patterns + LLM review focus for agent shortcuts. |
| `tests`        | Deterministic diff-patterns for no-op assertions + LLM review focus for test meaningfulness. |

A project enables a preset by listing its name in
`Audit.AuditTypes` (see `docs/projects.md`).

### Built-in auditor profiles

Named auditor profiles choose a bundle for a work-item shape before preset
expansion. The top-level `Audit` block is always the `default` profile. Set
`Audit.Profile` to select a project default and add custom bundles under
`Audit.Profiles`.

CodeyBox includes `uat`, a preset for UAT/test-generation work:

| Profile | Auditors | MaxIterations | Notes |
|---------|----------|---------------|-------|
| `uat` | `csharp:format-check`, `csharp:build-WaE`, `csharp:test-pass`, `security:gitleaks`, `security:semgrep`, `security:llm-review`, `cheating:deterministic-patterns` | 5 | Omits `completeness:llm-review` and `cheating:llm-review`; UAT lists often record known divergences in `.codeybox/suggestions.json` and timing tests may intentionally exercise wall-clock waits. |

### Built-in language presets

Language presets are selected with `Project.Audit.Languages`. They are
tool-only YAML resources; see `docs/languages.md`.

| Language | Marker files | auditors |
|---|---|---|
...
### `LlmReviewAuditor` (`CodeyBox.Audit.Llm`)

Runs an `IAgentRunner` with a review-style prompt. The agent is
instructed to write a JSON verdict to `/audit/result.json`:
...
If the file is missing or unparsable, the auditor reports a single Error
finding with the parser failure and the truncated agent output. The
pipeline treats that as a normal audit failure and re-runs on the next
iteration (the agent may not yet be reliably producing structured output).

Configured via `reviewFocus` and `llmAuditorName` in audit-type YAML.

Capability: `AgentCredentials | Network`.

## Per-item testing rigor gate

The `tests:mutation-rigor` auditor enforces a kill-the-mutant gate on the
code CHANGED in a work item, scoped to the diff and parallelised in the
runner. See [`mutation-testing.md`](mutation-testing.md) for configuration,
runtime budget, and ratchet semantics. Disabled by default — opt in per
project.

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
* `CodeyBox:PipelineTuning:AuditShortCircuitEnabled` — global,
  hot-reloadable switch for declared audit gates. Default `true`. When
  enabled, auditors with `CanShortCircuitOnBlockingFinding=true` run before
  the rest; a blocking gate result skips all remaining auditors for that
  iteration and sends the preserved gate findings to rework.

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

## Cross-agent review

By default, LLM auditors run with the same agent as the work phase (e.g.
Claude reviews Claude's own output). Same training → same blind spots → the
audit finds what the same model would have found while writing. To break this
correlation, configure a different agent for the audit phase.

### Configuration

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "my-app",
        "Agent": "claude",
        "Audit": {
          "AuditAgent": "gemini",
          "AuditTypes": ["security", "architecture", "completeness"]
        }
      }
    ]
  }
}
```

`AuditAgent` applies to all LLM-based auditors (`security:llm-review`,
`architecture`, `completeness:llm-review`, `cheating:llm-review`, etc.).
Tool auditors (`python:test-pass`, `node:lint`, `csharp:build-WaE`,
`security:gitleaks`, `security:semgrep`, `cheating:suppression-patterns`)
do not invoke an LLM and are unaffected.

For finer control, use `PerAuditorAgent` to route individual auditors to
specific agents:

```json
"Audit": {
  "AuditAgent": "gemini",
  "PerAuditorAgent": {
    "security:llm-review": "claude"
  },
  "AuditTypes": ["security", "completeness"]
}
```

Resolution precedence (per LLM auditor):
1. `PerAuditorAgent[<auditor name>]` if present.
2. Else `AuditAgent` if set.
3. Else the work agent (current behaviour; backwards compat).

### Audit-capability pool (capability-gated routing)

When at least one member of the routed agent class is tagged
`"Capabilities": ["audit"]`, the audit phase is restricted to those tagged
members across ALL routing paths (preferred agent, mid-iteration spill, class
fallback). A non-tagged member is **NEVER** picked for auditing — even when it
is the only one with quota. This removes the single-agent bottleneck that
serialised audits when the configured `AuditAgent` was the only one allowed to
run audit and its quota was exhausted.

```json
"AgentClasses": [
  {
    "Id": "frontier-coding",
    "Members": [
      { "Agent": "claude", "Billing": "Subscription", "QualityScore": 100, "Capabilities": ["audit"] },
      { "Agent": "codex",  "Billing": "Subscription", "QualityScore": 100, "Capabilities": ["audit"] },
      { "Agent": "gemini", "Billing": "Subscription", "QualityScore": 95,  "ReasoningMode": "high" }
    ]
  }
]
```

With both `claude` and `codex` tagged audit-capable, an exhausted `codex`
spills to `claude` (and vice-versa), and the two can run audits concurrently
up to their per-agent caps. `gemini` stays out of the audit pool even when it
has quota.

`AuditAgent` and `PerAuditorAgent[...]` are honoured as the **preferred
primary** within the audit-capable pool when set: a named agent that is itself
tagged audit-capable runs first; a named agent that is NOT tagged is demoted
with a warning (`quota_router.audit_agent_not_audit_capable`) and routing
falls back to the pool. With no `AuditAgent` set, the highest-quality
audit-capable member runs.

**Backward compat:** when NO member of the class carries the `audit` tag, the
opt-in pool is inactive and audit routing keeps its pre-capability behaviour
(legacy fall-through to the work agent and unfiltered class chain). The tag is
config-driven and hot-reloadable.

### Trade-offs

| Benefit | Cost |
|---------|------|
| Uncorrelated signal — two models with different priors reviewing the same diff | Second set of API credentials to manage |
| Security review on a more conservative model; architecture on a broader-context model | 2× (or more) quota draw for audit iterations |
| Different LLM prompt styles surface different classes of issues | More work-item latency if the audit agent is slow |

### Fallback behaviour

The pipeline falls back to the work agent (with a warning log) when:
- The configured audit agent is not registered in `IAgentRegistry`.
- The credential provider returns `null` for the audit agent (e.g. the
  `CODEYBOX_GEMINI_API_KEY` env var is unset).
- The audit agent's quota probe reports available% below the configured
  minimum threshold AND there is no work-item agent class to walk.

When the work item DOES have an agent class configured (see
`docs/agent-classes.md`) and the configured audit agent's quota is below
threshold, the audit router walks the work-item's class chain — preferring
class members that pass the same observed-failure + probe checks the
work-phase router uses — and routes the LLM auditor to the first viable
member. If every member of the class is also quota-exhausted, the LLM
auditor is **skipped with a warning for that iteration** rather than
parking the work item. Other (non-LLM) auditors still run and the item
keeps progressing; audit signal is degraded but not lost.

Fallback never crashes the pipeline. The `audit.cross_review_active`
audit-tier event is NOT emitted when fallback occurs;
`quota_router.audit_fallthrough` IS emitted so operators can observe when
the correlation-breaking benefit was lost for an iteration.
`audit.llm_auditor_skipped_quota` is emitted when an LLM auditor was
skipped because every candidate agent was quota-exhausted.

### Observability

| Event | When emitted |
|-------|--------------|
| `auditor.run` | After each auditor. Now includes `agentKind` property. |
| `audit.cross_review_active` | Once per iteration when at least one LLM auditor actually ran with a different agent (post-fallback). |
| `quota_router.audit_fallthrough` | Once per auditor when quota triggered fallthrough. |
| `audit.llm_auditor_skipped_quota` | Once per auditor when every candidate agent was quota-exhausted and the auditor was skipped for the iteration. |

The `work_item.audit_iteration` webhook event (see `docs/webhooks.md`)
gains an optional `auditAgentKind` field in its `details` object:
- Set to the audit agent kind string (e.g. `"gemini"`) when cross-review
  is active for that iteration.
- `null` when all auditors used the work agent.

## LLM auditor parallelism

By default, LLM-driven auditors within an audit iteration run **concurrently**.
`security:llm-review`, `completeness:llm-review`, and `cheating:llm-review`
all start at the same time, so wall-clock latency is approximately
`max(individual)` (~5–13 min) rather than the sum (~15–35 min).

Each parallel LLM auditor receives its own sandbox clone. Isolation ensures
a crash or file write in one auditor's sandbox cannot corrupt another's
`/audit/result.json` output.

Tool auditors (`security:gitleaks`, `security:semgrep`, language presets,
shell commands, diff-pattern matchers) are unaffected: they always run
sequentially in a single shared sandbox, regardless of this setting.

`MaxLlmAuditorParallelism` is a per-item fan-out policy, not a host VM budget.
Every LLM auditor sandbox still goes through
`CodeyBox:WorkerPool:MaxConcurrentSandboxes`; when several items audit at once,
excess auditor sandboxes queue at the global provider gate instead of exceeding
the host's configured VM ceiling.

### Configuration

```json
"Audit": {
  "MaxLlmAuditorParallelism": 3
}
```

| Value | Behaviour |
|---|---|
| `3` (default) | All three standard LLM auditors run concurrently — fastest audit wall-clock |
| `2` | Two run concurrently; the third waits for a free slot |
| `1` | Fully sequential — useful for debugging or avoiding API 429 errors |

**If you hit 429 rate-limit errors during audit**, set `MaxLlmAuditorParallelism: 1`.
The three LLM auditors queue up and their individual latencies are unchanged —
you trade total wall-clock time for reduced concurrent token draw.

**Choosing an intermediate value**: the audit is subscription-friendly when
`MaxLlmAuditorParallelism × peak-tokens-per-call < per-account-rate-limit`.
If your rate limit sits between 1× and 3× your per-auditor peak token draw,
set `MaxLlmAuditorParallelism: 2` rather than dropping all the way to `1`.

### Declared Short-Circuit Gates

`IAuditor.CanShortCircuitOnBlockingFinding` is a first-class auditor
capability. When the global `CodeyBox:PipelineTuning:AuditShortCircuitEnabled`
toggle is `true`, the pipeline runs all declaring auditors before the remaining
auditors. If any declared gate returns `Passed=false` or an Error finding, the
pipeline records that gate's report and findings, skips all remaining auditors
for the iteration, and reworks from those findings.

The built-in C# compile and full-test gates declare this capability:
`csharp:build-WaE` and `csharp:test-pass`. LLM review auditors do not.
This is separate from `Kind`; future auditors opt in by returning `true`.

`StopOnFirstFailure` is still available as the older project-level broad
fail-fast knob. It is order-dependent and, once parallel LLM execution begins,
all already-started LLM auditors run to completion. Declared short-circuit gates
avoid that cost by running before LLM fan-out starts.

**Baseline-image baking**: if sandbox baseline images have not been pre-baked,
parallel LLM auditor clones serialise on the bake step during the first audit
iteration. Subsequent iterations reuse the baked image and run concurrently as
expected. This is not a regression versus sequential execution — it only
affects the first iteration when images are cold.

### Observability

The existing `auditor.run` event is emitted once per auditor after its sandbox
completes. Events are emitted in **registration order** (the order auditors
appear in the config), not completion order — `Task.WhenAll` returns results
in input-task order and the post-processing loop iterates that stable array.
Use the auditor `name` property to correlate events with findings.

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
