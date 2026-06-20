# CodeyBox CLI

A typed command-line client for the CodeyBox orchestrator REST API.
Replaces hand-rolled `curl + jq` invocations with a real tool.

## Install

```bash
# Run from source (development)
dotnet run --project tools/CodeyBox.Cli -- <command>

# Publish a self-contained AOT binary
dotnet publish tools/CodeyBox.Cli -c Release -r linux-x64 -o ./bin/codeybox
./bin/codeybox/CodeyBox.Cli <command>
```

## Configure

```bash
codeybox configure
```

Prompts for API base URL and key, saved to `~/.config/codeybox/config.json`:

```json
{
  "apiBaseUrl": "http://localhost:5036",
  "apiKey": "..."
}
```

### Auth resolution order

For every call the CLI resolves credentials in this priority order:

1. `--api-url` / `--api-key` command-line flags
2. `CODEYBOX_CLI_API_URL` / `CODEYBOX_CLI_API_KEY` environment variables
3. `~/.config/codeybox/config.json`
4. Default base URL `http://localhost:5036`; missing key → error with hint

### Global flags

These can be placed before or after any subcommand:

| Flag | Description |
|------|-------------|
| `--api-url <url>` | Override API base URL |
| `--api-key <key>` | Override API bearer token |

## Commands

### `codeybox queue add`

Create a work item:

```bash
# Inline prompt
codeybox queue add --project myapp --title "Add healthz endpoint" --prompt "Add a /healthz endpoint..."

# Prompt from file
codeybox queue add --project myapp --title "Refactor logging" --prompt-file ./prompt.md

# Prompt from stdin (pipe-friendly)
echo "Add a healthz endpoint" | codeybox queue add --project myapp --title "healthz" --prompt-file -

# All options
codeybox queue add \
  --project myapp \
  --title "Refactor logging" \
  --prompt-file ./prompt.md \
  --agent claude \
  --work-branch feat/refactor-logging \
  --push-upstream \
  --depends-on aabbccdd-... \
  --depends-on eeff0011-...
```

Prints the new work item ID to stdout (use `--quiet` for ID only, `--json` for raw JSON).

### `codeybox queue template`

Expand a server-side task template into one queued check-and-act work item per
template entry:

```bash
codeybox queue templates/security --project myapp
codeybox queue template templates/security --project myapp
codeybox queue templates security --project myapp --priority 25 --quiet
```

Templates are read by the API from `CodeyBox:TemplateDirectory` (default
`templates`). Invalid templates fail before any items are queued.

### `codeybox queue ls`

List work items:

```bash
codeybox queue ls
codeybox queue ls --project myapp
codeybox queue ls --state Queued,Working
codeybox queue ls --state Working,Auditing --limit 20
codeybox queue ls --json | jq '.[].id'
codeybox queue ls --quiet   # IDs only, one per line
```

Default output:

```
ID          STATE         AGENT       PROJECT       TITLE                                UPDATED
aabbccdd…   Working       claude      myapp         Refactor logging                     2m ago
eeff0011…   Done          gemini      myapp         Add config validation                 1h ago
```

### `codeybox queue show <id>`

Show full detail for a work item:

```bash
codeybox queue show aabbccdd-...
codeybox queue show myapp:external-123
codeybox queue show aabbccdd-... --json
```

### `codeybox queue cancel <id>`

Cancel (DELETE) a work item:

```bash
codeybox queue cancel aabbccdd-...
```

### Work item queue verbs

Run operator actions that return a resulting work item state:

```bash
codeybox queue abandon aabbccdd-...
codeybox queue uncancel aabbccdd-...
codeybox queue promote aabbccdd-...
codeybox queue resume aabbccdd-...
codeybox queue replay aabbccdd-...
```

Use `--quiet` for state only or `--json` for the raw API response.

### `codeybox queue retry <id>`

Retry a failed work item:

```bash
codeybox queue retry aabbccdd-...
codeybox queue retry aabbccdd-... --from audit
codeybox queue retry aabbccdd-... --from merge
```

`--from` accepts: `work` (default), `audit`, `merge`, `upstream`.

### `codeybox queue watch <id>`

Watch a work item via SSE (`GET /workitems/{id}/events`), printing each state transition as events arrive. Falls back to 2s HTTP polling when SSE is unavailable (older server, connect error, non-200).

```bash
codeybox queue watch aabbccdd-...
codeybox queue watch aabbccdd-... --poll     # force HTTP polling
```

Exits when the item reaches a terminal state (`Done`, `Failed`, `Cancelled`, `AuditFailed`, `MergeConflictResolutionFailed`, `AbandonedAfterRecoveryAttempts`). Intermediate states such as `Merged` are followed until the item finishes.
Press `Ctrl+C` to stop early.

### `codeybox version`

```bash
codeybox version
```

## Common workflows

```bash
# Create and follow a work item
ID=$(echo "Add a /healthz endpoint" | codeybox queue add --project myapp --title "healthz" --prompt-file - --quiet)
codeybox queue watch "$ID"

# Poll the queue for active work
codeybox queue ls --state Queued,Working

# Retry from audit on a failed item
codeybox queue ls --state Failed --quiet | head -1 | xargs codeybox queue retry --from audit

# Pipe list output into jq
codeybox queue ls --json | jq '.[] | select(.state == "Failed") | .id'
```

## Security

- The API key is never written to stdout, stderr, or log files.
- Config file permissions are set by the OS; store the file on a user-only path.
- Use environment variables (`CODEYBOX_CLI_API_KEY`) in CI/CD instead of config files.
