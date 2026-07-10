# Audit Reports

Per-auditor findings are persisted to the SQLite database after every
auditor invocation, providing a durable record of which defects were
flagged, when, and by whom. The primary motivation is diagnosing audit
convergence failures: when blocking-finding counts fluctuate across
iterations (e.g. 5 → 3 → 4 → 2 → 3), operators can now see exactly
which findings appeared, disappeared, and re-appeared.

## Storage

Findings are written to the `audit_reports` table in the same SQLite
database as work items (`CodeyBox:StateDatabasePath`).

```sql
CREATE TABLE audit_reports (
    id              TEXT PRIMARY KEY,
    work_item_id    TEXT NOT NULL,
    iteration       INTEGER NOT NULL,
    audit_target    TEXT NOT NULL DEFAULT 'code', -- "plan", "code", or a future target
    auditor_name    TEXT NOT NULL,
    auditor_kind    TEXT NOT NULL,   -- "diff-pattern", "shell", "llm"
    worst_severity  TEXT NOT NULL,   -- "none", "Warning", "Error", etc.
    started_at      TEXT NOT NULL,   -- ISO-8601 with timezone
    ended_at        TEXT NOT NULL,
    duration_ms     INTEGER NOT NULL,
    findings_json   TEXT NOT NULL,   -- JSON array of finding objects
    raw_output      TEXT             -- NULL when auditor produced no output
);
```

One row is written per auditor per target-specific iteration. Rows
created before target persistence are migrated to `code`.

### Write overhead

The INSERT uses WAL mode (`PRAGMA journal_mode=WAL`) and is protected
by the same per-connection write semaphore as the work-item store.
Expected overhead is well under 50 ms per iteration for the default
auditor count.

## Findings JSON schema

Each element of `findings_json` is:

```json
{
  "Id": "f-a1b2c3d4",
  "Severity": "Error",
  "Title": "Missing null check",
  "Message": "The foo method does not validate its input.",
  "Files": ["src/Foo.cs"],
  "LineHints": [42]
}
```

## Stable finding IDs

Each finding is assigned a stable ID (`f-` + 8 lowercase hex chars) via
`FindingIdComputer.Compute(auditorName, title, files)`:

```
SHA-256( auditorName + "\0" + normalizedTitle + "\0" + sortedFiles )
→ first 4 bytes → 8 hex chars → "f-" prefix
```

Title normalisation:
1. Lowercase
2. Strip file references (`in src/Foo.cs`, `at path/to/file:10`)
3. Strip line references (`line 42`, `(line 42)`)
4. Collapse runs of whitespace to a single space and trim

**Limitation**: Two findings that describe the same defect using
different LLM-generated wording will receive different IDs. The ID
is a best-effort heuristic, not NLP-level deduplication.

## Raw output

When an auditor produces capturable output (stdout/stderr), it is
stored in `raw_output` after:

1. **Redaction** — the same secret-value patterns used by the
   `SensitiveDataRedactionEnricher` Serilog enricher are applied.
   Matched tokens (GitHub PATs `gho_*`/`ghp_*`/`github_pat_*`,
   Anthropic keys `sk-ant-*`, Google API keys `AIza*`) are replaced
   with `***`.
2. **Truncation** — output is capped at 256 KB (UTF-8 bytes). A
   `\n[...truncated]` suffix is appended when the original exceeded
   the cap.

`raw_output` is NULL when the auditor produced no capturable output.

## Retention

Rows are deleted by `AuditReportRetentionService`, a `BackgroundService`
that runs a sweep once immediately at startup and then daily.

The retention window is controlled by `CodeyBox:AuditLog:RetainedDays`
(default 30). Rows whose `started_at` is strictly before
`now - RetainedDays` are deleted. There is no separate config knob.

## API endpoints

### `GET /workitems/{id}/audit-reports`

Returns all stored reports grouped by target and iteration, with findings inline.
`rawOutputAvailable` indicates whether a `/raw` fetch will succeed.

See [`api.md`](api.md) for the full response shape.

### `GET /workitems/{id}/audit-reports/{target}/{iteration}/{auditor}/raw`

Returns the redacted, capped raw output as `text/plain; charset=utf-8`.
Returns `404` when the work item, row, or `raw_output` column is absent.

## Admin dashboard

The **Audit Reports** page (`/work-items/{id}/audit-reports`) provides:

- **Findings across iterations table** — rows are stable finding IDs,
  columns are target/iteration pairs. `✓` = present, `·` = absent. Helps
  operators see which defects persisted, resolved, or re-appeared.
- **Per-iteration expandable sections** — each auditor is a `<details>`
  block showing severity, duration, and individual findings with
  severity badges.
- **Raw output** — a "raw" button per auditor lazily fetches and
  displays the full captured output.

The **Timeline** page (`/work-items/{id}/timeline`) also shows findings
inline inside each `auditor_run` entry; the raw output button is
available there too.
