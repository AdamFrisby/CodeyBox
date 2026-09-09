using CodeyBox.Core;

namespace CodeyBox.Tests;

internal static class CredentialMaterialisationTestHelper
{
    public static bool IsStdinMaterialisation(SandboxExec exec, string homeRelativePath) =>
        exec.Argv.Count >= 9
        && exec.Argv[0] == "bash"
        && exec.Argv[1] == "-c"
        && exec.Argv[4] == "$HOME"
        && exec.Argv[5] == homeRelativePath
        && exec.Stdin is not null;

    public static bool IsEnvironmentMaterialisation(
        SandboxExec exec,
        string environmentVariable,
        string homeRelativePath) =>
        exec.Argv.Count >= 3
        && exec.Argv[0] == "bash"
        && exec.Argv[1] == "-c"
        && exec.Argv[2].Contains(environmentVariable, StringComparison.Ordinal)
        && exec.Argv[2].Contains(homeRelativePath, StringComparison.Ordinal)
        && exec.Stdin is null;
}
