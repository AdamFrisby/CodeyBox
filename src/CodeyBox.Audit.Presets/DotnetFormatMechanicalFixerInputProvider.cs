using CodeyBox.Audit;
using CodeyBox.Audit.Presets.Presets;
using CodeyBox.Core;

namespace CodeyBox.Audit.Presets;

public sealed class DotnetFormatMechanicalFixerInputProvider : IMechanicalFixerInputProvider
{
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
                auditor is not ILanguagePresetCommandSource languageSource ||
                !languageSource.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
                argvProvider.Argv.Count == 0)
            {
                continue;
            }

            return new DotnetFormatMechanicalFixerInput(
                argvProvider.Argv,
                languageSource.MarkerScript);
        }

        return null;
    }
}

public sealed record DotnetFormatMechanicalFixerInput(
    IReadOnlyList<string> FormatCheckArgv,
    string? ProjectMarkerScript) : IMechanicalFixerInput;
