# Config-Driven Audit Type Prompts

Audit-type LLM review focus prompts are loaded from YAML at startup. Built-in defaults are embedded under `CodeyBox.Audit.Presets/Defaults/audit-types`, and project files can override or add audit types from:

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

Project configuration under `CodeyBox:Presets:AuditTypeOverrides` is applied after `codeybox/audit-types`, so appsettings wins over project files:

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

The LLM review frame is loaded from `Defaults/llm-prompt-frame.yaml` and can be overridden at:

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
