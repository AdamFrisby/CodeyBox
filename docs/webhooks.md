# Webhook Events

CodeyBox can notify external systems of pipeline lifecycle events via webhooks.
Register a custom `IWebhookDispatcher` implementation in `Program.cs` to deliver
events to your endpoints. The default registration is a no-op.

## Event table

| Event name | Dispatched by | When fired | Payload fields |
|---|---|---|---|
| `work_item.upstream_pushing` | Orchestrator (github, git-generic) | Phase 4 begins (before push/PR) — only when `PushUpstream=true` and `Kind != noop` | `workItemId`, `projectId` |
| `work_item.pull_request_opened` | `GitHubUpstreamRemote` only | After a GitHub PR is successfully opened | `workItemId`, `projectId`, `workBranch`, `baseBranch`, `pullRequestNumber`, `pullRequestUrl` |
| `work_item.done` | Orchestrator (all upstream kinds) | Work item transitions to the Done state | `workItemId`, `projectId` |

> **Note:** `work_item.pull_request_opened` is only fired by `GitHubUpstreamRemote`.
> Projects using `Upstream.Kind=git-generic` do not emit this event but still receive
> `work_item.upstream_pushing` and `work_item.done`.
>
> `work_item.upstream_pushing` is **not** dispatched in two cases:
> 1. `Upstream.Kind=noop` — the upstream push phase is skipped entirely.
> 2. `PushUpstream=false` — regardless of `Kind` (including `github` and `git-generic`).
>
> In both cases, `work_item.done` is still dispatched when the work item completes.

## Implementing IWebhookDispatcher

```csharp
public sealed class MyWebhookDispatcher : IWebhookDispatcher
{
    public async Task DispatchAsync(string eventName, object payload, CancellationToken ct = default)
    {
        // serialize and POST payload to your endpoint
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<IWebhookDispatcher, MyWebhookDispatcher>();
```
