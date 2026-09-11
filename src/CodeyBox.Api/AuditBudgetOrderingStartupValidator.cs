using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Fails host startup when the audit-phase budgets are misordered. The
/// required ordering is auditor idle timeout &lt; per-iteration audit timeout
/// &lt; item-stale timeout &lt; sandbox wall clock; an inverted configuration
/// must be rejected rather than silently killing healthy long runs (a quiet
/// but progressing auditor recorded <c>incomplete</c>) or parking healthy
/// iterations as stale.
/// </summary>
/// <remarks>
/// <para>
/// This check lives in a hosted service rather than an
/// <see cref="IValidateOptions{T}"/> because the four budgets span two
/// options graphs: the idle and item-stale timeouts bind under
/// <see cref="CodeyBoxOptions"/> while the per-iteration timeout binds per
/// project under <see cref="ProjectsOptions"/>. Resolving both here runs
/// every typed validator first, so a range error still reports with its own
/// specific message before the ordering check runs.
/// </para>
/// <para>
/// Per-agent item-stale overrides are checked against the same per-iteration
/// budget but are exempt from the wall-clock leg, matching
/// <see cref="WorkerProgressWatchdogOptions.ValidateTimeoutOrdering"/>: a
/// batch-latency agent may legitimately outlive the default sandbox backstop.
/// </para>
/// </remarks>
internal sealed class AuditBudgetOrderingStartupValidator : IHostedService
{
    private readonly IOptions<CodeyBoxOptions> _codeyBox;
    private readonly IOptions<ProjectsOptions> _projects;
    private readonly ILogger<AuditBudgetOrderingStartupValidator> _log;

    public AuditBudgetOrderingStartupValidator(
        IOptions<CodeyBoxOptions> codeyBox,
        IOptions<ProjectsOptions> projects,
        ILogger<AuditBudgetOrderingStartupValidator> log)
    {
        _codeyBox = codeyBox;
        _projects = projects;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        Validate(_codeyBox.Value, _projects.Value);
        _log.LogDebug("Audit budget ordering validated: idle < per-iteration < item-stale < wall clock");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    internal static void Validate(CodeyBoxOptions codeyBox, ProjectsOptions projects)
    {
        ArgumentNullException.ThrowIfNull(codeyBox);
        ArgumentNullException.ThrowIfNull(projects);

        var idle = AuditBudgetOrdering.EffectiveIdleTimeout(
            codeyBox.PipelineTuning.AuditorIdleTimeout,
            codeyBox.PipelineTuning.CSharpTestPassAuditorIdleTimeout);
        var itemStale = codeyBox.WorkerProgressWatchdog.ItemStaleTimeout;
        TimeSpan? wallClock = SandboxResourceLimits.Default.WallClock;

        var defaultMinutes = projects.Defaults.Audit?.PerIterationTimeoutMinutes
            ?? ProjectAuditConfig.DefaultPerIterationTimeoutMinutes;
        AuditBudgetOrdering.Validate(
            idle,
            TimeSpan.FromMinutes(defaultMinutes),
            itemStale,
            wallClock,
            perIterationPath: "CodeyBox:Defaults:Audit:PerIterationTimeoutMinutes");

        for (var i = 0; i < projects.Projects.Count; i++)
        {
            var project = projects.Projects[i];
            var minutes = project.Audit?.PerIterationTimeoutMinutes ?? defaultMinutes;
            AuditBudgetOrdering.Validate(
                idle,
                TimeSpan.FromMinutes(minutes),
                itemStale,
                wallClock,
                perIterationPath: $"CodeyBox:Projects:{i}:Audit:PerIterationTimeoutMinutes");
        }

        foreach (var (key, per) in codeyBox.WorkerProgressWatchdog.PerAgent)
        {
            if (per?.ItemStaleTimeout is not { } entryStale || entryStale <= TimeSpan.Zero)
                continue;

            AuditBudgetOrdering.Validate(
                idle,
                TimeSpan.FromMinutes(defaultMinutes),
                entryStale,
                wallClock: null,
                perIterationPath: "CodeyBox:Defaults:Audit:PerIterationTimeoutMinutes",
                itemStalePath: $"CodeyBox:WorkerProgressWatchdog:PerAgent:{key}:ItemStaleTimeout");
        }
    }
}
