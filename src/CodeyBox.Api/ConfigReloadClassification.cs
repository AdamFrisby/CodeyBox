using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Whether editing a configuration key takes effect on reload or requires a restart.
/// </summary>
public enum ConfigReloadEffect
{
    /// <summary>
    /// The reload path re-binds the value at runtime; the next pickup / audit
    /// run observes the edit without a restart.
    /// </summary>
    HotReload,

    /// <summary>
    /// The value is captured into singleton plumbing at startup (or the reload
    /// is rejected outright). Editing the key at runtime has no effect until
    /// the process restarts.
    /// </summary>
    RestartRequired,
}

/// <summary>
/// Answers "will editing this key take effect without a restart" without
/// reading source. The map is derived from the same policy sets the reload
/// path itself uses (<see cref="WorkerPoolHotReloadPolicy"/>,
/// <see cref="PipelineTuningHotReloadPolicy"/>, and the guarded keys enforced
/// by <see cref="ImmutableCodeyBoxOptionsValidator"/>), so a key that changes
/// from captured to live-reloading changes its answer here automatically —
/// provided its policy entry moves with it. Tests pin both directions: every
/// hot-reloadable field must be observed by the reload fingerprint, and every
/// restart-required guarded key must still be rejected by the validator.
/// </summary>
public static class ConfigReloadClassification
{
    /// <summary>
    /// The <c>CodeyBox:*</c> keys rejected (or ignored) at reload by
    /// <see cref="ImmutableCodeyBoxOptionsValidator"/>. Kept as full key
    /// paths, matching the field names used in the validator's failure
    /// messages. A test fires the validator against each entry to prove the
    /// restart-required answer is still true.
    /// </summary>
    internal static IReadOnlyList<string> ValidatorGuardedKeyPaths { get; } =
    [
        "CodeyBox:SandboxProvider",
        "CodeyBox:StateDatabasePath",
        "CodeyBox:GitRootDirectory",
        "CodeyBox:GitCommandMaxOutputBytes",
        "CodeyBox:AgentStreams:Path",
        "CodeyBox:EnableSharedUpstreamMirror",
        "CodeyBox:SharedUpstreamMirrorDirectory",
        "CodeyBox:Incus:ProjectName",
        "CodeyBox:Incus:StagingDirectory",
    ];

    /// <summary>
    /// Legacy top-level knob that feeds the worker-pool resolve live on every
    /// reload (<c>WorkerPool.MaxConcurrentWorkers</c> wins when set, otherwise
    /// this fallback). Editing it hot-reloads through the same gate resize.
    /// </summary>
    internal const string LegacyConcurrencyKey = "CodeyBox:Concurrency";

