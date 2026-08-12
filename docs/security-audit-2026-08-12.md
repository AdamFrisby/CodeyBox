# CodeyBox security audit — 2026-08-12

**Status:** In progress  
**Revision:** `896b365fafab2e53eaaa327f2f75af9a7f2a8cda`  
**Priority:** 3 of 3  
**Target:** OWASP ASVS 5.0 Level 2, plus applicable Level 3 secrets, administration, plugin, agent-execution, sandbox, and release controls

This living report supersedes no historical document. `docs/security-audit.md` remains useful evidence, but its findings used a single-tenant, partially trusted API-caller model and require re-evaluation under the current hostile authenticated-organization-user model.

## Threat model

- Authenticated organization users, work-item data, repositories, branches, Git metadata, attachments, plugins, auditors, agents, and output are hostile.
- The raw orchestration API is a private service endpoint; its credential must not confer unrelated operator privileges.
- Agent credentials, audit output, subprocess streams, and temporary files are high-value secrets.
- A sandbox escape, cross-work-item repository access, arbitrary host mount, or service credential theft is Critical.
- Cloudflare Access is an edge control and must not be the only administrator security boundary.

## Confirmed findings

| ID | Severity | Finding | Evidence | Required disposition | Status |
| --- | --- | --- | --- | --- | --- |
| CB-2026-001 | High | The deployed admin UI has application authentication disabled. A Cloudflare policy error, origin bypass, or local foothold exposes operator functionality without a second authorization boundary. | `../deploy/codeybox-admin.service`: `CodeyBoxAdmin__RequireAuth=false` | Enable real origin-side authentication or validate Cloudflare Access JWTs at the origin with administrator authorization. | Remediated 2026-08-12: origin validates Cloudflare JWT issuer/audience and permits only `sinewavecompany.com`; optional native Google OAuth is supported. |
| CB-2026-002 | High | The production project configuration excludes Gitleaks, Semgrep, and LLM security review auditors. Security gates therefore do not run for the configured project. | `../deploy/codeybox.json`: auditor exclusions | Remove the exclusions or document a narrowly scoped, expiring exception with an equivalent mandatory control. | Remediated 2026-08-12: security preset enabled and all three security auditors are host-required/non-excludable. |
| CB-2026-003 | High | The deployed sandbox provider is Bubblewrap, sharing the host kernel, while the threat model treats autonomous agent code as hostile. | `../deploy/codeybox.json`: `SandboxProvider` | Use the runtime-tested Multipass or Incus kernel-isolated provider for hostile workloads; Bubblewrap may remain only for explicitly trusted/local profiles. | Risk constrained 2026-08-12: provider capability is enforced; production defaults untrusted and rejects Bubblewrap. This host explicitly runs trusted mode with acknowledgement. Dedicated-kernel testing remains open. |
| CB-2026-004 | Medium | Production dependencies included moderate advisories in OpenTelemetry OTLP, MailKit, and MimeKit. | `dotnet list CodeyBox.slnx package --vulnerable --include-transitive`; advisory output captured 2026-08-12 | Upgrade to patched compatible versions and add functional regression coverage. | Remediated 2026-08-12: OpenTelemetry 1.17 and MailKit/MimeKit 4.17; suppressions removed; notification/audit regression tests pass. |
| CB-2026-005 | Medium | The repository lacked a standard mandatory full build/test/security CI workflow; existing workflows cover specialized resilience and trusted pre-merge validation. | `.github/workflows` inventory | Add required build/test, SAST, dependency, secret, workflow, and container checks without weakening the existing untrusted/trusted workflow separation. | Partially remediated 2026-08-12: full build, deterministic/admin tests, dependency audit/review and CodeQL added. Dedicated secret/workflow scanning remains open. |
| CB-2026-006 | Medium | Repository governance does not currently enforce protected changes on the default branch. | GitHub default-branch protection and ruleset inspection on 2026-08-12 | Require reviewed pull requests, mandatory checks, and block force-push/deletion. | Open |
| CB-2026-007 | Medium | The test-only AngleSharp advisory was suppressed through the older bUnit dependency. | `tools/CodeyBox.Admin/tests/CodeyBox.Admin.Tests/CodeyBox.Admin.Tests.csproj` | Upgrade bUnit and remove the suppression. | Remediated 2026-08-12: bUnit 2.9 migration complete, suppression removed, 244 admin tests pass. |

## Additional code hardening — 2026-08-12

- Production admin startup now requires Cloudflare Access or Google plus an email-domain allowlist; its cookie is always Secure and local password login is Development-only.
- Required shell-auditor tool absence now raises `AuditUnavailableException`, preserving the infrastructure-failure path instead of producing an optional warning/finding.

## Positive controls observed

- The API authentication middleware fails closed unless an explicitly dangerous test/development switch is enabled.
- The raw API is loopback/backplane-bound and the admin UI is loopback-bound in the current deployment.
- Existing workflows carefully separate untrusted pull-request execution from trusted status publication.
- The repository contains extensive authorization, sandbox, credential, webhook, path, audit, and agent-supervision tests.
- Multipass and Incus providers implement stronger kernel isolation and host-enforced egress designs.
- Existing CodeyBox security documentation records prior findings and limitations candidly.

These observations require configuration verification and adversarial testing before release sign-off.

## Dynamic tests pending

- API-key scope, rotation, revocation, separation of service/operator roles, and raw API reachability.
- Admin cookies, CSRF, Access JWT/origin bypass, GitHub App OAuth state, webhook replay, SignalR/SSE, metrics, stdout, and audit downloads.
- Work-item authorization and hostile Git ref, hook, config, submodule, LFS, attachment, and template inputs.
- Credential leakage through argv, environment, files, logs, events, prompts, plugins, audit output, and crashes.
- Plugin/auditor loading and supply-chain integrity.
- Sandbox escape, egress, metadata SSRF, DNS rebinding, host Git access, resource exhaustion, suspend/resume, and cleanup.

## Audit-environment status

The host has Multipass installed with the QEMU driver, but instance launch fails because KVM/nested virtualization is unavailable. Destructive sandbox testing must use an external or software-emulated VM. A second Docker deployment on this kernel is not sufficient containment.

## Release gate

No Critical or High findings may remain. Medium findings require correction or named, time-bounded acceptance with a compensating control. Every fix requires a regression test and independent internal verification.
