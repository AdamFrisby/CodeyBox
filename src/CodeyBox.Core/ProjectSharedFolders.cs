namespace CodeyBox.Core;

/// <summary>
/// Access mode of one project shared folder.
/// <see cref="ReadWrite"/> lets agent code modify host files through the
/// mount: the operator accepts that a compromised or buggy agent can
/// create, overwrite, or delete anything under the shared host path.
/// Prefer <see cref="ReadOnly"/> for reference material and datasets.
/// </summary>
public enum SharedFolderMode
{
    ReadOnly = 0,
    ReadWrite = 1,
}

/// <summary>
/// Pipeline phases a shared folder is mounted into.
/// <c>Work</c> covers work, rework, merge, delegation, and check sandboxes;
/// <c>Audit</c> covers audit-agent and audit-tool sandboxes; <c>All</c>
/// covers every phase.
/// </summary>
public enum SharedFolderPhases
{
    Work = 0,
    Audit = 1,
    All = 2,
}

/// <summary>
/// Write-concurrency policy for one shared folder when several sandboxes of
/// the same project run at once.
/// <see cref="Shared"/> allows concurrent writers (last-writer-wins; the
/// operator accepts interleaved writes). <see cref="Exclusive"/> allows at
/// most one running item to hold the folder read-write; the
/// <see cref="ProjectSharedFolder.ExclusiveFallback"/> decides what the
/// others do.
/// </summary>
public enum SharedFolderWriterPolicy
{
    Shared = 0,
    Exclusive = 1,
}

/// <summary>
/// What an <see cref="SharedFolderWriterPolicy.Exclusive"/> folder does when
/// its write lease is already held by another running item.
/// <see cref="Wait"/> defers the sandbox creation until the holder finishes
/// (bounded wait, then a requeue). <see cref="ReadOnly"/> mounts the folder
/// read-only for the waiter instead of blocking it.
/// </summary>
public enum SharedFolderExclusiveFallback
{
    Wait = 0,
    ReadOnly = 1,
}

/// <summary>
/// One operator-declared host folder shared into a project's sandboxes as a
/// runtime mount (never copied, never baked into baselines).
/// </summary>
public sealed record ProjectSharedFolder
{
    /// <summary>Operator-chosen short name. Unique per project; used in the default sandbox path.</summary>
    public required string Name { get; init; }

    /// <summary>Canonical absolute host path shared into the sandbox.</summary>
    public required string HostPath { get; init; }

    /// <summary>Guest mount point. Defaults to <c>/shared/&lt;Name&gt;</c> at config resolution.</summary>
    public required string SandboxPath { get; init; }

    /// <summary>Requested access mode. Auditors always get read-only regardless of this value.</summary>
    public SharedFolderMode Mode { get; init; } = SharedFolderMode.ReadOnly;

    /// <summary>Phases receiving this mount. Default <see cref="SharedFolderPhases.All"/>.</summary>
    public SharedFolderPhases Phases { get; init; } = SharedFolderPhases.All;

    /// <summary>
    /// When true, a missing host path or an unsatisfiable provider fails the
    /// dispatch with an operator-visible error. When false, the mount is
    /// skipped for that sandbox (still reported in status/prompts as unavailable).
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>Write-concurrency policy. Default <see cref="SharedFolderWriterPolicy.Exclusive"/>.</summary>
    public SharedFolderWriterPolicy WriterPolicy { get; init; } = SharedFolderWriterPolicy.Exclusive;

    /// <summary>What waiters do when an exclusive write lease is held. Default <see cref="SharedFolderExclusiveFallback.Wait"/>.</summary>
    public SharedFolderExclusiveFallback ExclusiveFallback { get; init; } = SharedFolderExclusiveFallback.Wait;
}

/// <summary>Bounds for project shared-folder declarations.</summary>
public static class SharedFolderLimits
{
    /// <summary>Maximum shared folders accepted per project.</summary>
    public const int MaxSharesPerProject = 16;

