# CodeyBox.OpenStackSandboxPlugin

OpenStack-backed sandbox provider (first target: Infomaniak Public Cloud, a
standard OpenStack deployment). Contributes the `openstack` provider kind
(`ISandboxProvider`) through the plugin trust model — off unless an operator
allowlists `codeybox.openstack-sandbox` AND sets
`CodeyBox:Plugins:codeybox.openstack-sandbox:Enabled=true`.

The guest runs on infrastructure CodeyBox does not control, so this kind is
always classified `NotEnforced` by the host (same rule as
`docs/extending/daytona-sandbox-plugin.md`): acquisitions requiring enforced
network egress (a named network profile) are refused at placement, and any
service-side network controls the provider applies later are best-effort
defence in depth only.

## Current scope

This plugin currently ships the typed REST client only
(`OpenStackApiClient`): Keystone v3 application-credential auth with a
skewed token cache, service-catalog endpoint resolution, and Nova / Neutron /
Glance operations. No provider logic yet — that arrives as a follow-up item.

## Configuration

All values are hot-reloadable options under
`CodeyBox:Plugins:codeybox.openstack-sandbox` (sample values target
Infomaniak Public Cloud — nothing else in the plugin is cloud-specific):

```json
{
  "CodeyBox": {
    "Plugins": {
      "codeybox.openstack-sandbox": {
        "Enabled": false,
        "AuthUrl": "https://api.pub1.infomaniak.cloud:5000/v3",
        "Region": "dc4-a",
        "Interface": "public"
      }
    }
  }
}
```

## Credentials

The application-credential id and secret come **only** from the host
credential chain (environment), using the standard OpenStack names:

- `OS_AUTH_URL` (fallback when `AuthUrl` is empty)
- `OS_APPLICATION_CREDENTIAL_ID`
- `OS_APPLICATION_CREDENTIAL_SECRET` — never logged, never persisted
- `OS_REGION_NAME` (fallback when `Region` is empty)
- `OS_INTERFACE` (optional; `public` when unset)

Every endpoint — the auth URL and each catalog URL — must be `https`. A
cleartext `http` URL is refused unless the dev-only `AllowUnsafeHttp` option
is set, and even then only for loopback hosts: remote `http` URLs are refused
unconditionally, so the secret can never ride a cleartext request to a remote
host because of one operator edit.

## Client notes

- 401 re-authenticates once and retries; a second 401 surfaces as
  `Unauthorized`. 409/413/429 map to typed transient/quota errors with
  `Retry-After` honoured (header first, `overLimit` body second).
- All response bodies are size-bounded while streaming; list operations page
  with item/page caps and fail loudly on truncation instead of reporting a
  partial inventory.
- No hand-rolled crypto: the platform HTTP stack (TLS verification on) and,
  later, the OpenSSH CLI for keys/transport. No new third-party dependencies.
