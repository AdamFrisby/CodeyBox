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
        AuditLog.AuditorRun("security:llm-review", "Warning", TimeSpan.FromSeconds(5));

        var evt = Assert.Single(_sink.Events);
        Assert.True(GetScalar<bool>(evt, "Audit"));
        Assert.Equal("auditor.run", GetScalar<string>(evt, "EventName"));
        Assert.Equal("security:llm-review", GetScalar<string>(evt, "AuditorName"));
        Assert.Equal("Warning", GetScalar<string>(evt, "WorstSeverity"));
        Assert.Equal(5_000L, GetScalar<long>(evt, "DurationMs"));
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
    private readonly List<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Add(logEvent);
}
