using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Typed adapter errors. Thrown at the sink that detects them (target
/// validation, source handoff, provider HTTP mapping, artifact ingestion) so
/// the shared lifecycle can fail closed without silent passes.
/// </summary>
public abstract class UnityBuildException(string message, Exception? inner = null)
    : ExternalBuildException(message, inner);

/// <summary>Operator-approved target, editor, platform, or configuration rejected.</summary>
public sealed class UnityBuildTargetRejectedException(string detail)
    : UnityBuildException(detail);

/// <summary>Frozen-candidate source handoff cannot establish exact immutable correlation.</summary>
public sealed class UnityBuildSourceException(string detail)
    : UnityBuildException(detail);

/// <summary>Provider rejected the caller's credentials or the credential provider has none.</summary>
public sealed class UnityBuildAuthException(string detail, Exception? inner = null)
    : UnityBuildException(detail, inner);

/// <summary>Provider is reachable but cannot serve (5xx, 422, unsupported editor/platform).</summary>
public sealed class UnityBuildUnavailableException(string detail, Exception? inner = null)
    : UnityBuildException(detail, inner);

/// <summary>Artifact listing/download failed safely (expired link, bounds, digest, host).</summary>
public sealed class UnityBuildArtifactException(string detail, Exception? inner = null)
    : UnityBuildException(detail, inner);
