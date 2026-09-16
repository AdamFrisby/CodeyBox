using System.Diagnostics;

namespace CodeyBox.Git;

/// <summary>
/// Per-invocation git configuration for host-side git children that operate
/// on host-owned bare repositories.
/// </summary>
public static class LocalGitInvocation
{
    /// <summary>
    /// Adds the <c>-c</c> configuration every host-side git child needs:
    /// hooks stay disabled (the host owns orchestration decisions, not repo
    /// hooks), and the ambient <c>safe.bareRepository</c> hardening is relaxed
    /// for this invocation only.
    ///
    /// Hardened hosts — including this repository's own harness and audit
    /// sandboxes — export <c>safe.bareRepository=explicit</c>, under which git
    /// refuses to touch a bare repository discovered via the working
    /// directory. The repositories these children touch are created, owned,
    /// and scrubbed by the host (<c>SanitizeBareRepositoryConfig</c> rewrites
    /// foreign repo config to a minimal safe set, alternates are sanitized,
    /// and hooks are disabled here), so the ownership check buys nothing and
    /// only breaks host operations. The relaxation is scoped to the child
    /// argv — no persistent configuration is changed.
    /// </summary>
    /// <param name="psi">Child process configuration to extend. Must not be null.</param>
    /// <param name="disabledHooksPath">
    /// Directory git must use as the hooks path (an empty host-owned
    /// directory). Must not be null.
    /// </param>
    public static void ApplyHostConfig(ProcessStartInfo psi, string disabledHooksPath)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(disabledHooksPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"core.hooksPath={disabledHooksPath}");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
    }
}
