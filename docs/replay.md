# Replay

A **replay** is a new, independent work item cloned from an existing terminal
work item with a different agent or model. Source and replay share the same
prompt and base branch but get separate IDs, work branches, audit iterations,
merge commits, and PRs.

Replays exist so operators can answer: *"is the new agent actually better?"*
Without comparison data, agent selection is gut-feel. With replay, the same
prompt runs under Claude, Codex, Gemini — side by side, objectively comparable
on cost, wall-clock, and finding counts.

---

## Semantics

### Must be terminal

A replay can only be created from a source in a **terminal state** (Done,
Failed, AuditFailed, Cancelled). Non-terminal sources are rejected with `400`.

Rationale: the source's full history (audit findings, timings, cost) is only
meaningful once it has finished. Replaying a still-running item would produce
an incomplete comparison.

### A replay is a new work item

The replay gets its own:

- Internal UUID
- Work branch (auto-generated or caller-supplied)
- Queue position
- Audit iterations
- Cost record
- Timing record
- PR / merge commit

The source is left completely unchanged — it is never restarted or modified.

### DependsOn inheritance

The replay inherits the source's `dependsOn` list exactly. If the source
depended on items A and B, the replay also depends on A and B. This preserves
the dependency graph for replays that are part of a larger pipeline.

### Immutability

`replay_of_work_item_id` is set at creation and never updated via the API
(`PATCH` has no effect on it). Cycles are impossible by construction because a
replay's source must be terminal, and a terminal item cannot gain a new
replay-of link.

### Replays of replays

Allowed. The chain `source → replayA → replayB` is valid. The comparison
endpoint treats the *requested* item as the source and returns all items that
directly replay it.

### Orphan-on-cancel

When the source work item is **cancelled**, all replays that point to it have
their `replay_of_work_item_id` cleared (set to null). The replays continue
running; they are simply no longer linked to the (now-cancelled) source.

Replay items that were started before the source was cancelled are not
interrupted. This is intentional — replays are research artifacts and should
run to completion even when the source is abandoned.

---

## API

### `POST /workitems/{id}/replay`

Creates a replay of work item `{id}`.

**Request body** (all fields optional):

```json
{
  "agent": "gemini",
  "modelId": "gemini-3.0-pro",
  "agentClassId": "frontier-coding",
  "workBranch": "feat/foo-replay-gemini"
}
```

| Field | Default | Description |
|---|---|---|
| `agent` | source's agent | Override the agent. Must be a known agent kind. Clears `agentClassId` if set. |
| `modelId` | (none) | Runtime model hint passed to the agent CLI as `--model`. Not persisted. |
| `agentClassId` | source's agentClassId | Route via a named agent class instead of a direct agent. Clears `agent` if set. |
| `workBranch` | `<source-branch>-replay-<short-id>` | Override the auto-generated work branch. Standard branch-name rules apply. Must differ from baseBranch. |

**Responses:**

- `201 Created` — the new work item record (same shape as `GET /workitems/{id}`).
- `400 Bad Request` — source is not in a terminal state; unknown agent; invalid work branch.
- `404 Not Found` — source item does not exist.

The new item starts in `Queued` state and enters the normal pipeline.

### `GET /workitems/{id}/replays`

Returns the source work item and all items that directly replay it.

**Response:**

```json
{
  "source": { ...workItemDto },
  "replays": [
    { ...workItemDto, "replayOfWorkItemId": "<source-id>" },
    ...
  ]
}
```

Replays are ordered by `created_at` ascending (oldest first). The `source`
field is the item identified by `{id}` — if you pass a replay's ID, it becomes
the source in the response and its own replays are returned.

**Responses:**

- `200 OK` — source + replays list (replays may be empty).
- `404 Not Found` — the item does not exist.

---

## WorkItem fields

Both `GET /workitems/{id}` and `GET /workitems` now include:

```json
"replayOfWorkItemId": "<uuid or null>"
```

This field is `null` for items not created via the replay API, and non-null
(pointing to the source) for replays. It is cleared to `null` if the source
is cancelled (see orphan-on-cancel above).

---

## Admin dashboard

### Replay button

The **Replay** button appears on the work-item-detail page for any item in a
terminal state. Clicking it opens a modal with:

- **Agent** dropdown (defaults to current agent; lists claude, codex, copilot, gemini)
- **Model ID** text input (optional, e.g. `gemini-3.0-pro`)
- **Work branch** text input (optional; auto-generated if blank)
- **Create replay** / **Cancel** buttons

After creation, the dashboard navigates to the new replay's detail page.

### Comparison page

`/work-items/{id}/comparison` shows a side-by-side grid of the source and all
its replays. Columns include:

| Row | Source | Replay 1 | Replay 2 |
|---|---|---|---|
| Agent | claude | gemini | codex |
| Status | Done | Queued | Failed |
| Work branch | feat/source | feat/source-replay-abc12345 | feat/source-replay-def67890 |
| Created | … | … | … |
| Wall-clock | 45.0s | — | — |
| Token cost | $0.34 | — | — |

Wall-clock and token-cost rows appear only when timing or cost data has been
recorded for at least one column.

---

## One replay at a time

The endpoint creates a single replay per call. For a four-way bake-off, call
the endpoint four times with different `agent` values. There is no
"replay-across-all-agents" shortcut by design — the operator chooses which
agents to compare.
