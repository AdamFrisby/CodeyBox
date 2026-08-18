# Releases and changelogs

Two related features, both opt-in per project: **releases** group work items
onto a shared branch and gate it behind a codebase-wide deep audit, and
**changelog automation** turns a published GitHub release into a `CHANGELOG.md`
entry.

## Releases

A release is a named group of work items whose branches target
`release/{name}` instead of `main`. When every item in a closed release reaches
a terminal state, CodeyBox runs a deep audit — OWASP ASVS review, architecture
coherence, dependency CVE scan — over the whole codebase before merging the
release branch to `main` and optionally tagging it on GitHub.

Projects with `release.enabled = false` (the default) behave exactly as before.

```
open ─── close ──▶ closed ─── all items terminal ──▶ in_review
  │                  │                                    │
  │               abandon                           passes audit ──▶ released
  │                  │                                    │
  │                  ▼                               fails audit ──▶ failed ─── reopen ──▶ open
  └───── abandon ──▶ abandoned
```

| State | Meaning |
|---|---|
| `open` | Accepting new work items. The release branch is created on first work-item pickup. |
| `closed` | No new items; waiting for in-flight ones to finish. |
| `in_review` | Deep audit running. Remediation work items may be created automatically. |
| `released` | Branch merged to main, GitHub tag created if configured. |
| `failed` | Deep audit exceeded `deepAuditMaxIterations`. Reopen to add remediation items. |
| `abandoned` | Manually discarded from any non-`released` state. |

### Configuration

```yaml
projects:
  - id: my-app
    repositoryUrl: https://github.com/example/my-app
    release:
      enabled: true                         # false by default
      branchNameTemplate: "release/{name}"
      autoSyncMainIntervalMinutes: 720      # 12h; 0 disables
      deepAuditors:
        - owasp-asvs
        - arch-coherence
        - deps-cve-scan
      deepAuditMaxIterations: 5
      targetTag: ""                         # set to create a GitHub tag
```

`ReleaseMainSyncService` merges `main` into every open release branch every
`autoSyncMainIntervalMinutes`, so the branch does not drift far enough to make
the final merge painful. It never auto-resolves: a conflict emits a
`release.sync_conflict` webhook, logs a warning, and waits for a human to fix
and push.

An empty `deepAuditors` list skips the deep audit and goes straight to
`released` once all items are terminal. The built-ins:

| Name | Kind | What it does |
|---|---|---|
| `owasp-asvs` | LLM | OWASP ASVS L1/L2 review (V2, V3, V5, V7, V8, V9, V13) |
| `arch-coherence` | LLM | layer violations, circular dependencies, god objects, hardcoded config |
| `deps-cve-scan` | shell | per-language dependency scan; Critical/High are errors, Moderate/Medium warnings |

`deps-cve-scan` dispatches on `Project.Audit.Languages`: C# runs
`dotnet list package --vulnerable --include-transitive` against
`https://api.nuget.org/v3/index.json`, Python `pip-audit`/`safety`, Node
`npm audit --json` pinned to `https://registry.npmjs.org/` with repository npm
proxy settings blocked, Go `govulncheck -json ./...`, Rust `cargo audit`.
Languages with no marker file in the repository are skipped, and a declared
language whose scanner is missing from the sandbox emits an Info finding rather
than failing the audit. Omitting `Project.Audit.Languages` scans C# only; set it
to `[]` to run no language scanners at all.

Network-using tool auditors like `deps-cve-scan` need a provider that can
enforce `AuditToolAllowedHosts`. Multipass and Incus can, through the audit-tool
network profile. Bubblewrap cannot enforce a per-host allowlist, so CodeyBox
blocks these auditors there rather than handing them unrestricted host network
access. Custom deep auditors register through the plugin system — see
[`../extending/plugins.md`](../extending/plugins.md).

### Putting work items in a release

```
POST /workitems
{ "projectId": "my-app", "title": "Add JSON config support", "prompt": "...", "releaseId": "a1b2c3d4..." }
```

The release must exist, belong to the same project, and be `open`, with release
management enabled for the project. The orchestrator then overrides
`baseBranch` to the release branch — such items never target `main` — and
creates the branch atomically on first pickup.

