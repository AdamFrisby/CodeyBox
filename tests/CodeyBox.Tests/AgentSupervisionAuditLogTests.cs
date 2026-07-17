using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

public sealed class AgentSupervisionAuditLogTests : IDisposable
{
    private readonly TestSink _sink = new();

    // A dedicated, injected Serilog logger keeps this test's audit events off the
    // process-global Serilog.Log.Logger, so a concurrent host bootstrap that
    // reassigns the global static cannot reroute them away from _sink. This is
    // why the class no longer needs the GlobalSerilog serialization collection.
    private readonly Serilog.Core.Logger _auditLogger;

    public AgentSupervisionAuditLogTests()
    {
        _auditLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveDataRedactionEnricher>()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => _auditLogger.Dispose();

    [Fact]
    public async Task HumanInjection_EmitsQueuedStartedCompletedAuditEventsWithRedaction()
    {
        var service = new AgentSupervisionService(
            () => new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 4 },
            auditLogger: _auditLogger);
        await using var scope = await service.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var fakeGitHubToken = string.Concat("gho_", "ABCdef123456789012345678901234");
        var fakeAnthropicToken = string.Concat(
            "sk-ant-api03-",
            "ABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefABCdefAA");
        var receipt = await service.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest($"please inspect {fakeGitHubToken}", "operator-alice"));

        await scope.RunPendingInjectionsAsync(
            new AgentResult(true, "auto", null, null),
            (_, _) => Task.FromResult(new AgentResult(true, $"done {fakeAnthropicToken}", null, null)));

        var queued = Assert.Single(_sink.Events, e => Scalar<string>(e, "EventName") == "agent.supervision_injection_queued");
        var started = Assert.Single(_sink.Events, e => Scalar<string>(e, "EventName") == "agent.supervision_injection_started");
        var completed = Assert.Single(_sink.Events, e => Scalar<string>(e, "EventName") == "agent.supervision_injection_completed");

        Assert.Equal(receipt.InjectionId, Scalar<string>(queued, "InjectionId"));
        Assert.Equal(receipt.InjectionId, Scalar<string>(started, "InjectionId"));
        Assert.Equal(receipt.InjectionId, Scalar<string>(completed, "InjectionId"));
        Assert.Equal("operator-alice", Scalar<string>(queued, "Actor"));
        Assert.Equal("***", Scalar<string>(completed, "SessionId"));
        Assert.True(Scalar<bool>(completed, "Success"));

        var injectionText = Scalar<string>(queued, "InjectionText") ?? "";
        Assert.DoesNotContain("gho_", injectionText, StringComparison.Ordinal);
        Assert.Contains("***", injectionText, StringComparison.Ordinal);
        var summary = Scalar<string>(completed, "Summary") ?? "";
        Assert.DoesNotContain("sk-ant-api03", summary, StringComparison.Ordinal);
        Assert.Contains("***", summary, StringComparison.Ordinal);
        Assert.Equal(LogEventLevel.Information, completed.Level);
    }

    private static AgentSupervisionSessionStart Start() =>
        new(
            WorkItemId.New(),
            "project",
            "work",
            1,
            AgentKind.Claude,
            AgentInstanceId: null,
            ModelId: null,
            ReasoningMode: null,
            SandboxId: "sandbox",
            WorkingDirectory: "/work",
            Source: "test");

    private static T? Scalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        return default;
    }
}
