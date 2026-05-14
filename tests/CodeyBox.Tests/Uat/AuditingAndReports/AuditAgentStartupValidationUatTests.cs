using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests.Uat.AuditingAndReports;

/// <summary>
/// UAT coverage for audit-agent startup validation warnings and de-duplication.
/// Plan anchor: docs/uat/00-plan.md#audit-agent-startup-validation---warns-when-configured-audit-agents-lack-credentials
/// </summary>
public sealed class AuditAgentStartupValidationUatTests
{
    [Fact]
    public async Task StartupValidation_DeduplicatesGlobalAndPerAuditorAuditAgents()
    {
        var project = new Project
        {
            Id = new ProjectId("audit-uat"),
            DisplayName = "Audit UAT",
            RepositoryUrl = "file:///unused",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                AuditAgent = AgentKind.Gemini,
                PerAuditorAgent = new Dictionary<string, AgentKind>
                {
                    ["security:llm-review"] = AgentKind.Gemini,
                    ["quality:llm-review"] = AgentKind.Gemini,
                },
            },
        };
        var logger = new CapturingLogger<AuditAgentStartupValidationService>();
        var service = new AuditAgentStartupValidationService(
            new InMemoryProjectRepository(project),
            new SelectiveCredentialProvider(null),
            logger);

        await service.StartAsync(CancellationToken.None);
        await service.StartupTask;

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        var warning = Assert.Single(warnings);
        Assert.Contains("AuditAgent=gemini", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("work agent 'claude'", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupValidation_SkipsAuditAgentThatMatchesDefaultWorkAgent()
    {
        var project = new Project
        {
            Id = new ProjectId("audit-uat"),
            DisplayName = "Audit UAT",
            RepositoryUrl = "file:///unused",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                AuditAgent = AgentKind.Claude,
                PerAuditorAgent = new Dictionary<string, AgentKind>
                {
                    ["security:llm-review"] = AgentKind.Claude,
                },
            },
        };
        var logger = new CapturingLogger<AuditAgentStartupValidationService>();
        var service = new AuditAgentStartupValidationService(
            new InMemoryProjectRepository(project),
            new SelectiveCredentialProvider(null),
            logger);

        await service.StartAsync(CancellationToken.None);
        await service.StartupTask;

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