    /// <summary>
    /// Looks up the reload effect for a <c>:</c>-separated configuration key
    /// path such as <c>CodeyBox:PipelineTuning:AuditorAbsoluteTimeout</c>.
    /// Comparison is case-insensitive. Returns <c>false</c> for keys outside
    /// the classified surface (other blocks may hot-reload through their own
    /// bridges, or bind to nothing — see the unbound-key report on reload).
    /// </summary>
    public static bool TryGetEffect(string? keyPath, out ConfigReloadEffect effect)
    {
        effect = default;
        if (string.IsNullOrWhiteSpace(keyPath))
            return false;

        var key = keyPath.Trim();
        if (key.StartsWith("CodeyBox:WorkerPool:", StringComparison.OrdinalIgnoreCase))
        {
            var field = key["CodeyBox:WorkerPool:".Length..].Trim();
            if (WorkerPoolHotReloadPolicy.HotReloadableFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                effect = ConfigReloadEffect.HotReload;
                return true;
            }

            if (WorkerPoolHotReloadPolicy.RestartRequiredFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                effect = ConfigReloadEffect.RestartRequired;
                return true;
            }

            return false;
        }

        if (key.StartsWith("CodeyBox:PipelineTuning:", StringComparison.OrdinalIgnoreCase))
        {
            var field = key["CodeyBox:PipelineTuning:".Length..].Trim();
            if (PipelineTuningHotReloadPolicy.HotReloadableFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                effect = ConfigReloadEffect.HotReload;
                return true;
            }

            if (PipelineTuningHotReloadPolicy.RestartRequiredFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                effect = ConfigReloadEffect.RestartRequired;
                return true;
            }

            return false;
        }

        if (string.Equals(key, LegacyConcurrencyKey, StringComparison.OrdinalIgnoreCase))
        {
            effect = ConfigReloadEffect.HotReload;
            return true;
        }

        // The global work-timeout default is read live from the options
        // monitor at every dispatch, so edits take effect for subsequently
        // dispatched work without a restart.
        if (string.Equals(key, "CodeyBox:DefaultWorkTimeoutMinutes", StringComparison.OrdinalIgnoreCase))
        {
            effect = ConfigReloadEffect.HotReload;
            return true;
        }

        if (ValidatorGuardedKeyPaths.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            effect = ConfigReloadEffect.RestartRequired;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pure diff of the restart-required surface between two option snapshots:
    /// returns the full key paths whose effective value changed and therefore
    /// need a restart to take effect. Used by the reload coordinator to name
    /// the ignored keys at reload time instead of staying silent.
    /// </summary>
    internal static IReadOnlyList<string> DiffRestartRequiredKeys(CodeyBoxOptions before, CodeyBoxOptions after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changed = new List<string>();

        foreach (var field in WorkerPoolHotReloadPolicy.RestartRequiredFields)
        {
            if (!WorkerPoolRestartFieldEqual(field, before.WorkerPool, after.WorkerPool))
                changed.Add($"CodeyBox:WorkerPool:{field}");
        }

        if (!string.Equals(
                NormalizeSandboxProvider(before.SandboxProvider),
                NormalizeSandboxProvider(after.SandboxProvider),
                StringComparison.Ordinal)
            && !IsWithinReloadableProviderSet(
                NormalizeSandboxProvider(before.SandboxProvider),
                NormalizeSandboxProvider(after.SandboxProvider)))
        {
            changed.Add("CodeyBox:SandboxProvider");
        }

        CheckGuardedPath(changed, "CodeyBox:StateDatabasePath", NormalizePath(before.StateDatabasePath), NormalizePath(after.StateDatabasePath));
        CheckGuardedPath(changed, "CodeyBox:GitRootDirectory", NormalizePath(before.GitRootDirectory), NormalizePath(after.GitRootDirectory));
        if (before.GitCommandMaxOutputBytes != after.GitCommandMaxOutputBytes)
            changed.Add("CodeyBox:GitCommandMaxOutputBytes");
        CheckGuardedPath(changed, "CodeyBox:AgentStreams:Path", NormalizePath(before.AgentStreams.Path), NormalizePath(after.AgentStreams.Path));
        if (before.EnableSharedUpstreamMirror != after.EnableSharedUpstreamMirror)
            changed.Add("CodeyBox:EnableSharedUpstreamMirror");
        CheckGuardedPath(changed, "CodeyBox:SharedUpstreamMirrorDirectory", NormalizePath(before.SharedUpstreamMirrorDirectory), NormalizePath(after.SharedUpstreamMirrorDirectory));

        // Mirror the validator: Incus identity only matters while a provider in
        // the reloadable set is (or becomes) active; otherwise the keys bind
        // to nothing live and an edit needs no restart warning here (the
        // unbound-key report owns that signal).
        if (IsReloadableSandboxProvider(NormalizeSandboxProvider(before.SandboxProvider))
            || IsReloadableSandboxProvider(NormalizeSandboxProvider(after.SandboxProvider)))
        {
            CheckGuardedPath(
                changed,
                "CodeyBox:Incus:ProjectName",
                NormalizeString((before.Incus ?? new IncusSandboxConfig()).ProjectName),
                NormalizeString((after.Incus ?? new IncusSandboxConfig()).ProjectName));
            CheckGuardedPath(
                changed,
                "CodeyBox:Incus:StagingDirectory",
                NormalizeConfiguredPath(before.Incus?.StagingDirectory),
                NormalizeConfiguredPath(after.Incus?.StagingDirectory));
        }

        return changed;
    }

    private static bool WorkerPoolRestartFieldEqual(string field, WorkerPoolOptions before, WorkerPoolOptions after) =>
        field switch
        {
            nameof(WorkerPoolOptions.DispatchGateAcquisitionBackoff) =>
                before.DispatchGateAcquisitionBackoff == after.DispatchGateAcquisitionBackoff,
            nameof(WorkerPoolOptions.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation) =>
                before.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation == after.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation,
            nameof(WorkerPoolOptions.NoProgressBackoffBase) =>
                before.NoProgressBackoffBase == after.NoProgressBackoffBase,
            nameof(WorkerPoolOptions.NoProgressBackoffMax) =>
                before.NoProgressBackoffMax == after.NoProgressBackoffMax,
            nameof(WorkerPoolOptions.MaxNoProgressRedispatches) =>
                before.MaxNoProgressRedispatches == after.MaxNoProgressRedispatches,
            _ => WorkerPoolOptionsFieldEqualByReflection(field, before, after),
        };

    private static bool WorkerPoolOptionsFieldEqualByReflection(string field, WorkerPoolOptions before, WorkerPoolOptions after)
    {
        var property = typeof(WorkerPoolOptions).GetProperty(
            field,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        if (property is null)
            return true;

        return Equals(property.GetValue(before), property.GetValue(after));
    }

    private static void CheckGuardedPath(List<string> changed, string keyPath, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changed.Add(keyPath);
    }

    private static bool IsWithinReloadableProviderSet(string before, string after) =>
        IsReloadableSandboxProvider(before) && IsReloadableSandboxProvider(after);

    private static bool IsReloadableSandboxProvider(string value) =>
        SandboxProviderKinds.SupportsHotReload(value);

    private static string NormalizeString(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeSandboxProvider(string? value) =>
        NormalizeString(value).ToLowerInvariant();

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string NormalizeConfiguredPath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizePath(value);
}
