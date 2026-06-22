# Knobs (per-item directive framework)

A **knob** is a small, registered directive that nudges the agent's behaviour
on a single work item — without editing the pipeline core. Each knob is a
self-contained descriptor: a key, a value type / allowed-values set, a default,
a description, and a small set of optional per-phase hooks (today: the
work-prompt fragment seam).

Knobs are designed for *fan-out*: this surface is expected to grow to dozens of
small dials over time. Adding a new knob is a **localised** change — implement
[`IKnob`](../src/CodeyBox.Core/IKnob.cs), register it as a DI singleton, and
the knob is immediately visible to the API for set/validate, persisted on every
new work item, and consulted at work-prompt assembly time. No edits to the API
endpoints, the orchestrator, the SQLite store, or the preprocessor chain are
required.

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

At every work agent invocation, the
[`KnobWorkPromptPreprocessor`](../src/CodeyBox.Orchestrator/Knobs/KnobWorkPromptPreprocessor.cs)
loads the work item, resolves each registered knob's effective value
(item → project default → knob default), asks every knob for its prompt
fragment, and appends the non-empty fragments to the prompt as a single block:

```
## Per-item directives (knobs)

- **changeScope=surgical**: Change scope: SURGICAL. Make the smallest…
- **someOtherKnob=…**: …
```

Finite knobs may display the canonical value in the bullet label. Free-form
knobs never display the raw value in that shared label; a descriptor that opts
in to prompt fragments must delimit, encode, or avoid any raw value it emits in
its own fragment.

Rework, audit, merge, and check-and-act phases are intentionally left alone —
knobs only affect the initial work prompt today. Additional seams can be added
by extending `IKnob` with optional per-phase methods.

A knob whose effective value matches its existing default behaviour should
return `null` from its prompt-fragment method so the prompt stays
byte-identical to the pre-knob output. This is the contract: *"a knob with
nothing to say contributes nothing"*.

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
   work items, and injected by the work-prompt preprocessor.

## Registered knobs

### `changeScope` (default: `moderate`)

How aggressively the agent may restructure adjacent code while making the
requested change.

| Value      | Prompt fragment                                                                                                  |
|------------|------------------------------------------------------------------------------------------------------------------|
| `surgical` | "Smallest possible change; touch only the strictly-required code; do not refactor adjacent code; merge-friendly diff." |
| `moderate` | *(none)* — current default agent behaviour.                                                                       |
| `refactor` | "May restructure or re-architect the affected area to do this well, even with a larger and harder-to-merge diff." |

This item wires `changeScope` into the **work prompt only**. Its audit-side
enforcement (e.g. an auditor that flags out-of-scope edits when `surgical`) and
its merge-friendliness gating ship as separate dependent work items.

## Storage shape

- **WorkItem**: `IReadOnlyDictionary<string, string> Knobs` (case-insensitive
  keys). Persisted as `knobs_json TEXT NOT NULL DEFAULT '{}'` on the
  `work_items` row.
- **Project**: `IReadOnlyDictionary<string, string> Knobs`. Resolved at config
  load time from `Defaults.Knobs` ∪ `Projects[N].Knobs` with per-project
  override winning. Operator-supplied via project config; immutable on the
  resolved Project.
