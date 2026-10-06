using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// A resolved set of standalone shader targets ready for one validation
/// invocation, produced by <see cref="ShaderValidationSupport.ResolveTargets"/>.
/// </summary>
/// <param name="Targets">
/// Validated targets in operator configuration order (duplicates removed).
/// Every target carries an explicit resolved stage.
/// </param>
/// <param name="SingleStage">
/// The shared stage when every target resolved to the same stage — the only
/// shape a single <c>-S</c> flag can express — so the backend passes it
/// explicitly. Null for mixed-stage sets, which rely on per-file canonical
/// stage extensions instead (enforced at resolve time).
/// </param>
public sealed record ShaderTargetSet(IReadOnlyList<ShaderTarget> Targets, string? SingleStage);
