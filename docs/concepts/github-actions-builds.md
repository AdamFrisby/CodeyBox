# GitHub Actions execution/evidence adapter (CBX-NEXT-103)

The required-external-build gate answers "did the approved workflow build
and test the exact frozen candidate". This adapter supplies that answer for
GitHub Actions — as **one execution provider** behind the
language-neutral `IExternalBuildProvider` / `ExternalBuildEvidence`
contract in `CodeyBox.Core`. It dispatches (or adopts) runs, maps
run/job/test-report/package observations to neutral evidence, and verifies
the run actually checked out the requested candidate. Toolchain specifics
(workflow files, job names, report artifacts) live in
`CodeyBox.Build.GitHubActions`; Core only knows compile/tests/package
dimensions. A non-GitHub executor plugs into the same contract with no
GitHub concepts involved.

Official sources: https://docs.github.com/en/rest/actions/workflows,
https://docs.github.com/en/rest/actions/workflow-runs,
https://docs.github.com/en/rest/actions/artifacts
(API version `2022-11-28`, sent as `X-GitHub-Api-Version`).

## Activation (operator opt-in, no code change)

Disabled by default; nothing dispatches, polls, or reads until enabled:

```jsonc
// appsettings.Production.json (or any configuration source)
{
  "CodeyBox": {
    "ExternalBuilds": {
      "Enabled": true,
      "ApprovedTargets": {
        "unity-android": {
          "ProviderId": "github-actions",
          "TargetId": "unity-android",
          "Configuration": "release",
          "AllowGitPublication": true,
          "AllowSnapshotUpload": false
        }
      },
      // The framework accepts candidate refs only under these namespaces;
      // the GitHub adapter additionally needs a dispatchable branch
      // namespace because workflow_dispatch addresses branches/tags.
      "AllowedCandidateRefPrefixes": ["refs/candidates/", "refs/heads/codeybox-candidates/"]
    },
    "GitHubActionsBuilds": {
      "Enabled": true,
      "ApprovedWorkflows": {
        "unity-android": {
          "Owner": "acme",
          "Repository": "game",
          "WorkflowPath": ".github/workflows/build.yml",
          "Toolchain": "unity-6000.0",
          "Platform": "android",
          "Configuration": "release",
          "RequireTestReport": true
        }
      }
    }
  }
}
```

`GitHubActionsExternalBuildOptions` (`CodeyBox:GitHubActionsBuilds`)
hot-reloads via `IOptionsMonitor`. Registration
(`AddGitHubActionsExternalBuilds`) only adds options, the binding store,
and — in the credentialed overload — the HTTP transport plus the provider;
with `Enabled: false` nothing happens.

## How it works

1. The **host** freezes the dirty source, publishes it as a scoped
   temporary candidate branch (e.g.
   `refs/heads/codeybox-candidates/<idempotency-key>` — unique per
   idempotency key so uncertain dispatches correlate exactly), and starts
   the framework build with `CandidateRef` plus the `github.head_sha`
   input (the 40-hex commit sha of the published candidate; `github.merge_sha`
   when a merge-result candidate is required). No caller inputs are
   forwarded to workflows — only correlation and sha pins.
2. The adapter dispatches the approved workflow for the exact branch
   and pins the expected checkout sha. A real dispatch answers 2xx with
   **no run id** (the actual status is recorded; 204 is not assumed), so
   the result is uncertain and any retry must reconcile by correlation
   first — redelivering the same run, never a duplicate paid run.
3. The operator (or host) probes the exact correlated run and adopts
   that exact run id while the intent is still uncertain — the probe
   durably binds the provider run id to the idempotency key, so a
   restart before adoption simply probes again and redelivers the same
   run, never a duplicate paid run. Only correlation and sha pins travel
   as dispatch inputs; no caller inputs are forwarded.
4. Polls map `queued`/`in_progress`/`completed` plus every terminal
   conclusion (`success`, `failure`, `cancelled`, `skipped`, `timed_out`,
   `action_required`, `neutral`, `stale`). Before any conclusion is
   trusted, the adapter rejects repository/workflow/fork-event/checkout
   substitution by exact match: the run's `head_sha` must equal the pinned
   candidate (or merge-result) sha. A matching mutable ref, a success
   badge, or the latest run is never proof.
5. Build success never implies tests ran: with `RequireTestReport` (the
   default) the explicit approved report artifact
   (`codeybox-reports`/`codeybox-test-report.json`) must show failures=0
   and passes>0. Missing/expired/malformed/oversized reports, skipped
   required jobs, and expired artifacts yield typed
   insufficient/unavailable outcomes — never a pass. Package evidence
   comes from pinned-prefix artifacts, digested locally after bounded
   download.
6. Artifact downloads validate URLs against the exact-host allowlist,
   follow at most `MaxRedirects` manually, send the credential only to the
   API host, enforce byte caps before buffering, and treat 404/410 as
   expired (unavailable), 401/403 as auth-blocked, and 429 as typed
   rate-limit honoring Retry-After. Log and annotation endpoints are never
   called. Temporary refs are deleted only under the approved prefix, with
   the exact published identity, after retention or terminal state.

## Later setup requirements (already-existing workflows/permissions)

This task configures no access. Before enabling, the operator must already
have, per approved repository:

- the workflow file at the pinned path, with a `workflow_dispatch`
  trigger accepting the `codeybox_*` inputs and checking out the pinned
  sha (`actions/checkout` with `ref: inputs.codeybox_expected_head_sha`),
  plus jobs named exactly as pinned and a step uploading the report zip
  and package artifacts;
- a credential with `actions:read/write` + `contents:read` (GitHub App
  installation or fine-grained PAT), wired to the existing
  `IGitHubTokenProvider` and passed to the credentialed
  `AddGitHubActionsExternalBuilds` overload;
- branch protection/permissions allowing the host to push and delete the
  `codeybox-candidates/*` namespace only.

Without these, dispatches fail typed (auth/validation) and the gate stays
red — by design, with no silent pass.
