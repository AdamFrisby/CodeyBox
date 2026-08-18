# Agent Streams

CodeyBox captures the structured stdout event stream from agent invocations and
stores it as JSONL for later analysis. This layer does not interpret events; it
only requests the agent CLI's streaming JSON mode when available and persists
each stdout line as it arrives.

## Configuration

```json
{
  "CodeyBox": {
    "AgentStreams": {
      "Enabled": true,
      "Path": "logs/agents",
      "MaxFileSizeMb": 32,
      "RetainedDays": 14
    }
  }
}
```

| Key | Default | Description |
|---|---:|---|
| `Enabled` | `true` | When `false`, no stream flags are added and no files are opened. |
| `Path` | `logs/agents` | Root directory for captured streams. Must be writable at startup. |
| `MaxFileSizeMb` | `32` | Per-file cap. After the cap is reached, later stdout lines are dropped and a final `[...truncated by N bytes]` marker is appended. |
| `RetainedDays` | `14` | Daily sweep deletes older stream files. `0` keeps files forever. |

Startup validation rejects an empty path, `MaxFileSizeMb < 1`, or
`RetainedDays < 0`.

## File Layout

Files are written under:

```text
logs/agents/<workItemId>/<phase>-<iteration>-<short-uuid>.jsonl
```

Release-wide deep-audit LLM streams do not have a work item; those captures use
the release GUID in the same path position.

`<short-uuid>` is 6 lowercase hex characters, so retries do not overwrite prior
attempts.

Examples:

```text
logs/agents/abc12345-0000-0000-0000-000000000000/work-1-a1b2c3.jsonl
logs/agents/abc12345-0000-0000-0000-000000000000/audit-llm-security:llm-review-3-d4e5f6.jsonl
logs/agents/abc12345-0000-0000-0000-000000000000/rework-2-789012.jsonl
logs/agents/abc12345-0000-0000-0000-000000000000/merge-1-f0e1d2.jsonl
```

Captured phases are:

- `work`
- `rework`
- `merge`
- `audit-llm-<auditor-name>`

Work and merge use iteration `1`. Audit and rework use the current audit-loop
iteration.

## Capture Semantics

The stream file is stdout only. CodeyBox does not add log prefixes or analysis
records. Each line is redacted for known secret token patterns before writing,
using the same redaction pattern family as `SensitiveDataRedactionEnricher`.

Capture is best-effort. Disk errors are logged as warnings and do not fail the
agent invocation. Writers are closed when the agent run exits, including
cancellation and failure paths.

## Agent CLI Flags

Verified in this sandbox:

- Claude Code 2.1.128: `claude --print --output-format stream-json --verbose ...`
  (`claude --help` advertises `--output-format` choices including `stream-json`).
- Codex: `codex exec --json ...`

Not locally verifiable here:

- Gemini: the `gemini` binary was not installed in this sandbox. CodeyBox probes
  `gemini --help` inside the invocation sandbox and only adds
  `--output-format stream-json` when that help output advertises BOTH
  `--output-format` and `stream-json` (gemini-cli ≥ 0.40; the older `--json`
  flag was removed). If unavailable, the run falls back to normal text output
  and the plaintext-fallback summariser still produces a row.
- Antigravity: the `agy` binary is shape-compatible with Claude (Anthropic
  stream-json for claude-* gateway models, Gemini stream-json for gemini-*).
  CodeyBox first uses `agy --help` as a cheap prefilter, then runs a trivial
  `agy --print --output-format stream-json` functional probe and only adds the
  flag to real work when that probe exits successfully with parseable NDJSON.
  Results are cached per agy version. If the probe is ambiguous, prints usage,
  or fails, the runner falls back to agy's plaintext output and the
  plaintext-fallback summariser still produces a row.

Unsupported (plaintext-fallback only):

- Copilot: the current Copilot CLI runner does not expose a structured streaming
  mode, so no stream flag is added.
- Opencode: the `opencode run` CLI has no verified structured stream-json mode
  in this codebase; the runner emits plaintext stdout, captured as-is.
- Cursor: emits structured stream-json (see below); listed here only for
  completeness.

## Per-agent Capability Matrix

The stream-capture system supports two paths per agent:

1. **Structured** — runners that implement `IStructuredStreamAgentRunner` and
   pass `SupportsStructuredStreamAsync` write NDJSON events to the capture
   file. The kind-specific `IAgentStreamParser` (e.g. `ClaudeStreamParser`)
   extracts tool calls, token usage, stalls, and the final assistant message.
