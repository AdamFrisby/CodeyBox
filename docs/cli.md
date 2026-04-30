# CodeyBox CLI — Project Configuration Wizard

`CodeyBox.Cli` is a standalone interactive wizard that walks an operator
through configuring a new project entry and outputs the JSON snippet
to paste into `appsettings.json`.

## Running the wizard

```bash
dotnet run --project src/CodeyBox.Cli
```

No arguments are required. The wizard is fully interactive.

## What it asks

| Step | Prompt | Notes |
|------|--------|-------|
| 1 | **Project ID** | ASCII alphanumeric + `-` and `_`, 1–64 chars |
| 2 | **Display name** | Free-form human-readable label |
| 3 | **Repository URL** | `https://`, `git@`, `ssh://`, or filesystem path |
| 4 | **Base branch** | Defaults to `main` |
| 5 | **Agent** | `claude` · `copilot` · `codex` · `gemini` (default: `claude`) |
| 6 | **Upstream kind** | `noop` · `github` · `git-generic` |
| 6a | *(github)* | GitHub owner, repository name, token env var |
| 6b | *(git-generic)* | Generic URL, optional token env var |
| 7 | **Audit languages** | Multi-select from built-in presets (see below) |
| 8 | **Audit types** | Multi-select from built-in presets (see below) |
| 9 | **Network profiles** | Per-phase profile for Work / Rework / AuditAgent / AuditTool / Merge |

### Audit language presets

`python` · `typescript` · `javascript` · `go` · `rust` · `csharp` · `ruby` · `shell`

### Audit type presets

`security` · `architecture` · `quality` · `completeness` · `cheating` · `tests`

See [projects.md](projects.md) for what each preset runs.

### Network profile names

By default the wizard presents the four built-in profile names (`claude`,
`isolated`, `internet`, `internet-only`) plus a *skip* option. Skip means
the phase inherits its profile from `Defaults.NetworkProfiles`.

To present your own custom profile names instead, set the
`CODEYBOX_NETWORK_PROFILES` environment variable to a comma-separated list
before running the wizard:

```bash
CODEYBOX_NETWORK_PROFILES=claude,restricted,outbound \
  dotnet run --project src/CodeyBox.Cli
```

The names must match keys you have configured in `SandboxNetworkProfiles`
in `appsettings.json`.

## Output

After all prompts the wizard prints the generated JSON. In an interactive
terminal it renders inside a styled panel. When stdout is redirected the
wizard writes plain JSON, making it safe to capture directly:

```bash
dotnet run --project src/CodeyBox.Cli > snippet.json
```

The wizard also offers to write the JSON to a file of your choice before
exiting. Example output:

```json
{
  "Id": "my-app",
  "DisplayName": "My App",
  "RepositoryUrl": "https://github.com/me/my-app.git",
  "BaseBranch": "main",
  "Agent": "claude",
  "Upstream": {
    "Kind": "github",
    "GitHubOwner": "me",
    "GitHubRepository": "my-app",
    "TokenEnvVar": "MY_APP_GITHUB_TOKEN"
  },
  "Audit": {
    "Languages": ["typescript"],
    "AuditTypes": ["security", "architecture", "quality"]
  },
  "NetworkProfiles": {
    "Work": "claude",
    "AuditTool": "isolated"
  }
}
```

Paste the snippet into the `CodeyBox.Projects` array in
`src/CodeyBox.Api/appsettings.json` (or any environment-specific
override file):

```json
{
  "CodeyBox": {
    "Projects": [
      { ... paste here ... }
    ]
  }
}
```

## Building

The project is included in the solution and builds with the rest of
the codebase:

```bash
dotnet build CodeyBox.slnx
```
