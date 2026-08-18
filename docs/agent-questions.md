# Agent questions

CodeyBox supports an optional mechanism that lets the agent surface ambiguity to
a human operator mid-work, wait for an answer, and then resume with that context.
This is a project-level opt-in feature; by default it is disabled.

---

## Overview

When enabled, an agent may emit one or more `<codeybox-question>` blocks in its
stdout during the work phase. After the agent exits CodeyBox parses those blocks,
persists them as questions, parks the work item in the `NeedsOperatorInput` state,
and fires a webhook so operators know input is required.

The item stays parked indefinitely (no timeout) until every question is either
**answered** or **dismissed** via the REST API. Once all questions are resolved
the item automatically transitions to `WorkComplete` and re-enters the normal
pipeline. On the next run the agent receives the answered questions injected into
its prompt so it can apply them.

---

## Enabling agent questions

Set `AllowAgentQuestions: true` on the project in `appsettings.json`:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "my-app",
        "RepositoryUrl": "...",
        "AllowAgentQuestions": true
      }
    ]
  }
}
```

The default is `false`. When disabled, any `<codeybox-question>` blocks in agent
stdout are silently ignored and the pipeline proceeds normally.

---

## Agent contract

Agents MUST follow these rules when using the question protocol:

1. **Always continue with a sensible default.** After emitting a question block
   the agent must not wait or block — it should proceed immediately using its best
   guess. This keeps the sandbox from hanging and produces a usable diff even
   before the operator replies.

2. **Questions are advisory, not blocking.** The agent emits questions and keeps
   working. CodeyBox decides after the agent exits whether any questions need
   human attention.

3. **Use stable, descriptive IDs.** The `id` attribute is the deduplication key
   for a question within a work item. Reusing the same `id` across runs is fine
   and idempotent (the first text wins). IDs must be 1–64 characters, using only
   alphanumerics, hyphens, and underscores.

### Emitting a question

```
<codeybox-question id="q-migration-strategy">
Should I use forward-only migrations or allow rollbacks?
Default: forward-only (matches the rest of the codebase).
</codeybox-question>
```

The question text is trimmed, redacted of secrets, and truncated to 4 000 chars.
A maximum of **10 questions** per work item is enforced; questions beyond the cap
are silently dropped.

The system prompt injected by CodeyBox describes this protocol in detail when
`AllowAgentQuestions=true`. Agents do not need to discover or negotiate the
protocol themselves.

---

## Operator workflow

### Viewing open questions

```
GET /workitems/{id}/questions
```

Returns all questions for the work item with their current state (`open`,
`answered`, or `dismissed`). See [`api.md`](api.md) for the full response shape.

### Answering a question

```
POST /workitems/{id}/answer
{ "questionId": "q-migration-strategy", "answer": "Use rollbacks — our CI pipeline supports them." }
```

The answer is stored and, if it is the last open question, the item is
re-enqueued automatically.

Answered questions are injected into the agent's rework prompt:

```
## Operator answers to your questions

You asked the following question(s) and the operator has responded:

- **q-migration-strategy**
  Question:
  ```
  Should I use forward-only migrations or allow rollbacks?
  Default: forward-only (matches the rest of the codebase).
  ```
  Answer:
  ```
  Use rollbacks — our CI pipeline supports them.
  ```

Apply these answers to your work.
```

### Dismissing a question

```
POST /workitems/{id}/dismiss-question
{ "questionId": "q-migration-strategy", "reason": "Out of scope for this PR." }
```

Dismissed questions are **not shown** to the agent. From the agent's perspective
the question was never asked and it continues with its original default.

Both operations are **idempotent**: submitting the same answer or dismiss twice
returns `{ "status": "no-op" }` and leaves the stored value unchanged.

---

## Webhook events

| Event | Fired when |
|---|---|
| `work_item.question_asked` | A new question is parsed and persisted (one event per question) |
| `work_item.question_answered` | An operator answers a question |
| `work_item.question_dismissed` | An operator dismisses a question |

See [`webhooks.md`](webhooks.md) for payload shapes.

Subscribe to `work_item.question_asked` to alert your team when a work item needs
human input. Pair it with `work_item.work_complete` to detect automatic resumption
once all questions are resolved.

---

## State transitions

```
Queued → Working
           │
           ▼ (agent emits question, AllowAgentQuestions=true)
    NeedsOperatorInput
           │
           ▼ (all questions answered or dismissed)
      WorkComplete → Auditing → ... → Done
```

A parked item does not count toward the concurrency limit (it is excluded from
`CountInFlightAsync`). It also does not restart on service restart — the
orchestrator's replay pass deliberately skips `NeedsOperatorInput` items.

---

## Security and privacy

- Question text passes through `RawOutputRedactor` before storage. GitHub PATs,
  Anthropic API keys, and Google API keys are replaced with `***`.
- Question text is capped at 4 000 characters per question.
- There is a hard cap of 10 questions per work item regardless of how many
  `<codeybox-question>` blocks the agent emits.
- The `answeredBy` field is present in the `work_item.question_answered` webhook
  payload and in `GET /workitems/{id}/questions` responses, but is currently always
  `null` — the API-key authentication layer does not yet provide caller identity.
