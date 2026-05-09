# Config-Driven Audit Type Prompts

Audit-type LLM review focus prompts are loaded from operator-controlled configuration. Built-in defaults are embedded under `CodeyBox.Audit.Presets/Defaults/audit-types`, and startup config under the service content root can override or add audit types from:

```text
codeybox/audit-types/<audit-type-id>.yaml
```

## Audit Type Schema

```yaml
id: accessibility
displayName: "Accessibility review"
llmAuditorName: accessibility:llm-review
reviewFocus: |
  - Missing labels or names for interactive controls
  - Keyboard traps and unreachable controls
  - Color-only state or contrast regressions
```

Known audit types such as `security`, `completeness`, `cheating`, and `tests` keep their code-owned auditor behavior. Only their review focus text is configurable. New audit types become LLM review auditors by default.

Per-project appsettings can tune audit voices with `Audit.AuditTypes.<id>.ReviewFocus`. In object form, the `AuditTypes` keys are also the selected audit types for that project:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "alpha",
        "Audit": {
          "AuditTypes": {
            "security": {
              "ReviewFocus": "- Project-specific auth and tenant-boundary checks"
            },
            "completeness": {
              "ReviewFocus": "- Product acceptance criteria from the work item"
            }
          }
        }
      }
    ]
  }
}
```

The existing list form still works when no prompt overrides are needed:

```json
{
  "AuditTypes": ["security", "completeness", "cheating"]
}
```

Global operator configuration under `CodeyBox:Presets:AuditTypeOverrides` is applied after `codeybox/audit-types`, and per-project appsettings are applied last:

```json
{
  "CodeyBox": {
    "Presets": {
      "AuditTypeOverrides": {
        "completeness": {
          "ReviewFocus": "- Product-specific acceptance criteria"
        }
      }
    }
  }
}
```

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

Unknown placeholders fail startup with a clear validation error. The LLM auditor retry and JSON parsing logic remains in code; the configurable data is limited to review focus text and the surrounding frame template.

Per-project appsettings can also override the frame with `Audit.LlmPromptFrameTemplate`. The orchestrator does not read audit-type or frame YAML from audited repositories; repository changes to `codeybox/audit-types` and `codeybox/llm-prompt-frame.yaml` are ignored by audit composition. Prompt overrides are powerful and should remain operator-reviewed because these values are fed into filesystem/network-capable LLM auditors.
