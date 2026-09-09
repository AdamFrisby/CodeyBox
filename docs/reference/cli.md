# Command-line tools

Two separate binaries, easy to confuse:

| Tool | Project | What it does |
|---|---|---|
| `codeybox` | `tools/CodeyBox.Cli` | typed client for the REST API — queue, inspect, watch, pause. Full command reference in [`../../tools/CodeyBox.Cli/README.md`](../../tools/CodeyBox.Cli/README.md). |
| the wizard | `src/CodeyBox.Cli` (`CodeyBox.Wizard`) | interactive project-configuration generator; prints a JSON snippet for your config file. Documented below. |

## The project-configuration wizard

It asks a fixed sequence of questions and writes nothing — you paste the
result. No arguments:

```bash
dotnet run --project src/CodeyBox.Cli
```

### What it asks

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

`csharp` · `python` · `node` · `go` · `rust`

### Audit type presets

`security` · `architecture` · `quality` · `completeness` · `cheating` · `tests`

See [projects.md](../concepts/projects.md) for what each preset runs.

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

### Output

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
    "Languages": ["node"],
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

## Pausing an agent

The `codeybox` client exposes per-agent runtime pause controls:

```bash
codeybox agents pause claude --reason "reserve quota for oversight" --for 6h
codeybox agents pause claude/acct-a --reason "account flagged today"
codeybox agents resume claude
codeybox agents resume claude/acct-a
codeybox agents paused --json
```

`--for` accepts `s`, `m`, `h`, or `d` suffixes. Pausing affects new dispatch
only; in-flight runs continue. A route key such as `claude/acct-a` pauses only
that pooled subscription instance.
