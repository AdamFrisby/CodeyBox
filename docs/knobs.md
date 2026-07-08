# Knobs (per-item directive framework)

A **knob** is a small, registered directive that nudges the agent's behaviour
on a single work item — without editing the pipeline core. Each knob is a
self-contained descriptor: a key, a value type / allowed-values set, a default,
a description, and a small set of optional per-phase prompt hooks (today: the
work-prompt and audit-prompt fragment seams).

Knobs are designed for *fan-out*: this surface is expected to grow to dozens of
small dials over time. Adding a new knob is a **localised** change — implement
[`IKnob`](../src/CodeyBox.Core/IKnob.cs), register it as a DI singleton, and
the knob is immediately visible to the API for set/validate, persisted on every
new work item, and consulted at prompt-assembly time (work and audit). No
edits to the API endpoints, the orchestrator, the SQLite store, or the
preprocessor chain are required.

## Setting knobs on a work item

Knobs can be set in three places. Precedence is **item → project default →
knob default**:

1. **Per work item** via `POST /workitems`:

   ```json
   {
     "projectId": "my-project",
     "title": "Tweak one line in parser.cs",
     "prompt": "Replace the magic 4096 with the constant.",
     "knobs": {
       "changeScope": "surgical"
     }
   }
   ```

2. **Per work item, post-create** via `PATCH /workitems/{id}` (Queued items
   only). The map is **replace-set** — the entire stored map is overwritten,
   so send the full target map. Send `"knobs": {}` to clear all overrides.

3. **Per project default** in `codeybox-extra.json`:

   ```json
   {
     "Projects": [
       {
         "Id": "my-project",
         "RepositoryUrl": "https://github.com/example/repo.git",
         "Knobs": {
           "changeScope": "surgical"
         }
       }
     ]
   }
   ```

   Defaults can also be set at the orchestrator-wide level under
   `Defaults.Knobs`; per-project entries win on key collision. Set a project
   value to an empty string to *clear* a default that the orchestrator-wide
   `Defaults.Knobs` would otherwise apply.

## Validation

Every API set-time path validates against the registered `IKnobRegistry`:

- Unknown keys are rejected with a clear error naming the key and listing the
  known knobs.
- Values are normalised through the knob descriptor. Finite `AllowedValues`
  are matched case-insensitively and stored with the registered casing; knobs
  with no `AllowedValues` use their descriptor parser and still reject empty
  or whitespace-only values by default.
- There is no generic framework-level cap for map size, key length, value
  length, or control characters beyond the registry lookup and descriptor
  parser. Add those limits inside a descriptor when a specific knob needs
  them.

The PATCH endpoint follows the same Queued-only state machine that other
queued-only fields use; once an item leaves Queued, knob edits return 409.

Project-default knob maps in `codeybox-extra.json` (both `Defaults.Knobs` and
`Projects[N].Knobs`) are validated and canonicalised against the same registry
at config load/reload time. Unknown keys or invalid values reject the
candidate configuration with a clear error and keep the prior project snapshot
on hot reload. A project-side empty string is the only non-value sentinel: it
clears an inherited `Defaults.Knobs` entry for that known key.

## Resolution and prompt injection

At every WORK and AUDIT agent invocation, the
[`KnobWorkPromptPreprocessor`](../src/CodeyBox.Orchestrator/Knobs/KnobWorkPromptPreprocessor.cs)
loads the work item when one exists, resolves each registered knob's effective
value (item → project default → knob default), asks every knob for its
phase-specific prompt fragment, and appends the non-empty fragments to the
prompt as a single block. Audit calls with synthetic work-item ids still apply
project defaults; they simply have no item-level override map.

```
## Per-item directives (knobs)

- **changeScope=surgical**: Change scope: SURGICAL. Make the smallest…
- **someOtherKnob=…**: …
```

Finite knobs may display the canonical value in the bullet label. Free-form
knobs never display the raw value in that shared label; a descriptor that opts
in to prompt fragments must delimit, encode, or avoid any raw value it emits in
its own fragment.

Two prompt seams are wired today:

| Phase  | Knob method                | Notes |
|--------|----------------------------|-------|
| Work   | `GetWorkPromptFragment`    | Original seam — instructs the coding agent. |
| Audit  | `GetAuditPromptFragment`   | Lets a knob change how LLM auditors weigh blast radius / breadth / scope-creep on this item. Default implementation returns `null`, so existing knobs need no edits. |

Rework, merge, and check-and-act phases are intentionally left alone —
additional seams can be added by extending `IKnob` with optional per-phase
methods. A knob whose effective value matches its existing default behaviour
should return `null` from its prompt-fragment method so the prompt stays
byte-identical to the pre-knob output. This is the contract: *"a knob with
nothing to say contributes nothing"*. Some knobs may affect lifecycle outside
the prompt preprocessor while still using the same registry and validation
surface; `plan` is the first built-in example.

## Adding a new knob

A new knob is a four-step, localised change. No pipeline edits.

