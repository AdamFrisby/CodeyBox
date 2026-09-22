# CodeyBox: Forgejo Upstream (`codeybox.forgejo-upstream`)

First-class upstream remote for [Forgejo](https://forgejo.org) (self-hosted).
One project, one plugin. **Off unless an operator enables it.**

## Enablement

1. Add this assembly to `CodeyBox:Plugins:AssemblyPaths`.
2. Add `codeybox.forgejo-upstream` to `CodeyBox:Plugins:Allowlist`.
3. Set `Upstream.Kind = "forgejo"` on the project.

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.forgejo-upstream"],
      "AssemblyPaths": ["/opt/codeybox/plugins/CodeyBox.ForgejoUpstreamPlugin.dll"]
    },
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "https://forge.example.com/team/repo.git",
        "Upstream": {
          "Kind": "forgejo",
          "TokenEnvVar": "FORGEJO_TOKEN",
          "AutoMerge": true,
          "MergeMethod": "squash",
          "PluginConfig": {
            "BaseUrl": "https://forge.example.com/api/v1",
            "Owner": "team",
            "Repository": "repo"
          }
        }
      }
    ]
  }
}
```

## Configuration

Per-project `Upstream.PluginConfig` keys (highest precedence):

| Key | Required | Meaning |
|-----|----------|---------|
| `BaseUrl` | yes | Forgejo **API** base, e.g. `https://forge.example.com/api/v1`. The instance root is accepted too (`/api/v1` is appended). `http` is allowed — LAN instances are the norm for self-hosted forges. Must not embed credentials. |
| `Owner` | yes | Repository owner (user or organisation). |
| `Repository` | yes | Repository name. |
| `PageSize` | no | Items per API page, 1–100 (default 50, the Forgejo default). |
| `MaxListPages` | no | Page cap per listing, 1–50 (default 10). Bounds every list so a large repository yields a bounded answer, never an unbounded buffer. |

Calls that carry no project context — base-branch fetch, PR listing/reads,
release-sync merges, and all extended read surfaces — fall back to the
plugin-scoped section `CodeyBox:Plugins:codeybox.forgejo-upstream` with the
same keys (plus `TokenEnvVar`, naming the env var that holds the token).
Single-instance operators can therefore configure the repository once;
multi-repository operators should use per-project `PluginConfig` for the
write path and point the scoped fallback at the repository they want swept.

### Credentials

The token **never** appears in configuration files. The operator puts a
Forgejo API token (scope `write:repository` for PR write/merge, `read`
scopes suffice for read-only surfaces) in an environment variable, names it
in `Upstream.TokenEnvVar`, and the orchestrator forwards only the *name*.
This remote reads the value with
`Environment.GetEnvironmentVariable(name)` at call time, sends it as an
`Authorization: token …` header (Forgejo's documented scheme) and via a
short-lived `GIT_ASKPASS` script for git pushes. The value is redacted from
every error message, never logged, and never mounted into a sandbox — the
plugin references no sandbox assembly at all (covered by test).

## What is supported

Core lifecycle (`IUpstreamRemote`):

- Push work branch (inside `CompleteAsync`), open PR, optionally auto-merge.
- Reuse of `ExistingPullRequestNumber` for the orchestrator's race-recovery
  re-run (create is skipped, the still-open PR is merged).
- Release-sync branch merge via a host-side temp clone (`TryMergeUpstreamBranchAsync`).
- Base-branch fetch (`FetchBaseBranchAsync`); PR listing with prefix filter
  and forge-computed mergeability; PR state reads (open/closed/merged +
  merge sha).

Extended surfaces (all genuinely Forgejo API v1):

- **Reviews** — individual verdicts plus requested reviewers and a
  protection-derived quorum. `RequirementsMet` counts non-dismissed,
  non-stale approvals against the base branch's `required_approvals` and
  treats an outstanding change request as blocking (the common Forgejo
  setup; see approximations below).
- **Checks** — commit statuses plus the forge-computed combined state,
  which is what gates `RequiredChecksPassed`.
- **Comments** — plain issue comments, list and post.
- **Webhooks** — repository hooks, list/create/delete.
- **Repository metadata** — default branch, visibility
  (`public`/`private`/`internal`), branch protection rules.

Failure classification: unreachable / unauthorised / forbidden /
rate-limited / 5xx throw `ForgejoUpstreamException` (infrastructure — the
orchestrator retries, never a verdict on the diff). PR-already-exists
(409/422 on create) and merge-blocked (405/409 on merge) return partial
results with `Notes`.

## What is NOT supported (by design)

- **File-anchored and threaded comments** — Forgejo's issue-comment
  endpoint has no code anchor or reply concept. `PostCommentAsync` with
  `FilePath`/`Line`/`ReplyToId` returns `null` (unsupported), never a
  mislabelled plain comment.
- **Non-repository webhook scopes** (`organization`, `user`, `system`) —
  those are separate Forgejo resources; requests return `null`.
- **Releases/tags** — left on the contract default (`null`); out of scope
  for this provider.
- **Push-only `PushAsync`** — carries no project context, so it reports
  "use `CompleteAsync`", like the reference sample plugin.
- **`work_item.pull_request_opened` webhook emission** — the provider does
  not publish orchestrator webhooks; operators subscribe on the Forgejo
  side or via the created-PR URL in the work item record.

Unsupported is always non-fatal: callers log and continue. `null` means
"this forge cannot tell you"; a non-null empty list means "supported and
empty" — never confuse the two when gating a merge.

## Approximations (Forgejo concepts without a 1:1 contract field)

- Review `RequirementsMet` assumes rejected reviews block (Forgejo's
  `block_on_rejected_reviews`, commonly on) and approvals are non-stale.
- `RequiredChecksPassed` follows the forge's combined status; without a
  combined state it means "no failing check".
- PR merge-style mapping: `merge`→`merge`, `squash`→`squash`,
  `rebase`→`rebase` (Forgejo's `rebase-merge` is not used).

## Instance version requirements

Developed against Forgejo API v1 (verified against the live
`try.next.forgejo.org` swagger, Forgejo 15). The provider degrades honestly
on older instances: a missing endpoint (404) yields `null`/empty metadata,
never an error. Combined commit status (`/commits/{ref}/status`) and
`branch_protections` are the newest endpoints used; instances without them
fall back to the statuses list and protection-less metadata. Forgejo and
Gitea share ancestry but diverge — this provider targets Forgejo's own
surface (`type: "forgejo"` hooks, Forgejo token scopes) and is not tested
against Gitea.

## Verification without a live instance

No live Forgejo instance exists in CI, so integration coverage uses
recorded-shape fixtures (`tests/CodeyBox.Tests/Fixtures/Forgejo/`,
transcribed from the live swagger on 2026-09-21) asserting that real
response shapes parse into the provider's contract mappings, plus
queue-driven tests for every lifecycle and failure path. To run against a
real instance, point a scratch project at it and exercise
push → open → read → auto-merge; the provider logs PR numbers and URLs at
`Information` for correlation.
