# External IDs

Work items can carry a caller-supplied **external ID** — an opaque string that lets API consumers reference a work item by a familiar identifier from another system (JIRA, GitHub Issues, an internal tracker, etc.) without storing a separate UUID mapping.

## Why

Two problems external IDs solve:

1. **Integration with project trackers.** Every consumer that calls the CodeyBox API today must maintain its own `external_id → internal_uuid` mapping so it can correlate CodeyBox work items with tickets in JIRA, GitHub Issues, or a custom dashboard. Setting an `externalId` at creation time lets the orchestrator carry that correlation, so webhook receivers and admin users can see the origin ticket directly.

2. **Dependency batching.** The standard workflow to express "B depends on A" is: POST A → wait for 201 response → capture A's UUID → POST B with `dependsOn=[uuid]`. Every round-trip is a ~100 ms RTT. Queuing 50 dependent items takes 50 serialised RTTs.

   With external IDs, the caller generates identifiers locally (sequential numbers, ULIDs, anything) and POSTs all 50 in parallel. The `dependsOn` array accepts external IDs alongside UUIDs; the orchestrator resolves the cross-references at create time — no round-trips needed.

## Format and validation

| Rule | Detail |
|------|--------|
| **Length** | 1–256 characters |
| **Characters** | ASCII printable, no whitespace, no `/`, no `?`, no `;` `<` `=` `>` |
| **Reserved prefix** | Must not start with `wi-` |
| **UUID collision** | Must not be parseable as a UUID (would be ambiguous with internal IDs) |

The safe shape is: lowercase letters, digits, and the separators `_-:.` — for example, `JIRA-1234`, `gh-456`, `sprint-7:ticket-99`, `internal_tracker_id`.

Violating any rule returns `400 Bad Request`.

## Uniqueness

`externalId` is **unique per project**, not globally. Two different projects may legitimately use the same scheme (`JIRA-1234` in project `my-app` and `JIRA-1234` in project `other-app` are independent).

Null is allowed for items without an external ID; multiple null items in the same project coexist freely.

Attempting to create a second work item in the same project with a duplicate `externalId` returns:

```json
{
  "error": "externalId 'JIRA-1234' already exists in project 'my-app' for work item <uuid> (state: Queued)"
}
```

## Usage

### Setting an external ID at creation

```json
POST /workitems
{
  "projectId": "my-app",
  "externalId": "JIRA-1234",
  "title": "Add dark-mode support",
  "prompt": "...",
  "dependsOn": []
}
```

The `externalId` is optional. Omit it (or pass `null`) for items that don't need one.

### Referencing by external ID in GET/DELETE/PATCH

All endpoints that accept `{id}` now also accept a composite `<projectId>:<externalId>` path segment:

```
GET  /workitems/my-app:JIRA-1234
DELETE /workitems/my-app:JIRA-1234
PATCH  /workitems/my-app:JIRA-1234
```

The project part is required to avoid ambiguity between projects. If either part is empty the request is rejected with `400 Bad Request`. If the externalId isn't found in that project the request returns `404 Not Found`.

### Dependency batching without round-trips

```json
POST /workitems
{
  "projectId": "my-app",
  "externalId": "BATCH-1",
  "title": "Step 1",
  "prompt": "..."
}

POST /workitems
{
  "projectId": "my-app",
  "externalId": "BATCH-2",
  "title": "Step 2",
  "prompt": "...",
  "dependsOn": ["BATCH-1"]       ← externalId, resolved at create time
}

POST /workitems
{
  "projectId": "my-app",
  "externalId": "BATCH-3",
  "title": "Step 3",
  "prompt": "...",
  "dependsOn": ["BATCH-2"]       ← also an externalId
}
```

All three can be POSTed in parallel (in any order that keeps the dep chain intact — or sequentially but without waiting for UUID responses). The `dependsOn` array also accepts a **mix** of external IDs and UUIDs:

```json
"dependsOn": ["BATCH-1", "abcd-1234-..."]
```

#### Resolution semantics

- Resolution happens **once, at create time**. The stored `dependsOn` contains only internal UUIDs.
- Deleting and re-creating a dependency under the same `externalId` does **not** re-link existing dependents — they remain pointed at the old UUID. Operators who care should re-create dependent items too.
- If any `dependsOn` entry (UUID or externalId) cannot be resolved, the request returns `400 Bad Request` with the unresolved ID enumerated.

## Webhook payloads

Every webhook event that carries a work item payload gains `externalId` alongside the existing `id`:

```json
{
  "event": "work_item.done",
  "workItem": {
    "id": "abcd-1234-...",
    "externalId": "JIRA-1234",
    ...
  }
}
```

When the work item has no external ID, `externalId` is `null` (or omitted if the receiver uses `JsonIgnoreCondition.WhenWritingNull`).

## Integration patterns

### JIRA sync

```python
# Create a CodeyBox work item linked to a JIRA ticket
resp = codeybox.create_work_item(
    project_id="my-app",
    external_id=jira_ticket.key,  # e.g. "JIRA-4567"
    title=jira_ticket.summary,
    prompt=build_prompt(jira_ticket),
)
# No need to store resp["id"] — look up later via:
# GET /workitems/my-app:JIRA-4567
```

### GitHub Issues automation

```python
external_id = f"GH-{issue.number}"

# Check if a work item already exists
r = requests.get(f"/workitems/my-app:{external_id}")
if r.status_code == 404:
    codeybox.create_work_item(project_id="my-app", external_id=external_id, ...)
```

### Webhook receiver correlation

```python
@app.route("/webhook", methods=["POST"])
def on_webhook():
    payload = request.json
    if payload["event"] == "work_item.done":
        external_id = payload["workItem"]["externalId"]
        if external_id and external_id.startswith("JIRA-"):
            jira.transition(external_id, status="Done")
```
