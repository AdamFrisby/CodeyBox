# Plugins Manual UAT

Use a non-production CodeyBox deployment. Preserve plugin DLL checksums, effective configuration, `/plugins` payloads, audit report payloads, work item IDs, relevant log excerpts, and screenshots with the UAT run record.

## External Plugin Installation And Discovery

1. Build or obtain a third-party plugin DLL that references only `CodeyBox.PluginSdk` and `CodeyBox.Core`.
2. Configure `CodeyBox:Plugins:AssemblyPaths` or `CodeyBox:Plugins:PackageDirectories` to point at the DLL.
3. Add the plugin ID to `CodeyBox:Plugins:Allowlist`.
4. Restart the API and inspect startup logs for `plugin.loaded` and initialization messages.
5. Call `GET /plugins` and confirm auditor plugins appear with the expected ID and display name.

Pass criteria: only allowlisted compatible plugins load, incompatible plugins are rejected with a compatibility message, and the host remains usable after startup.

## Third-Party Auditor Plugin Flow

1. Install an auditor plugin that emits a deterministic finding for a known fixture repository.
2. Configure a project `Audit.Custom` entry with `{ "Kind": "plugin", "PluginId": "<installed-id>" }`.
3. Queue a work item against the fixture repository.
4. Confirm the plugin auditor appears in audit logs and produces a normal audit report row.
5. Remove or misspell the plugin ID and restart, then confirm startup or audit setup reports the missing plugin clearly without running that plugin.

Pass criteria: plugin findings are indistinguishable from built-in auditor findings in pipeline state, report persistence, and dashboard display.

## Real Forge Upstream Plugin Flow

1. Install an upstream remote plugin for a staging forge such as Gitea or Forgejo.
2. Configure a project with `Upstream.Kind` matching the plugin remote `Name`.
3. Put non-secret forge settings in `Upstream.PluginConfig` and the token in the env var named by `Upstream.TokenEnvVar`.
4. Queue a work item with `PushUpstream=true`.
5. Confirm the work branch is pushed, the PR or merge request is created, and the work item reaches `Done`.
6. Repeat with an unknown `Upstream.Kind` and confirm the error lists built-ins and installed plugin kinds.

Pass criteria: project config is passed to the plugin, tokens do not appear in config or logs, and built-in upstream kinds cannot be shadowed by plugin names.

## Real Secret-Manager Credential Plugin Flow

1. Install a credential provider plugin backed by the operator-approved secret manager.
2. Configure plugin-scoped settings under `CodeyBox:Plugins:<plugin-id>`.
3. Configure at least two projects: one with `CredentialProviderPriority` listing the plugin and one with an empty priority list.
4. Queue work for agents covered by the plugin and agents not covered by the plugin.
5. Rotate or expire a time-bound credential in the secret manager and queue another work item.
6. Simulate a backend outage and confirm the surfaced failure matches the documented operational policy.

Pass criteria: project priority controls plugin selection and order, unsupported agents fall through deterministically, short-lived credentials refresh after expiry, and no raw secret values appear in logs or audit reports.
