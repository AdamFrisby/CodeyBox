using System.Runtime.CompilerServices;

namespace CodeyBox.Cli.Tests;

/// <summary>
/// Keeps the CLI tests from resolving the developer's real CodeyBox configuration.
///
/// <para><c>ConfigResolver.ConfigDir</c> falls back to <c>%APPDATA%/codeybox</c>
/// whenever <c>CODEYBOX_CLI_CONFIG_DIR</c> is unset. On a machine where the CLI has
/// been configured that directory holds a real API key, so any test asserting that
/// no key is configured instead observes the host's key: the command succeeds,
/// returns 0, and the assertion on a non-zero exit code fails. The same tests pass
/// on a clean machine, which is why this presents as an environment-dependent
/// failure rather than a consistent one.</para>
///
/// <para><see cref="Reset"/> exists because tests that override the variable must
/// restore it to this isolated directory, not to null. Restoring null re-exposes
/// the host configuration to every test that runs afterwards in the same process.</para>
/// </summary>
internal static class CliTestConfigIsolation
{
    internal static string IsolatedConfigDir { get; } =
        Path.Combine(Path.GetTempPath(), "codeybox-cli-tests-config");

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(IsolatedConfigDir);
        var configFile = Path.Combine(IsolatedConfigDir, "config.json");
        if (File.Exists(configFile)) File.Delete(configFile);
        Reset();
    }

    /// <summary>Restores the config directory to the isolated one. Use instead of setting null.</summary>
    internal static void Reset() =>
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", IsolatedConfigDir);
}
