using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a CLI-tool deployment: build the binary, stage it on PATH inside
/// the substrate, then verify the tool runs by invoking the recipe's
/// readiness command (defaults to <c>&lt;artifact-path&gt; --version</c>).
/// "Expose" returns the in-substrate binary path so callers can invoke the
/// tool through the same sandbox handle.
/// </summary>
public sealed class CliDeploymentDriver : SandboxDeploymentDriverBase
{
    public const string SettingsKeyInvocationCommand = "invocation-command";

    public CliDeploymentDriver(ILogger<CliDeploymentDriver>? log = null, Func<DateTimeOffset>? clock = null)
        : base(log, clock) { }

    public override string Kind => DeploymentKinds.Cli;

    public override void ValidateRecipe(DeploymentRecipe recipe)
    {
        base.ValidateRecipe(recipe);
        if (string.IsNullOrWhiteSpace(recipe.ArtifactPath))
            throw new ArgumentException("DeploymentRecipe.ArtifactPath is required for kind 'cli'.", nameof(recipe));
    }

    protected override async Task ProbeReadyAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        var invocation = recipe.Settings.TryGetValue(SettingsKeyInvocationCommand, out var c)
            && !string.IsNullOrWhiteSpace(c)
                ? c.Replace("{artifact}", recipe.ArtifactPath!, StringComparison.Ordinal)
                : $"{Shell.Quote(recipe.ArtifactPath!)} --version";

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", invocation],
            WorkingDirectory = context.WorkingDirectory,
            ExtraEnvironment = recipe.Environment,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"cli invocation '{invocation}' exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
    }

    protected override DeploymentEndpoint BuildEndpoint(ISandbox sandbox, DeploymentRecipe recipe, DeploymentContext context)
        => new()
        {
            Kind = DeploymentEndpointKind.Cli,
            Path = recipe.ArtifactPath,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sandbox.id"] = sandbox.Id,
            },
        };
}

internal static class Shell
{
    /// <summary>Single-quote a path so it survives sh -c. Quotes are escaped via the standard '\'' dance.</summary>
    public static string Quote(string value)
    {
        if (value is null)
            return "''";
        var escaped = value.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"'{escaped}'";
    }
}
