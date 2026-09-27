# CodeyBox: Credo Elixir Linter

Auditor plugin wrapping [Credo](https://github.com/rrrene/credo): it analyses the
audited repository with `credo suggest --format json .` and reports each issue as an
audit finding with the Credo check name (e.g. `Credo.Check.Readability.ModuleDoc`) and
`file:line` location. Elixir analysis only.

## What it reports

- One finding per Credo issue. The title carries the check name and the first line of
  the message (e.g. "Credo.Check.Readability.ModuleDoc: Modules should have a
  @moduledoc tag."); the description carries the tool, check, tool-reported category,
  location, the full message, and the numeric priority. `Location` is `path:startLine`.
- **Gate behaviour: partially blocking — stated explicitly.** The `warning` category
  (likely mistakes — Credo's own red, exit-status-16 category) maps to `Error` and
  fails the audit. `design` and `refactor` map to `Warning` (advisory);
  `consistency` and `readability` (naming and style) map to `Info` (advisory).
  Style-level findings are reported but never fail the audit — this auditor is NOT
  blocking by default for non-warning findings, by design. `MinimumSeverity` only
  drops findings below the threshold; it never raises advisory findings to blocking.

## What it cannot see

- **Non-Elixir files.** Credo checks Elixir (`.ex`, `.exs`) — everything else produces
  no findings. A repository with no checkable files is a clean pass (empty `issues`
  array), not an error.
- **Files Credo excludes.** The repository `.credo.exs` `files.included` /
  `files.excluded` lists decide the analysed set, plus Credo's own defaults. The scan
  adds `--files-excluded` only via operator `ExtraArguments`.
- **Suppressed violations.** Credo honors inline `# credo:disable-for-this-file` /
  `# credo:disable-for-next-line` / `# credo:disable-for-previous-line` /
  `# credo:disable-for-lines` comments authored in the audited tree, and offers no flag
  to make them inert — the subject can silence findings line-by-line and the auditor
  honors them. The larger suppression surface is the repository `.credo.exs` itself
  (disabled checks, `files.excluded`): it is repo-authored and honored because the
  project's own lint contract is the meaningful check — changes to it are visible in
  the audited diff.
- **Formatter drift.** The scan is `credo suggest`, not `mix format --check-formatted`:
  formatting drift never becomes a finding. Gate formatting separately if you want it.
- **The repository ruleset's blind spots.** Only the checks the configuration enables
  are run. A clean audit against a minimal `.credo.exs` says nothing about checks the
  repo never enabled. Operators who want a fixed bar pass `--only` / `--strict` /
  `--min-priority` in `ExtraArguments` or pin an operator-owned config (see below).
- **More than `MaxFindings` issues.** Findings beyond `MaxFindings` (default 1000) are
  dropped and the truncation is reported in the raw output.

## Exit codes and failure classification

Credo's convention (verified against the 1.7.19 sources — **not** assumed from the
common "0 clean / 1 findings / 2 error" table). The exit status is a bitmask: each
issue category contributes one bit (`consistency: 1`, `design: 2`, `readability: 4`,
`refactor: 8`, `warning: 16`), OR-ed together:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed clean (empty `issues` array) | Verdict (pass) |
| `0` with issues | Only via operator `--mute-exit-status`: issues still reported | Verdict (warning-category findings still fail; the flag cannot silence the gate) |
| `1`–`31` | Analysis completed with issues (any category-bit combination) | Verdict (findings fail or advise per the severity map) |
| `1`–`31` with no JSON on stdout | Ran but emitted no report | Infrastructure — the JSON parser fails closed |
| `128` / `129` / `130` | Generic error / config parser error / config loaded but invalid — analysis did not complete | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `credo` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **Credo `1.7.19`** (`ExpectedVersion` in scoped
config). A linter's checks change between releases, so an unpinned tool would
change findings under you: the auditor probes `credo --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: Credo ships as a
Mix escript (which also needs an Elixir runtime), and no distro package carries a
version pin. Provision the pinned release in your sandbox baseline **only
when this plugin is enabled**:

```sh
# baseline bake step: Elixir runtime plus the pinned credo escript
mix escript.install hex credo 1.7.19 --force
credo --version   # must print 1.7.19
```

(Ensure the escript install directory is on `PATH` for the sandbox user.)

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning verifies `credo` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.credo"],
      "Enabled": ["codeybox.credo"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.credo" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.credo`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.7.19` | Pinned Credo release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config-file` — an operator-pinned `.credo.exs` outside the repository. Ignored when `ExtraArguments` already supplies `--config-file`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Note this only narrows: `warning`-category findings are the only ones that fail the audit. |
| `IncludedRules` / `ExcludedRules` | — | Exact Credo check names to keep/drop (e.g. `Credo.Check.Readability.ModuleDoc`). |
| `ExcludePaths` | `deps/`, `_build/`, `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--only <checks>`, `--ignore-checks <checks>`, `--strict`, `--min-priority <level>`, `--files-excluded <glob>`, or `--config-name`. A repeated `--format` replaces the JSON report the parser expects and breaks the run into infrastructure failure. `--mute-exit-status` collapses findings exits to `0` but findings are still reported. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is honored by necessity.** The audit subject writes
the repository, and Credo lets source files suppress the linter inline
(`# credo:disable-for-next-line` drops the finding for that line;
`# credo:disable-for-this-file` for the file). Unlike some linters there is no flag
to make these inert, so findings the comments suppress never surface; expect
*fewer* findings than the raw check count on repos that rely on disables. The
`.credo.exs` configuration itself (`checks.enabled/disabled`, `files.excluded`) is
likewise repo-authored and honored. The auditor runs under `AuditCapabilities.None`
(no agent credentials, no network). For a fully operator-owned gate, pin an
out-of-repo file via `ConfigPath` — noting that inline disables remain honored even
then.

## Default scope

`credo suggest .` — the repository `.credo.exs` `files.included` / `files.excluded`
lists decide what gets checked; that is the project's own declaration of checkable
scope. On top of that, the finding-level `ExcludePaths` backstop lists vendored
(`deps/` — Hex packages, `vendor/`, `third_party/`, `node_modules/`) and generated
(`_build/`, `dist/`, `build/`, `out/`, `coverage/`) prefixes: violations there belong
to upstream packages or build output, not the change under audit — reporting them
produces noise that trains operators to ignore the auditor. Credo reports
scan-relative paths, so the backstop matches normally (unlike tools that emit
absolute URIs). Re-include a path by overriding `ExcludePaths`, or narrow the scan
with `--files-excluded` in `ExtraArguments`.