    /// <summary>Maximum characters accepted in a share name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Maximum UTF-8 bytes accepted in one configured path.</summary>
    public const int MaxPathUtf8Bytes = 4096;
}

/// <summary>
/// Config-token vocabulary for <see cref="ProjectSharedFolder.Phases"/>.
/// Accepts <c>work</c>, <c>audit</c>, and <c>all</c> (case-insensitive).
/// </summary>
public static class ProjectSharedFolderPhaseNames
{
    public const string Work = "work";
    public const string Audit = "audit";
    public const string All = "all";

    /// <summary>Every phase token accepted in project configuration.</summary>
    public static readonly IReadOnlyList<string> Tokens = [Work, Audit, All];

    /// <summary>Normalises a configured token, or null when it is not a known phase.</summary>
    public static string? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var known in Tokens)
        {
            if (string.Equals(normalized, known, StringComparison.Ordinal))
                return known;
        }
        return null;
    }

    /// <summary>
    /// True when a folder declared with <paramref name="declared"/> is mounted
    /// into a sandbox running <paramref name="sandboxScope"/> (the same scope
    /// vocabulary as <see cref="ProjectSandboxSecretScopes"/>). The
    /// <c>work</c> declaration matches work, rework, merge, delegation, check,
    /// and planning sandboxes; <c>audit</c> matches audit-agent and audit-tool;
    /// <c>all</c> matches everything.
    /// </summary>
    public static bool MatchesScope(SharedFolderPhases declared, string sandboxScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxScope);
        if (declared == SharedFolderPhases.All)
            return true;
        var normalized = sandboxScope.Trim().ToLowerInvariant();
        var isAudit = string.Equals(normalized, ProjectSandboxSecretScopes.Audit, StringComparison.Ordinal)
            || string.Equals(normalized, ProjectSandboxSecretScopes.AuditAgent, StringComparison.Ordinal)
            || string.Equals(normalized, ProjectSandboxSecretScopes.AuditTool, StringComparison.Ordinal);
        return declared == SharedFolderPhases.Audit ? isAudit : !isAudit;
    }
}

