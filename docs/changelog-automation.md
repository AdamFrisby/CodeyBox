# Changelog Automation

CodeyBox can automatically generate `CHANGELOG.md` entries when a GitHub release is
published. The orchestrator walks the merged PR history between two tags, asks an LLM
to summarise and categorise the changes, and either returns the text directly (manual
flow) or creates a work item that applies the update to the repo (webhook flow).

---

## How it works

```
GitHub release published
        │
        ▼
POST /webhooks/github/release
  • HMAC-SHA256 validated
  • Project resolved by repository URL
  • Previous tag looked up via GitHub Releases API
        │
        ▼
IPullRequestEnumerator
  • GitHub Compare API: commits between fromTag → toTag
  • Extract PR numbers from merge-commit messages
  • Fetch each PR's title + body (cap: 200 PRs)
        │
        ▼
IChangelogGenerator (Claude)
  • Redact secrets from PR bodies
  • Build structured prompt (batch if > 100 KB)
  • Call Anthropic Messages API
  • Parse response into Markdown + category map
        │
        ▼
WorkItem created
  Prompt: "apply this changelog entry to CHANGELOG.md"
  → normal CodeyBox pipeline (audit → merge → upstream push)
```

---

## Configuration

Global options live under `CodeyBox:Changelog` in `appsettings.json`:

```json
{
  "CodeyBox": {
    "Changelog": {
      "Enabled": true,
      "GeneratorAgent": "claude",
      "GeneratorModelId": "claude-opus-4-8",
      "ChangelogPath": "CHANGELOG.md",
      "SectionHeaderFormat": "## [{tag}] - {date:yyyy-MM-dd}",
      "GitHubWebhookSecretEnvVar": "CODEYBOX_GH_RELEASE_WEBHOOK_SECRET"
    }
  }
}
```

| Field | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master switch. Set `false` to disable globally. |
| `GeneratorAgent` | `"claude"` | LLM agent for generation. Only `"claude"` is currently supported. |
| `GeneratorModelId` | `"claude-opus-4-8"` | Model ID passed to the Anthropic API. |
| `ChangelogPath` | `"CHANGELOG.md"` | Path to the changelog file within the repo. |
| `SectionHeaderFormat` | `"## [{tag}] - {date:yyyy-MM-dd}"` | Header template. Supports `{tag}` and `{date:yyyy-MM-dd}`. |
| `GitHubWebhookSecretEnvVar` | `null` | Name of the env var holding the HMAC secret for GitHub webhooks. When `null`, signatures are not verified (not recommended for production). |

### Per-project overrides

Add a `Changelog` block to the project's entry in `projects.json`:

```json
{
  "id": "my-app",
  "changelog": {
    "enabled": true,
    "changelogPath": "docs/CHANGELOG.md",
    "sectionHeaderFormat": "## {tag} ({date:yyyy-MM-dd})"
  }
}
```

---

## Webhook setup (automatic mode)

1. **Set the secret** in the operator environment:
   ```bash
   export CODEYBOX_GH_RELEASE_WEBHOOK_SECRET="$(openssl rand -hex 32)"
   ```

2. **Point the `GitHubWebhookSecretEnvVar` config** to that variable name:
   ```json
   { "CodeyBox": { "Changelog": { "GitHubWebhookSecretEnvVar": "CODEYBOX_GH_RELEASE_WEBHOOK_SECRET" } } }
   ```

3. **Register the webhook on GitHub**:
   - Go to repo → Settings → Webhooks → Add webhook.
   - Payload URL: `https://your-host/webhooks/github/release`
   - Content type: `application/json`
   - Secret: same value as `CODEYBOX_GH_RELEASE_WEBHOOK_SECRET`
   - Events: select **Releases** only.

4. **Publish a release** on GitHub. CodeyBox receives the webhook, generates a
   changelog entry, and creates a work item that applies it to `CHANGELOG.md`.

The endpoint returns `202 Accepted` immediately. The actual CHANGELOG.md edit
happens through the normal work item pipeline (agent → audit → merge → upstream push).

### HMAC validation

Incoming requests are validated against the `X-Hub-Signature-256` header using
HMAC-SHA256 with the configured secret. A missing or invalid signature returns
`401` with no body. The secret is **never** logged.

---

## Manual invocation (preview / regenerate)

```http
POST /projects/{id}/release
Authorization: Bearer <CODEYBOX_API_KEY>
Content-Type: application/json

{
  "fromTag": "v1.2.0",
  "toTag":   "v1.3.0"
}
```

Returns the generated markdown text synchronously:

```json
{
  "markdown": "## [v1.3.0] - 2026-05-15\n\n### Added\n- ...\n",
  "categoryToPrNumbers": {
    "Added": [16, 18],
    "Fixed": [17]
  },
  "wasCapped": false
}
```

`wasCapped: true` means the release had more than 200 PRs and the oldest were
omitted. Re-run with a narrower tag range if full coverage is needed.

### Error responses

| Status | Reason |
|---|---|
| `400` | `fromTag` or `toTag` missing; project has no GitHub upstream configured; changelog disabled for this project. |
| `404` | Unknown project ID. |

---

## Bootstrapping on a repo with no CHANGELOG.md

The work item prompt instructs the agent to **create** `CHANGELOG.md` if it does
not exist. The agent will prepend the generated entry and commit the new file.

On first use, set `fromTag` to the earliest tag in your history (or omit it and
set `fromTag` to `HEAD~1` as a sentinel — you'll get a partial list).

---

## PR cap and batching

The enumerator caps at **200 PRs per release**. If a release contains more,
`wasCapped: true` is set and the oldest PRs (beyond 200) are omitted.

When the combined title + body payload exceeds **100 KB**, the generator splits
the PRs into batches, summarises each batch independently, then merges the partial
summaries in a final LLM pass. This keeps individual API calls well within
Anthropic's context limits while handling large releases.

---

## Security

- GitHub PATs are read from environment variables at call time, never stored.
- PR bodies are run through `RawOutputRedactor` before being sent to the LLM,
  replacing any GitHub PATs, Anthropic keys, or Google API keys with `***`.
- The webhook endpoint (`/webhooks/github/release`) is exempt from API key
  authentication but validates HMAC-SHA256.
- No webhook payload data is logged.
