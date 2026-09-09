# Presets: languages and audit types

Two preset catalogues decide what the audit phase actually runs. A **language
preset** says how to detect a language in a repository and which tool auditors
it gets. An **audit type** bundles the tool auditors, deterministic diff
patterns, and LLM review focus for one review dimension — security,
completeness, accessibility, whatever you define.

Both compose from four layers, later layers winning:

1. built-in defaults, embedded in `CodeyBox.Audit.Presets/Defaults/`;
2. repository files — `codeybox/languages/*.yaml`,
   `codeybox/audit-types/*.yaml` — read only when the project repository is
   available as a local `file://` worktree, and treated as **untrusted**;
3. global operator config under `CodeyBox:Presets:LanguageOverrides` /
   `CodeyBox:Presets:AuditTypeOverrides`;
4. per-project appsettings.

Everything is schema-validated before use, whatever the layer. Invalid YAML,
unknown or missing fields, bad LLM placeholders, and likely command typos fail
startup loudly, naming the file and a JSON-pointer field location — `argv:
["dottest"]` reports `did you mean 'dotnet'?`, and a selected id of `cshrap`
or `securty` fails with a did-you-mean against the composed catalogue.

## Language presets

Defaults live in `Defaults/languages`; a repository extends or replaces them
from `codeybox/languages/<language-id>.yaml`.

### Schema

```yaml
id: elixir
displayName: "Elixir"
marker:
  globs: ["**/mix.exs"]
  # Optional script form. It must print project directories, one per line.
  script: |
    find . -name mix.exs -exec dirname {} \; | sort -u
auditors:
  - name: elixir:test-pass
    argv: ["mix", "test"]
  - name: elixir:format-check
    script: "mix format --check-formatted"
    toolName: "mix"
    treatExit127AsMissingTool: true
```

`id`, `marker`, and `auditors` are required for a new language. For an existing language, a file with the same `id` appends auditors by default:

```yaml
id: csharp
auditors:
  - name: csharp:custom
    argv: ["dotnet", "tool", "run", "custom-check"]
```

Set `replace: true` to replace the built-in auditor list. For an existing language, detection markers are preserved when the override omits `marker`:

```yaml
id: csharp
replace: true
marker:
  globs: ["**/*.csproj"]
auditors:
  - name: csharp:test-pass
    argv: ["dotnet", "test"]
```

### What a repository may not set

A language preset defines shell commands, so a repository file is validated
before its auditors are expanded and two fields are rejected outright:

- `/marker/script` — use `/marker/globs`.
- `/auditors/[]/script` — use `/auditors/[]/argv`.

Operator configuration is trusted and carries neither restriction.

### Per-project overrides

Per-project appsettings can tune a language with `Audit.Languages.Overrides.<language-id>`. These overrides append to defaults unless `Replace` is true:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "alpha",
        "Audit": {
          "Languages": {
            "0": "csharp",
            "Overrides": {
              "csharp": {
                "Replace": true,
                "Auditors": [
                  { "Name": "csharp:test-pass", "Argv": ["dotnet", "test"] }
                ]
              }
            }
          }
        }
      }
    ]
  }
}
```

## Audit types

Defaults live in `Defaults/audit-types`; a repository overrides or adds them
from `codeybox/audit-types/<audit-type-id>.yaml`.

### Schema

```yaml
id: accessibility
displayName: "Accessibility review"
llmAuditorName: accessibility:llm-review
auditors:
  - name: accessibility:axe-linter
    argv: ["npm", "run", "axe-check"]
patterns:
  - regex: 'aria-hidden="true"'
    description: "Manual aria-hidden check"
reviewFocus: |
  - Missing labels or names for interactive controls
  - Keyboard traps and unreachable controls
  - Color-only state or contrast regressions
```

All audit types, including built-ins such as `security`, `completeness`, `cheating`, and `tests`, use this configuration-driven mechanism. New audit types can combine shell tools, deterministic diff-patterns, and LLM review auditors.

`auditors`, `patterns`, and `reviewFocus` are optional. An audit type will include:
- A shell auditor for each entry in `auditors`.
- A `DiffPatternAuditor` if `patterns` is non-empty.
- An `LlmReviewAuditor` if `reviewFocus` is non-empty.

### Composition and overrides

For an existing audit type, a YAML file with the same `id` appends auditors and patterns by default, and replaces the `reviewFocus` if supplied (only in trusted configuration). Set `replace: true` to replace the entire definition.

Three fields are rejected in a repository file, because each would let an
untrusted repository steer an LLM auditor that has filesystem and network
access:

- `/llmAuditorName` — only built-in or plugin-provided names are accepted.
- `/reviewFocus` — prompt tuning belongs in trusted project config.
- `/auditors/[]/script` — use `/auditors/[]/argv`.

Per-project appsettings can tune audit voices with `Audit.AuditTypes.<id>.ReviewFocus`. You can also override auditors and patterns from appsettings:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "alpha",
        "Audit": {
          "AuditTypes": {
            "security": {
              "ReviewFocus": "- Project-specific auth and tenant-boundary checks",
              "Auditors": [
                { "Name": "security:custom-scanner", "Argv": ["custom-scan", "."] }
              ]
            }
          }
        }
      }
    ]
  }
}
```

The existing list form still works when no prompt or auditor overrides are needed:

```json
{
  "AuditTypes": ["security", "completeness", "cheating"]
}
```

### The LLM prompt frame

The LLM review frame is loaded from `Defaults/llm-prompt-frame.yaml` and can be overridden by operator startup config at:

```text
codeybox/llm-prompt-frame.yaml
```

Allowed placeholders are:

```text
{{workingDirectory}}
{{reviewFocus}}
{{baseBranch}}
{{workBranch}}
{{originalPrompt}}
{{resultFile}}
```

Unknown placeholders fail startup with a clear validation error. Selected audit-type ids are validated against the composed catalog, so typos such as `securty` fail startup with a did-you-mean suggestion. The LLM auditor retry and JSON parsing logic remains in code; the configurable data is limited to review focus text and the surrounding frame template.

Per-project appsettings can also override the frame with `Audit.LlmPromptFrameTemplate`. Repository-owned prompt files are loaded when the project repository is available as a local `file://` worktree/path; appsettings overrides are applied last. Prompt overrides are powerful because these values are fed into filesystem/network-capable LLM auditors, so production operators should decide which repositories may supply them.
