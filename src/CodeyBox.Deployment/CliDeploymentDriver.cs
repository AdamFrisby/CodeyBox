using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a CLI-tool deployment: build or stage the binary as directed by the
/// recipe, then verify the tool runs by invoking the recipe's
/// readiness command (defaults to <c>&lt;artifact-path&gt; --version</c>).
/// "Expose" returns the in-substrate binary path so callers can invoke the
/// tool through the same deployment handle.
///
/// <para>The default invocation runs the artifact path through
/// <see cref="Shell.Quote"/>; recipes that override via
/// <see cref="SettingsKeyInvocationCommand"/> own quoting in their template
/// (the <c>{artifact}</c> placeholder substitutes a single-quoted form).</para>
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
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        // {artifact} substitutes a shell-quoted artifact path so a recipe-author
        // template like `{artifact} --selftest` survives whitespace / quote
        // metacharacters; the default template applies Shell.Quote directly.
        var quotedArtifact = Shell.Quote(recipe.ArtifactPath!);
        var invocation = recipe.Settings.TryGetValue(SettingsKeyInvocationCommand, out var c)
            && !string.IsNullOrWhiteSpace(c)
                ? c.Replace("{artifact}", quotedArtifact, StringComparison.Ordinal)
                : $"{quotedArtifact} --version";

        var result = await RunDeploymentExecAsync(
            substrate,
            recipe,
            context,
            "cli invocation",
            ["sh", "-c", invocation],
            recipe.Environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"cli invocation '{Tail(invocation)}' exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
    }

    protected override DeploymentEndpoint BuildEndpoint(IDeploymentSubstrate substrate, DeploymentRecipe recipe, DeploymentContext context)
        => new()
        {
            Kind = DeploymentEndpointKind.Cli,
            Path = recipe.ArtifactPath,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["substrate.id"] = substrate.Id,
                ["endpoint.scope"] = "sandbox-exec",
                ["sandbox.path"] = recipe.ArtifactPath!,
            },
        };
}

internal static class Shell
{
    /// <summary>Single-quote a path so it survives sh -c. Quotes are escaped via the standard '\'' dance.</summary>
    public static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "''";
        var escaped = value.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"'{escaped}'";
    }
}
