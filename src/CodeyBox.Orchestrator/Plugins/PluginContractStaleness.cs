using System.Reflection;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Detects the stale-assembly failure mode: a plugin built against a different
/// version of the host contracts (<c>CodeyBox.Core</c>/<c>CodeyBox.PluginSdk</c>)
/// than the running host. The CLR surfaces this as assembly-bind or type-load
/// failures naming the host contract assembly — a predictable operational error
/// (typically a host rebuild that did not rebuild the plugin projects) that
/// deserves its own message, not a generic "load failed".
///
/// <para>Pure classifier over exception objects: it never loads anything.</para>
/// </summary>
internal static class PluginContractStaleness
{
    /// <summary>
    /// Operator-facing explanation used wherever a stale-contracts failure is reported.
    /// </summary>
    public const string OperatorMessage =
        "was built against a different version of the host contracts " +
        "(CodeyBox.Core/CodeyBox.PluginSdk); rebuild the plugin against the current host";

    /// <summary>
    /// Returns true when <paramref name="ex"/> (or anything it wraps, including
    /// <see cref="ReflectionTypeLoadException.LoaderExceptions"/>) looks like a
    /// host-contract bind failure rather than a generic load failure.
    /// </summary>
    public static bool IsStaleContractFailure(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ReflectionTypeLoadException reflectionTypeLoad &&
                reflectionTypeLoad.LoaderExceptions.Any(IsStaleContractFailure))
            {
                return true;
            }

            if (current is FileLoadException
                or FileNotFoundException
                or BadImageFormatException
                or TypeLoadException
                or MissingMethodException
                or MissingFieldException
                or MissingMemberException)
            {
                if (MentionsHostContracts(current.Message))
                    return true;
            }
        }

        return false;
    }

    private static bool MentionsHostContracts(string? message) =>
        !string.IsNullOrEmpty(message) &&
        (message.Contains("CodeyBox.Core", StringComparison.Ordinal) ||
         message.Contains("CodeyBox.PluginSdk", StringComparison.Ordinal));
}