/// <summary>
/// Pure mount/routing decisions over resolved <see cref="ProjectSharedFolder"/>
/// entries. Keeps the decision logic testable without I/O; filesystem and
/// concurrency effects live in <see cref="SharedFolderValidator"/> and
/// <see cref="SharedFolderWriterGate"/>.
/// </summary>
public static class SharedFolderPolicy
{
    /// <summary>
    /// Effective read-only flag for one folder in one sandbox. Auditors always
    /// get read-only mounts regardless of the configured mode so audit code
    /// can never mutate shared host state.
    /// </summary>
    public static bool EffectiveReadOnly(ProjectSharedFolder folder, bool isAudit)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return isAudit || folder.Mode == SharedFolderMode.ReadOnly;
    }

    /// <summary>True when <paramref name="folder"/> is mounted into <paramref name="sandboxScope"/>.</summary>
    public static bool AppliesToPhase(ProjectSharedFolder folder, string sandboxScope)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return ProjectSharedFolderPhaseNames.MatchesScope(folder.Phases, sandboxScope);
    }

    /// <summary>
    /// Builds the runtime <see cref="SandboxMount"/> for one folder. The
    /// read-only flag is enforced at the VM/mount level by the provider
    /// (Incus virtiofs <c>readonly</c>, bubblewrap <c>--ro-bind</c>), never
    /// merely by permissions inside the guest.
    /// </summary>
    public static SandboxMount ToSandboxMount(ProjectSharedFolder folder, bool isAudit)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new SandboxMount
        {
            SandboxPath = folder.SandboxPath,
            HostPath = folder.HostPath,
            ReadOnly = EffectiveReadOnly(folder, isAudit),
        };
    }

    /// <summary>
    /// Capability tags the sandbox provider must declare to serve the required
    /// folders applicable to this phase. Read-only shares need
    /// <see cref="SandboxCapabilities.HostFolderShares"/>; read-write shares
    /// additionally need <see cref="SandboxCapabilities.HostFolderSharesReadWrite"/>.
    /// </summary>
    public static IReadOnlyList<string> RequiredCapabilitiesForPhase(
        IReadOnlyList<ProjectSharedFolder> folders,
        string sandboxScope,
        bool isAudit)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var needsShares = false;
        var needsReadWrite = false;
        foreach (var folder in folders)
        {
            if (!folder.Required || !AppliesToPhase(folder, sandboxScope))
                continue;
            needsShares = true;
            if (!EffectiveReadOnly(folder, isAudit))
                needsReadWrite = true;
        }
        if (!needsShares)
            return [];
        return needsReadWrite
            ? [SandboxCapabilities.HostFolderShares, SandboxCapabilities.HostFolderSharesReadWrite]
            : [SandboxCapabilities.HostFolderShares];
    }

    /// <summary>
    /// Routing-time check: a project whose required shares cannot be satisfied
    /// by a provider must not be dispatched to it. Returns success plus an
    /// empty reason when every applicable required folder is satisfiable, or
    /// failure plus an operator-visible reason naming the folder, the
    /// provider, and the missing capability.
    /// </summary>
    public static (bool Ok, string Reason) CheckProvider(
        IReadOnlyList<ProjectSharedFolder> folders,
        string sandboxScope,
        bool isAudit,
        IReadOnlyList<string> providerCapabilities,
        string providerName)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(providerCapabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        static bool Has(IReadOnlyList<string> caps, string tag) =>
            caps.Any(c => string.Equals(c?.Trim(), tag, StringComparison.OrdinalIgnoreCase));
        foreach (var folder in folders)
        {
            if (!folder.Required || !AppliesToPhase(folder, sandboxScope))
                continue;
            if (!Has(providerCapabilities, SandboxCapabilities.HostFolderShares))
            {
                return (false,
                    $"Project shared folder '{folder.Name}' requires host folder shares, " +
                    $"but sandbox provider '{providerName}' does not declare capability " +
                    $"'{SandboxCapabilities.HostFolderShares}'.");
            }
            if (!EffectiveReadOnly(folder, isAudit)
                && !Has(providerCapabilities, SandboxCapabilities.HostFolderSharesReadWrite))
            {
                return (false,
                    $"Project shared folder '{folder.Name}' requires a read-write host folder share, " +
                    $"but sandbox provider '{providerName}' does not declare capability " +
                    $"'{SandboxCapabilities.HostFolderSharesReadWrite}'.");
            }
        }
        return (true, string.Empty);
    }
}

/// <summary>
/// Inputs for <see cref="SharedFolderValidator"/> load-time validation.
/// Delegates default to the live filesystem; tests inject fakes (for example
/// a symlink map) so escape probes stay hermetic.
/// </summary>
public sealed record SharedFolderValidationContext
{
    /// <summary>
    /// Canonical allow-list roots (the Incus <c>AllowedHostMountRoots</c>
    /// extended with the provider staging root). Empty rejects every host path.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>Orchestrator bare-repo root. Shares inside or containing it are rejected.</summary>
    public string? RepositoriesDirectory { get; init; }

    /// <summary>Orchestrator state-database path. Shares inside or containing it are rejected.</summary>
    public string? StateDatabasePath { get; init; }

    /// <summary>Guest workspace mount point shares must not overlap. Default <c>/work</c>.</summary>
    public string WorkspaceSandboxPath { get; init; } = "/work";

    /// <summary>Resolves a host path to its canonical form (absolute, symlinks resolved). Null uses the default walk.</summary>
    public Func<string, string>? CanonicalizeHostPath { get; init; }

    /// <summary>Host directory probe. Null uses <see cref="Directory.Exists"/>.</summary>
    public Func<string, bool>? DirectoryExists { get; init; }

    /// <summary>Host file probe. Null uses <see cref="File.Exists"/>.</summary>
    public Func<string, bool>? FileExists { get; init; }

    /// <summary>Operator home directory. Null uses the current user's profile directory.</summary>
    public Func<string?>? HomeDirectory { get; init; }
}

