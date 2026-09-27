# CodeyBox: Cppcheck C/C++ Analyser

Auditor plugin wrapping [Cppcheck](https://cppcheck.sourceforge.io/): it analyses the
audited repository with `cppcheck --xml --xml-version=2 --enable=all
--error-exitcode=1 --quiet .` and reports each diagnostic as an audit finding with the
Cppcheck check id (e.g. `nullPointer`, `uninitvar`, `unusedFunction`) and `file:line`
location. C and C++ analysis only.

## What it reports

- One finding per Cppcheck diagnostic. The title carries the check id and the first
  line of the message (e.g. "nullPointer: Null pointer dereference: p"); the
  description carries the tool, rule, tool-reported severity, location, the full
  message, and the CWE number when the tool supplies one. `Location` is
  `path:startLine` — cppcheck reports worktree-relative paths, so locations are
  repo-relative.
- **Gate behaviour: hybrid / severity-driven — not blocking on every finding.**
  Diagnostics at cppcheck severity `error` (definite defects: null-pointer
  dereference, uninitialized variables, resource leaks, …) map to `Error` and fail
  the audit. `warning`, `style`, `performance` and `portability` map to `Warning`
  (advisory); `information` (e.g. `missingInclude`) maps to `Info`. The declared map
  covers exactly the severity vocabulary `cppcheck --errorlist` reports for 2.13.0 —
  never passing raw levels through. `MinimumSeverity` only drops findings, it never
  raises them.

## What it cannot see

- **Non-C/C++ files.** Cppcheck checks `.c`, `.cpp`, `.cxx`, `.cc`, `.c++`, `.ipp`,
  `.ixx`, `.tpp`, `.txx` (and headers) — everything else produces no findings. A
  repository with no checkable files is a clean pass (cppcheck exits 1 with
  "could not find or open any of the paths given." and no report; the auditor maps
  exactly that diagnostic to zero findings), not an error.
- **Suppressed diagnostics (by default, none).** Inline `// cppcheck-suppress <id>`
  comments are inert unless the scan passes `--inline-suppr` (it does not), and
  suppression-list files apply only when named (see `SuppressionsPath`) — the audit
  subject cannot silence findings from inside the repository. Pass `--inline-suppr`
  in `ExtraArguments` to deliberately trust repo-authored suppression.
- **Configuration the repository does not declare.** Cppcheck reads no config file
  from the repository — there is no repo-local config format — so the scan always
  runs the same operator-visible flags. The larger precision surface is the code
  itself: missing headers produce advisory `missingInclude` (information) findings
  rather than blocking, and analysis precision follows from what is checked in.
  Operators who want a fixed bar (custom platform, library configs, a compile
  database via `--project`) pass it in `ExtraArguments` or pin a suppressions file
  via `SuppressionsPath`.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.
- **More than `MaxOutputBytesPerStream` of report.** The XML report lives on stderr
  and shares the per-stream capture cap (default 1 MiB); overruns truncate the
  stream, which fails the parse loudly as infrastructure rather than silently
  dropping findings.

## Exit codes and failure classification

Cppcheck's convention (verified against 2.13.0 — **not** assumed from the common
"0 clean / 1 findings / 2 error" table). Note the inversion: without
`--error-exitcode` cppcheck exits `0` even with defects, which is why the scan passes
`--error-exitcode=1` explicitly. Also note the channels: the XML report goes to
**stderr**; progress lines and usage diagnostics go to **stdout**.

