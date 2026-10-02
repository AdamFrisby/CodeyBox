using CodeyBox.Majordomo;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Runtime override for the majordomo autonomy mode. The configured
/// <see cref="MajordomoServerOptions.Mode"/> remains the default; the
/// operator panel flips this switch without a config reload, and clearing
/// it returns the fleet to the configured mode. A singleton: one fleet,
/// one switch.
/// </summary>
public sealed class MajordomoAutonomySwitch
{
    private readonly Lock _gate = new();
    private MajordomoAutonomyMode? _override;

    /// <summary>Raised after the override changes; carries the effective mode.</summary>
    public event Action<MajordomoAutonomyMode>? Changed;

    /// <summary>The operator override, or null to follow configuration.</summary>
    public MajordomoAutonomyMode? Override
    {
        get
        {
            lock (_gate)
                return _override;
        }
    }

    /// <summary>Resolves the effective mode: the override when set, else the configured mode.</summary>
    public MajordomoAutonomyMode Resolve(MajordomoAutonomyMode configured)
    {
        lock (_gate)
            return _override ?? configured;
    }

    /// <summary>True when an operator override is in force (rather than configuration).</summary>
    public bool IsOverridden
    {
        get
        {
            lock (_gate)
                return _override.HasValue;
        }
    }

    /// <summary>Sets the operator override to exactly <paramref name="mode"/>.</summary>
    public MajordomoAutonomyMode Set(MajordomoAutonomyMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, $"mode must be a defined {nameof(MajordomoAutonomyMode)}");
        MajordomoAutonomyMode effective;
        lock (_gate)
            effective = (_override = mode).Value;
        Changed?.Invoke(effective);
        return effective;
    }

    /// <summary>Clears the override so the configured mode applies again.</summary>
    public MajordomoAutonomyMode Clear(MajordomoAutonomyMode configured)
    {
        lock (_gate)
            _override = null;
        Changed?.Invoke(configured);
        return configured;
    }
}
