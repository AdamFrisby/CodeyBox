# CodeyBox: Bitbucket Cloud Upstream (`codeybox.bitbucket-upstream`)

First-class upstream remote for [Bitbucket Cloud](https://bitbucket.org) (API 2.0).
One project, one plugin. **Off unless an operator enables it.**

## Enablement

1. Add this assembly to `CodeyBox:Plugins:AssemblyPaths`.
2. Add `codeybox.bitbucket-upstream` to `CodeyBox:Plugins:Allowlist`.
3. Set `Upstream.Kind = "bitbucket"` on the project.

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.bitbucket-upstream"],
      "AssemblyPaths": ["/opt/codeybox/plugins/CodeyBox.BitbucketUpstreamPlugin.dll"]
    },
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "https://bitbucket.org/myteam/myproject.git",
        "Upstream": {
          "Kind": "bitbucket",
          "TokenEnvVar": "BITBUCKET_CREDENTIAL",
          "AutoMerge": true,
          "MergeMethod": "merge",
          "PluginConfig": {
            "Workspace": "myteam",
            "Repository": "myproject"
          }
        }
      }
    ]
  }
}
```

## Configuration

Per-project `Upstream.PluginConfig` keys (highest precedence), falling back to
the plugin-scoped section `CodeyBox:Plugins:codeybox.bitbucket-upstream`
(same keys, plus `TokenEnvVar`). All values are re-read on every call, so
edits apply without a restart (hot-reloadable).

| Key | Required | Meaning |
|-----|----------|---------|
| `Workspace` | yes | Bitbucket workspace id (the `{workspace}` URL segment). |
| `Repository` | yes | Repository slug (the `{repo_slug}` URL segment). |
| `BaseUrl` | no | API base, default `https://api.bitbucket.org/2.0`. Override only to route through a proxy. Must be `https` and must not point at loopback/private addresses. Must not embed credentials. |
| `PageSize` | no | Items per API page, 1–100 (default 50). |
| `MaxListPages` | no | Page cap per listing, 1–50 (default 10). Bounds every list so a large repository yields a bounded answer or an explicit error — never a silent partial list. |
| `TokenEnvVar` | scoped only | Name of the env var holding the credential (or set `Upstream.TokenEnvVar` per project). |

Calls that carry no project context — base-branch fetch, PR listing/reads,
release-sync merges, and all extended read surfaces — use the scoped
fallback. Single-instance operators can therefore configure the repository
once; multi-repository operators should use per-project `PluginConfig` for
the write path and point the scoped fallback at the repository they want
swept. No instance-version requirement: Bitbucket Cloud is a hosted SaaS on
API 2.0.

### Credentials

The credential **never** appears in configuration files. The operator stores
it in an environment variable and names that variable in
`Upstream.TokenEnvVar`; this remote reads the value with
`Environment.GetEnvironmentVariable(name)` at call time. Two forms are
accepted:

- `username:app-password` — an [app password](https://support.atlassian.com/bitbucket-cloud/docs/create-an-app-password/)
  (recommended; needs `pullrequest:write` for PR write/merge, `repository:read`
  suffices for read-only surfaces). Sent as HTTP Basic auth and as
  `GIT_USERNAME`/`GIT_PASSWORD` for git pushes.
- a bare token — an OAuth access token or repository access token, sent as
  `Authorization: Bearer …` (git pushes use username `x-token-auth`).

The value is redacted from every error message, never logged, never placed
in a URL or request body, and never mounted into a sandbox — the plugin
references no sandbox assembly at all (covered by test).

## What is supported

Core lifecycle (`IUpstreamRemote`):

- Push work branch (inside `CompleteAsync`), open PR, optionally auto-merge
  (`merge` → `merge_commit`, `squash` → `squash`, `rebase` → `fast_forward`).
- Reuse of `ExistingPullRequestNumber` for the orchestrator's race-recovery
  re-run (create is skipped, the still-open PR is merged).
- Release-sync branch merge via a host-side temp clone
  (`TryMergeUpstreamBranchAsync`) — Bitbucket Cloud exposes no
  branch-to-branch merge API.
- Base-branch fetch (`FetchBaseBranchAsync`); PR listing with prefix filter;
  PR state reads (open/merged/closed + merge sha).

Extended surfaces (all genuinely Bitbucket Cloud API 2.0):

- **Reviews** — PR participants mapped to verdicts (`approved` → approved,
  `changes_requested` → changes requested, otherwise pending/commented),
  outstanding reviewers, and the quorum from the destination branch's
  `require_approvals_to_merge` restriction (`*` wildcards honoured).
- **Checks** — commit build statuses (`SUCCESSFUL/FAILED/INPROGRESS/STOPPED`
  → passing/failing/pending/cancelled). Bitbucket's model is coarser than
  GitHub checks — statuses attach to a commit with a key and state, with no
  check-run structure — and is reported as such. `RequiredChecksPassed`
  means "nothing failing" (empty counts as satisfied) because Bitbucket
  exposes no combined required-check verdict.
- **Comments** — plain and inline PR comments, list and post; replies via the
  `parent` reference. File-anchored *posts* are unsupported (Bitbucket inline
  comments need diff-hunk positions a bare path-plus-line cannot faithfully
  supply) and return `null` without touching the forge.
- **Webhooks** — repository hooks, list/create/delete. Bitbucket Cloud
  webhooks are repository-scoped only; any other requested scope returns
  `null` rather than a forced translation.
- **Repository metadata** — main branch, visibility (`public`/`private`;
  Bitbucket has no `internal`), branch protections grouped from branch
  restrictions. Restriction reads need elevated scopes; when unreadable the
  metadata is reported with no rules rather than failing.

Failure classification: unreachable / unauthorised / forbidden /
rate-limited (retried with capped backoff honouring `Retry-After`) / 5xx
throw `BitbucketUpstreamException` (infrastructure — the orchestrator
retries; never a verdict on the diff). Soft outcomes (PR already exists,
merge blocked) return partial results.

## What is NOT supported (by design)

- **Releases** — Bitbucket Cloud has no releases concept.
  `CreateTagAndReleaseAsync` keeps the contract default (`null`).
- **Merge-conflict detection** — Bitbucket Cloud exposes no mergeability
  flag on PRs, so `HasMergeConflict` is always `false`. The stale-base
  sweeper cannot detect conflicts on this forge; open CodeyBox PRs are still
  listed so operators can see them.
- **Non-repository webhook scopes** — `organisation`/`user`/`system` return
  `null` (unsupported) rather than a mistranslation.
- **File-anchored comment posts** — return `null` (see above); listing
  inline comments *is* supported.
