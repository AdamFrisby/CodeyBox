# Config-Driven Audit Type Prompts

Audit-type auditors, deterministic patterns, and LLM review focus prompts are loaded from configuration. Built-in defaults are embedded under `CodeyBox.Audit.Presets/Defaults/audit-types`, and a project repository can override or add audit types from:

```text
codeybox/audit-types/<audit-type-id>.yaml
```

## Audit Type Schema

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

## Composition and Overrides

For an existing audit type, a YAML file with the same `id` appends auditors and patterns by default, and replaces the `reviewFocus` if supplied (only in trusted configuration). Set `replace: true` to replace the entire definition.

**Security Restriction**: Repository-provided configuration (`codeybox/audit-types/*.yaml`) is considered untrusted. For security reasons, the following fields are NOT allowed in repository files:
- `/llmAuditorName`: Only built-in or plugin-provided names are allowed.
- `/reviewFocus`: LLM prompt tuning is restricted to trusted project configuration (`appsettings.json`) to prevent prompt-injection attacks from untrusted repositories.
- `/auditors/[]/script`: Use `/auditors/[]/argv` instead.

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

Global operator configuration under `CodeyBox:Presets:AuditTypeOverrides` is applied after `codeybox/audit-types`, and per-project appsettings are applied last.

## Frame Prompt

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