/// <summary>
/// Load-time validation for project shared folders: canonical absolute paths,
/// allow-list containment after symlink resolution, sensitive-location and
/// workspace-overlap rejection, and duplicate/overlap rejection. Throws
/// <see cref="InvalidOperationException"/> with an operator-actionable message
/// naming the project and folder; a missing host path fails the same way
/// (operator-visible error, never a silent skip).
/// </summary>
public static class SharedFolderValidator
{
    private static readonly string[] SensitiveDirSegments =
        ["credentials", "credential", "secrets", "secret"];

    private static readonly string[] ProtectedGuestRoots =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64",
        "/proc", "/root", "/sbin", "/sys", "/usr",
    ];

    /// <summary>Validates every folder for one project. Returns the canonical host paths keyed by folder name.</summary>
    public static IReadOnlyDictionary<string, string> ValidateAll(
        string projectId,
        IReadOnlyList<ProjectSharedFolder> folders,
        SharedFolderValidationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(context);
        if (folders.Count > SharedFolderLimits.MaxSharesPerProject)
        {
            throw new InvalidOperationException(
                $"Project '{projectId}' declares {folders.Count} shared folders; " +
                $"at most {SharedFolderLimits.MaxSharesPerProject} are accepted.");
        }
        var canonicalHosts = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenSandboxPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenHostPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var canonicalize = context.CanonicalizeHostPath ?? DefaultCanonicalize;
        var dirExists = context.DirectoryExists ?? Directory.Exists;
        var fileExists = context.FileExists ?? File.Exists;
        foreach (var folder in folders)
        {
            if (folder is null)
                throw new InvalidOperationException($"Project '{projectId}' has a null shared-folder entry.");
            var folderName = folder.Name ?? string.Empty;
            var where = $"projects[{projectId}].SharedFolders[{folderName}]";
            ValidateName(projectId, folder);
            if (!seenNames.Add(folderName))
                throw new InvalidOperationException($"Project '{projectId}' has a duplicate shared-folder name '{folderName}'.");
            if (string.IsNullOrWhiteSpace(folder.HostPath) || !Path.IsPathRooted(folder.HostPath))
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath must be an absolute host path (got '{folder.HostPath}').");
            }
            RejectBoundedText(projectId, where, folder.HostPath, "HostPath");
            var sandboxPath = NormalizeSandboxPath(projectId, where, folder.SandboxPath);
            if (seenSandboxPaths.TryGetValue(sandboxPath, out var clash))
            {
                throw new InvalidOperationException(
                    $"Project '{projectId}' has duplicate shared-folder sandbox path '{sandboxPath}' " +
                    $"('{clash}' and '{folderName}').");
            }
            seenSandboxPaths.Add(sandboxPath, folderName);
            ValidateSandboxPath(projectId, where, sandboxPath, context.WorkspaceSandboxPath);

            string canonicalHost;
            try
            {
                canonicalHost = canonicalize(folder.HostPath);
            }
            catch (FileNotFoundException ex)
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath '{folder.HostPath}' does not exist.", ex);
            }
            if (!Path.IsPathRooted(canonicalHost))
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath '{folder.HostPath}' did not resolve to an absolute path.");
            }
            if (!dirExists(canonicalHost) && !fileExists(canonicalHost))
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath '{folder.HostPath}' does not exist.");
            }
            if (!dirExists(canonicalHost) && folder.Mode == SharedFolderMode.ReadWrite)
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath '{folder.HostPath}' is an individual file: read-write shares must " +
                    "be directories (Incus virtiofs cannot safely expose a writable host file); " +
                    "mount a directory or use Mode ReadOnly.");
            }
            RejectSensitiveHostPath(projectId, where, canonicalHost, context);
            RejectAllowListEscape(projectId, where, folder.HostPath, canonicalHost, context, canonicalize);
            if (seenHostPaths.TryGetValue(canonicalHost, out var hostClash))
            {
                throw new InvalidOperationException(
                    $"Project '{projectId}' mounts host path '{canonicalHost}' twice " +
                    $"('{hostClash}' and '{folderName}').");
            }
            seenHostPaths.Add(canonicalHost, folderName);
            canonicalHosts.Add(folderName, canonicalHost);
        }
        RejectOverlappingSandboxPaths(projectId, folders);
        return canonicalHosts;
    }

    /// <summary>
    /// Non-throwing per-folder status used by the project API: reports
    /// existence, allow-list, and sensitive-path verdicts without failing
    /// the request.
    /// </summary>
    public static SharedFolderStatus ResolveStatus(
        string projectId,
        ProjectSharedFolder folder,
        SharedFolderValidationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var canonical = ValidateAll(projectId, [folder], context);
            return new SharedFolderStatus(
                folder.Name, folder.HostPath, canonical[folder.Name], folder.SandboxPath,
                folder.Mode, folder.Phases, folder.Required, folder.WriterPolicy, folder.ExclusiveFallback,
                Exists: true, Allowed: true, Error: null);
        }
        catch (InvalidOperationException ex)
        {
            var missing = ex.InnerException is FileNotFoundException
                || ex.Message.Contains("does not exist", StringComparison.Ordinal);
            return new SharedFolderStatus(
                folder.Name, folder.HostPath, null, folder.SandboxPath,
                folder.Mode, folder.Phases, folder.Required, folder.WriterPolicy, folder.ExclusiveFallback,
                Exists: !missing, Allowed: false, Error: ex.Message);
        }
    }

    private static void ValidateName(string projectId, ProjectSharedFolder folder)
    {
        var name = folder.Name;
        var where = $"projects[{projectId}].SharedFolders";
        if (string.IsNullOrWhiteSpace(name) || name.Length > SharedFolderLimits.MaxNameLength)
        {
            throw new InvalidOperationException(
                $"{where} entries need a Name of 1..{SharedFolderLimits.MaxNameLength} characters.");
        }
        if (!char.IsLetterOrDigit(name[0]))
            throw new InvalidOperationException($"{where} name '{name}' must start with a letter or digit.");
        foreach (var ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.'))
            {
                throw new InvalidOperationException(
                    $"{where} name '{name}' may only contain letters, digits, '-', '_' or '.'.");
            }
        }
    }

    private static void RejectBoundedText(string projectId, string where, string value, string field)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(value);
        if (bytes > SharedFolderLimits.MaxPathUtf8Bytes)
        {
            throw new InvalidOperationException(
                $"{where}.{field} exceeds {SharedFolderLimits.MaxPathUtf8Bytes} UTF-8 bytes.");
        }
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                throw new InvalidOperationException(
                    $"{where}.{field} must not contain control characters.");
            }
        }
    }

    /// <summary>
    /// Normalises a configured guest mount path: must be absolute, collapses
    /// duplicate slashes, strips a trailing slash, and rejects control
    /// characters and over-long values.
    /// </summary>
    public static string NormalizeSandboxPath(string projectId, string where, string? sandboxPath)
    {
        if (string.IsNullOrWhiteSpace(sandboxPath) || sandboxPath[0] != '/')
        {
            throw new InvalidOperationException(
                $"{where}.SandboxPath must be an absolute guest path (got '{sandboxPath}').");
        }
        var builder = new System.Text.StringBuilder(sandboxPath.Length);
        var previousSlash = false;
        foreach (var ch in sandboxPath.Trim())
        {
            if (char.IsControl(ch))
            {
                throw new InvalidOperationException(
                    $"{where}.SandboxPath must not contain control characters.");
            }
            if (ch == '/')
            {
                if (previousSlash)
                    continue;
                previousSlash = true;
            }
            else
            {
                previousSlash = false;
            }
            builder.Append(ch);
        }
        var collapsed = builder.ToString();
        if (collapsed.Length > 1)
            collapsed = collapsed.TrimEnd('/');
        if (string.IsNullOrEmpty(collapsed))
            collapsed = "/";
        RejectBoundedText(projectId, where, collapsed, "SandboxPath");
        return collapsed;
    }

    private static void ValidateSandboxPath(
        string projectId, string where, string sandboxPath, string workspace)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(workspace) ? "/work" : workspace.Trim();
        if (sandboxPath == "/" || sandboxPath == workspaceRoot
            || IsGuestDescendant(sandboxPath, workspaceRoot))
        {
            throw new InvalidOperationException(
                $"{where}.SandboxPath '{sandboxPath}' overlaps the workspace mount '{workspaceRoot}'.");
        }
        foreach (var root in ProtectedGuestRoots)
        {
            if (sandboxPath == root || IsGuestDescendant(sandboxPath, root))
            {
                throw new InvalidOperationException(
                    $"{where}.SandboxPath '{sandboxPath}' overlaps protected guest path '{root}'.");
            }
        }
        if (sandboxPath == "/run" || IsGuestDescendant(sandboxPath, "/run")
            || sandboxPath == "/tmp" || IsGuestDescendant(sandboxPath, "/tmp"))
        {
            throw new InvalidOperationException(
                $"{where}.SandboxPath '{sandboxPath}' overlaps a provider-reserved guest path.");
        }
    }

    private static bool IsGuestDescendant(string path, string root) =>
        path.Length > root.Length
        && path.StartsWith(root, StringComparison.Ordinal)
        && path[root.Length] == '/';

    private static void RejectSensitiveHostPath(
        string projectId, string where, string canonicalHost, SharedFolderValidationContext context)
    {
        var home = context.HomeDirectory?.Invoke()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home))
        {
            foreach (var leaf in (string[])[Path.Combine(home, ".ssh"), Path.Combine(home, ".codeybox"), Path.Combine(home, ".gnupg")])
            {
                if (HostPathPolicy.IsWithinDirectory(canonicalHost, leaf)
                    || HostPathPolicy.IsWithinDirectory(leaf, canonicalHost))
                {
                    throw new InvalidOperationException(
                        $"{where}.HostPath '{canonicalHost}' is a sensitive location and cannot be shared.");
                }
            }
        }
        var segments = canonicalHost.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            foreach (var sensitive in SensitiveDirSegments)
            {
                if (string.Equals(segment, sensitive, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{where}.HostPath '{canonicalHost}' looks like a credential/secret directory and cannot be shared.");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(context.RepositoriesDirectory)
            && Path.IsPathRooted(context.RepositoriesDirectory))
        {
            var repos = context.RepositoriesDirectory;
            if (HostPathPolicy.IsWithinDirectory(canonicalHost, repos)
                || HostPathPolicy.IsWithinDirectory(repos, canonicalHost))
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath '{canonicalHost}' overlaps the orchestrator's repository directory and cannot be shared.");
            }
        }
        if (!string.IsNullOrWhiteSpace(context.StateDatabasePath))
        {
            var stateDir = Path.GetDirectoryName(Path.GetFullPath(context.StateDatabasePath));
            if (!string.IsNullOrWhiteSpace(stateDir) && Path.IsPathRooted(stateDir)
                && (HostPathPolicy.IsWithinDirectory(canonicalHost, stateDir)
                    || string.Equals(
                        Path.GetFullPath(canonicalHost).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(context.StateDatabasePath).TrimEnd(Path.DirectorySeparatorChar),
                        HostPathPolicy.HostPathComparison)))
            {
                throw new InvalidOperationException(
                    $"{where}.HostPath '{canonicalHost}' overlaps the orchestrator's state directory and cannot be shared.");
            }
        }
    }

    private static void RejectAllowListEscape(
        string projectId,
        string where,
        string configuredHostPath,
        string canonicalHost,
        SharedFolderValidationContext context,
        Func<string, string> canonicalize)
    {
        var resolvedRoots = new List<string>();
        foreach (var root in context.AllowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                continue;
            try
            {
                resolvedRoots.Add(canonicalize(root));
            }
            catch (FileNotFoundException)
            {
                continue;
            }
        }
        if (resolvedRoots.Count == 0)
        {
            throw new InvalidOperationException(
                $"{where}.HostPath '{configuredHostPath}' is outside the allowed host-mount roots " +
                $"(no allow-list roots are configured; sharing is disabled).");
        }
        if (!resolvedRoots.Any(root => HostPathPolicy.IsWithinDirectory(canonicalHost, root)))
        {
            throw new InvalidOperationException(
                $"{where}.HostPath '{configuredHostPath}' is outside the allowed host-mount roots.");
        }
    }

    private static void RejectOverlappingSandboxPaths(string projectId, IReadOnlyList<ProjectSharedFolder> folders)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            for (var j = i + 1; j < folders.Count; j++)
            {
                var left = folders[i].SandboxPath;
                var right = folders[j].SandboxPath;
                if (string.Equals(left, right, StringComparison.Ordinal)
                    || IsGuestDescendant(left, right) || IsGuestDescendant(right, left))
                {
                    throw new InvalidOperationException(
                        $"Project '{projectId}' shared folders '{folders[i].Name}' and '{folders[j].Name}' " +
                        $"have overlapping sandbox paths ('{left}' vs '{right}').");
                }
            }
        }
    }

    private static string DefaultCanonicalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            throw new FileNotFoundException("Cannot canonicalize a path that does not exist.", fullPath);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("Host path has no filesystem root.");
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"Unable to resolve host symlink '{current}'.");
            }
        }
        return Path.GetFullPath(current);
    }
}

