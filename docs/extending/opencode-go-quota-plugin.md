# opencode-go Quota Plugin

Out-of-tree quota meter for the opencode-go plan. Ships as the
`codeybox.opencode-go-quota` plugin so provider-specific knowledge (endpoint
shape, used-vs-remaining direction, window names) never lands in the
provider-neutral orchestrator.

## What it meters

Two members ride the same plan and are served by one reader:

- the **opencode** agent's opencode-go models (an empty model id means the
  agent default, which rides the same subscription), and
- a **copilot** member whose configured BYOK provider base URL
  (`CodeyBox:Copilot:Provider:BaseUrl`) is the zen go endpoint. A copilot
  member pointed at any other BYOK provider is never claimed — `/usage`
  exists only on this plan, so those members keep resolving to the per-kind
  probe path.

## Endpoint

```http
GET {ProviderBaseUrl}/usage
Authorization: Bearer <opencode-go key>
```

No custom `User-Agent` is required on this path. `percent` is the fraction
**used**; availability is `100 - percent` (inverted relative to meters that
report a remaining fraction). All three windows (`rolling`, `weekly`,
`monthly`) are parsed; the snapshot reports the minimum across windows with
the binding window's reset. A window whose `status` is not `"ok"` — or any
missing/unparseable window — maps the whole reading to Unknown (Permanent)
rather than inventing availability.

## Configuration

Under `CodeyBox:Plugins:codeybox.opencode-go-quota:` in `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.opencode-go-quota"],
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "codeybox.opencode-go-quota": {
        "ProviderBaseUrl": "https://opencode.ai/zen/go/v1",
        "CacheTtlSeconds": 60,
        "TimeoutSeconds": 10,
        "SustainedFailureWarningThreshold": 3
      }
    }
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `ProviderBaseUrl` | `https://opencode.ai/zen/go/v1` | `/usage` is appended; the host is never hardcoded. |
| `CopilotProviderBaseUrl` | _(unset)_ | Override for the copilot `Handles` gate. Unset = read the live `CodeyBox:Copilot:Provider:BaseUrl`. |
| `ApiKey` | _(unset)_ | Bearer token. Falls back to `CODEYBOX_OPENCODE_GO_API_KEY`. Never logged. |
| `CacheTtlSeconds` | `60` | Response-cache TTL, re-read per call (hot-reloadable). |
| `TimeoutSeconds` | `10` | Per-request timeout, re-read per call (hot-reloadable). |
| `SustainedFailureWarningThreshold` | `3` | Consecutive failures before the probe logs at Warning (earlier ones stay at Debug). |

Failure classification matches the in-tree probes: transport errors, 5xx,
408 and 429 are Transient (last-known-good may stand in); 401/403 and
unparseable bodies are Permanent; a missing key is NoCredential.
