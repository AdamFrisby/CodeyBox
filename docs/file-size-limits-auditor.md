# File Size Limits Auditor Plugin

`codeybox.file-size-limits` is a deterministic auditor plugin that flags source
files that grow beyond configured byte or line-count limits. It is discovered
through the normal plugin loader and must be enabled as a project custom plugin
auditor; it is not part of the built-in preset auditor list.

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.FileSizeLimitsAuditorPlugin.dll"],
      "Allowlist": ["codeybox.file-size-limits"]
    },
    "Auditors": {
      "FileSizeLimits": {
        "WarnFileLines": 800,
        "MaxFileLines": 1500,
        "WarnFileBytes": 102400,
        "MaxFileBytes": 153600,
        "GrandfatherMode": "block-growth",
        "IncludeGlobs": ["**/*.cs"],
        "ExcludeGlobs": [
          "**/bin/**",
          "**/obj/**",
          "**/*.generated.cs",
          "**/*.Designer.cs",
          "**/Migrations/**"
        ]
      }
    },
    "Projects": [
      {
        "Id": "my-project",
        "Audit": {
          "Custom": [
            { "Kind": "plugin", "PluginId": "codeybox.file-size-limits" }
          ]
        }
      }
    ]
  }
}
```

## Keys

| Key | Default | Description |
|---|---:|---|
| `WarnFileLines` | `800` | Warning threshold for line count. `0` disables line warnings. |
| `MaxFileLines` | `1500` | Blocking threshold for line count. `0` disables line blocking. |
| `WarnFileBytes` | `102400` | Warning threshold for file bytes. `0` disables byte warnings. |
| `MaxFileBytes` | `153600` | Blocking threshold for file bytes. `0` disables byte blocking. |
| `GrandfatherMode` | `block-growth` | `block-growth` blocks new over-cap files and already-over-cap files that grew versus the base branch. `strict` blocks every over-cap file. |
| `IncludeGlobs` | `["**/*.cs"]` | Files to audit. |
| `ExcludeGlobs` | see example | Files to skip, defaulting to build output, generated/designer files, and migrations. |

Line and byte caps are independent. A file can block on lines while staying
under the byte cap, or block on bytes while staying under the line cap. The
auditor reads `CodeyBox:Auditors:FileSizeLimits` on each run, so config changes
apply to the next audit invocation.
