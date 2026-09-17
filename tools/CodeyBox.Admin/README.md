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
| `CodeyBoxAdmin:RequireAuth` | `false` | Require authenticated operators for every dashboard route |
| `CodeyBoxAdmin:Authentication:CloudflareAccess:Enabled` | `false` | Accept a validated Cloudflare Access assertion forwarded by the edge |
| `CodeyBoxAdmin:Authentication:CloudflareAccess:TeamDomain` | — | Cloudflare Access team domain, required when the assertion flow is enabled |
| `CodeyBoxAdmin:Authentication:CloudflareAccess:Audience` | — | Exact Cloudflare Access application audience tag, required when enabled |
| `CodeyBoxAdmin:Authentication:Google:ClientId` | — | Native Google OAuth client ID (both Google values required) |
| `CodeyBoxAdmin:Authentication:Google:ClientSecret` | — | Native Google OAuth client secret; environment/secret store only |
| `CodeyBoxAdmin:Authentication:AllowedEmailDomains` | — | Required production allowlist for Cloudflare and Google identities |

### API bearer token

The dashboard authenticates to the orchestrator with the same bearer token the CLI uses. Set it via environment variable — **never** write it to a config file:

```bash
CODEYBOX_API_KEY=your-32-char-secret dotnet run --project tools/CodeyBox.Admin/src/CodeyBox.Admin.Web
```

### Operator authentication

When `RequireAuth=true`, every dashboard route requires an authenticated operator.
Production startup fails closed unless at least one of these fully configured mechanisms
and an email-domain allowlist are present:

- **Cloudflare Access** — enable the `CloudflareAccess` block above. The dashboard
  validates the signed `Cf-Access-Jwt-Assertion` against the configured team's OIDC
  metadata and exact application audience. It never trusts the convenience email header.
  A valid Access session signs the operator in on each request, so no second prompt is shown.
- **Native Google OAuth** — set `CodeyBoxAdmin__Authentication__Google__ClientId` and
  `CodeyBoxAdmin__Authentication__Google__ClientSecret` in protected environment
  configuration, then register `https://<admin-host>/signin-google` as an authorized
  redirect URI in the Google OAuth client. The login page presents **Continue with Google**
  and uses a secure, HttpOnly local session cookie.

Both methods may be enabled. A direct/internal request with no Cloudflare assertion can then
use Google; an invalid assertion is rejected rather than silently falling back. Local
username/password login remains Development-only.

## Design system

Dark, dense, plain CSS (`wwwroot/css/admin.css`, no frameworks). Dark is the
default; a light variant ships via `[data-theme="light"]` on
`<html>`, toggled from the nav (◐) and remembered in `localStorage`.

### Reusable components (`Components/Shared/`)

| Component | Use |
|-----------|-----|
| `StatusChip` + `StatusVocabulary` | **The only way to render a state.** Covers every `WorkItemState` (25), release states, suggestion states, severities, agent availability, quota bands, breaker state, project rollups and supervision session states. Same value → identical chip everywhere. |
| `CopyableId` | Monospace identifiers (ids, SHAs, branches). Prefix display, full value on hover, one-click copy button; text stays selectable. `ShowFull` for detail pages, `CopyButtonOnly` next to nav links. |

Every status pairs a tone (colour) with a **distinct glyph and text label** —
colour is never the only carrier. `DesignSystemTests` asserts all 25 work-item
states resolve, all glyphs are distinct, and no page formats a state inline
(`state-@`, `severity-badge--@`, `fleet-dot-@`, `fleet-outcome-@` are banned in
`Components/Pages/`).

### Numbers and times (`Services/AdminFormat`)

One pure, invariant-culture helper: `FormatDurationMs` (`850ms`, `12.0s`,
`3m 4s`, `2h 0m`), `FormatShortAge` (`4m`), `FormatRelative` (`4m ago`),
`FormatCountdown` (quota resets), `FormatCount` (`12.3K`), `FormatUsd`,
`FormatDateTime`. Pages keep their call sites but delegate bodies here.

### Surfaces and type

