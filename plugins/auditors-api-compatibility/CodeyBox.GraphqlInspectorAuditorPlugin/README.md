# CodeyBox: graphql-inspector GraphQL Schema Compatibility Auditor

Auditor plugin wrapping
[graphql-inspector](https://the-guild.dev/graphql/inspector/docs) (`@graphql-inspector/cli`):
it diffs a baseline GraphQL schema against the current one, and reports each
breaking, dangerous, and safe change as an audit finding with a rule id
derived from the upstream change catalogue and the audited schema file as
its location.

## What it reports

- One finding per `✖` / `⚠` / `✔` line in the tool's `diff` report. The
  title carries the rule id — the upstream `ChangeType` (e.g.
  `FIELD_REMOVED`, `ENUM_VALUE_REMOVED`, `FIELD_ARGUMENT_ADDED`) recovered
  from the message shape, or the level-derived `BREAKING_CHANGE` /
  `DANGEROUS_CHANGE` / `NON_BREAKING_CHANGE` fallback when the message
  matches no known template — so `IncludedRules` / `ExcludedRules` select by
  rule. The description carries the tool, rule, tool severity, location, and
  the change message.
- `Location` is the configured `NewSchema` pointer whenever it names a
  repository-relative file (e.g. `schema.graphql`). URL, `git:` / `github:`,
  and absolute pointers carry no repository location, so those findings
  report no location rather than a guessed one. The tool never reports line
  numbers, so findings never carry one.
- **Severity: blocking on breaking changes.** The declared mapping sends the
  tool's `BREAKING` criticality to `Error` and fails the audit; `DANGEROUS`
  maps to `Warning` and `NON_BREAKING` maps to `Info`, both advisory. Raw
  tool levels never reach findings.

## What it cannot see

- **Anything but the two configured schemas.** Findings compare exactly
  `OldSchema` against `NewSchema` — the tool never walks the repository, so
  unlisted schema files, code-first schemas that are never exported to SDL,
  and stitching/gateway composition outside the two pointers are invisible.
- **Behavioural breakage.** Only schema-structure changes the upstream diff
  covers — not resolver semantics, performance, or deprecation-policy
  compliance beyond the change catalogue.
- **Usage-gated breakage.** The `considerUsage` rule (which downgrades
  breaking changes based on real operation usage) needs a JavaScript usage
  module and is not offered; every removal counts as breaking unless a
  builtin `DiffRules` entry says otherwise.
- **Output format: text, not JSON.** The `diff` command exposes no JSON (or
  SARIF) output mode — verified against `@graphql-inspector/cli` 7.0.0, whose
  command builder accepts only `--rule`, `--onComplete`, and `--onUsage`.
  The auditor parses the human-readable report; message templates are pinned
  to that release (see *Version pinning*). A future release that rewords
  messages keeps reporting through the level-derived fallback rule ids —
  findings degrade to coarser rule ids, they never vanish.

## Exit codes and failure classification

`graphql-inspector diff` does **not** follow the common "0 = clean, 1 =
findings, 2 = could not run" convention — verified against the 7.0.0 source
(`packages/diff-command`) and live runs:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | diff completed; no breaking changes (dangerous / safe changes may be listed) | findings (advisory) or pass |
| `1` | **ambiguous:** breaking changes found (`process.exit(1)`) *or* could not run (thrown load error exits `1` via yargs) | findings when change lines are present; infrastructure otherwise |
| `1` with **no** change lines on stdout (error dump, stack trace) | load/parse failure the exit code does not distinguish | infrastructure |
| `1` with change lines but **none** breaking | report contradicts the exit contract | infrastructure |
| `0` or `1` with empty / unrecognizable stdout | no verifiable verdict | infrastructure (never a pass) |
| `126` / `127` | cannot execute / not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

A missing `graphql-inspector` binary and an unparseable report are likewise
infrastructure failures naming the tool — never a passing audit.

## Version pinning

The auditor is verified against **`@graphql-inspector/cli` `7.0.0`**
(`DefaultExpectedVersion` in code): the change catalogue, message
templates, and exit convention move between releases, so an unpinned binary
changes findings under you. Pin that exact release at provisioning time:

```sh
npm install -g @graphql-inspector/cli@7.0.0 graphql
graphql-inspector diff --help   # must list the diff command
```

There is deliberately **no runtime version probe**: `graphql-inspector
--version` prints `unknown` on every invocation (verified), so a probe could
never confirm the pin and would fail closed on the genuine binary. The pin
lives at provisioning time instead of pretending to verify at scan time.

All requirements are declared **verify-only** — no `AptPackage`:

- `graphql-inspector` — no distro package carries it. Install the pinned
  release into the baseline via `CodeyBox:MultipassExtraRuncmd` /
  `CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions` (see above).
- `node` — the CLI runs on Node.js 18+. Bake a Node.js runtime alongside
  the pinned CLI; without it the presence probe fails closed as
  infrastructure.

## Network egress

With two file pointers the check is fully offline (two SDL parses and an
in-memory diff — no credentials held). URL pointers (`https:` schemas) and
`github:` pointers need egress to those hosts plus any token/header the
operator configures; the auditor declares no network capability itself, so
sandbox egress policy governs. Prefer checking the SDL files into the
repository over diffing live endpoints.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.graphql-inspector"],
      "Enabled": ["codeybox.graphql-inspector"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.graphql-inspector" }
```

Only then does the declared `graphql-inspector` tool requirement reach
baseline provisioning (presence-verified at bake time; installed via the
operator's npm step because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.graphql-inspector`, resolved per
run (hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `OldSchema` | — (required) | Baseline schema pointer: SDL file (`schema/old.graphql`), `git:<ref>:<path>`, `github:` pointer, or URL. Unset (or over 1024 chars) is a deterministic infrastructure failure. |
| `NewSchema` | — (required) | Current schema pointer, same forms as `OldSchema`. Its repo-relative file form becomes each finding's location. |
| `DiffRules` | — | Comma-separated builtin diff rules, appended as `--rule <name>` argv pairs: `dangerousBreaking`, `suppressRemovalOfDeprecatedField`, `ignoreDescriptionChanges`, `safeUnreachable`. Anything else is a deterministic configuration failure — custom rule modules load sandbox-side JavaScript and are not accepted. A repository-root file shadowing a configured entry is likewise a failure (see *Repository-controlled input*). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity — e.g. `error` keeps only breaking changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (e.g. `FIELD_REMOVED`, `BREAKING_CHANGE`). |
| `ExcludePaths` | — | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Applies to the `NewSchema` file location above. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--federation`, `--federationV2`, `--aws`, `--method`, `--header` / `--token` for URL schemas. Entries that load code or replace the verified contract (`--rule`, `--onComplete`, `--onUsage`, `-r` / `--require`, including `=` and joined-short `-r<module>` forms) are a deterministic configuration failure — use `DiffRules` for builtin rules. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

`graphql-inspector diff` reads **no standalone config file** and no
repository config — its only inputs are the two pointers and the flags
above. The one repository-controlled surface is rule-name shadowing (see
*Repository-controlled input* below): a repository-root file whose name
matches a configured `DiffRules` entry fails the audit closed as
infrastructure before the scan runs.

## Repository-controlled input

`diff` reads no repository config file. Its code-loading surfaces are
`--rule` with a module path, `--onComplete` / `--onUsage` (JavaScript
modules; `--onComplete` additionally replaces the exit-1-on-breaking
contract the parser relies on), and `-r` / `--require`. Operator
`ExtraArguments` carrying any of those flags — including the joined short
form `-r<module>` — are a deterministic infrastructure failure; the
side-effect-free builtin rules are offered instead through the scoped
`DiffRules` key, which accepts only the exact allowlist
(`dangerousBreaking`, `suppressRemovalOfDeprecatedField`,
`ignoreDescriptionChanges`, `safeUnreachable`).

The allowlist alone is not sufficient: `graphql-inspector` resolves each
`--rule` name against its working directory (the audited repository root)
and `require`s a hit as code *before* consulting its builtin rule table
(verified against `@graphql-inspector/diff-command` 7.0.0). A
repository-root file named e.g. `dangerousBreaking` would therefore execute
audit-subject code in-process with the CLI — able to suppress breaking
changes and read operator-supplied argv such as `--token`. Before every
scan, the auditor probes the repository root for files shadowing the
configured `DiffRules` entries and fails closed with an infrastructure
error naming the file(s) when any is present. Either delete the file(s) or
drop the shadowed rule(s) from `DiffRules`. With no `DiffRules` configured
no `--rule` argv is emitted and no probe is needed.

## Default scope

`graphql-inspector diff <OldSchema> <NewSchema>` over exactly the two
configured pointers: the tool parses those two schemas and nothing else, so
vendored or generated code elsewhere in the tree can never produce findings
and the auditor ships no `ExcludePaths` default. Both pointers are required
with no guessed default — schema layout varies per repository, and an
invented default risks a silent no-op diff. Point them at the checked-in SDL
files (not at directories, and prefer files over live URLs for
reproducibility).
