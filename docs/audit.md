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
       Mechanical fixers (deterministic, may commit)
         │
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
                                       └─no-changes──→ infra fallback,
                                                        escalation, park,
                                                        or AuditFailed at
                                                        the no-progress ceiling
                    │
                    └─maxIters──→ AuditFailed (terminal)
```

`AuditFailed` is treated as a terminal failure distinct from `Failed`
so operators can dashboards-filter "agent didn't converge" separately
from "infrastructure broke."

## Mechanical edit step

Before each audit iteration, CodeyBox can run configured
`IMechanicalFixer` implementations against the work branch. This is a
separate primitive from both auditors and LLM-driven work/rework:

- Auditors are read-only and produce verdicts/findings.
- Work/rework calls an agent and is non-deterministic.
- Mechanical fixers are deterministic, no-model transforms that may edit the
  work tree.

Fixers run after the initial work phase and after every rework phase, before
auditors inspect the tree. If a fixer changes files, the pipeline commits the
delta as a clearly labeled mechanical commit and pushes the work branch before
continuing. That commit carries the same prompt-revision metadata trailers as
agent commits, but it is not treated as agent work and does not trip the
"agent produced no changes" failure path.

Mechanical edit failures are infrastructure failures for the item, not audit
findings. A successful no-op fixer produces no commit.

The v1 built-in fixer is `dotnet-format`. It is enabled by default when the
C# language preset is enabled, unless the project sets
`Audit.MechanicalFixers: []`. It reuses the active `csharp:format-check`
auditor command and language marker discovery, removing only read-only flags
such as `--verify-no-changes`. This guarantees the normalizer and the matching
format auditor run under the same SDK, configuration, and sandbox baseline.

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

### Build/test gate ordering

In addition to capability flags, an auditor can declare a `Role` of
`AuditorRole.BuildTestGate`. The pipeline GUARANTEES every BuildTestGate
auditor runs and passes before any LLM-driven auditor runs in the same
audit iteration. The LLM panel requires verified build and test evidence:
either one gate with `gateEvidence: build-and-test`, or separate passing
`build` and `test` gates. If any BuildTestGate auditor produces a blocking
finding or reports unverified evidence, the LLM panel is skipped for that
iteration — the LLM prompt frame asserts that CI built the project and ran
tests with no failures, and that claim must never be false. The findings
still flow to rework as normal.

This gate is independent of `StopOnFirstFailure`: even when the option is
`false` (the default), a failing build/test gate still short-circuits the
LLM panel. Tool auditors without the role (e.g. format-check) do not gate
the panel, because the panel's CI claim only covers build and tests.

Built-in language presets mark the `<lang>:build-*` (where the preset
ships a separate build step — currently only `csharp:build-WaE`) and the
`<lang>:test-*` auditor as BuildTestGate. Go and Rust test commands build the
tested packages as part of the command, so their `<lang>:test-*` gates
advertise `build-and-test` evidence. Node and Python default test commands
(`npm test`, `pytest`) advertise `test` evidence only; projects that want LLM
review for those languages must also provide build evidence, for example with
a trusted custom build gate.
Gate metadata is accepted only from trusted configuration: built-in presets
or operator/project appsettings overrides. Repository-supplied YAML can add
shell auditors, but it cannot declare `role` or `gateEvidence`, because that
would let an untrusted repository forge the CI evidence used to unlock LLM
review. In trusted config, set `role: build-test-gate` plus explicit
`gateEvidence` for any custom step (e.g. a separate `tsc`/`cargo check`/
cross-compile) whose successful run should contribute to the LLM panel gate.
If `gateEvidence` is omitted, the gate contributes no build/test evidence.
The built-in `process:build-script` auditor runs a repository-owned
`build.sh` as an ordinary tool audit only; it is not trusted build evidence
and cannot unlock LLM review.

### .NET build/test gate NuGet-home precondition

The .NET gates (`csharp:build-WaE`, `csharp:test-pass`) and the
non-skippable `process:required-build` gate invoke `dotnet`, which on the
first restore materialises a NuGet user-config directory under
`$DOTNET_CLI_HOME/.nuget/NuGet` — falling back to `$HOME/.nuget/NuGet` when
`DOTNET_CLI_HOME` is unset. If that parent is present but not writable
(a root-owned `~/.nuget` is common in agent sandboxes), restore aborts
before it reads `RestoreConfigFile` with `Failed to read NuGet.Config due
to unauthorized access ... Access to the path '.../.nuget/NuGet' is
denied`. `RestoreConfigFile` (pinned in `Directory.Build.props`) selects
*which* config to read; it does not stop the user-config directory probe,
so only pointing `DOTNET_CLI_HOME`/`HOME` at a writable location avoids it.
Verified empirically: with `RestoreConfigFile` pinned and every project
already restored (`All projects are up-to-date for restore`), a `dotnet
build` still creates `$HOME/.nuget/NuGet/NuGet.Config` — the user-config
directory is materialised unconditionally while NuGet loads default
settings, *before* `RestoreConfigFile` is consulted. There is therefore no
committed-repo mechanism (NuGet.Config, `Directory.Build.props`, an MSBuild
property, or a `Directory.Build.rsp` response file that injects default
restore args) that redirects this probe for a `dotnet` the harness launches
directly; the home is chosen from process environment alone. Re-verified
empirically against a root-owned `~/.nuget`: passing `RestoreConfigFile`
explicitly (`-p:RestoreConfigFile=...`) and injecting the same via a
`Directory.Build.rsp` both still abort with the identical `.../.nuget/NuGet
... denied`; only redirecting `DOTNET_CLI_HOME` (or `HOME`) to a writable
directory lets restore succeed while the root-owned `~/.nuget` stays
untouched.

For every dotnet invocation CodeyBox itself launches this is handled
automatically and needs no operator action: the `SandboxRequiredBuildVerifier`
`BuildScript` prologue exports both `DOTNET_CLI_HOME` and `HOME` to a
writable repo-local `.dotnet-cli-home` (writability-probed, so an inherited
but root-owned value self-heals to the fallback), and
`DotnetCliHomeConventions` stamps `DOTNET_CLI_HOME` on audit-tool sandboxes
and on `dotnet` shell-auditor invocations. The repo-root `build.sh` applies
the same writability-aware selection for the `process:build-script` gate.

**Operator precondition (not a repo defect).** No committed repo file can
redirect a `dotnet` process that a harness launches *outside* these seams
(a bare `dotnet build`/`dotnet test` run directly on the host), because the
NuGet home is chosen from process environment, not repository configuration.
If such a step fails with the `~/.nuget ... denied` error, the audit host —
not the branch — must make the NuGet home writable (a non-root-owned
`~/.nuget`, or a writable `DOTNET_CLI_HOME`/`HOME` exported before the
command). The solution otherwise builds warnings-clean once the home is
writable.

The recovery is encoded as `scripts/reclaim-nuget-home.sh` so it is
discoverable and repeatable rather than tribal knowledge (run it on the host
before the direct `dotnet` step, or once to heal a persistent home). It is
safe and idempotent: it acts only when the NuGet home is unhealthy — its
config directory is not writable, or an existing `NuGet.Config` is unreadable
(which aborts restore with the same "Failed to read NuGet.Config" the gate
hits) — leaves a healthy home untouched, and, when it must reclaim, renames the
unwritable directory aside to a numbered backup (never deleting the possibly
root-owned contents) before recreating a writable one. Its healthy-no-op,
create, reclaim (unwritable-dir and unreadable-config), and unset-`HOME`
branches are covered by `ReclaimNuGetHomeScriptTests`.

**Recovery.** Either export a writable home before the gate command:

```sh
export DOTNET_CLI_HOME="$PWD/.dotnet-cli-home" HOME="$PWD/.dotnet-cli-home"
```

or reclaim a root-owned `~/.nuget` in place. Removing the stale entry is
governed by the write bit on `$HOME` itself (the parent), not by the
root-owned `~/.nuget`'s own permissions, so an unprivileged agent whose home
directory is writable can move it aside non-destructively and recreate a
writable one. The reclaim is safe to re-run — the audit host may re-provision `~/.nuget`
root-owned again between iterations, or a prior iteration's reclaim may already
have healed it (a healed home persists on the host's filesystem and reads back
as a no-op), so recovery must be idempotent rather than assume a clean slate:

```sh
if [ -w "$HOME" ] && [ -e "$HOME/.nuget" ] && [ ! -w "$HOME/.nuget/NuGet" ]; then
  # Move aside to a PID-unique name: a prior reclaim may have left a
  # `.nuget.unwritable.*.bak` that is itself root-owned and thus not removable
  # by an unprivileged agent, and reusing a fixed backup name would `mv` the
  # new `.nuget` *inside* that surviving directory instead of beside it.
  mv "$HOME/.nuget" "$HOME/.nuget.unwritable.$$.bak" && mkdir -p "$HOME/.nuget/NuGet"
