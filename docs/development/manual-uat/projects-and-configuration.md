# Projects And Configuration Manual UAT

These procedures cover the manual/spec-only checks from `docs/uat/00-plan.md`
for the Projects And Configuration section. Run them against a disposable
CodeyBox deployment and preserve the effective config file, startup logs, API
responses, and relevant screenshots with the UAT run record.

## Real Operator Project Config Review And Startup

1. Prepare an operator config file with at least two projects, distinct IDs,
   repository URLs, default branches, default agents, upstream settings, audit
   profiles, budget caps, network profiles, and release settings.
2. Put upstream tokens only in environment variables named by each project
   `Upstream.TokenEnvVar`; do not place token values in the config file.
3. Start the API with the config file layered through the normal operator
   mechanism, such as `CODEYBOX_EXTRA_CONFIG`.
4. Inspect startup logs for project/config validation failures and confirm the
   process reaches a ready state.
5. Call `GET /projects` and `GET /projects/{id}` with an API token and compare
   the returned operator-readable fields with the source config.

Pass criteria: valid projects load, invalid IDs or duplicate IDs fail startup,
missing token values are not present in config/logs, and the project endpoints
show the resolved display name, repository URL, branch, agent, upstream kind,
audit languages/types, and audit iteration count.

## Production-Like Startup Validation

1. Start a non-Development host with `CodeyBox:SandboxProvider` unset.
2. Repeat with `CodeyBox:SandboxProvider=process` and
   `CodeyBox:DangerouslyAllowProcessSandbox=false`.
3. Repeat with OTel enabled and no `CodeyBox:Otel:OtlpEndpoint`.
4. Repeat with changelog automation enabled and no
   `CodeyBox:Changelog:GitHubWebhookSecretEnvVar`.
5. Repeat with all required settings present and an API key of at least 32
   characters in `CODEYBOX_API_KEY`.

Pass criteria: unsafe or incomplete configurations fail before accepting
requests, while the complete production-like configuration starts and keeps
auth enabled.

## Authenticated API And Health Probe Smoke

1. Start the API with auth enabled and a known `CODEYBOX_API_KEY`.
2. Call `GET /healthz` without an `Authorization` header.
3. Call `GET /healthz` with an invalid bearer token.
4. Call a protected endpoint such as `GET /projects` without a token, with an
   invalid token, and with the configured token.
5. Configure a deployment monitor or load balancer liveness probe to call
   `GET /healthz` anonymously.

Pass criteria: `/healthz` consistently returns `200 OK` with `{"status":"ok"}`
without requiring auth, invalid tokens do not break the health probe, and
protected endpoints reject missing or invalid bearer tokens.
