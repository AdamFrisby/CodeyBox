# CodeyBox: Clang-Tidy C/C++ Analyzer

Auditor plugin wrapping [clang-tidy](https://clang.llvm.org/extra/clang-tidy/):
it enumerates the audited repository's C/C++ translation units, analyses them
with `clang-tidy --export-fixes=/dev/stdout …`, and reports each YAML
diagnostic as an audit finding with the check name (e.g.
`modernize-use-nullptr`, `clang-analyzer-core.NullDereference`,
`clang-diagnostic-error`) and the file location. C and C++ analysis only.

## What it reports

- One finding per clang-tidy YAML diagnostic. The title carries the check
  name and the first line of the message (e.g. "modernize-use-nullptr: use
  nullptr"); the description carries the tool, rule, tool-reported level,
  location, and the full message. `Location` is the repository-relative path
  (`src/app.cpp`) — the YAML report carries byte offsets, not line numbers,
  so there is no `:line` suffix.
- **Diagnostics in headers.** Headers included by an enumerated translation
  unit are analysed with it, and their diagnostics are reported (relativized
  the same way). The same header diagnostic re-emitted through several
  translation units is reported once.
- **Compile errors** (`clang-diagnostic-error`, e.g. a file that does not
  parse) are findings too — level `Error`, so they fail the audit rather
  than vanishing as tool noise.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Level `Error` maps to `Error` and fails the audit; level
  `Warning` maps to `Warning` and is advisory. `MinimumSeverity` only drops
  findings, it never raises them — there is no mode in which a warning fails
  the audit. Set `MinimumSeverity: error` for an errors-only gate.

## What it cannot see

- **Non-C/C++ files.** Only `*.c`, `*.C`, `*.cc`, `*.cpp`, `*.cxx`, `*.cp`
  and `*.c++` files are passed as translation units. Headers (`*.h`,
  `*.hpp`, …) are analysed only as included by those units — a header no
  enumerated unit includes is never analysed. A repository with no
  translation units is an infrastructure failure, not a pass (see below).
- **Line numbers.** The `--export-fixes` report records a byte `FileOffset`,
  not a line. Findings locate the file; re-run `clang-tidy` locally for the
  exact line.
- **Files discovery prunes.** The fixed discovery script skips `.git/`,
  `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`,
  `coverage/`, `.venv/`, `venv/` and `__pycache__/` at any depth. Findings
  under those prefixes (e.g. diagnostics in an included vendored header) are
  additionally dropped by the default `ExcludePaths`.
- **Suppressed diagnostics.** `// NOLINT` / `// NOLINTNEXTLINE` comments
  authored in the audited tree are honored — clang-tidy offers no switch to
  ignore them, so a suppressed diagnostic never becomes a finding. Suppression
  comments are visible in the audited diff.
- **The repository configuration's blind spots.** A `.clang-tidy` file in the
  repo selects checks and check options unless `Checks` or `ConfigFile`
  overrides it; only enabled checks fire. A clean audit says nothing about
  checks the configuration never enabled. The scan stderr (kept in the raw
  output) shows "Suppressed N warnings (N with check filters)" when filters
  hid diagnostics.
- **Accurate builds.** Without a `compile_commands.json` (auto-discovered
  from the file's parent directories) or `CompileFlags`, files are analysed
  with default flags — include paths and language standards may be wrong and
  diagnostics may be missed or spurious. Provide one or the other for real
  codebases.
- **System headers.** System-header diagnostics are off by default (the
  tool's own default); pass `--system-headers` in `ExtraArguments` to see
  them.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

clang-tidy's convention (verified against 18.1.3 — **not** assumed from the
common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0`, no YAML on stdout | Ran clean — the tool emits no report when no diagnostic fired | Verdict (pass) |
| `0` with YAML on stdout | Ran, warnings found | Verdict (advisory unless an `Error` entry is present) |
| `1` with YAML on stdout | Ran with errors (compile errors in the analysed code) | Verdict (`Passed = false` — `Error` entries fail) |
| `1` without YAML | Could not run: bad flags, unreadable config, no checks enabled, missing input — stdout is usage text or location-less driver errors | Infrastructure (`AuditUnavailableException`) |
| `0` with text diagnostics but no YAML | Ran, but the report sink was overridden (operator `--export-fixes` pointed elsewhere) | Infrastructure — a hijacked sink must never read as a pass |
| no translation units discovered | Nothing to analyse | Infrastructure (deterministic) — disable this auditor for non-C/C++ projects |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `clang-tidy` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **clang-tidy `18.1.3`** (`ExpectedVersion` in scoped
config — the Ubuntu 18.1.3 build, whose `--version` prints `Ubuntu LLVM
version 18.1.3`). A scanner's checks change between releases, so an unpinned
tool would change findings under you: the auditor probes `clang-tidy
--version` before every run and reports an infrastructure failure on any
other version.

The tool requirement declares **`AptPackage = "clang-tidy"`**, so baseline
provisioning installs it via apt **only when this plugin is enabled** (and
always verifies its presence). The Debian metapackage tracks the distro
default release, so keep the provisioned release and `ExpectedVersion` in
step: after a baseline upgrade that moves clang-tidy, update `ExpectedVersion`
to the new release (or hold the package) — otherwise the auditor fails closed
until you do.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs/verifies `clang-tidy` only in that
state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.clang-tidy"],
      "Enabled": ["codeybox.clang-tidy"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.clang-tidy" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.clang-tidy`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `18.1.3` | Pinned clang-tidy release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Checks` | — | Check selection passed to `--checks` (e.g. `-*,modernize-*,clang-analyzer-*`). Unset means the tool default plus the repository `.clang-tidy`. Ignored when `ExtraArguments` already supplies `--checks`. |
| `ConfigFile` | `null` | Path passed to `--config-file` — an operator-pinned config outside the repository. Ignored when `ExtraArguments` already supplies `--config` or `--config-file` (the tool forbids combining `--config` with `--config-file`). |
| `CompileFlags` | — | Comma-separated compiler flags, each passed through as `--extra-arg=<flag>` (e.g. `-std=c++17,-Iinclude`). For include paths and language standards when no `compile_commands.json` is present. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). `error` gives an errors-only gate; nothing escalates warnings to failures. |
| `IncludedRules` / `ExcludedRules` | — | Exact check names to keep/drop (e.g. `modernize-use-nullptr`, `clang-diagnostic-error`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`, `.venv/`, `venv/`, `__pycache__/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings; discovery pruning (above) is fixed. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra clang-tidy options appended after the file list (the tool accepts options in any position). Useful for `--header-filter`, `--system-headers`, `--warnings-as-errors`, `-p <build-path>`. Compiler flags belong in `CompileFlags`, not here. An `--export-fixes` here redirects the report away from stdout and breaks the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound (discovery probes share it under a 30 s cap). Exceeding it is infrastructure, not a pass. C/C++ analysis is slow — raise it for large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is partly on.** The audit subject writes
the repository, and clang-tidy lets source files suppress findings inline
(`// NOLINT` drops the diagnostic; `// NOLINTNEXTLINE(check)` drops one
check on the next line). Unlike some linters there is no flag to turn this
off, so suppressions are always honored — expect fewer findings than the raw
check set would produce on repos that use them. The larger surface is the
`.clang-tidy` configuration itself: it is repo-authored and honored because
the project's own analysis contract is the meaningful check — changes to it
are visible in the audited diff. The auditor runs under
`AuditCapabilities.None` (no agent credentials, no network). For a fully
operator-owned gate, set `Checks` (e.g. `-*,<wanted-checks>`) or pin an
out-of-repo file via `ConfigFile`.

## Default scope

The fixed discovery script enumerates translation units (`*.c`, `*.C`,
`*.cc`, `*.cpp`, `*.cxx`, `*.cp`, `*.c++`, sorted, at most 256 files per
scan) while pruning `.git/` and the vendored/generated directories listed
above: that is the project's own declaration of analysable scope minus code
that is not the change under audit. Headers are covered through inclusion,
not enumeration. The finding-level `ExcludePaths` backstop repeats the
vendored/generated prefixes for diagnostics the tool reports in included
headers: violations there belong to upstream packages or build output —
reporting them produces noise that trains operators to ignore the auditor.
Re-include a path by overriding `ExcludePaths`; repos with more than 256
translation units in scope fail closed with guidance instead of scanning a
silently truncated set.
