# Agent Stream Analysis

CodeyBox analyses captured agent stream JSONL files after a work item reaches a terminal state. The analyser is read-only: it opens the persisted stream files, parses line-by-line, and writes derived rows to `agent_stream_summaries`.

## What Is Computed

Each invocation summary includes:

- total stream duration and time to first assistant token
- input, output, and cached input token counts
- authoritative cost from the agent result event when present
- tool calls with name, redacted input summary, start/end timestamps, duration, success flag, and output bytes
- stall events
- final assistant text when the CLI stream exposes it during on-demand parsing

Claude, Codex, and Gemini parsers are registered separately because their CLI stream shapes differ. Unsupported agents produce an empty `unknown` summary so the dashboard can remain graceful when stream JSON is not available.

## Stall Detection

A stall is an inter-event gap greater than `StallThreshold` (default 30 seconds). Gaps after a `result` event are ignored because the stream has already ended.

Stalls are classified from the prior event state:

- `tool_execution`: after a `tool_use` while a tool result is still outstanding
- `llm`: after assistant text or a `tool_result`, while waiting for model output
- `unknown`: any other long gap

Truncated files are accepted. A `tool_use` without a matching result is recorded as unfinished with null end time, null duration, and unknown success.

Captured CLI streams do not always include timestamps. The analyser prefers timestamps emitted by the stream events themselves. When a captured file has no event timestamps, the read path exposes the file's capture start/end metadata and byte offsets so the parser can assign a best-effort monotonic clock without modifying the JSONL. Those fallback timings are approximate, but they keep tool durations, stall gaps, total stream duration, and the thinking-vs-executing split available for real Codex and Claude captures that omit per-event timestamps.

## Thinking Vs Executing

`executingMs` is the wall-clock union of completed tool-call intervals per invocation. Overlapping parallel tool calls are counted once. `thinkingMs` is `totalAgentDurationMs - executingMs`, clamped at zero. This split identifies whether a slow invocation was mostly model-side waiting/thinking or tool execution.

## Persistence

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

Cached dashboard summaries keep final assistant text null so persisted rows stay numeric and structural; the on-demand analysis endpoint returns it from the live parse.