Tokens: `--bg` (page) → `--bg-card` (cards, tables) → `--bg-inset` (code,
prompt/output wells) → `--bg-overlay` (modals, `.overlay`/`.modal-overlay`,
elev-3). Reusable `.card`, `.panel-inset`, `.overlay` classes. Small type
(13px, 1.6 line height), monospace identifiers, `:focus-visible` outlines on
every interactive element, fluid rem layouts that scale to 200%.

## Pages

| Route | Description |
|-------|-------------|
| `/` | Queue overview — all work items, auto-refreshes every 5 s |
| `/needs-attention` | Needs-you queue — everything awaiting a human ordered by attention score, with inline evidence (infra vs. rejected change, blocking findings with auditors, repeat/shrink signal), in-place retry/delegate/cancel, and a cancel confirmation that names stranded dependants. Auto-refreshes every 30 s. |
| `/map` | Fleet map — every non-terminal item as one 2D canvas, grouped into chains, with an attention-driven camera. Auto-refreshes every 5 s; idle frames cost nothing. |
| `/fleet` | Fleet view — one row per project: status dot, current phase, queued/in-flight counts, last-5 outcomes, 30-day spend. Auto-refreshes every 5 s. |
| `/supervision` | Live multi-session agent supervision and injection. Requires `CodeyBox:AgentSupervision:Enabled=true`. |
| `/work-items/new` | Create a new work item |
| `/work-items/{id}` | Detail view: full prompt (collapsible), state, error, deps; live stdout panel for in-flight items |
| `/work-items/{id}/edit` | Edit title/prompt/agent — Queued items only |
| `/work-items/{id}/timeline` | Audit-replay timeline — chronological log of every agent/audit event. Auto-refreshes every 5 s for in-flight items. Supports `?kind=`, `?since=`, `?iteration=` filter params. |
| `/work-items/{id}/journey` | Journey graph — phases actually visited with loop counts, audit-cycle convergence (stuck vs. converging), budget remaining, per-phase agent/duration/sandbox links, infra interruptions kept distinct, current position and wait. |
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

## Fleet map

`/map` is the headline screen: the whole fleet as one 2D canvas, left on a
spare monitor. It derives everything from the pure projection layer
(`CodeyBox.Admin.Model`) — chains, activity, attention — and adds only
rendering and camera behaviour:

- **Layout** (`FleetMapLayout`): one lane per chain, one column per
  dependency depth, rows by id. Positions are sticky across refreshes — an
  item that did not change does not move.
- **Camera** (`CameraDirector`): idle, it dwells through the most active
  chains; failures, parks and conflicts seize it and hold it while
  unresolved; any drag/zoom takes manual control immediately, with a
  **Resume auto-follow** button as the way back. Single items fill the frame,
  busy chains frame the chain, a quiet fleet pulls back to everything.
- **Nodes** (`MapNodeStyler`): full detail (title, agent, age, attempt count)
  at working zoom, a short id mid-zoom, a bare shape far out. Text is drawn
  in screen space, never below the readable minimum. Blocked items are
  diamonds, running items circles, slot-waiting items squares — shape and
  tone, never colour alone.
- **Motion as signal** (`MapTransitionDetector`): only state changes,
  unblocks, chain completions, arrivals and departures animate. A quiet fleet
  produces byte-identical frames, so the page skips the JS bridge and the
  canvas schedules zero animation frames — idle cost is one string comparison
  per poll.
- **`prefers-reduced-motion`** is honoured end to end: the director never
  moves the camera on its own and all travel becomes instant; the map stays
  fully usable as a static view plus the text-equivalent chain list.

All map knobs live under `CodeyBoxAdmin:FleetMap` in `appsettings.json`
(bound with reload-on-change): lane/column gaps, dwell seconds, the urgent
attention threshold, focus zooms, detail thresholds, and the readable-text
minimum.

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
├── src/CodeyBox.Admin.Model/       # Pure projection layer (chains, activity,
│                                   # vitals, attention) — no I/O, no clock.
│                                   # See its README for endpoint mapping.
└── tests/CodeyBox.Admin.Tests/     # xUnit + bunit component tests
└── tests/CodeyBox.Admin.Model.Tests/ # xUnit tests for the projection layer
```