1. Implement `IKnob` in `CodeyBox.Orchestrator/Knobs/MyKnob.cs`:

   ```csharp
   public sealed class MyKnob : IKnob
   {
       public string Key => "myKnob";
       public string Description => "Short operator-facing description.";
       public IReadOnlyList<string> AllowedValues { get; } = ["a", "b", "c"];
       public string DefaultValue => "a";

       public string? GetWorkPromptFragment(string value) => value switch
       {
           "a" => null,                 // default behaviour — contribute nothing
           "b" => "Apply behaviour B.",
           "c" => "Apply behaviour C.",
           _ => null,
       };
   }
   ```

   For boolean, numeric, range-limited, or structured knobs, keep parsing local
   to the descriptor by setting `ValueType`/`ClrType` and overriding
   `ParseValue` when the built-in parser is not enough. Prompt-contributing
   free-form knobs must either declare finite `AllowedValues` or explicitly
   opt in via `AllowsFreeFormPromptFragments` after delimiting/encoding any
   raw user-controlled value as untrusted data.

2. Register the knob as a DI singleton in `Program.cs`:

   ```csharp
   builder.Services.AddSingleton<IKnob, MyKnob>();
   ```

3. Add a row to this document describing the knob.

4. Done — the new knob is automatically validated by the API, persisted on
   work items, and injected by the prompt preprocessor (work and audit
   phases). Override `GetAuditPromptFragment` to also steer LLM auditors.

## Registered knobs

### `changeScope` (default: `moderate`)

How aggressively the agent may restructure adjacent code while making the
requested change.

| Value      | Work-prompt fragment                                                                                              | Audit-prompt fragment |
|------------|-------------------------------------------------------------------------------------------------------------------|----------------------|
| `surgical` | "Smallest possible change; touch only the strictly-required code; do not refactor adjacent code; merge-friendly diff." | "Minimise blast radius — flag out-of-scope refactor / adjacent rewrites / broadened renames / restructuring beyond the strictly-required code as findings; scope inflation IS a defect for this item." |
| `moderate` | *(none)* — current default agent behaviour.                                                                       | *(none)* — current default auditor behaviour. |
| `refactor` | "May restructure or re-architect the affected area to do this well, even with a larger and harder-to-merge diff." | "Do NOT penalise breadth / restructuring / renames per se — restructuring is in scope; focus on whether it is principled and correct, not on whether the diff could have been smaller." |

`changeScope` shapes the work, audit, and merge phases:

- **Work**: per the work-prompt-fragment column above.
- **Audit**: per the audit-prompt-fragment column above. Applied to every LLM
  auditor that runs through the prompt-preprocessor chain (architecture,
  quality, completeness, security, tests, etc.), so a surgical item flags
  out-of-scope breadth that a refactor item does not.
- **Merge**: the effective value is stamped on merge timing metadata (the
  clean host-side `merge.git.merge_clean_host` timing scope and the conflicted
  `merge.agent.exec` timing scope's `change_scope` tag, plus the
  `AgentDuration` meter's `change_scope` dimension, matching the surrounding
  snake_case tag convention). The value is resolved by the DI-provided
  `IMergeScopeResolver`, which uses the registered `IKnobRegistry`; the
  generic agentic conflict resolver receives only a neutral `MergeScopeHint`
  containing the label and whether it should emit its start log. The
  merge-phase rebase path (when a rebase hits conflicts) also carries the
  value through; work/rework/audit phases intentionally do NOT tag this
  dimension on the meter — the lever is on the merge surface today.
  Scheduling biases for refactor-vs-normal items remain provided by
  `JobType.Refactor`'s project-exclusive dispatcher gate (see
  [`OrchestratorService`](../src/CodeyBox.Orchestrator/OrchestratorService.cs)
  — `RefactorCandidateBlockedReason` and the surrounding refactor drain
  logic); operators that want a `changeScope=refactor` item to skip
  pile-ups in hot files should mark the item `JobType.Refactor` as well.

### `plan` (default: `off`)

Whether to run a planning-only phase before implementation.

| Value | Behaviour |
|-------|-----------|
| `off` | *(none)* — current default lifecycle: `Queued → Working`. |
| `on` | Runs `Planning → PlanReview → PlanApproved` before `Working`. The agent produces a stored PLAN artifact without imported code changes; `Plan`-target auditors review that artifact, blocking findings trigger plan rework, and the loop stops only when the plan passes or `MaxPlanReviewIterations` is reached. |

When `plan=on`, the approved plan is surfaced on the work item API/dashboard and
is included in the subsequent implementation prompt. Planning uses the work
agent and work sandbox/network profile, but its sandbox is discarded: file edits,
commits, and pushes from the planning turn are not imported.

## Storage shape

- **WorkItem**: `IReadOnlyDictionary<string, string> Knobs` (case-insensitive
  keys). Persisted as `knobs_json TEXT NOT NULL DEFAULT '{}'` on the
  `work_items` row.
- **Project**: `IReadOnlyDictionary<string, string> Knobs`. Resolved at config
  load time from `Defaults.Knobs` ∪ `Projects[N].Knobs` with per-project
  override winning. Operator-supplied via project config; immutable on the
  resolved Project.
