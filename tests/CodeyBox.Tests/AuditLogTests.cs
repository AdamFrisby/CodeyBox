using CodeyBox.Core;
using Serilog;
using Serilog.Context;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the <see cref="AuditLog"/> helper and <see cref="SensitiveDataRedactionEnricher"/>.
///
/// Each test class instance gets a fresh Serilog logger wired to an in-memory
/// <see cref="TestSink"/>. The <c>GlobalSerilog</c> xUnit collection serializes
/// this class with every other test class that mutates the static
/// <see cref="Log.Logger"/> (notably <c>WebApplicationFactory</c>-based tests
/// whose Program.cs bootstrap re-creates the global logger), so concurrent
/// startup can't swap our sink out mid-assertion.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AuditLogTests : IDisposable
{
    private readonly TestSink _sink = new();

    public AuditLogTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveDataRedactionEnricher>()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    // ── Audit flag ───────────────────────────────────────────────────────────

    [Fact]
    public void AgentStarted_emits_Audit_true()
    {
        AuditLog.AgentStarted(AgentKind.Claude, "vm-01", "work");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
    }

    [Fact]
    public void AgentStarted_emits_correct_EventName_and_properties()
    {
        AuditLog.AgentStarted(AgentKind.Claude, "vm-02", "work");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("agent.started", GetScalar<string>(evt, "EventName"));
        Assert.Equal("claude", GetScalar<string>(evt, "Agent"));
        Assert.Equal("vm-02", GetScalar<string>(evt, "Sandbox"));
        Assert.Equal("work", GetScalar<string>(evt, "Phase"));
    }

    [Fact]
    public void AgentFinished_emits_duration_and_success_properties()
    {
        AuditLog.AgentFinished(AgentKind.Claude, "vm-03", success: true, exitCode: null, TimeSpan.FromSeconds(30));

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("agent.finished", GetScalar<string>(evt, "EventName"));
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("claude", GetScalar<string>(evt, "Agent"));
        Assert.Equal("vm-03", GetScalar<string>(evt, "Sandbox"));
        Assert.True(GetScalar<bool>(evt, "Success"));
        Assert.Equal(30_000L, GetScalar<long>(evt, "DurationMs"));
    }

    [Fact]
    public void OperatorControlledAuditFields_remove_line_breaks()
    {
        AuditLog.AgentPaused(
            new AgentKind("codex\r\nforged"),
            "operator reason\r\nforged",
            "operator\r\nforged",
            expiresAt: null);
        AuditLog.ProjectQueuePaused(
            new ProjectId("project-safe"),
            "queue reason\r\nforged");

        Assert.All(_sink.Events, evt =>
        {
            foreach (var value in evt.Properties.Values.OfType<ScalarValue>()
                         .Select(value => value.Value).OfType<string>())
            {
                Assert.DoesNotContain('\r', value);
                Assert.DoesNotContain('\n', value);
            }
        });
    }

    // ── Scope propagation ────────────────────────────────────────────────────

    [Fact]
    public void WorkItemScope_propagates_WorkItemId_to_nested_events()
    {
        var id = WorkItemId.New();
        using (AuditLog.WorkItemScope(id))
        {
            AuditLog.AgentStarted(AgentKind.Claude, "vm-04", "work");
        }

        var evt = Assert.Single(_sink.Events);
        Assert.Equal(id.ToString(), GetScalar<string>(evt, "WorkItemId"));
    }

    [Fact]
    public void ProjectScope_propagates_ProjectId_to_nested_events()
    {
        var pid = new ProjectId("my-project");
        using (AuditLog.ProjectScope(pid))
        {
            AuditLog.AgentStarted(AgentKind.Codex, "vm-05", "work");
        }

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("my-project", GetScalar<string>(evt, "ProjectId"));
    }

    [Fact]
    public void WorkItemScope_does_not_leak_outside_using_block()
    {
        var id = WorkItemId.New();
        using (AuditLog.WorkItemScope(id))
        {
            // scope is active here
        }
        // scope disposed — WorkItemId must no longer appear
        AuditLog.AgentStarted(AgentKind.Claude, "vm-06", "work");

        var evt = Assert.Single(_sink.Events);
        Assert.False(evt.Properties.ContainsKey("WorkItemId"));
    }

    [Fact]
    public void WorkItemScope_and_ProjectScope_can_nest_independently()
    {
        var wid = WorkItemId.New();
        var pid = new ProjectId("nested-project");

        using (AuditLog.WorkItemScope(wid))
        using (AuditLog.ProjectScope(pid))
        {
            AuditLog.WorkItemTransitioned(wid, "Working");
        }

        var evt = Assert.Single(_sink.Events);
        Assert.Equal(wid.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("nested-project", GetScalar<string>(evt, "ProjectId"));
    }

    // ── Redaction policy ─────────────────────────────────────────────────────

    [Fact]
    public void RedactionEnricher_redacts_property_whose_name_contains_Token()
    {
        Log.Logger
            .ForContext("SomeToken", "plaintext-value")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "SomeToken"));
    }

    [Fact]
    public void RedactionEnricher_redacts_property_whose_name_contains_Secret()
    {
        Log.Logger
            .ForContext("WebhookSecret", "mysecret")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "WebhookSecret"));
    }

    [Fact]
    public void RedactionEnricher_redacts_property_whose_name_contains_Password()
    {
        Log.Logger
            .ForContext("DbPassword", "hunter2")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "DbPassword"));
    }

    [Fact]
    public void RedactionEnricher_redacts_property_whose_name_contains_Authorization()
    {
        Log.Logger
            .ForContext("Authorization", "Bearer tok123")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "Authorization"));
    }

    [Fact]
    public void RedactionEnricher_redacts_property_whose_name_contains_ApiKey()
    {
        Log.Logger
            .ForContext("OpenAiApiKey", "sk-proj-test123")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "OpenAiApiKey"));
    }

    [Fact]
    public void RedactionEnricher_redacts_gho_token_value_regardless_of_key_name()
    {
        Log.Logger
            .ForContext("Description", "gho_abc123XYZ789abc")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "Description"));
    }

    [Fact]
    public void RedactionEnricher_redacts_ghp_token_value()
    {
        Log.Logger
            .ForContext("Info", "ghp_SomeFineGrainedToken01")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "Info"));
    }

    [Fact]
    public void RedactionEnricher_redacts_github_pat_value()
    {
        Log.Logger
            .ForContext("Info", "github_pat_SOME_TOKEN_HERE")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "Info"));
    }

    [Fact]
    public void RedactionEnricher_redacts_anthropic_key_value()
    {
        Log.Logger
            .ForContext("Info", "sk-ant-api03-sometoken")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("***", GetScalar<string>(evt, "Info"));
    }

    [Fact]
    public void RedactionEnricher_redacts_session_id_inside_string_property()
    {
        const string SessionId = "e61b65a0-0f1e-4469-94f0-0be82d71b909";
        Log.Logger
            .ForContext(
                "StdoutTail",
                $$"""{"type":"system","subtype":"init","session_id":"{{SessionId}}"}""")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        var tail = GetScalar<string>(evt, "StdoutTail");
        Assert.DoesNotContain(SessionId, tail);
        Assert.Contains("\"session_id\":\"***\"", tail);
    }

    [Fact]
    public void RedactionEnricher_does_not_redact_normal_string_properties()
    {
        Log.Logger
            .ForContext("Agent", "claude")
            .ForContext("EventName", "test.event")
            .Information("test");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("claude", GetScalar<string>(evt, "Agent"));
        Assert.Equal("test.event", GetScalar<string>(evt, "EventName"));
    }

    // ── Event taxonomy spot-checks ───────────────────────────────────────────

    [Fact]
    public void TokenRead_emits_auth_token_read_event()
    {
        AuditLog.TokenRead("CODEYBOX_MY_TOKEN", new ProjectId("acme"));

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("auth.token_read", GetScalar<string>(evt, "EventName"));
        Assert.Equal("CODEYBOX_MY_TOKEN", GetScalar<string>(evt, "EnvVar"));
        Assert.Equal("acme", GetScalar<string>(evt, "ProjectId"));
    }

    [Fact]
    public void SandboxCreated_emits_sandbox_created_event()
    {
        AuditLog.SandboxCreated("codeybox-abc123", "claude");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("sandbox.created", GetScalar<string>(evt, "EventName"));
    }

    [Fact]
    public void SandboxLeakDisposed_emits_reason_for_classification()
    {
        AuditLog.SandboxLeakDisposed(
            "codeybox-stale",
            ageMinutes: 90.5,
            diskMb: 512,
            disposedAt: DateTimeOffset.UtcNow,
            reason: "untracked_sandbox_age_threshold_exceeded");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("sandbox.leak_disposed", GetScalar<string>(evt, "EventName"));
        Assert.Equal("codeybox-stale", GetScalar<string>(evt, "SandboxName"));
        Assert.Equal("untracked_sandbox_age_threshold_exceeded", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void SandboxProvisioningTransientRetry_emits_sandbox_provisioning_transient_retry_event()
    {
        var workItemId = WorkItemId.New();

        AuditLog.SandboxProvisioningTransientRetry(workItemId, "mount", 2, "multipass-socket-unreachable");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("sandbox.provisioning_transient_retry", GetScalar<string>(evt, "EventName"));
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("mount", GetScalar<string>(evt, "Operation"));
        Assert.Equal(2, GetScalar<int>(evt, "Attempt"));
        Assert.Equal("multipass-socket-unreachable", GetScalar<string>(evt, "ErrorClass"));
    }

    [Fact]
    public void SandboxAgentInfrastructureFailure_emits_sandbox_prefixed_event()
    {
        var workItemId = WorkItemId.New();

        AuditLog.SandboxAgentInfrastructureFailure(
            workItemId,
            AgentKind.Codex,
            "codeybox-vm-127",
            "work",
            "agent exited 127",
            "agent binary was not found in the sandbox");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("sandbox.agent_infra_failure", GetScalar<string>(evt, "EventName"));
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("codex", GetScalar<string>(evt, "Agent"));
        Assert.Equal("codeybox-vm-127", GetScalar<string>(evt, "Sandbox"));
        Assert.Equal("work", GetScalar<string>(evt, "Phase"));
        Assert.Equal("agent exited 127", GetScalar<string>(evt, "Summary"));
        Assert.Equal("agent binary was not found in the sandbox", GetScalar<string>(evt, "Reason"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
    }

    [Fact]
    public void SandboxAgentInfrastructureFailure_normalizes_multiline_fields()
    {
        AuditLog.SandboxAgentInfrastructureFailure(
            WorkItemId.New(),
            AgentKind.Codex,
            "codeybox-vm-127",
            "work",
            "agent exited 127\nnext line",
            "binary missing\r\ninstall codex");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("agent exited 127 next line", GetScalar<string>(evt, "Summary"));
        Assert.Equal("binary missing  install codex", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void TransientRetryAttempted_emits_transient_retry_attempted_event()
    {
        var workItemId = WorkItemId.New();

        AuditLog.TransientRetryAttempted(workItemId, "periodic", "retried", "WaitingForTransientRetry", "actualFrom=merge");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("transient_retry_attempted", GetScalar<string>(evt, "EventName"));
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("periodic", GetScalar<string>(evt, "Source"));
        Assert.Equal("retried", GetScalar<string>(evt, "Outcome"));
        Assert.Equal("WaitingForTransientRetry", GetScalar<string>(evt, "State"));
        Assert.Equal("actualFrom=merge", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void AuditPassed_emits_audit_passed_event()
    {
        AuditLog.AuditPassed(2);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("audit.passed", GetScalar<string>(evt, "EventName"));
        Assert.Equal(2, GetScalar<int>(evt, "Iteration"));
    }

    [Fact]
    public void AuditFailed_emits_audit_failed_event_at_Warning()
    {
        AuditLog.AuditFailed(3, 5);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("audit.failed", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
    }

    [Fact]
    public void SelfReviewChecklistInjected_emits_event_when_injected()
    {
        var id = WorkItemId.New();
        AuditLog.SelfReviewChecklistInjected(id, injected: true);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("work_prompt.self_review_checklist", GetScalar<string>(evt, "EventName"));
        Assert.Equal("INJECTED", GetScalar<string>(evt, "State"));
        Assert.Equal(id.ToString(), GetScalar<string>(evt, "WorkItemId"));
    }

    [Fact]
    public void SelfReviewChecklistInjected_emits_event_when_omitted()
    {
        AuditLog.SelfReviewChecklistInjected(WorkItemId.New(), injected: false);

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("work_prompt.self_review_checklist", GetScalar<string>(evt, "EventName"));
        Assert.Equal("OMITTED", GetScalar<string>(evt, "State"));
    }

    [Fact]
    public void WebhookDelivered_emits_webhook_delivered_event()
    {
        AuditLog.WebhookDelivered("my-endpoint", "work_item.done", 200, 1);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("webhook.delivered", GetScalar<string>(evt, "EventName"));
        Assert.Equal("my-endpoint", GetScalar<string>(evt, "Endpoint"));
        Assert.Equal(200, GetScalar<int>(evt, "StatusCode"));
    }

    [Fact]
    public void AuditorRun_emits_auditor_run_event_with_correct_properties()
    {
        AuditLog.AuditorRun("security:llm-review", "Warning", TimeSpan.FromSeconds(5), AgentKind.Gemini);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("auditor.run", GetScalar<string>(evt, "EventName"));
        Assert.Equal("security:llm-review", GetScalar<string>(evt, "AuditorName"));
        Assert.Equal("Warning", GetScalar<string>(evt, "WorstSeverity"));
        Assert.Equal(5_000L, GetScalar<long>(evt, "DurationMs"));
        Assert.Equal("gemini", GetScalar<string>(evt, "AgentKind"));
    }

    [Fact]
    public void ClaudeUnauthorizedObserved_emits_agent_claude_unauthorized_event()
    {
        AuditLog.ClaudeUnauthorizedObserved("work", "vm-99");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("agent.claude_unauthorized", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal("work", GetScalar<string>(evt, "Phase"));
        Assert.Equal("vm-99", GetScalar<string>(evt, "SandboxName"));
    }

    [Fact]
    public void ClaudeUnauthorizedObserved_NullSandbox_FallsBackToUnknownPlaceholder()
    {
        AuditLog.ClaudeUnauthorizedObserved("audit", sandboxName: null);

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("agent.claude_unauthorized", GetScalar<string>(evt, "EventName"));
        Assert.Equal("audit", GetScalar<string>(evt, "Phase"));
        Assert.Equal("(unknown)", GetScalar<string>(evt, "SandboxName"));
    }

    [Fact]
    public void ClaudeTokenPushedToVm_emits_claude_token_pushed_to_vm_event_at_Information()
    {
        AuditLog.ClaudeTokenPushedToVm("codeybox-vm-77");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("agent.claude_token_pushed_to_vm", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.Equal("codeybox-vm-77", GetScalar<string>(evt, "SandboxName"));
    }

    [Fact]
    public void ClaudeTokenPushFailed_emits_claude_token_push_failed_event_at_Warning()
    {
        AuditLog.ClaudeTokenPushFailed("codeybox-vm-77", "exit 1: permission denied");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("agent.claude_token_push_failed", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal("codeybox-vm-77", GetScalar<string>(evt, "SandboxName"));
        Assert.Equal("exit 1: permission denied", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void CrossReviewActive_emits_audit_cross_review_active_event()
    {
        AuditLog.CrossReviewActive(AgentKind.Claude, AgentKind.Gemini);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("audit.cross_review_active", GetScalar<string>(evt, "EventName"));
        Assert.Equal("claude", GetScalar<string>(evt, "WorkAgent"));
        Assert.Equal("gemini", GetScalar<string>(evt, "AuditAgent"));
    }

    [Fact]
    public void QuotaAuditFallthrough_emits_quota_router_audit_fallthrough_event_at_Warning()
    {
        AuditLog.QuotaAuditFallthrough(AgentKind.Gemini, AgentKind.Claude, "security:llm-review");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("quota_router.audit_fallthrough", GetScalar<string>(evt, "EventName"));
        Assert.Equal(Serilog.Events.LogEventLevel.Warning, evt.Level);
        Assert.Equal("gemini", GetScalar<string>(evt, "ExhaustedAgent"));
        Assert.Equal("claude", GetScalar<string>(evt, "FallbackAgent"));
        Assert.Equal("security:llm-review", GetScalar<string>(evt, "AuditorName"));
    }

    [Fact]
    public void LlmAuditorParkedQuota_emits_audit_llm_auditor_parked_quota_event_at_Warning()
    {
        var id = WorkItemId.New();

        AuditLog.LlmAuditorParkedQuota(id, "security:llm-review", candidateCount: 3);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("audit.llm_auditor_parked_quota", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal(id.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("security:llm-review", GetScalar<string>(evt, "AuditorName"));
        Assert.Equal(3, GetScalar<int>(evt, "CandidateCount"));
    }

    [Fact]
    public void QuotaRetryAttempted_emits_source_outcome_state_and_reason()
    {
        var id = WorkItemId.New();

        AuditLog.QuotaRetryAttempted(id, "periodic", "skipped:quota-still-gated", "WaitingForQuotaReset", "all members exhausted");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("quota_retry_attempted", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.Equal(id.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("periodic", GetScalar<string>(evt, "Source"));
        Assert.Equal("skipped:quota-still-gated", GetScalar<string>(evt, "Outcome"));
        Assert.Equal("WaitingForQuotaReset", GetScalar<string>(evt, "State"));
        Assert.Equal("all members exhausted", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void QuotaRetryAttempted_NullReason_RoundTripsThroughEmptyStringSentinel()
    {
        var id = WorkItemId.New();

        AuditLog.QuotaRetryAttempted(id, "periodic", "skipped:router-unavailable", "WaitingForQuotaReset", reason: null);

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("quota_retry_attempted", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.Equal("periodic", GetScalar<string>(evt, "Source"));
        Assert.Equal("skipped:router-unavailable", GetScalar<string>(evt, "Outcome"));
        Assert.Equal("", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void WorkItemResumed_emits_work_item_resumed_event_with_From_and_Reason()
    {
        var id = WorkItemId.New();

        AuditLog.WorkItemResumed(id, from: "audit", reason: "operator fixed auditor #100");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("work_item.resumed", GetScalar<string>(evt, "EventName"));
        Assert.Equal(id.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("audit", GetScalar<string>(evt, "From"));
        Assert.Equal("operator fixed auditor #100", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void WorkItemResumed_NullReason_RoundTripsThroughEmptyStringSentinel()
    {
        // Serilog drops null properties, so the audit emitter forces null
        // through "" so the timeline reader can rely on the Reason property
        // being present. Both ends must stay in sync — see
        // AuditLogTimelineReader case "work_item.resumed".
        var id = WorkItemId.New();

        AuditLog.WorkItemResumed(id, from: "work", reason: null);

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("work_item.resumed", GetScalar<string>(evt, "EventName"));
        Assert.Equal("work", GetScalar<string>(evt, "From"));
        Assert.Equal("", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void AgentAttemptTimeoutFallback_emits_distinct_timeout_fallback_event_at_Warning()
    {
        var workItemId = WorkItemId.New();

        AuditLog.AgentAttemptTimeoutFallback(
            workItemId,
            phase: "rework",
            iteration: 5,
            fromAgent: AgentKind.Gemini,
            fromModel: "gemini-2.5-pro",
            toAgent: AgentKind.Codex,
            toModel: "gpt-5.2",
            reason: "attempt timed out after 240m");

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("agent.attempt_timeout_fallback", GetScalar<string>(evt, "EventName"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("rework", GetScalar<string>(evt, "Phase"));
        Assert.Equal(5, GetScalar<int>(evt, "Iteration"));
        Assert.Equal("gemini", GetScalar<string>(evt, "FromAgent"));
        Assert.Equal("gemini-2.5-pro", GetScalar<string>(evt, "FromModel"));
        Assert.Equal("codex", GetScalar<string>(evt, "ToAgent"));
        Assert.Equal("gpt-5.2", GetScalar<string>(evt, "ToModel"));
        Assert.Equal("attempt timed out after 240m", GetScalar<string>(evt, "Reason"));
    }

    [Fact]
    public void RebaseResolverAgentSelected_emits_event_naming_chosen_agent()
    {
        var workItemId = WorkItemId.New();

        AuditLog.RebaseResolverAgentSelected(workItemId, AgentKind.Cursor);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("rebase_resolver.agent_selected", GetScalar<string>(evt, "EventName"));
        Assert.Equal("cursor", GetScalar<string>(evt, "ChosenAgent"));
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
    }

    [Fact]
    public void LlmPanelSkippedBuildTestGate_emits_event_with_work_item_and_skipped_count()
    {
        var workItemId = WorkItemId.New();

        AuditLog.LlmPanelSkippedBuildTestGate(workItemId, skippedAuditorCount: 3);

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.Equal("audit.llm_panel_skipped_build_test_gate", GetScalar<string>(evt, "EventName"));
        Assert.Equal(workItemId.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal(3, GetScalar<int>(evt, "SkippedCount"));
    }

    // ── Scoped sink override ─────────────────────────────────────────────────

    [Fact]
    public void PushScopedLogger_routes_events_to_scoped_sink_not_global()
    {
        var scoped = new TestSink();
        using var scopedLogger = new LoggerConfiguration().WriteTo.Sink(scoped).CreateLogger();

        using (AuditLog.PushScopedLogger(scopedLogger))
            AuditLog.AgentStarted(AgentKind.Claude, "vm-scoped", "work");

        // Assert on the unique marker rather than global emptiness: unrelated
        // tests in other collections may emit audit events into the global sink
        // concurrently, but none of them use this sandbox name.
        Assert.DoesNotContain(_sink.Events, e => GetScalar<string>(e, "Sandbox") == "vm-scoped");
        var evt = Assert.Single(scoped.Events);
        Assert.Equal("agent.started", GetScalar<string>(evt, "EventName"));
        Assert.Equal("vm-scoped", GetScalar<string>(evt, "Sandbox"));
    }

    [Fact]
    public void PushScopedLogger_restores_previous_target_on_dispose()
    {
        var scoped = new TestSink();
        using var scopedLogger = new LoggerConfiguration().WriteTo.Sink(scoped).CreateLogger();

        using (AuditLog.PushScopedLogger(scopedLogger))
            AuditLog.AgentStarted(AgentKind.Claude, "vm-inside", "work");

        AuditLog.AgentStarted(AgentKind.Claude, "vm-outside", "work");

        Assert.Equal("vm-inside", GetScalar<string>(Assert.Single(scoped.Events), "Sandbox"));
        Assert.Single(_sink.Events, e => GetScalar<string>(e, "Sandbox") == "vm-outside");
        Assert.DoesNotContain(_sink.Events, e => GetScalar<string>(e, "Sandbox") == "vm-inside");
    }

    [Fact]
    public void PushScopedLogger_nested_scopes_restore_to_outer_scope()
    {
        var outer = new TestSink();
        var inner = new TestSink();
        using var outerLogger = new LoggerConfiguration().WriteTo.Sink(outer).CreateLogger();
        using var innerLogger = new LoggerConfiguration().WriteTo.Sink(inner).CreateLogger();

        using (AuditLog.PushScopedLogger(outerLogger))
        {
            using (AuditLog.PushScopedLogger(innerLogger))
                AuditLog.AgentStarted(AgentKind.Claude, "vm-inner", "work");

            AuditLog.AgentStarted(AgentKind.Claude, "vm-outer", "work");
        }

        Assert.Equal("vm-inner", GetScalar<string>(Assert.Single(inner.Events), "Sandbox"));
        Assert.Equal("vm-outer", GetScalar<string>(Assert.Single(outer.Events), "Sandbox"));
        Assert.DoesNotContain(
            _sink.Events,
            e => GetScalar<string>(e, "Sandbox") is "vm-inner" or "vm-outer");
    }

    [Fact]
    public async Task PushScopedLogger_isolates_concurrent_flows_from_each_other()
    {
        // The reason this override exists: each async flow must capture only its
        // own audit events even while another concurrent flow has a different
        // scope active. Interleave two flows so that both scopes overlap in time
        // and each emits while the other's scope is live; if the override were a
        // shared field rather than AsyncLocal, the events would cross over.
        var sinkA = new TestSink();
        var sinkB = new TestSink();
        using var loggerA = new LoggerConfiguration().WriteTo.Sink(sinkA).CreateLogger();
        using var loggerB = new LoggerConfiguration().WriteTo.Sink(sinkB).CreateLogger();

        using var bothScoped = new Barrier(2);

        async Task EmitUnder(Serilog.ILogger logger, string sandbox)
        {
            using (AuditLog.PushScopedLogger(logger))
            {
                bothScoped.SignalAndWait();
                await Task.Yield();
                AuditLog.AgentStarted(AgentKind.Claude, sandbox, "work");
            }
        }

        await Task.WhenAll(
            Task.Run(() => EmitUnder(loggerA, "vm-flow-a")),
            Task.Run(() => EmitUnder(loggerB, "vm-flow-b")));

        Assert.Equal("vm-flow-a", GetScalar<string>(Assert.Single(sinkA.Events), "Sandbox"));
        Assert.Equal("vm-flow-b", GetScalar<string>(Assert.Single(sinkB.Events), "Sandbox"));
        Assert.DoesNotContain(
            _sink.Events,
            e => GetScalar<string>(e, "Sandbox") is "vm-flow-a" or "vm-flow-b");
    }

    [Fact]
    public void AuditorTimedOut_emits_correct_properties()
    {
        var id = WorkItemId.New();
        AuditLog.AuditorTimedOut(id, "my-auditor", new AgentKind("codex"), 2, "vm-01");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("audit.auditor_timed_out", GetScalar<string>(evt, "EventName"));
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal(id.ToString(), GetScalar<string>(evt, "WorkItemId"));
        Assert.Equal("my-auditor", GetScalar<string>(evt, "Auditor"));
        Assert.Equal("codex", GetScalar<string>(evt, "Agent"));
        Assert.Equal(2, GetScalar<int>(evt, "Iteration"));
        Assert.Equal("vm-01", GetScalar<string>(evt, "SandboxId"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        // Handle numeric widening: long stored as long, int requested
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }
}

/// <summary>In-memory Serilog sink for test assertions.</summary>
internal sealed class TestSink : Serilog.Core.ILogEventSink
{
    private readonly object _gate = new();
    private readonly List<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToList();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
            _events.Add(logEvent);
    }

    public void Clear()
    {
        lock (_gate)
            _events.Clear();
    }
}
