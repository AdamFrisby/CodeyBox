# Releases

A **release** is a named grouping of work items whose PRs all target a shared
release branch rather than `main` directly. When all work items in a closed
release reach a terminal state, CodeyBox automatically runs a codebase-wide
**deep audit** (OWASP ASVS checks, architecture coherence, CVE scans) before
merging the release branch into `main` and optionally creating a GitHub
release tag.

Releases are **opt-in per project**. Projects with `release.enabled = false`
(the default) see zero behavior change.

---

## State machine

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
| `open` | Accepting new work items. Release branch created on first work item pickup. |
| `closed` | No new work items; waiting for in-flight items to finish. |
| `in_review` | Deep audit running. Remediation work items may be created automatically. |
| `released` | Branch merged to main; GitHub release tag created (if configured). |
| `failed` | Deep audit exceeded `maxIterations`. Human review required. Reopen to add remediation items. |
| `abandoned` | Manually discarded from any non-`released` state. |

---

## Configuration

Enable releases in `codeybox.yaml` (or `codeybox.jsonc`):

```yaml
projects:
  - id: my-app
    repositoryUrl: https://github.com/example/my-app
    release:
      enabled: true                 # required; false by default
      branchNameTemplate: "release/{name}"  # default
      autoSyncMainIntervalMinutes: 720      # 12 h; 0 = disable
      deepAuditors:
        - owasp-asvs
        - arch-coherence
        - deps-cve-scan
      deepAuditMaxIterations: 5            # default
      targetTag: ""                        # optional; set to create a GitHub tag
```

### `autoSyncMainIntervalMinutes`

The `ReleaseMainSyncService` background service merges `main` into every open
release branch at this interval. This keeps the release branch from diverging
too far from `main`, reducing the final-merge conflict surface. Set to `0` to
disable; defaults to `720` (12 hours).

On merge conflict the service emits a `release.sync_conflict` webhook and logs
a warning. It does **not** auto-resolve conflicts — a human must fix the
conflict and push to the release branch.

### `deepAuditors`

Names of the built-in deep auditors to run during `in_review`. Available
built-in auditors:

| Name | Kind | Description |
|---|---|---|
| `owasp-asvs` | llm | OWASP ASVS L1/L2 security review (V2, V3, V5, V7, V8, V9, V13) |
| `arch-coherence` | llm | Architecture coherence: layer violations, circular deps, god objects, hardcoded config |
| `deps-cve-scan` | shell | Dispatches by `Project.Audit.Languages`: C# `dotnet list package --vulnerable --include-transitive` with NuGet pinned to `https://api.nuget.org/v3/index.json`, Python `pip-audit`/`safety`, Node `npm audit --json --registry https://registry.npmjs.org/`, Go `govulncheck -json ./...`, Rust `cargo audit`. Critical/High = Error, Moderate/Medium = Warning |

LLM auditors require agent credentials. The shell auditor (`deps-cve-scan`)
requires the matching language scanner to be installed in the sandbox image;
if a declared language's scanner is absent it emits an Info finding and passes
rather than failing. Languages without marker files in the repository are
skipped. If `Project.Audit.Languages` is omitted or explicitly empty,
`deps-cve-scan` runs no language-specific scanners.

Custom auditors can be registered via the plugin system (see `plugins.md`).

An empty `deepAuditors` list skips the deep audit entirely and transitions
directly to `released` once all work items are terminal.

---

## Work items in a release

Set `releaseId` when creating a work item to associate it with a release:

```
POST /workitems
{
  "projectId": "my-app",
  "title": "Add JSON config support",
  "prompt": "...",
  "releaseId": "a1b2c3d4..."
}
```

Constraints:
- The release must exist, belong to the same project, and be in `Open` state.
- Release management must be enabled for the project (`release.enabled = true`).

The orchestrator overrides `baseBranch` to the release branch for any work
item with a `releaseId`. The release branch is created atomically on the first
work item pickup (SETIFNULL).

Work items added to a release **do not** target `main` — their PRs and work
branches are opened against `release/{name}` instead.

---

## API

See [`api.md`](api.md) for the full endpoint reference. Release endpoints are
grouped under `/releases`.

---

## Webhooks

See [`webhooks.md`](webhooks.md) for the full webhook event reference. Release
events are prefixed `release.`.
