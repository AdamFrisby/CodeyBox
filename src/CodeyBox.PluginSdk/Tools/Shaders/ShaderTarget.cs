using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// One validated standalone shader target: a repository-relative shader file
/// plus the canonical shader stage it validates as. Produced by
/// <see cref="ShaderValidationSupport.ResolveTargets"/> — construct through
/// that seam so stage resolution (explicit suffix, canonical extension,
/// configured default) and the single-stage/mixed-stage invocation rule stay
/// in one place for every backend in this family.
/// </summary>
/// <param name="Path">
/// Repository-relative shader file path as it travels to the tool argv
/// (forward slashes, no leading <c>./</c>, contained in the worktree).
/// </param>
/// <param name="Stage">
/// Canonical lowercase stage name (e.g. <c>"vert"</c>, <c>"frag"</c>) from
/// <see cref="ShaderValidationSupport.Stages"/>.
/// </param>
/// <param name="StageFromExtension">
/// True when the stage was read from the file's canonical stage extension
/// rather than an explicit <c>path:stage</c> suffix or the configured
/// default. Mixed-stage invocations can only express per-file stages through
/// extensions, so this flag drives that rule.
/// </param>
public sealed record ShaderTarget(string Path, string Stage, bool StageFromExtension);
