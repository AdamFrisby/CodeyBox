using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

public sealed class DotnetFormatMechanicalFixerInputProvider : IMechanicalFixerInputProvider
{
    /// <summary>
    /// Fallback marker discovery for custom <c>csharp:format-check</c>
    /// auditors that do not expose an <see cref="IAuditorLanguageContext"/>.
    /// Lists every directory that contains a top-level <c>*.csproj</c>,
    /// preserving the behaviour of the bundled csharp preset's marker globs.
    /// </summary>
    internal const string DefaultCsharpMarkerScript =
        "find . -type f \\( -name '*.csproj' -o -name '*.sln' -o -name '*.slnx' \\) " +
        "-not -path './.git/*' -printf '%h\\n' | sort -u";

    public IReadOnlyList<IMechanicalFixerInput> BuildInputs(IReadOnlyList<IAuditor> auditors)
    {
        var command = ResolveCommand(auditors);
        return command is null ? [] : [command];
    }

    internal static DotnetFormatMechanicalFixerInput? ResolveCommand(IReadOnlyList<IAuditor> auditors)
    {
        foreach (var auditor in auditors)
        {
            if (!auditor.Name.Equals(DotnetFormatMechanicalFixer.FormatCheckAuditorName, StringComparison.OrdinalIgnoreCase) ||
                auditor is not IShellAuditorArgvProvider argvProvider ||
                argvProvider.Argv.Count == 0)
            {
                continue;
            }

            var languageSource = auditor as IAuditorLanguageContext;
            if (languageSource is not null &&
                !languageSource.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var markerScript = languageSource is null || string.IsNullOrWhiteSpace(languageSource.MarkerScript)
                ? DefaultCsharpMarkerScript
                : languageSource.MarkerScript;

            return new DotnetFormatMechanicalFixerInput(
                argvProvider.Argv,
                markerScript);
        }

        return null;
    }
}

internal sealed record DotnetFormatMechanicalFixerInput(
    IReadOnlyList<string> FormatCheckArgv,
    string? ProjectMarkerScript) : IMechanicalFixerInput;
