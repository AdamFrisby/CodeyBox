using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Captures the startup snapshot of CodeyBoxOptions fields that cannot be
/// rebound safely at runtime and rejects subsequent reloads that change them.
///
/// The framework's <see cref="IOptionsMonitor{TOptions}"/> still surfaces the
/// rejection — consumers reading CurrentValue see the previous, validated
/// value. Operators see an OptionsValidationException in the log, the message
/// names the offending field, and they know a restart is required to apply
/// the change.
///
/// Fields guarded here are the ones whose value is captured by open file
/// handles, long-lived listeners, or singleton constructors elsewhere in the
/// service graph — re-binding them mid-flight would either leak the prior
/// resource or quietly continue using the stale value, which is worse than
/// rejecting the change outright.
///
/// The startup snapshot is captured on the first call to
/// <see cref="Validate"/> so it reflects every layered configuration source
/// (file overlays, environment variables, test ConfigureAppConfiguration
/// hooks) rather than just whatever the builder had bound when the validator
/// was registered.
/// </summary>
public sealed class ImmutableCodeyBoxOptionsValidator : IValidateOptions<CodeyBoxOptions>
{
    private Snapshot? _snapshot;
    private readonly Lock _gate = new();

    /// <summary>
    /// Default constructor: lazily captures the first-validated value as the
    /// startup snapshot.
    /// </summary>
    public ImmutableCodeyBoxOptionsValidator() { }

    /// <summary>
    /// Explicit-snapshot constructor for unit tests: pass the values to treat as
    /// the captured startup snapshot. Production wiring should prefer the
    /// parameterless constructor.
    /// </summary>
    public ImmutableCodeyBoxOptionsValidator(CodeyBoxOptions startupSnapshot)
    {
        _snapshot = Capture(startupSnapshot);
    }

    public ValidateOptionsResult Validate(string? name, CodeyBoxOptions options)
    {
        lock (_gate)
        {
            if (_snapshot is null)
            {
                _snapshot = Capture(options);
                return ValidateOptionsResult.Success;
            }

            var failures = new List<string>();
            Check("CodeyBox:SandboxProvider", _snapshot.SandboxProvider, NormalizeString(options.SandboxProvider), failures);
            Check("CodeyBox:StateDatabasePath", _snapshot.StateDatabasePath, NormalizePath(options.StateDatabasePath), failures);
            Check("CodeyBox:GitRootDirectory", _snapshot.GitRootDirectory, NormalizePath(options.GitRootDirectory), failures);
            Check("CodeyBox:AgentStreams:Path", _snapshot.AgentStreamsPath, NormalizePath(options.AgentStreams.Path), failures);

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }

    private static Snapshot Capture(CodeyBoxOptions options) => new(
        NormalizeString(options.SandboxProvider),
        NormalizePath(options.StateDatabasePath),
        NormalizePath(options.GitRootDirectory),
        NormalizePath(options.AgentStreams.Path));

    private static void Check(string field, string startup, string candidate, List<string> failures)
    {
        if (!string.Equals(startup, candidate, StringComparison.Ordinal))
            failures.Add(
                $"{field} cannot be changed at runtime (startup='{startup}', requested='{candidate}'). " +
                "Restart CodeyBox to apply this change.");
    }

    private static string NormalizeString(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value); }
        catch { return value.Trim(); }
    }

    private sealed record Snapshot(
        string SandboxProvider,
        string StateDatabasePath,
        string GitRootDirectory,
        string AgentStreamsPath);
}

/// <summary>
/// Rejects reloads of <see cref="CodeyBox.Projects.ProjectsOptions"/> that
/// would remove a project that still has non-terminal work items. Adding new
/// projects and editing the body of an existing project both pass cleanly —
/// only removals against live state are blocked.
///
/// The check uses the live <see cref="IWorkItemStore"/>, so newly-completed
/// items free up their project for removal without any explicit signal.
/// </summary>
public sealed class ProjectsOptionsRemovalValidator : IValidateOptions<CodeyBox.Projects.ProjectsOptions>
{
    private static readonly WorkItemState[] TerminalStates =
    {
        WorkItemState.Done,
        WorkItemState.Failed,
        WorkItemState.Cancelled,
        WorkItemState.AuditFailed,
        WorkItemState.MergeConflictResolutionFailed,
        WorkItemState.AbandonedAfterRecoveryAttempts,
    };

    private readonly IWorkItemStore _workItems;

    public ProjectsOptionsRemovalValidator(IWorkItemStore workItems)
    {
        _workItems = workItems;
    }

    public ValidateOptionsResult Validate(string? name, CodeyBox.Projects.ProjectsOptions options)
    {
        var configuredIds = new HashSet<string>(
            options.Projects
                .Select(p => p.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))!,
            StringComparer.Ordinal);

        // Aggregate in-flight items by project id. The check is a config-change
        // path (cold) — synchronously enumerating the store is acceptable.
        var inFlightByProject = CollectInFlightAsync(_workItems).GetAwaiter().GetResult();

        var failures = new List<string>();
        foreach (var (projectId, count) in inFlightByProject)
        {
            if (!configuredIds.Contains(projectId))
                failures.Add(
                    $"CodeyBox:Projects: project '{projectId}' cannot be removed — " +
                    $"{count} non-terminal work item(s) still reference it. " +
                    "Cancel or wait for those items to complete, then retry the config edit.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static async Task<Dictionary<string, int>> CollectInFlightAsync(IWorkItemStore store)
    {
        var terminal = new HashSet<WorkItemState>(TerminalStates);
        var byProject = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var item in store.ListAsync())
        {
            if (terminal.Contains(item.State)) continue;
            var key = item.ProjectId.Value;
            byProject[key] = byProject.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        return byProject;
    }
}