Endpoints are grouped under `/releases` in
[`../reference/api.md`](../reference/api.md); events are prefixed `release.`
in [`../reference/webhooks.md`](../reference/webhooks.md).

## Changelog automation

When a GitHub release is published, CodeyBox walks the merged PRs between the
previous tag and the new one, asks an LLM to summarise and categorise them, and
either returns the markdown (manual call) or opens a work item that writes it
into `CHANGELOG.md` (webhook flow).

```
GitHub release published
        │
        ▼  POST /webhooks/github/release — HMAC-SHA256 validated,
        │  project resolved by repository URL, previous tag from the Releases API
        ▼
IPullRequestEnumerator — commits between fromTag→toTag via the Compare API,
        │  PR numbers from merge-commit messages, title+body per PR (cap 200)
        ▼
IChangelogGenerator — redact secrets, batch if >100 KB, call the Messages API,
        │  parse into markdown + category map
        ▼
WorkItem "apply this changelog entry to CHANGELOG.md" → the normal pipeline
```

Unlike coding work, this LLM call is made by the orchestrator process itself,
not inside a sandbox.

### Configuration

```json
{
  "CodeyBox": {
    "Changelog": {
      "Enabled": true,
      "GeneratorAgent": "claude",
      "GeneratorModelId": "claude-opus-4-7",
      "ChangelogPath": "CHANGELOG.md",
      "SectionHeaderFormat": "## [{tag}] - {date:yyyy-MM-dd}",
      "GitHubWebhookSecretEnvVar": "CODEYBOX_GH_RELEASE_WEBHOOK_SECRET"
    }
  }
}
```

| Field | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Global switch. |
| `GeneratorAgent` | `"claude"` | The only supported generator today. |
| `GeneratorModelId` | `"claude-opus-4-7"` | Model passed to the Anthropic API. |
| `ChangelogPath` | `"CHANGELOG.md"` | Path inside the repository. |
| `SectionHeaderFormat` | `"## [{tag}] - {date:yyyy-MM-dd}"` | Supports `{tag}` and `{date:yyyy-MM-dd}`. |
| `GitHubWebhookSecretEnvVar` | `null` | Env var holding the HMAC secret. With no secret configured, signatures are not verified — do not run that way in production. |

Per-project overrides go in the project's entry:

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

### Wiring the webhook

1. Generate a secret and export it under the name in
   `GitHubWebhookSecretEnvVar`:
   `export CODEYBOX_GH_RELEASE_WEBHOOK_SECRET="$(openssl rand -hex 32)"`.
2. On GitHub: repo → Settings → Webhooks → Add webhook. Payload URL
   `https://your-host/webhooks/github/release`, content type
   `application/json`, the same secret, and **Releases** as the only event.
3. Publish a release.

The endpoint validates `X-Hub-Signature-256` (HMAC-SHA256), returns `202` right
away, and does the edit through the normal work-item pipeline. An absent or
wrong signature gets a `401` with no body. The secret is never logged, and no
payload data is logged.

### Generating one by hand

```http
POST /projects/{id}/release
Authorization: Bearer <CODEYBOX_API_KEY>
Content-Type: application/json

{ "fromTag": "v1.2.0", "toTag": "v1.3.0" }
```

returns the markdown synchronously:

```json
{
  "markdown": "## [v1.3.0] - 2026-05-15\n\n### Added\n- ...\n",
  "categoryToPrNumbers": { "Added": [16, 18], "Fixed": [17] },
  "wasCapped": false
}
```

`400` means a missing tag, no GitHub upstream on the project, or changelog
generation disabled for it; `404` is an unknown project.

`wasCapped: true` means the range held more than 200 PRs and the oldest were
dropped — re-run over a narrower tag range for full coverage. Above 100 KB of
combined PR titles and bodies the generator summarises in batches and merges the
partial summaries in a final pass, keeping each call inside the model's context
window.

If the repository has no `CHANGELOG.md`, the work-item prompt tells the agent to
create it. For a first run, point `fromTag` at the earliest tag you care about.

PR bodies pass through `RawOutputRedactor` before reaching the model, replacing
GitHub PATs, Anthropic keys, and Google API keys with `***`. GitHub PATs
themselves are read from the environment at call time and never stored.
