# Agent Runners And Credentials Manual UAT

Use this checklist for the scenarios that require real vendor CLIs, real OAuth/API credentials, or a VM sandbox. Do not commit real credentials or captured auth files.

## Prerequisites

- A disposable repository configured as a CodeyBox project.
- A sandbox provider matching the deployment under test, preferably Multipass for VM credential-transfer validation.
- Installed CLIs in the sandbox image: `claude`, `codex`, `gemini`, and, when enabled, `copilot`.
- Test credentials for each selected vendor with permission to run a trivial prompt.
- Security logging enabled so agent stdout/stderr and Serilog output can be inspected for redaction.

## CLI Runner Base And Preemption

1. Start a long-running work item with a CLI that writes resumable state under its documented scratchpad directory.
2. Send orchestrator shutdown or work-item cancellation while the agent is running.
3. Verify the matching process receives TERM, `/run/codeybox/agent-turn/scratchpad.tgz` is created, and its `manifest.tsv` lists only allowlisted scratchpad paths.
4. Restart the orchestrator and resume the work item.
5. Verify the resumed run can see the restored scratchpad and unrelated credentials such as SSH config, OAuth auth files outside the allowlist, and `.git` data are absent from the archive.

## Claude

1. Configure Claude OAuth credentials and run a work item with `captureStructuredStream` enabled.
2. Verify the sandbox invocation uses `claude --print --dangerously-skip-permissions`, includes the configured `--model`, and uses `--output-format stream-json --verbose` when supported.
3. Verify the sandbox contains `~/.claude/.credentials.json` with mode `0600`.
4. Repeat with an API-key credential and verify the OAuth file materialization step is skipped.
5. Run a text-only PR/changelog generation path and verify success. Repeat with an invalid credential and verify the HTTP failure body is surfaced without leaking tokens.

## Codex

1. Configure subscription auth through `CODEX_AUTH_JSON` or a mounted `~/.codex/auth.json`.
2. Run a work item with a model and reasoning mode.
3. Verify the invocation uses `codex exec --dangerously-bypass-approvals-and-sandbox`, includes the chosen `--model`, and passes reasoning through `-c model_reasoning_effort=<value>`.
4. Verify `--json-stream` is used when advertised, `--json` is used when only that flag is advertised, and structured capture is disabled with a warning when neither flag is available.
5. Verify an existing mounted `~/.codex/auth.json` is not overwritten during sandbox preparation.

## Gemini

1. Configure Gemini OAuth credentials and run a work item with a Gemini 3 high-reasoning model ID.
2. Verify the invocation uses `gemini --yolo --skip-trust -p <prompt>`, includes `--model <configured-model>`, and does not add unsupported reasoning flags.
3. Verify `~/.gemini/oauth_creds.json` and `~/.gemini/settings.json` are materialized in the sandbox.
4. Verify ANSI progress output is stripped from non-structured stdout/stderr logs.
5. Repeat with API-key auth and verify OAuth file materialization is skipped.

## Copilot

1. Configure a least-privilege GitHub token for Copilot CLI auth.
2. Run a work item selecting Copilot directly.
3. Verify the invocation shape is `copilot -p <prompt>`.
4. Supply model and reasoning overrides and verify they are ignored without failing the work item.
5. Verify missing or under-scoped token failures surface as normal non-zero agent failures.

## Credential Chain And Smoke Gate

1. Configure at least two credential plugins plus the environment fallback provider.
2. Set `CredentialProviderPriority` on one project and verify only the listed plugins are tried, in order, between built-in-first providers and built-in-last fallback providers.
3. Use a plugin that returns no credential for one agent and verify fallback credentials are used.
4. Use a time-bound credential and verify it is reused before expiry and fetched again after expiry.
5. Start the orchestrator with smoke probes enabled and verify configured agents emit startup smoke results.
6. Queue a work item with known-bad credentials and verify pickup is blocked before sandbox creation with an `agent.smoke_failed` webhook.
7. Set `SkipCredentialSmokeTest` for a project and verify pickup proceeds without calling the smoke probe.
