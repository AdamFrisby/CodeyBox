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
| `/work-items/new` | Create a new work item |
| `/work-items/{id}` | Detail view: full prompt (collapsible), state, error, deps |
| `/work-items/{id}/edit` | Edit title/prompt/agent — Queued items only |
| `/work-items/{id}/timeline` | Audit-replay timeline — chronological log of every agent/audit event. Auto-refreshes every 5 s for in-flight items. Supports `?kind=`, `?since=`, `?iteration=` filter params. |

## In scope (v1)

- Queue view with reorder (up/down arrows)
- Create work item (project dropdown, prompt textarea, depends-on multi-select)
- Edit queued item (title, prompt, agent)
- Cancel non-terminal item
- Retry terminal-failed item from work/audit
- Drill-in detail view with collapsible prompt
- Audit-replay timeline with per-kind filter chips, iteration grouping, copy-as-JSON

## Out of scope (v1)

- Drag-and-drop reorder (HTML5 DnD is wired but not implemented)
- Webhook delivery log
- Multi-user auth (only a single cookie gate placeholder)

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
