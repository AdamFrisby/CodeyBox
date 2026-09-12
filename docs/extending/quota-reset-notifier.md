# Quota Reset Notifier Plugin

The quota-reset notifier (`codeybox.quota-reset-notifier`) is the delivery
half of the Quota Reset Advisor. The statistics plugin's reset-optimality
evaluator (3/5) answers *"should I spend a banked reset credit now?"* but is
report-only by design — this plugin watches that verdict on a schedule and,
when the advice flips to `shouldSpend=true`, pings the operator with an
HMAC-signed `quota.reset_optimal` webhook event: *"it is a good time to spend
a reset"*.

## How it works

```
MetricSamplerHost ──► IMetricSampler "quota-reset-notifier" ◄── QuotaResetNotifierPlugin
                              │
                              ├─► IResetOptimalityAdvisor.AdviseAsync(agent)   (statistics plugin)
                              ├─► IResetCreditExpiryEstimator.EstimateAsync()  (credit count, best-effort)
                              ▼
                        IWebhookDispatcher.PublishAsync(quota.reset_optimal)
                              │
                              └─► operator's HTTPS endpoint (HMAC-signed per webhook config)
```

Each tick the plugin evaluates advice for every watched agent. A spend
verdict produces exactly one ping per optimal window per agent: a verdict for
an already-pinged window is never re-sent, and verdicts inside `Cooldown` of
the last ping are suppressed so a flapping deadline cannot page the operator
every tick. A verdict for a *new* window (a new decision deadline) re-pings
once the cooldown has elapsed. The exact rule lives in
`ResetOptimalNotifyPolicy` in `CodeyBox.Core` and is covered by unit tests.

The plugin degrades gracefully: with the statistics plugin absent there is no
advisor and each tick is a no-op; with the credit estimator absent the
payload's `bankedCredits` is null rather than failing the notification.

## Enabling the plugin

Like every other plugin it must be allowlisted before the loader registers
it. In `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "Allowlist": ["codeybox.statistics", "codeybox.quota-reset-notifier"]
    }
  }
}
```

After enabling, restart the orchestrator. The notifier fires on its first
interval after startup. To receive the ping, configure a webhook endpoint
whose event filter includes `quota.reset_optimal` (see
[`docs/reference/webhooks.md`](../reference/webhooks.md)) — delivery signing
(HMAC-SHA256 via `SecretEnvVar`) is handled by the existing webhook
infrastructure, not by this plugin.

## Configuration reference

Bind from `CodeyBox:Plugins:codeybox.quota-reset-notifier` in
`appsettings.json`. All keys are hot-reloadable — changes take effect on the
next tick without a host restart.

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.quota-reset-notifier": {
        "Enabled": true,
        "IntervalSeconds": 900,
        "Agents": ["codex"],
        "CooldownSeconds": 86400
      }
    }
  }
}
```

| Key | Type | Default | Description |
|---|---|---|---|
| `Enabled` | bool | `true` | Master switch. When false the loop keeps running but never evaluates or publishes; flipping it back on resumes on the next tick. |
| `IntervalSeconds` | number | `900` | How often advice is re-evaluated per watched agent. Clamped to a minimum of 10 s. |
| `Agents` | string[] | `["codex"]` | Agents to watch. Add `claude` once its 4.8 upgrade is proven. Empty = notify for none. |
| `CooldownSeconds` | number | `86400` | Minimum interval between pings for the same agent. Same-window repeats are suppressed regardless of this value. |
