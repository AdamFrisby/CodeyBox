namespace CodeyBox.PluginSdk;

/// <summary>
/// Declares a single external binary the plugin invokes at runtime (inside a
/// sandbox or on the host). Repeatable: apply once per binary.
///
/// <para>This is plugin <em>metadata</em>, not an instruction. The host
/// validates the declared names against a strict character allowlist,
/// contributes the validated requirements of <em>enabled</em> plugins to the
/// sandbox baseline identity and bake (install + verification steps the host
/// constructs itself), and reports unmet requirements at startup. A disabled
/// plugin's declarations change nothing. The host never executes this
/// attribute's strings as shell.</para>
///
/// <para>Keep <see cref="AptPackage"/> and <see cref="InstallHint"/> accurate:
/// an invalid declaration fails closed — the plugin is skipped at load and
/// the operator is told why.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CodeyBoxPluginRequiresToolAttribute : Attribute
{
    /// <summary>
    /// Bare executable name the plugin invokes, e.g. <c>"dotnet"</c> or
    /// <c>"pytest"</c>. Must be a bare name (no path separators, no
    /// whitespace, no shell metacharacters); the host rejects anything else.
    /// </summary>
    public string Binary { get; }

    /// <summary>
    /// Optional Debian package name the host installs into sandbox baselines
    /// for enabled plugins (host-constructed <c>apt-get install</c>; the
    /// plugin supplies only the package name). Omit when the tool is not
    /// apt-installable — the host still verifies its presence at bake time
    /// and the operator provisions it via their own baseline steps.
    /// </summary>
    public string? AptPackage { get; set; }

    /// <summary>
    /// Optional operator-facing guidance shown in startup warnings and bake
    /// failures (e.g. where to get the tool when it is not apt-installable).
    /// Display text only: length-capped, control characters stripped, never
    /// executed.
    /// </summary>
    public string? InstallHint { get; set; }

    public CodeyBoxPluginRequiresToolAttribute(string binary)
    {
        Binary = binary;
    }
}
