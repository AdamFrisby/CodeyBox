using System.Diagnostics;
using CodeyBox.OpenStackSandboxPlugin;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Harness;

/// <summary>
/// <c>openstack-smoke</c> subcommand: with the <c>openstack</c> sandbox plugin
/// enabled and credentials present, acquires one OpenStack sandbox, runs
/// <c>uname -a</c> plus a file stage round-trip, and disposes it — printing
/// per-step timings. The sandbox is disposed on every path (the smoke
/// runner owns a <c>try/finally</c>), and Ctrl-C cancels the in-flight step
/// while still tearing the sandbox down.
/// </summary>
public static class OpenStackSmokeCommand
{
    public enum ParseStatus { Ok, Usage, Invalid }

    public sealed record Parsed(ParseStatus Status, string? ConfigPath, string? Error);

    /// <summary>
    /// Pure arg parser (no I/O) so the CLI surface is unit-testable without a cloud.
    /// </summary>
    public static Parsed Parse(string[] args)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help")
            return new Parsed(ParseStatus.Usage, null, null);

        string? configPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                default:
                    return new Parsed(ParseStatus.Usage, null, $"Unknown option: {args[i]}");
            }
        }

        return new Parsed(ParseStatus.Ok, configPath, null);
    }

    public static void PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: codeybox-harness openstack-smoke [--config <path>]");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  --config <path>  JSON config file holding CodeyBox:Plugins:codeybox.openstack-sandbox");
        error.WriteLine("                    (default: ./appsettings.json when present). Environment variables");
        error.WriteLine("                    bind over file values with __ nesting.");
        error.WriteLine();
        error.WriteLine("Requires the plugin enabled plus OS_APPLICATION_CREDENTIAL_ID and");
        error.WriteLine("OS_APPLICATION_CREDENTIAL_SECRET in the environment. Always disposes the");
        error.WriteLine("sandbox, including on failure or Ctrl-C. Exit 0 only when every step passed.");
    }

    /// <summary>
    /// Builds the plugin options from config file plus environment (same
    /// section and credential chain as production) and runs the smoke flow.
    /// </summary>
    public static async Task<int> RunAsync(Parsed parsed, TextWriter output, TextWriter error)
    {
        var configPath = parsed.ConfigPath;
        if (configPath is null && File.Exists("appsettings.json"))
            configPath = "appsettings.json";
        if (configPath is not null && !File.Exists(configPath))
        {
            error.WriteLine($"Config file not found: {configPath}");
            return Program.ExitLaunchFailed;
        }

        var builder = new ConfigurationBuilder().AddEnvironmentVariables();
        if (configPath is not null)
            builder.AddJsonFile(configPath, optional: false, reloadOnChange: false);
        var options = OpenStackSandboxOptions.FromConfiguration(
            builder.Build().GetSection("CodeyBox:Plugins:" + OpenStackSandboxOptions.PluginId));

        if (!options.Enabled)
        {
            error.WriteLine(
                "The openstack sandbox plugin is not enabled: set " +
                $"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:Enabled=true " +
                "(and allowlist the plugin) in " + (configPath ?? "the environment") + ".");
            return Program.ExitLaunchFailed;
        }

        using var cts = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
        }
        Console.CancelKeyPress += OnCancel;
        try
        {
            var wall = Stopwatch.StartNew();
            var result = await OpenStackSandboxSmoke.RunAsync(options, output, roundTripToken: null, ct: cts.Token)
                .ConfigureAwait(false);
            wall.Stop();
            output.WriteLine($"[smoke] total {wall.Elapsed} — " + (result.Succeeded ? "PASS" : "FAIL"));
            if (!result.Succeeded)
            {
                error.WriteLine($"Smoke failed: {result.Failure}");
                return Program.ExitLaunchFailed;
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }
}
