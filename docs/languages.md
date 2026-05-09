# Config-Driven Language Presets

Language presets are loaded at startup from YAML. Built-in defaults ship as embedded resources under `CodeyBox.Audit.Presets/Defaults/languages`, and project-level files can extend or replace them from:

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

`id`, `marker`, and `auditors` are required for a new language. For an existing language, a project file with the same `id` appends auditors by default:

```yaml
id: csharp
auditors:
  - name: csharp:custom
    argv: ["dotnet", "tool", "run", "custom-check"]
```

Set `replace: true` to replace the built-in language definition:

```yaml
id: csharp
replace: true
marker:
  globs: ["**/*.csproj"]
auditors:
  - name: csharp:test-pass
    argv: ["dotnet", "test"]
```

## Validation

All built-in and project YAML is validated at startup. Invalid YAML, missing required fields, bad LLM placeholders, and common command typos fail startup with the file path and a JSON-pointer-style field location. For example, `argv: ["dottest"]` reports a `did you mean 'dotnet'?` diagnostic.

Project configuration under `CodeyBox:Presets:LanguageOverrides` is applied after files, so appsettings wins over `codeybox/languages`.
