# CodeyBox Admin

A Blazor Server web dashboard for the CodeyBox orchestrator.

Communicates with the CodeyBox API over REST + JSON. Has **zero shared project references** to the orchestrator source; a bug here cannot take the orchestrator down.

## Running

```bash
dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web
```

The dashboard binds to `http://localhost:5000` by default. To change it:

```bash
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web
```

## Configuration

All settings live in `appsettings.json` (or environment variable overrides using `CodeyBoxAdmin__` prefix):

| Key | Default | Description |
|-----|---------|-------------|
| `CodeyBoxAdmin:ApiBaseUrl` | `http://localhost:5050` | Base URL of the CodeyBox orchestrator API |
| `CodeyBoxAdmin:RequireAuth` | `false` | Enable cookie-based login gate |

### API bearer token

The dashboard authenticates to the orchestrator with the same bearer token the CLI uses. Set it via environment variable — **never** write it to a config file:

```bash
CODEYBOX_API_KEY=your-32-char-secret dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web
```

### Auth gate

When `RequireAuth=true`, all dashboard pages require a cookie login. The placeholder login page is at `/login`. For v1 this is suitable for trusted internal networks only — configure a TLS-terminating reverse proxy (nginx, Caddy) in front before exposing externally.

## Pages

| Route | Description |
|-------|-------------|
| `/` | Queue overview — all work items, auto-refreshes every 5 s |
| `/fleet` | Fleet view — one row per project: status dot, current phase, queued/in-flight counts, last-5 outcomes, 30-day spend. Auto-refreshes every 5 s. |
| `/work-items/new` | Create a new work item |
| `/work-items/{id}` | Detail view: full prompt (collapsible), state, error, deps; live stdout panel for in-flight items |
| `/work-items/{id}/edit` | Edit title/prompt/agent — Queued items only |
| `/work-items/{id}/timeline` | Audit-replay timeline — chronological log of every agent/audit event. Auto-refreshes every 5 s for in-flight items. Supports `?kind=`, `?since=`, `?iteration=` filter params. |
| `/work-items/{id}/timings` | Per-item timing breakdown — stacked bar of phases, drill-down step table, top-10 slowest steps |
| `/work-items/{id}/diff` | Diff preview — unified diff of the work branch vs. base branch, with file list, +/- stats, truncation banner, and "Copy as patch" link |
| `/timings/aggregate` | System-wide aggregate — median and p95 per step across the last N completed work items, configurable N picker |

## Fleet view

`/fleet` is the operator dashboard for running 5–20+ projects. It answers "what is everything doing right now?" at a glance without opening individual queue pages.

**Columns per project:**

| Column | Description |
|--------|-------------|
| Project | Display name + short ID |
| Status | Colored dot — grey (idle), blue (in-flight), yellow (queued only), red (paused) |
| Current phase | State of the most-recently-updated in-flight item, or `—` |
| Queued | Count of items in `Queued` state |
| In-flight | Count of non-terminal, non-Queued items |
| Last 5 | Glyphs for the 5 most recent terminal items (✓ Done, ✗ Failed/AuditFailed, ! Cancelled) |
| Budget (30 d) | Rolling 30-day spend if cost-reporting is available, with a bar; `—` otherwise |
| Actions | "Pause project" / "Resume project" buttons (falls back to global pause while per-project pause is pending) |

The top of the page also has an **Agent controls** panel for pausing one
agent kind with a reason and optional duration, plus a paused-agent table with
per-agent resume buttons.

**Limitations (pending future work items):**

- Per-project pause/resume requires the *budget-alerts* work item. The page shows a fallback banner directing operators to the global pause button on the Queue page.
- `monthlyBudgetUsd` (spend cap) requires the *budget-alerts* work item. Until then the budget column shows spend only.

## In scope (v1)

- Queue view with reorder (up/down arrows)
- Create work item (project dropdown, prompt textarea, depends-on multi-select)
- Edit queued item (title, prompt, agent)
- Cancel non-terminal item
- Retry terminal-failed item from work/audit
- Drill-in detail view with collapsible prompt
- Audit-replay timeline with per-kind filter chips, iteration grouping, copy-as-JSON
- Live stdout panel on work-item detail: real-time streaming via SignalR, sticky auto-scroll, tail fetch for late-joining

## Live Stdout

The work-item detail page (`/work-items/{id}`) shows a **Live Output** panel while
an agent is running. Once the run finishes the panel switches to **Output Tail** and
shows the last 16 KB buffered by the orchestrator.

**How it works:**
1. On first render the page calls `GET /workitems/{id}/stdout-tail` to populate the
   initial tail (in case the user navigated to the page after the run started).
2. It then opens a server-side .NET SignalR connection to `{ApiBaseUrl}/hubs/agent-stdout`
   and subscribes to the work item's group.  The bearer token from `CODEYBOX_API_KEY`
   is sent as a request header — it never reaches the browser.
3. Each `stdoutChunk` event appends to the `<pre>` panel.  Auto-scroll is sticky: if
   you scroll up, auto-scroll suspends; scrolling back to the bottom resumes it.
4. The `streamComplete` event shows a "Stream complete." footer and the panel heading
   switches to "Output Tail".

**Security:** secrets (GitHub PATs, Anthropic keys) are redacted by the orchestrator
before reaching the hub.  The work item prompt is never broadcast.

## Out of scope (v1)

- Drag-and-drop reorder (HTML5 DnD is wired but not implemented)
- Webhook delivery log
- Multi-user auth (only a single cookie gate placeholder)

### Diff rendering

The diff page renders unified diffs server-side in Razor (no JavaScript dependency). `diff2html` was considered but not adopted — it requires either an NPM build step or CDN access from the server, while server-side rendering achieves the same result with zero extra dependencies. The parser splits the diff by `diff --git` headers (falling back to `--- /+++` lines) and maps each line to a CSS class (`diff-add`, `diff-del`, `diff-hunk`, `diff-meta`, `diff-ctx`) for coloring.

## Architecture

The dashboard is a sibling project under `tools/` with **no `<ProjectReference>` to any `src/CodeyBox.*` project**. All types under `Models/` are locally-defined DTOs that mirror the orchestrator's JSON shapes. Drift between the two is acceptable; a project-reference dependency is not.

```
tools/CodeyBox.Admin/
├── src/CodeyBox.Admin.Web/         # Blazor Server web app
│   ├── Components/                 # Razor components + pages
│   ├── Models/                     # Local DTOs (no shared types)
│   ├── Services/                   # CodeyBoxApiClient + interface
│   └── wwwroot/css/admin.css       # Plain CSS, no JS frameworks
└── tests/CodeyBox.Admin.Tests/     # xUnit + bunit component tests
```
