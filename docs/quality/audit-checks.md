# Audit check runs (forge Checks publication)

Structured audit findings can be published as forge check runs on the exact
audited commit — currently GitHub Checks only. This is strictly opt-in and
disabled by default at two levels: the global section and the per-project
flag. Enabling it changes no merge gating, live settings, or grants.

A published check reports what the audit found, nothing more. Publishing is
not permission to merge, does not bypass required checks, does not enable a
deployment, and does not grant token scopes.

## Opt-in

```jsonc
// Global section (hot-reloadable; all values validated at startup).
"CodeyBox:AuditCheckPublication": {
  "Enabled": false,              // master switch, default false
  "CheckName": "codeybox-audit",
  "MaxAnnotationsPerBatch": 50,  // capped at the GitHub per-call limit
  "MaxAnnotationBatches": 2,     // bounded batch budget per check run
  "MaxSummaryChars": 32768,
  "MaxAnnotationMessageChars": 4096,
  "MaxAnnotationTitleChars": 120,
  "MaxAnnotationLineSpan": 20,
  "MaxOmittedFindingsInSummary": 20,
  "RetryMaxAttempts": 5,         // bounded backoff for transport/rate limits
  "RetryBaseDelay": "00:00:02",
  "RetryMaxDelay": "00:02:00",
  "RequireDelivery": false       // see "Delivery policy" below
}
```

```jsonc
// Per-project (reloads with the project list; default disabled).
"AuditChecks": {
  "Enabled": false,
  "CheckName": null              // null = use the global CheckName
}
```

Both must be true before anything is published.

## What is published

For one work-item iteration at the exact audited commit SHA (40-hex; anything
else is rejected rather than risking attachment to a different commit):

- one **aggregate** check run combining the iteration's verdict, plus
- one check run per **auditor** with reports in that iteration.

Each run carries provenance in its summary: work item, audit target,
iteration/attempt, scope, source revision, and the audited SHA. The audit
verdict is an explicit input derived from report-row existence (a report row
with zero findings means the audit ran and passed) — it is never inferred
from the findings count.

### Verdict mapping

| Audit outcome | GitHub conclusion | Meaning |
|---|---|---|
| Passed | `success` | The audit ran; findings (if any) stand as published |
| Failed | `failure` | The audit ran and reports blocking findings |
| Missing / skipped / unsupported forge | `skipped` | No coverage — never successful coverage |
| Cancelled | `cancelled` | The audit was cancelled |
| Infrastructure failure | `action_required` | Operator action needed |

Queued and in-progress runs carry no conclusion (GitHub rejects conclusions
on unfinished runs). A missing audit may still be published as a `skipped`
check so the *absence* of coverage is visible on the commit instead of
silent.

## Annotations

Annotations are built only from structured findings (`Files` + `LineHints`),
never from truncated `RawOutput`. A finding becomes a line annotation only
when it names exactly one repository-relative path that passes
canonicalize-then-contain validation (relative, `/`-separated, no `..`, no
absolute forms, no drive specs, no control characters) and at least one
positive line hint within the configured line-span cap. Everything else is
listed in the summary — never as an invented line annotation.

All user/tool text is secret-redacted and escaped; summaries, titles, and
messages are length-capped. GitHub accepts at most 50 annotations per call,
so findings are sent in bounded batches (`MaxAnnotationBatches`); overflow is
disclosed in the summary and preserved via the complete-report link below.
The check-run title/summary always state how many annotations were published
vs. omitted.

## Complete-report link

Because batches are bounded, every summary links the full report
(`DetailsUrl`). The link must be an absolute http(s) URL and must preserve
the host's authorization — tokens are never embedded in the URL. The linked
report (not the bounded annotation set) is authoritative.

## Reconciliation and retries

Publication progress is persisted in `audit_check_publications`, keyed by
repository, exact SHA, work item, target, iteration, attempt, and scope:

- **Lost create responses / restarts / concurrent delivery** converge by
  `external_id` (a correlation handle, not an idempotency key): the intended
  check run is re-read before any create is retried, and completed keys are
  never re-published.
- **Ambiguous batch writes** (a timeout where the batch may have landed) are
  reconciled by re-reading the run's annotations and sending only what is
  missing. When the re-read itself fails, the result is flagged uncertain
  and the summary discloses it — batches are never blindly duplicated.
- **Stale completions** (an older iteration/attempt finishing after a newer
  run began) are rejected in storage and reported as superseded.
- **Transport and rate-limit failures** retry with bounded exponential
  backoff honoring the server's `Retry-After`. **Authentication/permission
  denial (401/403) and request validation failures are terminal blocked
  states**: they are surfaced with the missing scope and never retried, and
  no broader grant is ever requested silently.

## Supported credentials

Publication reuses the project's existing GitHub credential plumbing
(per-request headers; tokens never logged or persisted). Any credential the
host already supports for upstream writes works, provided it carries Checks
write permission:

- fine-grained PAT with Checks **read+write** on the repository;
- classic PAT with `repo` scope;
- GitHub App installation token with `checks:write`.

A 401/403 names the missing permission in the blocked reason. No new live
credentials are created by enabling this feature.

## Delivery policy

The audit verdict is independent of transport success: a failed delivery
never rewrites the verdict. By default (`RequireDelivery: false`) delivery
failure is recorded visibly on the publication row and retried within the
bounded budget, then parked as blocked. Setting `RequireDelivery: true`
additionally surfaces exhausted delivery as a distinct
`DeliveryRequired` outcome for the caller to decide on. Neither setting
changes merge gating by itself.

## SARIF follow-up (separate prerequisite)

These check runs are human-readable verdicts with bounded annotations — they
are **not** a SARIF upload. Lossless machine-readable results still need a
dedicated artifact store/export with content digest, tool/schema identity,
the exact SHA, and retention/access controls; the existing 256 KB capped
`RawOutput` cannot satisfy that. Do not treat a published check as proof of
SARIF delivery.
