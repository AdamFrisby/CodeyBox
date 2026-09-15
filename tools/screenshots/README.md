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

Only pages that render real content are committed. Two are deliberately
excluded: **Statistics** errors in a seeded instance because the statistics
plugin is not loaded, and **Supervision** renders essentially empty with no
live workers. Shipping either would advertise a broken or blank screen.
