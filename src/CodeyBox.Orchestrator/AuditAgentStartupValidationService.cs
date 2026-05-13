using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Validates audit-agent credentials at startup for every configured project.
/// When a project's <see cref="ProjectAudit.AuditAgent"/> or
/// <see cref="ProjectAudit.PerAuditorAgent"/> names an agent whose credential
/// cannot be resolved, a warning is logged and the orchestrator falls through
/// to the work agent at runtime. Non-fatal: the host starts regardless.
/// </summary>
public sealed class AuditAgentStartupValidationService : IHostedService
{
    private readonly IProjectRepository _projects;
    private readonly ICredentialProvider _credentials;
    private readonly ILogger<AuditAgentStartupValidationService> _log;

    // Exposed so tests can await all background work after StartAsync returns.
    internal Task StartupTask { get; private set; } = Task.CompletedTask;

    public AuditAgentStartupValidationService(
        IProjectRepository projects,
        ICredentialProvider credentials,
        ILogger<AuditAgentStartupValidationService> log)
    {
        _projects = projects;
        _credentials = credentials;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        StartupTask = Task.Run(() => ValidateAllAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ValidateAllAsync(CancellationToken ct)
    {
        IReadOnlyList<Project> projects;
        try
        {
            projects = await _projects.ListAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Startup audit-agent validation: could not load projects");
            return;
        }

        foreach (var project in projects)
            await ValidateProjectAsync(project, ct);
    }

    private async Task ValidateProjectAsync(Project project, CancellationToken ct)
    {
        var auditKinds = new HashSet<AgentKind>();
        var audits = new[] { project.Audit.ResolveProfile() }
            .Concat(project.Audit.Profiles.Values);

        foreach (var audit in audits)
        {
            if (audit.AuditAgent is { } auditAgent)
                auditKinds.Add(auditAgent);

            foreach (var kind in audit.PerAuditorAgent.Values)
                auditKinds.Add(kind);
        }

        foreach (var kind in auditKinds)
        {
            if (kind == project.DefaultAgent)
                continue;

            try
            {
                var credential = await _credentials.GetAsync(kind, ct);
                if (credential is null)
                {
                    _log.LogWarning(
                        "Project '{ProjectId}': AuditAgent={AuditAgent} configured but no credential found " +
                        "for agent '{AuditAgent}'; will fall through to work agent '{WorkAgent}' at runtime",
                        project.Id.Value, kind.Value, kind.Value, project.DefaultAgent.Value);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Project '{ProjectId}': could not resolve credential for AuditAgent={AuditAgent}",
                    project.Id.Value, kind.Value);
            }
        }
    }
}
