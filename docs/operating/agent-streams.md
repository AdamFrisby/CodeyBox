# Agent output streams

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

## File layout

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

## What lands in the file

The stream file is stdout only. CodeyBox does not add log prefixes or analysis
records. Each line is redacted for known secret token patterns before writing,
using the same redaction pattern family as `SensitiveDataRedactionEnricher`.

Capture is best-effort. Disk errors are logged as warnings and do not fail the
agent invocation. Writers are closed when the agent run exits, including
cancellation and failure paths.

## Which CLIs stream

Confirmed structured modes:

- Claude Code 2.1.128: `claude --print --output-format stream-json --verbose …`
- Codex: `codex exec --json …`

Probed at runtime, because the flag depends on the installed version:

- Gemini: CodeyBox probes
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

No structured mode, so plaintext fallback only:

- Copilot: the current Copilot CLI runner does not expose a structured streaming
  mode, so no stream flag is added.
- Opencode: the `opencode run` CLI has no verified structured stream-json mode
  in this codebase; the runner emits plaintext stdout, captured as-is.

## Capability matrix

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

### Attributing a file to an agent

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
.jsonl (so auth and usage diagnostics that fire *before* any structured
event — a failure mode that otherwise leaves no trace at all — survive
for post-mortem inspection)
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

### Why tool auditors have no files

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

See [`../reference/api.md`](../reference/api.md#agent-streams) for response shapes.

## Storage cost

Typical streams are a few KB to about 1 MB per invocation. A five-iteration item
with three LLM auditors per iteration can produce around 20 stream files. At
roughly 10-30 MB per work item and 100 work items per week, the default 14-day
retention is about 30 GB of stream data.

`logs/` is gitignored, which covers the default `logs/agents/` path.

## What the analyser derives

Once a work item reaches a terminal state, a read-only pass parses its captured
files and writes derived rows to `agent_stream_summaries`.

### What each summary holds

Each invocation summary includes:

- total stream duration and time to first assistant token
- input, output, and cached input token counts
- authoritative cost from the agent result event when present
- tool calls with name, redacted input summary, start/end timestamps, duration, success flag, and output bytes
- stall events
- final assistant text when the captured stream includes it

Claude, Codex, and Gemini parsers are registered separately because their CLI stream shapes differ. Unsupported agents produce an empty `unknown` summary so the dashboard can remain graceful when stream JSON is not available.

### Stall detection

A stall is an inter-event gap greater than `StallThreshold` (default 30 seconds). Gaps after a `result` event are ignored because the stream has already ended.

Stalls are classified from the prior event state:

- `tool_execution`: after a `tool_use` while a tool result is still outstanding
- `llm`: after assistant text or a `tool_result`, while waiting for model output
- `unknown`: any other long gap

Truncated files are accepted. A `tool_use` without a matching result is recorded as unfinished with null end time, null duration, and unknown success.

Captured CLI streams do not always include timestamps. Claude Code 2.1 stream-json and Codex CLI 0.128 command-execution events can omit per-event `timestamp`, `started_at`, `completed_at`, and duration fields. In that case the analyser keeps the capture file read-only and projects timestamp-less events onto the known invocation start/end window from the matching work-item cost row. It uses JSONL line position when line count is available, otherwise byte offset and captured file size. Those projected times drive total duration, TTFT, stalls, tool-call intervals, and the thinking/executing split for real captured streams that only contain raw CLI stdout. If there is no matching invocation timing context, timestamp-less events still contribute tokens, cost, tool frequency, input summaries, output sizes, and final text, but timing fields remain zero or null.

### Thinking vs executing

`executingMs` is the wall-clock union of completed tool-call intervals per invocation. Overlapping parallel tool calls are counted once. Tool results that include an explicit duration but no start/end timestamps contribute that duration without forming an interval. When stream events are timestamp-less but the invocation timing context is available, projected event times create estimated tool intervals. `thinkingMs` is `totalAgentDurationMs - executingMs`, clamped at zero.

### Persistence and cost reconciliation

The cache table is:

```sql
CREATE TABLE agent_stream_summaries (
    work_item_id    TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
    file_name       TEXT NOT NULL,
    phase           TEXT NOT NULL,
    iteration       INTEGER,
    agent_kind      TEXT NOT NULL,
    total_duration_ms INTEGER NOT NULL,
    time_to_first_token_ms INTEGER,
    input_tokens    INTEGER,
    output_tokens   INTEGER,
    cached_input_tokens INTEGER,
    estimated_usd   REAL,
    tool_calls_json TEXT NOT NULL,
    stalls_json     TEXT NOT NULL,
    final_assistant_message TEXT,
    summarised_at   TEXT NOT NULL,
    PRIMARY KEY (work_item_id, file_name)
);
```

Retrying a terminal work item invalidates cached stream summaries for that item. The next terminal transition causes a fresh summary pass.

Cost in the agent `result` event is more authoritative than the pipeline cost extractor. On a summary pass, the SQLite cost store updates the newest matching `work_item_costs` row by work item, phase, iteration, and agent kind. If no row exists, it inserts a stream-sourced row with `raw_metadata_json.source = "agent_stream_analyser"`.

Audit stream files use detailed phases such as `audit-llm-security:llm-review`; reconciliation also matches the canonical `audit` phase used by pipeline cost rows so stream result costs correct the existing row instead of adding a duplicate.

Cached dashboard summaries and on-demand API analysis responses expose the same final assistant text parsed from the stream. The field remains nullable because not every CLI schema emits final text and truncated files may end before the final response.