| Exit | Meaning | Classification |
|---|---|---|
| `0` with XML report | Checked clean (empty `<errors>`) | Verdict (pass) |
| `1` with XML report on stderr | Checked, defects found | Verdict (`Passed = false` only when an `error`-severity diagnostic is present — `style`/`warning` findings are advisory) |
| `1` with "could not find or open any of the paths given." on stdout, no XML | Ran, but the tree has no checkable C/C++ files | Verdict (pass, zero findings) |
| `1` with other text, no XML | Could not run: bad flags (`unrecognized command line option`), unreadable suppressions file | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `cppcheck` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **Cppcheck `2.13.0`** (`ExpectedVersion` in scoped
config). A scanner's checks change between releases, so an unpinned tool would
change findings under you: the auditor probes `cppcheck --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement declares **`AptPackage: cppcheck`** — the host installs it
into the sandbox baseline via `apt-get` **only when this plugin is enabled**, and
verifies its presence at bake time. The apt package tracks the distro release
(`2.13.0` on current Ubuntu LTS baselines); if your baseline's distro ships a
different cppcheck, set `ExpectedVersion` to the provisioned release instead of
fighting the package manager.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs and verifies `cppcheck` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.cppcheck"],
      "Enabled": ["codeybox.cppcheck"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.cppcheck" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.cppcheck`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.13.0` | Pinned cppcheck release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `SuppressionsPath` | `null` | Path passed to `--suppressions-list` — an operator-owned suppressions file **outside** the audited repository. Ignored when `ExtraArguments` already supplies `--suppressions-list`. Never points into the repo: a repo-authored file would let the subject silence the gate. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Useful as `warning` to silence `information` noise (e.g. `missingInclude` on trees without configured includes). |
| `IncludedRules` / `ExcludedRules` | exclude `checkersReport` | Exact cppcheck check ids to keep/drop (e.g. `nullPointer`, `uninitvar`, `unusedFunction`). `checkersReport` (a tool-configuration meta message emitted on every `--enable=all` run, with no file position) is excluded by default; setting `ExcludedRules` replaces that default. |
| `ExcludePaths` | `vendor/`, `third_party/`, `external/`, `node_modules/`, `build/`, `out/`, `dist/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; cppcheck reports worktree-relative paths so these prefixes match. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--std=<c++20\|c11|…>`, `--disable=<id>` (subtract from `--enable=all`), `--enable=<ids>` (replaces `--enable=all` wholesale — cppcheck `--enable` is additive so yours wins by replacing), `-i<dir>` scan-time ignores, `--inline-suppr` (trust repo-authored suppression comments), `--project=<compile-commands>` or `--check-level=exhaustive`. Overriding `--xml-version` changes the report shape the parser expects and breaks the run into infrastructure failure; `--output-file` redirects the report away from stderr with the same effect; overriding `--error-exitcode` changes the exit convention the auditor classifies on — any non-`0`/`1` verdict exit becomes infrastructure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. Whole-tree C++ analysis is the slow case; raise toward the 3600 s ceiling for large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a truncated XML report fails the parse as infrastructure — raise the cap rather than accepting partial findings). |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and cppcheck lets source files suppress the analyser inline
(`// cppcheck-suppress nullPointer`). The scan never passes `--inline-suppr`, so
those comments are inert — findings surface for code the comments would have
suppressed (verified against 2.13.0). Expect *more* findings than a local
`cppcheck --inline-suppr` run on repos that rely on suppression comments; that is
the gate working as intended. Suppression-list files are likewise never auto-read
from the repository; only the operator-owned `SuppressionsPath` (or an explicit
`--suppressions-list` in `ExtraArguments`) names one.

## Default scope

`cppcheck .` with `--enable=all` — every C/C++ file under the worktree is checked;
that is the project's own declaration of checkable scope. The scan adds `--quiet`
so it never pollutes captured output with progress lines, and writes nothing into
the audited tree. On top of that, the finding-level `ExcludePaths` backstop lists
vendored (`vendor/`, `third_party/`, `external/`, `node_modules/`) and generated
(`build/`, `out/`, `dist/`, `coverage/`) prefixes: diagnostics there belong to
upstream packages or build output, not the change under audit — reporting them
produces noise that trains operators to ignore the auditor. Re-include a path by
overriding `ExcludePaths`, or narrow the scan itself with `-i<dir>` in
`ExtraArguments` (which also saves scan time on large vendored trees — the
finding-level filter does not).
