# CodeyBox: ApiCompat .NET API Compatibility Auditor

Auditor plugin wrapping
[Microsoft.DotNet.ApiCompat.Tool](https://learn.microsoft.com/dotnet/fundamentals/apicompat/global-tool)
(`apicompat`): it loads the contract (baseline) and implementation
assemblies or packages into Roslyn symbols, diffs their public API surface
with the upstream rule set, and reports each unsuppressed difference as an
audit finding with the `CP####`/`PKV###` diagnostic id and the compared
artifact path.

## What it reports

- One finding per unsuppressed compatibility difference — a `CP####: …`
  line on apicompat's **error channel (stderr)** under an
  `API compatibility errors between '<left>' (left) and '<right>' (right):`
  header. The diagnostic id (`CP0002` member removed, `CP0006` interface
  member added, `CP0017` parameter renamed, `PKV0006` target framework
  dropped, …) is the finding's rule id, so `ExcludedRules`/`IncludedRules`
  select by id. The message carries the member DocId and both artifact
  paths.
- Warning-channel diagnostics (on **stdout** — e.g. `CP1003` reference
  search directories not found, `PKV0008` baseline TFM ignored) become
  `Warning` findings; `info CP…` informational differences become `Info`.
- `Location` is the compared artifact path from the message — the one
  matching a configured right-side (implementation) operand, else the
  message's only path token, else the header's right side. apicompat has
  no source `file:line` notion: locations are assembly/package paths, and
  a path that cannot be attributed produces no location rather than a
  guess.
- **Severity: blocking on breaking changes.** The declared mapping sends
  error-channel diagnostics to `Error` (fails the audit), warning-channel
  to `Warning`, `info` to `Info`. Raw tool vocabulary never reaches
  findings.

## What it cannot see

- **Behavioural breakage.** Only signature-/surface-level differences the
  upstream rule set covers — not semantics, behavioural regressions, or
  dependency bumps' transitive breakage.
- **Non-public API** by default — `--respect-internals` (via
  `ExtraArguments`) opts in.
- **Optional rules** are off unless enabled: attribute matching
  (`--enable-rule-attributes-must-match`), parameter-name checks
  (`--enable-rule-cannot-change-parameter-name`), strict mode
  (`--strict-mode`) — all passable via `ExtraArguments`.
- **Suppressed differences.** A `--suppression-file` or `--noWarn` entry
  removes the finding entirely — apicompat never logs it. See
  *Suppression posture* for who may provide that file.
- **Unbuilt code.** The auditor compares already-built artifacts; it does
  not compile the repository. Missing operands fail closed.

## What an operator must configure

This auditor has **no default scope**: which artifacts constitute the
public API is an operator decision, so both sides of a comparison are
required configuration. Missing or half-configured operands are a
deterministic infrastructure failure — loud, never a pass.

**Assembly mode** — compare contract (left/baseline) vs implementation
(right/current) artifacts. Each operand is a `.dll` path, a directory of
assemblies, or a `*` glob; a comma-separated list in one value matches
apicompat's own tokenisation:

| Key | Flag | Use when |
|---|---|---|
| `Left` | `--left`/`-l`/`--left-assembly` | The contract: baseline build output — e.g. assemblies baked into the sandbox image or produced by a pre-audit step. |
| `Right` | `--right`/`-r`/`--right-assembly` | The implementation: the current build output under audit. |

**Package mode** — validate a `.nupkg` (self-contained TFM checks, and a
baseline diff when `BaselinePackage` is set):

| Key | Flag | Use when |
|---|---|---|
| `Package` | `package <file>` positional | The package under audit. |
| `BaselinePackage` | `--baseline-package <file>` | The released package to diff against. Optional — without it, package-internal checks still run. |

Assembly and package mode are mutually exclusive; mixing them, or
configuring the same operand through both a scoped key and
`ExtraArguments`, is a deterministic failure.

**Suppression file (optional).** `SuppressionFile` → `--suppression-file`.
apicompat reads no config file automatically — suppression files must be
named explicitly — so the gate is about *where* the file lives: a
repository-relative path is writable by the change under audit and is a
deterministic failure unless `TrustRepositorySuppression` is `true`;
absolute paths provisioned outside the worktree are always allowed.

## Exit codes and failure classification

apicompat does **not** follow the common "0 = clean, 1 = findings, 2 =
could not run" convention — verified against 10.0.401 source and binaries:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | ran; no unsuppressed error differences | pass (warning/info findings still reported) |
| `1` with `CP####`/`PKV###` diagnostics on stderr | ran; breaking changes found | findings |
| `1` with **no** diagnostics | usage error, missing operand, bad suppression file, crash — exit 1 is shared between findings and every failure | infrastructure |
| `126` / `127` | cannot execute / not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

Note the history this encodes: before the .NET 10 line, the tool exited
`0` even when it found breaking changes (dotnet/sdk#50989). The
`ExpectedVersion` pin exists so that convention is the one being probed.

A missing `apicompat` binary, a version mismatch, a missing
repository-relative operand, a timeout, and unparseable output are
likewise infrastructure failures naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **Microsoft.DotNet.ApiCompat.Tool `10.0.401`**
(`ExpectedVersion` in scoped config): the rule set, message templates, and
exit convention change between releases. `apicompat --version` is probed
before every run; any other version is an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`, since
no distro package carries it. Install into the baseline via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`:

```sh
dotnet tool install --global Microsoft.DotNet.ApiCompat.Tool --version 10.0.401
apicompat --version   # must print 10.0.401(+<commit>)
```

The shim needs a .NET runtime — a dotnet SDK or runtime install in the
baseline is a prerequisite. `dotnet tool install` itself needs network or
a local package cache at provision time; the audit run needs **none** —
the comparison is offline metadata analysis, so the auditor declares no
`Network` capability.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.apicompat"],
      "Enabled": ["codeybox.apicompat"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.apicompat" }
```

Only then does the declared `apicompat` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.apicompat`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `10.0.401` | Pinned tool release; any other installed version fails closed as infrastructure. Set to the release you provisioned. |
| `Left` / `Right` | — | Assembly-mode operands (required together, one channel): contract vs implementation — `.dll` paths, directories, `*` globs, comma-separated lists. Repository-relative literals are probed for presence before the scan. |
| `Package` / `BaselinePackage` | — | Package mode: the `.nupkg` under audit and an optional baseline `.nupkg`. Mutually exclusive with `Left`/`Right`. |
| `SuppressionFile` | — | `--suppression-file` path. Repository-relative requires `TrustRepositorySuppression`; absolute paths (operator-provisioned outside the worktree) are always allowed. |
| `TrustRepositorySuppression` | `false` | When `true`, suppression files inside the audited repository are honored. When `false`, a repo-relative suppression path is a deterministic failure — the audited change could edit it to suppress its own breakage. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity — e.g. `error` keeps only breaking changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact diagnostic ids to keep/drop (e.g. `CP0017`). |
| `ExcludePaths` | — | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Locations are the compared artifact paths, so exclusions match those operands (e.g. `artifacts/baseline/`). |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--strict-mode`, `--respect-internals`, `--enable-rule-*`, `--noWarn`, `--exclude-attributes-file`, `--left/right-assembly-references`, `-v`, or an entirely operator-supplied `package <nupkg> …` invocation. `--generate-suppression-file` is refused: it routes differences into a file instead of the report, a silent pass. |
| `TimeoutSeconds` | `600` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

There is deliberately none: the auditor reports only on the operand pair
the operator names, so vendored or generated code is never scanned unless
pointed at, and `ExcludePaths` needs no defaults. A repository or
configuration that supplies no operands fails closed deterministically —
enabling this auditor without naming the compared artifacts is a loud
misconfiguration, not a silent skip.