/// <summary>Resolved visibility for one configured share, served by the project API.</summary>
public sealed record SharedFolderStatus(
    string Name,
    string? ConfiguredHostPath,
    string? ResolvedHostPath,
    string SandboxPath,
    SharedFolderMode Mode,
    SharedFolderPhases Phases,
    bool Required,
    SharedFolderWriterPolicy WriterPolicy,
    SharedFolderExclusiveFallback ExclusiveFallback,
    bool Exists,
    bool Allowed,
    string? Error);

/// <summary>
/// Short generated agent-context section listing the shared folders mounted
/// into the sandbox, appended to work/rework prompts so agents know the
/// reference material exists. Folder names and sandbox paths are validated
/// at config load (letters/digits plus <c>-_. </c>, absolute guest paths
/// without control characters), so they render verbatim.
/// </summary>
public static class SharedFolderPromptSection
{
    /// <summary>Maximum folders listed before the section truncates with a count line.</summary>
    public const int MaxListedFolders = 16;

    /// <summary>
    /// Builds the section for the folders mounted into this sandbox. Returns
    /// empty when no folder applies to <paramref name="sandboxScope"/>.
    /// </summary>
    public static string Build(
        IReadOnlyList<ProjectSharedFolder> folders,
        string sandboxScope,
        bool isAudit)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxScope);
        var applicable = folders
            .Where(f => f is not null && SharedFolderPolicy.AppliesToPhase(f, sandboxScope))
            .Take(MaxListedFolders + 1)
            .ToList();
        if (applicable.Count == 0)
            return string.Empty;
        var shown = applicable.Take(MaxListedFolders).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Shared reference folders");
        sb.Append("The operator mounted host folders into this sandbox");
        sb.Append(isAudit
            ? " as read-only mounts (audit sandboxes always get read-only mounts, even for folders configured read-write)"
            : " (read-only unless marked read-write)");
        sb.AppendLine(". Prefer reading them over reconstructing their contents:");
        foreach (var folder in shown)
        {
            var readOnly = SharedFolderPolicy.EffectiveReadOnly(folder, isAudit);
            sb.Append("- `").Append(folder.SandboxPath).Append("` (").Append(folder.Name).Append(", ");
            sb.Append(readOnly ? "read-only" : "read-write");
            if (!readOnly)
                sb.Append(" — writes here modify host files");
            sb.AppendLine(")");
        }
        if (applicable.Count > shown.Count)
            sb.AppendLine($"- …and {applicable.Count - shown.Count} more.");
        return sb.ToString();
    }
}

