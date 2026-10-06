# CodeyBox: CUE Schema Validation Auditor

Auditor plugin wrapping [`cue vet`](https://cuelang.org/docs/)
(`cue vet -c`): it validates selected generated configuration files against
operator/project-approved CUE constraints and reports each validation
diagnostic as an audit finding with its exact file/line location preserved.

## What it reports

- One finding per `cue vet` diagnostic block. CUE reports no rule ids, so
  every finding carries the synthesized `cue/validation` rule id —
  `IncludedRules`/`ExcludedRules` select it as one unit.
- `Location` is the validated data file with a `:line` suffix wherever the
  tool supplies one. A diagnostic references both the constraint and the
  offending value (`./schema/svc.cue:5:24`, `./data/bad.yaml:2:11`); the
  auditor prefers the non-`.cue` reference (the data under test) and falls
  back to the first reference. Single-line diagnostics (`bad.yaml:1: did
  not find expected ',' or ']'`) report their message-prefix location;
  diagnostics without any location (e.g. `replicas: incomplete value int`)
  report path-less.
- **Severity: every diagnostic fails the audit by default.** CUE has no
  severity levels — a diagnostic means the input was not proven valid — so
  all findings map to `Error`. There is no advisory mode; narrow scope with
  `SchemaPaths`, `InputPaths`, `SchemaExpression`, or `ExcludedRules`
  instead.

## What it cannot see

- **Anything outside the explicit file set.** The auditor never walks the
  tree: only `SchemaPaths` + `InputPaths` operands reach the tool.
- **Runtime semantics.** `cue vet` is static constraint validation: it
  cannot tell whether endpoints respond, clusters admit the manifests, or a
  plan would go green.
- **Imports that need the network.** The auditor runs without network
  egress and never fetches anything during the audit — registry inputs
  (`cue.dev/x/foo@latest`), module patterns (`./...`), and URLs are
  rejected, and ambient injection (`-t/--inject`, `-T/--inject-vars`) has no
  supported spelling. An import that can only resolve over the network
  fails closed as infrastructure. Vendor module dependencies into the tree
  or the baseline instead.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (the tool still validates them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`.
- **Missing or empty inputs.** Unconfigured schemas, files absent from the
  audited tree, and empty operands are infrastructure failures, not a pass.
  Enable this auditor only on projects that carry the configured CUE
  constraints and data files.

## Exit codes and failure classification

Verified by running the pinned binary (0.17.1) across a matrix of clean,
violation-bearing, incomplete-value, missing-file, bad-flag, broken-YAML,
and unresolved-import invocations (consistent with `cue help vet`: "The
command is silent when it succeeds; otherwise it reports any errors
found"):

| Exit | stderr | Meaning | Classification |
|---|---|---|---|
| `0` | silent | validation succeeded against verified nonempty operands | clean pass |
| `1` | diagnostic blocks (`message:` + `path:line:col` references) | validation failures found | findings |
| `1` | `stat …: no such file or directory`, `unknown flag: …`, `import failed: …`, fetch errors | could not run | infrastructure |
| `1` | none / unrecognized | could not run or output truncated | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

The discriminator is the diagnostic output, not the exit code: exits `0`
and `1` are findings-producing, and an exit without a parseable
diagnostic block fails closed as infrastructure through the parser. A
missing `cue` binary, a version mismatch, a timeout, unparseable output,
and an unresolved import are likewise infrastructure failures naming the
tool — never a passing audit. A scanner that silently passes because it
did not run is the worst outcome available, and this auditor has no path
that produces it: a silent exit `0` is accepted as a pass only after the
pre-scan probe proves every configured operand exists and is nonempty.

## Version pinning

The auditor is pinned to **cue `0.17.1`** (`ExpectedVersion` in scoped
config): the language version, standard library, and diagnostic shape
change between releases, so an unpinned binary would change findings under
you. `cue version` (which prints `cue version v0.17.1` plus the language
and Go versions; the pin reads the tool version) is probed before every
run; any other version is an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries cue. Provision the pinned upstream release into the
sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
CUE_VERSION=0.17.1
curl -fsSL "https://github.com/cue-lang/cue/releases/download/v${CUE_VERSION}/cue_v${CUE_VERSION}_linux_amd64.tar.gz" -o /tmp/cue.tar.gz
tar -xzf /tmp/cue.tar.gz -C /tmp cue
install -c -m 0755 /tmp/cue /usr/local/bin/cue
cue version   # must print the pinned version
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.cue"],
      "Enabled": ["codeybox.cue"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.cue" }
```

Only then does the declared `cue` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared). Enabling alone validates nothing:
`SchemaPaths` must name at least one constraint file before the auditor
will run — without it the auditor reports misconfigured-unavailable
rather than passing vacuously.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.cue`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.17.1` | Pinned cue release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `SchemaPaths` | — (**required**) | Comma-separated repo-relative `.cue` constraint files (at least one). Passed as the leading `cue vet` operands. Registry references, URLs, directories, and `./...` are rejected. |
| `InputPaths` | — | Comma-separated repo-relative files under validation: `.cue`, `.json`, `.jsonl`, `.ndjson`, `.yaml`, `.yml`, `.toml`, `.txt` (the formats `cue vet` documents). Same locality rules as schemas. Empty → the schemas themselves are still type-checked. |
| `SchemaExpression` | — | Optional `-d/--schema` expression selecting the schema (e.g. `#Service`); repeatable on the tool but single here. Unset → each file is checked against the root of the loaded CUE. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. All CUE diagnostics map to `Error`, so the default keeps everything. |
| `IncludedRules` / `ExcludedRules` | — | The only rule id is `cue/validation`: exclude it to silence the auditor without disabling the plugin (finding-level filter, applied after the scan). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell): additional local operands only, under the same containment and extension policy as `InputPaths`. **Any flag-looking entry is rejected** with a deterministic infrastructure error — `cue vet` flags are managed by the auditor, and `-i/--ignore` (masks failures), `-C/--chdir` (escapes the worktree), `-t/-T` (ambient injection), and `-I/--proto_path` (widens import scope) have no supported spelling here. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

There is no default scan. The invocation is always
`cue vet -c [-d <expression>] -- <schemas...> <inputs...>` from the
work-tree root with explicit operator-configured operands — no tree walk,
no package expansion, no fetches. An operator who wants the schemas
themselves type-checked with no data files leaves `InputPaths` empty; an
operator who wants concreteness checking of data without constraints still
configures the constraint file that defines the check (use a trivial
`#Data: {...}` schema or the data's own package file as both schema and
input).
