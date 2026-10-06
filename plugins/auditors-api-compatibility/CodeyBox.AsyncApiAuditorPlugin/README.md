# CodeyBox: AsyncAPI Contract Compatibility Auditor

Auditor plugin wrapping the [AsyncAPI CLI](https://www.asyncapi.com/docs/tools/cli)
(`@asyncapi/cli`): it validates the candidate and baseline AsyncAPI documents,
then diffs the baseline against the candidate and reports each breaking,
unclassified, and non-breaking document change as an audit finding with a
stable rule id and the candidate document as its location.

## What it reports

- One finding per entry in the tool's `diff --format json` report. The title
  carries the rule id — `{CATEGORY}_{ACTION}` (e.g. `BREAKING_REMOVE`,
  `NON_BREAKING_ADD`, `UNCLASSIFIED_EDIT`), or `{CATEGORY}_CHANGE` when the
  entry carries no action — so `IncludedRules` / `ExcludedRules` select by
  rule. The description carries the tool, rule, tool severity, location, and
  the change (`<action> <document-pointer> (<category> change)`).
- `Location` is the configured `NewSpec` pointer (e.g.
  `events/asyncapi.yaml`). The tool reports JSON-pointer paths *into* the
  documents — never file/line — so findings never carry a line number.
- **Severity: blocking on breaking changes.** The declared mapping sends the
  tool's `breaking` category to `Error` and fails the audit; `unclassified`
  maps to `Warning` and `non-breaking` maps to `Info`, both advisory. Raw
  tool levels never reach findings.
- **A reported difference is not proof of consumer breakage.** The diff
  classifies document edits against its built-in standard; it knows nothing
  about real operation usage, so a `breaking` finding is a compatibility
  risk to review — not a proven consumer incompatibility.

## What it cannot see

- **Anything but the two configured documents.** Findings compare exactly
  `OldSpec` against `NewSpec` — the tool never walks the repository, so
  unlisted documents, code-first contracts that are never exported, and
  gateway composition outside the two pointers are invisible.
- **Behavioural breakage.** Only document-structure changes the upstream
  classifier covers — not broker semantics, performance, or payload
  semantics beyond the change catalogue.
- **Usage-gated breakage.** Override-based reclassification (`--overrides`)
  is deliberately not offered: it would let reported categories diverge
  from the tool's built-in standard and, for an in-repo overrides file, let
  the diff author tune its own gate.
- **Remote documents.** URLs, context names, and remote `$ref` targets are
  rejected (see *Repository-controlled input*): the check is fully offline
  over two checked-in files.

## Exit codes and failure classification

`asyncapi diff` does **not** follow the common "0 = clean, 1 =
findings, 2 = could not run" convention — verified against the 5.0.7
`src/apps/cli/commands/diff.ts` source:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | diff completed; no breaking changes (unclassified / non-breaking changes may be listed) | findings (advisory) or pass |
| `1` | **ambiguous:** breaking changes found (`DiffBreakingChangeError`, printed *after* the JSON report) *or* could not run (load/parse/validation failure, no JSON report) | findings when the report carries breaking entries; infrastructure otherwise |
| `1` with **no** breaking entry on stdout (error dump, or a report with only advisory changes) | load/parse failure, or a report contradicting the exit contract | infrastructure |
| `0` with a breaking entry on stdout | report contradicts the exit contract | infrastructure |
| `0` or `1` with empty / unrecognizable stdout | no verifiable verdict (an invalid document short-circuits the command to a silent exit `0` — validation gates this before the scan) | infrastructure (never a pass) |
| `126` / `127` | cannot execute / not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

The pre-scan `asyncapi validate --fail-severity error` probes over both
documents exit `0` when the document is usable and non-zero otherwise; any
non-zero validation is coverage unavailable naming the document — never a
pass and never a finding. A missing `asyncapi` binary, a version other than
the pin, and an unparseable report are likewise infrastructure failures
naming the tool.

## Version pinning

The auditor is verified against **`@asyncapi/cli` `5.0.7`**
(`DefaultExpectedVersion` in code, the release documented in the
[official CLI usage reference](https://www.asyncapi.com/docs/tools/cli/usage)):
the classification standard, message catalogue, and exit convention move
between releases, so an unpinned binary changes findings under you. The
auditor probes `asyncapi --version` before every scan; a missing binary, an
unrecognised version string, or any other installed release fails closed as
infrastructure. Operators running a different pinned build set
`ExpectedVersion` in scoped config to match what they provisioned. Pin the
release at provisioning time:

```sh
npm install -g @asyncapi/cli@5.0.7
asyncapi --version   # must print 5.0.7
```

All requirements are declared **verify-only** — no `AptPackage`:

- `asyncapi` — no distro package carries a pinned release. Install the
  pinned release into the baseline via `CodeyBox:MultipassExtraRuncmd` /
  `CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions` (see above).
- `node` — the CLI runs on Node.js (5.x needs 18+). Bake a Node.js runtime
  alongside the pinned CLI; without it the presence probe fails closed as
  infrastructure.

## Network egress

With two local file documents the check is fully offline (two parses and
an in-memory diff — no credentials held, no egress). URL documents, remote
`$ref` targets, and proxy flags are rejected, so the auditor declares no
network capability itself and sandbox egress policy governs by denial.
Prefer checking the AsyncAPI documents into the repository.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.asyncapi"],
      "Enabled": ["codeybox.asyncapi"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.asyncapi" }
```

Only then does the declared `asyncapi` tool requirement reach baseline
provisioning (presence-verified at bake time; installed via the operator's
npm step because no `AptPackage` is declared). Explicit later operator
setup is required: provision the pinned CLI + Node.js, then configure
`OldSpec` / `NewSpec` below — nothing is enabled live by this change.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.asyncapi`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `OldSpec` | — (required) | Approved baseline document: repository-relative `.json` / `.yaml` / `.yml` file. Unset, overlong, non-document, URL, context-name, absolute, or `..`-escaping values are a deterministic infrastructure failure. |
| `NewSpec` | — (required) | Candidate document, same forms as `OldSpec`. Its repo-relative path becomes each finding's location. |
| `CompatibilityPolicy` | `all` | Which change categories the tool collects, passed explicitly as `--type`: `all`, `breaking`, `non-breaking`, `unclassified`. Anything else is a deterministic configuration failure. Severity mapping is fixed (see above) regardless of the selection. |
| `ExpectedVersion` | `5.0.7` | Pinned CLI release. A different installed version fails closed as infrastructure. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity — e.g. `error` keeps only breaking changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (e.g. `BREAKING_REMOVE`, `UNCLASSIFIED_EDIT`). |
| `ExcludePaths` | — | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Applies to the `NewSpec` file location above. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Entries that steer what the diff measures or where its verdict goes (`--type` / `-t`, `--format` / `-f`, `--markdownSubtype`, `--overrides` / `-o`, `--save-output` / `-s`, `--no-error`, `--watch` / `-w`, `--log-diagnostics`, `--diagnostics-format`, `--fail-severity`, `--proxyHost`, `--proxyPort`, including `=` and joined-short forms) are a deterministic configuration failure — use `CompatibilityPolicy` to select categories. |
| `TimeoutSeconds` | `300` | Per-run bound for the scan (probes share it under a 30-second cap); exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

`asyncapi diff` reads **no standalone config file** and no repository
config — its only inputs are the two documents and the flags above.

## Repository-controlled input

The CLI resolves each positional as a file, a URL, or a stored *context
name* — and a missing file falls through to context lookup, then to
auto-detecting an unrelated spec file in the working directory. Either
fallback would silently compare the wrong documents, so the auditor closes
both before the scan:

- Only repository-relative `.json` / `.yaml` / `.yml` paths are accepted.
  URLs (`://`), scheme prefixes (`http:`, `git:`, …), absolute paths,
  `..` escapes, and bare context names (no document extension) are
  rejected as deterministic configuration failures.
- Both specs are then proven to be regular files (`test -f`) inside the
  audited worktree, so the file branch of the tool's own resolution is the
  only reachable one — a missing baseline can never fall through to a
  context entry or an auto-detected file.
- Both documents must pass `asyncapi validate --fail-severity error`:
  without this gate an invalid document would short-circuit the diff to a
  silent exit `0` — indistinguishable from "no changes" without the
  pre-check.
- Old and new pointers must differ: a document compared against itself
  passes vacuously.

## Default scope

`asyncapi diff <OldSpec> <NewSpec> --format json --type <policy>` over
exactly the two configured documents: the tool parses those two documents
and nothing else, so vendored or generated code elsewhere in the tree can
never produce findings and the auditor ships no `ExcludePaths` default.
Both pointers are required with no guessed default — document layout
varies per repository, and an invented default risks a silent no-op diff.
Point them at the checked-in AsyncAPI files (not at directories, and never
at URLs or context names).

## Limits

- Findings describe document edits, not proven consumer impact; pair this
  gate with consumer-compatibility testing for high-risk event contracts.
- The JSON report shape, category catalogue, and exit convention are
  pinned to `@asyncapi/cli` 5.0.7 — a future release that renames
  categories fails closed as infrastructure rather than mis-mapping.
- Documents using features newer than the 5.0.7-bundled parser (or AsyncAPI
  versions it cannot parse) fail validation and are reported as coverage
  unavailable, not as clean.
- Reports beyond the per-stream capture bound are truncated and fail
  closed; raise `MaxOutputBytesPerStream` or narrow `CompatibilityPolicy`
  for very large diffs.
