# Auditing And Reports Manual UAT

Source plan: `docs/uat/00-plan.md#Auditing-And-Reports`

Use a non-production CodeyBox deployment with authentication enabled unless a step explicitly says otherwise. Preserve the work item IDs, audit report API payloads, dashboard screenshots, and relevant log excerpts with the UAT run record.

## Operator Config Review For Presets

1. Configure a real project with an explicit audit profile, at least one language preset, one audit type preset, and one project override.
2. Start the API and confirm startup succeeds without preset validation errors.
3. Queue a small work item and confirm the selected auditor list in logs matches the intended project profile.
4. Confirm bundled defaults are still available for a second project that does not use the override.

Pass criteria: the configured project runs only its intended language and audit-type auditors, and the second project still uses the bundled defaults.

## Real Tool Execution

1. Run a representative repository through the `uat` audit profile in the same sandbox provider used by operators.
2. Confirm `csharp:format-check`, `csharp:build-WaE`, `csharp:test-pass`, `security:gitleaks`, and `security:semgrep` execute with the documented arguments.
3. Introduce a reversible formatting or build failure and verify the finding blocks the work item.
4. Remove the failure and verify the next iteration passes.

Pass criteria: tool failures produce audit findings with raw output available from the audit report endpoint, and passing tools preserve a successful report row.

## LLM Prompt And Deep Audit Review

1. Run a small work item with `security:llm-review` enabled and an audit agent different from the work agent.
2. Inspect the captured prompt and confirm it includes the original task, diff context, configured review focus, and project-specific prompt frame.
3. Create a test release branch and run the configured deep auditors against it.
4. Review the generated remediation work items for clear scope and actionable findings.

Pass criteria: the LLM auditor uses the configured audit agent when credentials exist, prompt framing is understandable, and deep-audit remediation items are actionable.

## Startup Logs For Mixed Agents

1. Configure one project whose audit agent has credentials and one project whose audit agent does not.
2. Start the host and inspect startup logs.
3. Confirm the missing-credential project logs one warning per distinct audit agent and names the fallback work agent.
4. Confirm the valid project emits no warning.

Pass criteria: startup never blocks, warnings are actionable, and duplicate per-auditor overrides do not create noisy repeated warnings.

## Mixed Tool And LLM Audit Suite

1. Queue a work item that runs deterministic tool auditors and at least one LLM auditor in the same audit iteration.
2. Confirm compatible auditors run concurrently while credential-requiring auditors receive only the configured audit credential mounts.
3. Verify `StopOnFirstFailure` behavior in a separate run by making the first registered blocking auditor fail.
4. Confirm every auditor invocation appears as a separate report row with its own raw output availability.

Pass criteria: parallel execution, credential isolation, failure ordering, and report persistence match the project configuration.

## Large Finding Set UI Review

1. Seed or generate an audit report with multiple iterations, multiple auditors, and at least 50 findings.
2. Open the admin audit report page for the work item.
3. Verify the iteration matrix remains readable, stable finding IDs correlate repeated findings, and null or deleted-file locations render without UI breakage.
4. Open raw output for at least one auditor and confirm large output is bounded and readable.

Pass criteria: the page remains responsive and visually coherent, and large or locationless findings do not hide other findings.

## Long-Running Retention Check

1. Configure `CodeyBox:AuditLog:RetainedDays` to the operator-approved value.
2. Run the host through at least two daily retention windows in a staging environment.
3. Query `audit_reports` before and after each sweep.
4. Confirm rows with `started_at < UtcNow - RetainedDays` are deleted and rows at or after the cutoff remain.

Pass criteria: retained rows match the configured window after multiple daily sweeps, and zero-delete sweeps do not create noisy logs.