2. **Plaintext fallback** — every captured stream is summarised even when no
   structured events are recognised. The fallback summariser records line
   count, byte count, detected-error line count (lines containing
   `error`/`fatal`/`panic`/`exception`/`traceback`), a duration window from
   the cost row, and the last ~10 non-empty lines of output as the tail. The
   row keeps the originally-resolved `AgentKind` so dashboards still attribute
   the run to the right agent.

| Agent       | Structured stream-json | Plaintext fallback | Notes                                                              |
|-------------|------------------------|--------------------|--------------------------------------------------------------------|
| `claude`    | Yes (Anthropic NDJSON) | Yes                | `--output-format stream-json --verbose`                            |
| `codex`     | Yes (`codex exec --json`) | Yes             | Codex stream-json shape.                                           |
| `cursor`    | Yes (Claude-shape)     | Yes                | `--output-format stream-json --stream-partial-output`              |
| `gemini`    | Conditional            | Yes                | `gemini --help` probed for `--output-format stream-json`.          |
| `antigravity` | Conditional          | Yes                | Functional `agy --print --output-format stream-json` probe.        |
| `opencode`  | No                     | Yes                | Plaintext stdout only.                                             |
| `copilot`   | No                     | Yes                | Plaintext stdout only.                                             |

### Sniff and Kind Resolution

The orchestrator's sniffer asks each registered parser "do you claim this
line?" using the first 20 NDJSON lines of the file. Two parsers
(`AntigravityStreamParser`, `CursorStreamParser`) deliberately return false
for every line — their on-wire event shapes are byte-identical to Claude's,
so claiming by shape would mis-attribute real Claude streams. Attribution for
those two agents instead comes from the cost row (phase-matched, recorded by
the orchestrator at dispatch) and the work item's declared agent. The
production registration order is `antigravity, claude, codex, copilot,
cursor, gemini, opencode, unknown`; a parser-collision regression test
pins the expected sniff outcome. `CopilotStreamParser` and
`OpencodeStreamParser` follow the same "claims nothing by shape" rule —
their CLIs emit plaintext, so attribution comes from the work-item's
declared agent kind and the plaintext-fallback path produces the row.

### Stderr capture

For plaintext-fallback runs the runner tees stderr verbatim into the same
callback so the captured file carries diagnostics (CLI banners, OAuth
refresh chatter, error text); the plaintext summariser's `errors=` count
reflects the merged stream.

For agents that emit structured stream-json, the runner line-buffers
stderr until the next newline and then wraps each complete line in a
single-line JSON envelope of the form
`{"type":"codeybox.stderr","text":"<line>"}` before forwarding it through
the same callback. The envelope keeps stderr visible in the captured
.jsonl (so auth/usage diagnostics that fire BEFORE any structured event
is emitted — the failure mode that produced the antigravity
observability black hole — are recoverable from post-mortem inspection)
without ever interleaving non-JSON noise into the file. The per-line
buffer is bounded (64 KiB) so a CLI that emits a pathological newline-
free line cannot grow host memory before the on-disk size cap engages;
overflow is stamped with a recoverable `[...stderr line truncated]`
marker.

Provider parsers recognise the `codeybox.stderr` envelope type and fold
each captured line into the summary's `FinalAssistantMessage` under a
`[stderr-tail]` marker (capped at ~8 KiB so the persisted summary row
cannot balloon with attacker-influenceable stderr output). This keeps
stderr diagnostics visible even when the structured stream contains a
few recognised events but no provider final message — the auth/usage
failure shape where the stream begins with a normal `system/init` and
then emits only stderr. When no structured event is recognised at all,
the run falls through to the plaintext summariser whose `errors=` count
and tail also surface the envelope lines.

### Tool-Auditor Files

Tool-based auditors (lint, build, file-size, …) do NOT open a capture file:
they emit deterministic output, never invoke an LLM through this codepath,
and would otherwise produce an empty `audit-llm-*.jsonl` plus an empty
`agent_stream_summaries` row. Only LLM-style auditors and the work/rework/
merge phases open capture files.

## API

- `GET /workitems/{id}/agent-streams` lists captured files and metadata. The
  list is bounded (`limit`, default 100, maximum 500); `lineCount` is only
  populated when `includeLineCount=true`.
- `GET /workitems/{id}/agent-streams/{fileName}` returns the raw stream as
  `application/x-ndjson`.

See [`api.md`](api.md#agent-streams) for response shapes.

## Storage Cost

Typical streams are a few KB to about 1 MB per invocation. A five-iteration item
with three LLM auditors per iteration can produce around 20 stream files. At
roughly 10-30 MB per work item and 100 work items per week, the default 14-day
retention is about 30 GB of stream data.

`logs/` is gitignored, which covers the default `logs/agents/` path.