/// <summary>
/// Per-folder exclusive write gate: at most one running item holds the
/// read-write mount for an <see cref="SharedFolderWriterPolicy.Exclusive"/>
/// folder. <see cref="SharedFolderWriterPolicy.Shared"/> folders skip the
/// gate (concurrent writers allowed, documented). Register as a host
/// singleton so concurrent pipeline workers coordinate through one instance;
/// inject it (never statics) so tests isolate per fixture.
/// </summary>
public sealed class SharedFolderWriterGate
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, SemaphoreSlim> _leases = new(StringComparer.Ordinal);

    /// <summary>Gate key for one project's folder.</summary>
    public static string KeyFor(string projectId, string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        return projectId + "\0" + folderName;
    }

    /// <summary>
    /// Acquires the write posture for one folder. Shared folders return a
    /// read-write lease immediately. Exclusive folders with a
    /// <see cref="SharedFolderExclusiveFallback.ReadOnly"/> fallback return a
    /// read-only downgraded lease when the write lease is held; with
    /// <see cref="SharedFolderExclusiveFallback.Wait"/> this waits up to
    /// <paramref name="waitTimeout"/> (default 5 minutes) and throws
    /// <see cref="TimeoutException"/> naming the folder when the holder does
    /// not finish in time, so the caller requeues instead of piling up.
    /// </summary>
    public async Task<SharedFolderWriterLease> AcquireAsync(
        string key,
        SharedFolderWriterPolicy policy,
        SharedFolderExclusiveFallback fallback,
        TimeSpan? waitTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (policy == SharedFolderWriterPolicy.Shared)
            return SharedFolderWriterLease.ForWrite(key, this, ownsLease: false);
        var gate = GetGate(key);
        if (fallback == SharedFolderExclusiveFallback.ReadOnly)
        {
            var taken = await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
            return taken
                ? SharedFolderWriterLease.ForWrite(key, this, ownsLease: true)
                : SharedFolderWriterLease.DowngradedReadOnly(key);
        }
        var timeout = waitTimeout ?? TimeSpan.FromMinutes(5);
        bool acquired;
        try
        {
            acquired = await gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"Cancelled while waiting for exclusive write access to shared folder '{key}'.", ex);
        }
        if (!acquired)
        {
            throw new TimeoutException(
                $"Timed out after {timeout.TotalSeconds:N0}s waiting for exclusive write access to shared folder '{key}'; " +
                "another running item still holds it.");
        }
        return SharedFolderWriterLease.ForWrite(key, this, ownsLease: true);
    }

    private SemaphoreSlim GetGate(string key)
    {
        lock (_sync)
        {
            if (!_leases.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _leases.Add(key, gate);
            }
            return gate;
        }
    }

    internal void Release(string key)
    {
        SemaphoreSlim? gate;
        lock (_sync)
            _leases.TryGetValue(key, out gate);
        try
        {
            gate?.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}

/// <summary>Write posture for one folder held by one sandbox creation.</summary>
public sealed class SharedFolderWriterLease : IAsyncDisposable
{
    private SharedFolderWriterGate? _gate;
    private readonly string _key;
    private bool _disposed;

    private SharedFolderWriterLease(string key, SharedFolderWriterGate? gate, bool readWrite, bool downgraded)
    {
        _key = key;
        _gate = gate;
        ReadWrite = readWrite;
        DowngradedToReadOnly = downgraded;
    }

    internal static SharedFolderWriterLease ForWrite(string key, SharedFolderWriterGate gate, bool ownsLease) =>
        new(key, ownsLease ? gate : null, readWrite: true, downgraded: false);

    internal static SharedFolderWriterLease DowngradedReadOnly(string key) =>
        new(key, null, readWrite: false, downgraded: true);

    /// <summary>True when the sandbox may mount the folder read-write.</summary>
    public bool ReadWrite { get; }

    /// <summary>True when an exclusive folder was downgraded to read-only because the write lease was held.</summary>
    public bool DowngradedToReadOnly { get; }

    /// <summary>Releases the exclusive write lease, if held.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        var gate = Interlocked.Exchange(ref _gate, null);
        if (gate is not null)
            gate.Release(_key);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
