# Config-Driven Language Presets

Language presets are loaded from configuration. Built-in defaults ship as embedded resources under `CodeyBox.Audit.Presets/Defaults/languages`, and a project repository can extend or replace them from:

```text
codeybox/languages/<language-id>.yaml
```

## Schema

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

## Trust Boundary

Language presets can define shell commands. The orchestrator reads repository-owned `codeybox/languages` when the project repository is available as a local `file://` worktree/path at startup or composition time, then validates the resulting catalog before expanding auditors. Appsettings overrides remain the operator-controlled layer and win over repository files.

## Validation

All built-in YAML, repository YAML, operator YAML, and appsettings preset overrides are schema-validated before use. Invalid YAML, unknown fields, missing required fields, bad LLM placeholders, and common command typos fail loudly with the file path and a JSON-pointer-style field location. Selected language ids are also validated against the composed catalog; typos such as `cshrap` fail startup with a did-you-mean suggestion. For example, `argv: ["dottest"]` reports a `did you mean 'dotnet'?` diagnostic.

Global operator configuration under `CodeyBox:Presets:LanguageOverrides` is applied after files, so appsettings wins over `codeybox/languages`.

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
