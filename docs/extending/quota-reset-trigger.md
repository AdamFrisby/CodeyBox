# Quota Reset-Credit Consume Trigger

The consume trigger (`ResetCreditConsumeTrigger` in `CodeyBox.Core`) is the
action half of the Quota Reset Advisor (5/5). The advisor (3/5) and notifier
(4/5) are report-only by design — this trigger is the only component that may
call `POST {base}/wham/rate-limit-reset-credits/consume`, and each call costs
approximately **80 USD, irreversibly**.

Because a defect here spends real money, the trigger is fail-closed at every
step. When a design choice trades safety against capability, it chooses
safety: prefer refusing to act over acting on incomplete information.

## Gate chain

A trigger attempt proceeds only when **all** of these hold, in order:

1. The advisor verdict is `shouldSpend=true`. Any hold (or missing advice) refuses.
2. `Enabled` is true. Default **false** — off means no request under any circumstance.
3. The kill-switch is disengaged. Re-read on **every** attempt, so engaging it
   blocks the next attempt with no restart.
4. The decision was not already consumed (idempotent replay — no new request).
5. `available_count` is readable, fresh (within `MaxBalanceAgeSeconds`), and positive.
   An unknown, stale, or zero balance refuses.
6. The per-period cap (default: 1 credit / $80 USD per 30 days) is not yet reached,
   checked against persisted history — a restart cannot reset the budget.
7. `AllowLiveSpend` is true. Default **false**: enabling the feature alone only
   reaches dry-run, which logs the full intended request (including the redeem
   key and which credit would be consumed) and issues nothing. Live spend needs
   this second, separate decision.

## Idempotency

`redeem_request_id` is derived deterministically (SHA-256) from the authorising
decision — agent, optimal window, deadline, credit spend-by, and reason — and
persisted **before** the request is issued. Every retry for that decision reuses
the same key, so a transport failure, timeout, or crash between dispatch and
response can never consume twice. An ambiguous failure is reconciled against
the balance read endpoints (a decremented or unreadable balance is treated as
consumed) rather than retried blind. The consume path is serialised, so
concurrent attempts for one decision issue a single request.

## Configuration reference

All keys are hot-reloadable — changes take effect on the next attempt without
a host restart. This is load-bearing for the kill-switch, which must never be
captured at startup.

| Key | Type | Default | Description |
|---|---|---|---|
| `Enabled` | bool | `false` | Master switch. Off means no request under any circumstance. |
| `AllowLiveSpend` | bool | `false` | Second decision required for a live call. False = dry-run (log, don't issue). |
| `KillSwitch` | bool | `false` | While engaged, no request is issued regardless of any other setting. |
| `MaxCreditsPerPeriod` | number | `1` | Hard cap per period, enforced against persisted history. |
| `PeriodDays` | number | `30` | Rolling window the cap is enforced over. |
| `MaxBalanceAgeSeconds` | number | `900` | A balance older than this is stale and refuses. |

The cap is always expressed in both credits and money (`1 credit ($80 USD)
per 30d`) in configuration surfaces, logs, and audit records.

## Audit

Every attempt — spend or refusal — appends an audit record carrying what the
advisor reported, which gate allowed or refused, the idempotency key, the
outcome, and the running period-to-date spend, so a consumed credit is
attributable afterwards.

## Testing rule

No automated test may reach the live endpoint. The trigger holds no HTTP
client of its own: the consume call sits behind the injected
`IResetCreditConsumeTransport`, and tests exercise the decision logic against
a fake. A test suite that can spend credit by being run is unacceptable.
