namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Typed refusal to provision (or resume) a Tart sandbox in Softnet mode
/// when the Softnet packet filter is unusable or its policy cannot be built.
/// Fail-closed by construction: callers must not fall back to NAT — the only
/// recovery is the named <see cref="SetupStep"/>.
/// </summary>
public sealed class TartSoftnetUnavailableException : InvalidOperationException
{
    public TartSoftnetUnavailableException(string errorClass, string setupStep, string detail)
        : base($"Tart Softnet unavailable (errorClass={errorClass}): {detail} Setup step: {setupStep}")
    {
        ErrorClass = errorClass;
        SetupStep = setupStep;
        Detail = detail;
    }

    /// <summary>Stable machine-readable class: softnet-missing, softnet-denied, softnet-policy-unknown.</summary>
    public string ErrorClass { get; }

    /// <summary>Operator action that restores Softnet mode (also logged and surfaced to the host verifier).</summary>
    public string SetupStep { get; }

    /// <summary>Human-readable detail (probe output tail or policy cause).</summary>
    public string Detail { get; }
}