fi
```

The `-e "$HOME/.nuget"` guard skips the move when no `~/.nuget` exists at all
(a writable `$HOME` lets `dotnet` create it unaided); the block only fires for
the present-but-unwritable case that actually aborts restore.

Prefer exporting `DOTNET_CLI_HOME` where the launcher allows it; the reclaim
path is the fallback for a bare host-launched `dotnet` that no repo file or
environment export can reach.

## Built-in auditors

### `ShellCommandAuditor` (`CodeyBox.Audit.Shell`)

Runs an arbitrary command inside the audit sandbox. Exit code 0 = pass;
non-zero = fail with stdout/stderr captured as a single Error finding.
If the top-level tool is absent, ordinary shell auditors emit a non-blocking
Info finding, build/test gate auditors emit Error, and presets can raise the
missing-tool signal. The built-in `security:gitleaks` and `security:semgrep`
auditors raise missing tools to Warning so security coverage loss is visible
without wedging audits by default.

Configured via YAML in `auditors` list:

```yaml
auditors:
  - name: "golangci-lint"
    argv: ["golangci-lint", "run", "./..."]
    missingToolSeverity: warning
```

Use for any tool with the standard "exit 0 = good" contract: linters,
type-checkers, formatters in `--check` mode, SAST scanners that exit
non-zero on findings.

Capability: `None`.

### `DotnetTestAuditor` (`CodeyBox.Audit.Shell`)

Backs the built-in `csharp:test-pass` gate. It is a first-class
`ITestRunnerAuditor` rather than a generic `ShellCommandAuditor`, so
test-command construction, the `dotnet test` result classifier, per-test
hang handling, and (future) test selection are declared members of the type
instead of scattered `argv`-shape special-casing.

`BuildInvocation(selection, options)` owns the full argv:

- Base command comes from the `csharp` language YAML (`dotnet test --no-build`).
- A narrowed `TestSelection` appends `--filter` (OR-joined, defaulting bare
  names to `FullyQualifiedName=`).
- A configured `CSharpTestPassBlameHangTimeout` appends
  `--blame-hang --blame-hang-timeout <value>`.

With an all-tests selection and default options the emitted command is
byte-identical to the legacy generic-shell path. Execution (tool-presence
probe, missing-tool handling, classification) is delegated to a
`ShellCommandAuditor`, so run semantics are unchanged. The type is
DI-registered as `ITestRunnerAuditor` so the test-selector seam can enumerate
its `TestSuiteDescriptor`. Run options
(`CSharpTestPassAuditorIdleTimeout` / `CSharpTestPassBlameHangTimeout`) are
sourced through the type from `CodeyBox:PipelineTuning` and hot-reload.

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

## Rework no-changes disambiguation

When a rework agent commits nothing new, the pipeline does **not**
unconditionally terminal-fail the work item. The empty result is
disambiguated into three causes, each with its own item-level handling.
The asymmetry with the initial-work phase (still fail-fast) is deliberate:
no audit/rework loop sits behind initial work to recover a "declined to
do anything" outcome.

### Step 1: classify before deciding

Before treating an empty rework as the agent's verdict on the findings,
the pipeline checks infrastructure-failure evidence:

1. The **auth-required classifier** is consulted before the empty result
   is treated as a verdict — a Success exit with an auth-required signature throws
   `AgentAuthRequiredException` from `RunAgentPhaseAsync`, which the
   availability breaker can turn into a per-agent exclusion. In an
   agent class, the same exception is a fallback trigger, so the work
   item re-routes through normal scoring before falling back to a
   terminal auth-required failure. On audit rework clean-exit/no-diff,
   captured stdout/stderr auth signatures publish the availability exclusion
   before reroute. Other stdout-only auth evidence and runner terminal
   diagnostics still use the existing in-VM corroboration policy before they
   can bench the agent globally. Operator-configured stderr auth patterns are
   trusted directly.
2. The **quota / usage classifier** runs on the **rework** no-diff branch
   over captured stdout/stderr before the empty result is treated as genuine.
   Runner log `TerminalDiagnostic` text is a separate side channel, so on the
   rework path it requires a fresh quota probe to corroborate exhaustion before
   the pipeline records observed quota state or re-routes. It also checks
   `TerminalDiagnostic` quota evidence on initial work no-diff, preserving the
   Antigravity / `agy` exit-0 usage-cap park while keeping genuine initial
   no-ops fail-fast. Several CLIs swallow usage-cap errors as exit-0; a quota
   match throws `TerminalQuotaError`, which the agent-class fallback wrapper
   converts into a re-route to a healthy class member (or, on the single-agent
   path, parks the item in `WaitingForQuotaReset`). Unauthorized quota
   detections (`401` / `403`) are routed as auth-required infrastructure
   failures, not quota reset parks.

Neither infra path counts against convergence, parks the item as
operator-input, or terminal-fails it as "cannot resolve findings."

### Step 2: converge-aware handling

If no infra signature matched, the no-diff outcome is genuinely the
agent declining to commit anything. `RunAgentPhaseAsync` throws
`ReworkProducedNoChangesException` and `RunAuditReworkAsync` catches it:

* **Converging + escalation budget remains** — when the audit history
  shows convergence progress (blocking-findings decreased, fingerprint
  changed, work-branch tip moved, &c — see `HasAuditConvergenceProgress`)
  and `CodeyBox:PipelineTuning:EmptyReworkEscalationRetries` is positive,
  the rework is re-dispatched up to that many times. Each retry prepends
  an escalation header to the rework prompt instructing the agent that
  its previous pass committed nothing and it MUST modify files, or state
  precisely why each finding is invalid or already satisfied. If any retry
  produces a real commit the loop continues normally; otherwise it falls
  through to the park path below.
* **Park for operator review** — when escalation is disabled, or every
  escalation pass came back empty after convergence, the item parks through
  the same operator-input event/details shape as the audit max-iteration path
  while using an empty-rework-specific `LastError`. The operator can resume
  the item with a clearer prompt or merge by hand.

Hard terminal failure for no-progress work remains the audit-loop ceiling
path through `AuditFailedException`: once the final audit iteration itself
still has blocking findings and no convergence signals, the item fails. A
blank rework after audit iteration N is still in-budget when it feeds audit
iteration N+1, so it parks rather than claiming the max-iteration ceiling was
reached early.

### Configuration

`CodeyBox:PipelineTuning:EmptyReworkEscalationRetries` — non-negative
integer. Default `1`. Set to `0` to skip escalation entirely (empty
non-infra rework goes straight to the park/fail policy). Hot-reloaded with
the rest of `PipelineTuning`.

### Why initial work stays fail-fast

The initial work phase (`isInitial==true` in `RunAgentPhaseAsync`)
continues to throw `InvalidOperationException("Agent produced no changes
to commit")` on a genuine empty commit. There is no audit/rework loop
sitting behind it to converge a "declined to work" outcome — the failure
must be visible to the operator immediately so they can re-prompt or
re-route to a different agent rather than the orchestrator silently
spending budget on escalation retries that have nowhere to land. The one
exception is runner-owned `TerminalDiagnostic` quota evidence: because that
side channel is produced by the runner, not task prose, an initial
clean-exit/no-diff quota block parks as `WaitingForQuotaReset` instead of
being mislabeled as a prompt/no-op failure.

### Relation to the agent-level no-changes breaker

Item-level park / escalation is one of two layers:

* **Item-level resilience** (this section): protects a single converging
  item from being discarded on one empty pass.
* **Agent-level breaker** (`RecordNoChangesOutcomeAsync` /
  `IAgentAvailabilityRegistry`): tracks N consecutive distinct work items
  with no diff and excludes the agent across the fleet when the streak
  trips. The breaker still fires on a genuinely-empty rework — only the
  auth / quota classifier branches skip it, because an infra failure
  isn't evidence the agent itself is broken.

## Rework non-compile loop-back

When the required-build gate (see `RequiredBuildGate`) discovers a build
failure during the **audit** phase it surfaces a blocking finding so the
loop performs another rework — this has always been recoverable. When the
**same** failure is produced by a rework (the agent's commit broke the
build), the gate also defers rather than terminal-failing the work item:
the next audit iteration's build check picks it up as a blocking finding
through the same `RunForAuditAsync` path, giving the loop another chance
to converge within the existing `MaxIterations` budget.

This loop-back is scoped to required-build failures from audit-driven
rework, including the case where the rework leaves an already
non-compiling branch unchanged. Only when the iteration budget is
exhausted does the audit ceiling take over — parking the item at
`NeedsOperatorInput` if convergence signals are visible (the blocking
findings, fingerprints, or work-branch tip changed across iterations) or
finishing at `AuditFailed` if no progress is detectable.

**Scope.** The loop-back applies to the rework path only — an initial
work phase that leaves the branch non-compiling still terminal-fails
with `failureKind=build`, because no audit/rework loop sits behind the
initial work to converge on a fix.

**Sibling unification.** The rework no-changes guard above no longer
unconditionally terminal-fails the work item — empty rework on a
converging item now escalates or parks. See *Rework no-changes
disambiguation* above for the full policy.

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
* `CodeyBox:PipelineTuning:BlockRedundantDotnetBuildTestInAuditSandbox` —
  global, hot-reloadable switch for the audit-sandbox `dotnet` shim. Default
  `true`. The shim turns auditor-initiated `dotnet build` and `dotnet test`
  into immediate successful no-ops with a notice because the deterministic
  build/test gate already ran; other `dotnet` subcommands pass through, and
  work/merge/conflict-resolution sandboxes are not modified.

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
member. If every member of the class (the entire spill-to-peer pool) is
also quota-exhausted, the work item **parks in `WaitingForQuotaReset`**
and the `QuotaRetryScheduler` resumes the same audit iteration when
quota returns. A silently-skipped auditor would let a Pass verdict
emerge with an incomplete review set — the per-auditor independent-gate
contract requires every configured auditor to have produced a verdict
before the iteration can pass.

Fallback never crashes the pipeline. The `audit.cross_review_active`
audit-tier event is NOT emitted when fallback occurs;
`quota_router.audit_fallthrough` IS emitted so operators can observe when
the correlation-breaking benefit was lost for an iteration.
`audit.llm_auditor_parked_quota` is emitted when the work item is
parked because every candidate agent for an LLM auditor was
quota-exhausted.

### Observability

| Event | When emitted |
|-------|--------------|
| `auditor.run` | After each auditor. Now includes `agentKind` property. |
| `audit.cross_review_active` | Once per iteration when at least one LLM auditor actually ran with a different agent (post-fallback). |
| `quota_router.audit_fallthrough` | Once per auditor when quota triggered fallthrough. |
| `audit.llm_auditor_parked_quota` | Once per auditor when every candidate agent was quota-exhausted; the work item parks in `WaitingForQuotaReset` and the `QuotaRetryScheduler` resumes the same iteration when quota returns. |

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
