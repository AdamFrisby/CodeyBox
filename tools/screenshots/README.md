# Admin screenshots

Regenerates the images in `screenshots/` from the real Admin UI, so they cannot
drift silently from what the product actually renders.

They are captured against a **seeded, frozen instance** — `admin-seeded` writes
a deterministic SQLite database (fixed seed, work items in every lifecycle
state) and freezes the queue on boot, so the same seed produces the same
screenshots. No live fleet, no credentials, and no real repository is involved.

## Regenerate

```bash
# 1. Build Release — admin-seeded runs its children with --no-build -c Release.
dotnet build -c Release CodeyBox.slnx

# 2. Seed the throwaway database and serve the API + Admin.Web.
dotnet run --project tools/CodeyBox.Harness -c Release --no-build -- admin-seeded seed
dotnet run --project tools/CodeyBox.Harness -c Release --no-build -- admin-seeded serve

# 3. In another shell, capture. Needs Node and playwright-core.
npm install playwright-core
node tools/screenshots/capture.mjs
```

`capture.mjs` honours three environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `ADMIN_URL` | `http://localhost:5070` | Admin.Web base URL |
| `OUT_DIR` | `./out` | Where PNGs are written |
| `CHROMIUM_PATH` | Playwright's bundled build | Explicit Chromium executable |

Capture at `1440x900`, `deviceScaleFactor: 2`.

## What is committed

Only pages that render real content and no error or warning state are
committed. `scan.mjs` checks each page for alert/warning/error elements and
red or amber text — checking body text for error *words* is not enough, because
a banner element can carry the failure without the word appearing in prose.

Three pages are deliberately excluded:

- **Statistics** fails to load in a seeded instance even with the statistics
  plugin enabled — it needs real quota and usage history, which seeded data
  does not have.
- **Supervision** renders essentially empty with no live workers.
- **Fleet** carries a permanent banner: *"Per-project pause requires the
  budget-alerts work item; falling back to global queue pause."* That is
  accurate — the feature is unimplemented, and every user sees it — but the
  README should not showcase a page whose most prominent element says a
  feature is missing. The screenshot is not committed.

The seeded instance must load the statistics plugin, or **Capacity** renders
"Capacity analysis unavailable. Is the statistics plugin loaded?":

```
CodeyBox__Plugins__AssemblyPaths__0=<repo>/plugins/quota/CodeyBox.StatisticsPlugin/bin/Release/net10.0/CodeyBox.StatisticsPlugin.dll
CodeyBox__Plugins__Allowlist__0=codeybox.statistics
```
