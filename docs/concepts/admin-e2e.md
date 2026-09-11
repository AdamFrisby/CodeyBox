# Admin E2E: seeded instance + fake agents

CodeyBox's own admin web (Blazor, `tools/CodeyBox.Admin`) is covered by E2E
tests that run ON TOP of the existing E2E stack (test-case foundation, the
cheap-model CUA author from the E2E-authoring item, the replay engine) — not
by rebuilding any of it. Two new pieces make that possible:

## 1. Seeded, self-contained instance with fake agents

`src/CodeyBox.AdminSeed/`:

- `SeededFakeAgentRunner` (`IAgentRunner`, kind `seeded-fake`) — no LLM, no
  VM, no network. Success stages one deterministic markdown file through the
  sandbox's own exec channel (`tee` argv + stdin, never a shell string).
  Marker-driven branches (`[seeded-fake:quota|auth|fail|empty]`) reproduce
  quota-park, auth, failure, and empty-diff outcomes; unmarked prompts hash
  into success/failure buckets from the configured seed. Options
  (`CodeyBox:SeededFakeAgents`) are hot-reloadable knobs: `Enabled`, `Seed`,
  `ArtifactFileName`, `QuotaResetSeconds`, `DefaultSuccessBuckets`.
- `SeededFakeQuotaProbe` — deterministic healthy/exhausted snapshots so the
  quota/capacity pages render without provider APIs.
- `AdminSeedData` — pure builder: 2 projects, 17 work items covering every
  lifecycle state, audit reports (pass + blocking), open + released
  releases, a suggestion. Same spec in → identical content out.
- `AdminSeeder` — writes that content through the REAL SQLite stores into a
  throwaway DB (canonicalize-then-contain path guard; re-seeds wipe first).

The API opts in with `CodeyBox:SeededFakeAgents:Enabled=true`; when false
nothing registers and production routing is untouched.

## 2. Harness verbs + recipe

`codeybox-harness admin-seeded seed --seed 42 --db <path>` writes the DB.
`codeybox-harness admin-seeded serve` starts the orchestrator API plus
Admin.Web on that DB in the foreground (loopback http only), freezes the
queue on boot so seeded states stay deterministic (`--live` opts out), and
tears both children down on Ctrl+C. Usable for manual demos and exploratory
runs, not just E2E.

`CodeyBoxAdminRecipe` (`src/CodeyBox.ExploratoryTesting/Recipes/`) wires
this into the graphical-sandbox harness: build steps, a deterministic
reset+seed step, and a serve run step exposing Admin.Web at
`http://localhost:5070`.

## 3. Artifacts + suite

`tests/CodeyBox.Tests/E2eArtifacts/Admin/` holds the committed outputs of
the deterministic authoring pipeline: `admin-queue.trace.json` (recorded
session: queue → All tab → filter → work-item detail → quota) and
`admin-queue.replay.json` (the emitted deterministic artifact).

- `AdminWebE2eReplayTests`: the cheap-model CUA (`CheapModelCuaAuthor` +
  scripted explorer over a RecordingComputerUseBridge) explores the seeded
  UI and emits the artifact; the replay engine replays the committed trace
  against a layout-shifted copy of that UI through the real
  `ComputerUseBridge` (real mouse/keyboard events) and asserts on rendered
  state. `CODEYBOX_WRITE_ARTIFACTS=1` regenerates the committed files from
  the same pipeline; default runs assert byte-stability.
- `AdminSeedTests`: runner outcomes, seed determinism, real-store
  round-trips, recipe shape, harness CLI parsing.
- `tools/CodeyBox.Admin/tests/.../SeededAdminUiTests.cs`: bUnit coverage
  over the lowest-branch pages (Capacity, Statistics, Plugins) and seeded
  action flows. Admin.Web branch coverage: ~49% → ~56% (floor: 50%).
