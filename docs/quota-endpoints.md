# Quota Endpoint Capture

CodeyBox probes subscription quotas without logging OAuth tokens or raw response
bodies at runtime. This page records redacted response structures used by the
defensive parsers and tests.

## Claude

Probe:

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <redacted>
```

Captured redacted response structure, mirrored in
`tests/CodeyBox.Tests/Fixtures/Quota/claude-oauth-usage.redacted.json`:

```json
{
  "plan_type": "max",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": {
      "used_percent": 20,
      "limit_window_seconds": 18000,
      "reset_after_seconds": 3600,
      "reset_at": 1778091218
    },
    "secondary_window": {
      "used_percent": 10,
      "limit_window_seconds": 604800,
      "reset_after_seconds": 500000,
      "reset_at": 1778605571
    }
  },
  "additional_rate_limits": [
    {
      "limit_name": "claude-sonnet-4-6",
      "metered_feature": "claude_sonnet",
      "rate_limit": {
        "primary_window": { "used_percent": 30, "limit_window_seconds": 18000, "reset_at": 1778091218 },
        "secondary_window": { "used_percent": 40, "limit_window_seconds": 604800, "reset_at": 1778605571 }
      }
    },
    {
      "limit_name": "claude-opus-4-7",
      "metered_feature": "claude_opus",
      "rate_limit": {
        "primary_window": { "used_percent": 100, "limit_window_seconds": 18000, "reset_at": 1778091218 },
        "secondary_window": { "used_percent": 95, "limit_window_seconds": 604800, "reset_at": 1778605571 }
      }
    }
  ]
}
```

`primary_window` is treated as the 5-hour rolling window and
`secondary_window` as the weekly window. The parser uses the most constrained
available percentage across windows.

## Codex

The installed Codex CLI binary references `/backend-api/wham/usage` for account
rate limits. The dashboard billing endpoints are API-key billing and are not
used for ChatGPT subscription quota.

Probe:

```http
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <redacted>
ChatGPT-Account-Id: <redacted>
```

Captured redacted response structure, mirrored in
`tests/CodeyBox.Tests/Fixtures/Quota/codex-wham-usage.redacted.json`:

```json
{
  "user_id": "user_REDACTED",
  "account_id": "acct_REDACTED",
  "email": "redacted@example.com",
  "plan_type": "prolite",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": {
      "used_percent": 34,
      "limit_window_seconds": 18000,
      "reset_after_seconds": 5865,
      "reset_at": 1778091218
    },
    "secondary_window": {
      "used_percent": 37,
      "limit_window_seconds": 604800,
      "reset_after_seconds": 520217,
      "reset_at": 1778605571
    }
  },
  "additional_rate_limits": [
    {
      "limit_name": "GPT-5.3-Codex-Spark",
      "metered_feature": "codex_bengalfox",
      "rate_limit": {
        "allowed": true,
        "limit_reached": false,
        "primary_window": {
          "used_percent": 0,
          "limit_window_seconds": 18000,
          "reset_after_seconds": 18000,
          "reset_at": 1778103354
        },
        "secondary_window": {
          "used_percent": 0,
          "limit_window_seconds": 604800,
          "reset_after_seconds": 519837,
          "reset_at": 1778605191
        }
      }
    }
  ],
  "credits": {
    "has_credits": false,
    "unlimited": false,
    "overage_limit_reached": false,
    "balance": "0"
  },
  "rate_limit_reached_type": null
}
```
