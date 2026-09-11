using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Generates a pull request description by running the configured agent in a
/// minimal sandbox with the diff and context as a structured prompt.
/// This is the <see cref="PrDescriptionStrategy.Agentic"/> strategy: retained
/// as a supported option alongside the default completion strategy. It places
/// a sandbox acquisition on the merge critical path and executes an agent over
/// attacker-influenceable diff content, so prefer the completion strategy
/// unless sandboxed generation is explicitly wanted.
///
/// Prompt construction, middle-out diff truncation and input/output redaction
/// are inherited from <see cref="PullRequestDescriptionGeneratorBase"/> and
/// shared with the completion strategy via <see cref="PrDescriptionPrompt"/>.
/// </summary>
public sealed class LlmPullRequestDescriptionGenerator : PullRequestDescriptionGeneratorBase
{
    private readonly ISandboxProvider _sandboxes;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;

    public LlmPullRequestDescriptionGenerator(
        ISandboxProvider sandboxes,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        PrDescriptionOptions opts,
        ILogger<LlmPullRequestDescriptionGenerator> log)
        : base(opts, log)
    {
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    /// <inheritdoc />
    protected override async Task<string> GenerateCoreAsync(
        PullRequestDescriptionRequest safeRequest,
        string prompt,
        string truncatedDiff,
        CancellationToken ct)
    {
        var opts = Options;
        var agentKind = new AgentKind(opts.GeneratorAgent);
        if (!_agents.TryGet(agentKind, out var runner))
            throw new InvalidOperationException(
                $"PR description generator: no agent runner registered for kind '{opts.GeneratorAgent}'");

        var credential = await _credentials.GetAsync(agentKind, ct).ConfigureAwait(false);
        var env = credential?.EnvironmentVariables ?? new Dictionary<string, string>();

        var spec = new SandboxSpec
        {
            ImageReference = opts.SandboxImageReference,
            Mounts = [],
            Environment = env,
            Network = new SandboxNetworkPolicy
            {
                AllowedHosts = opts.AgentAllowedHosts,
            },
            WorkingDirectory = "/work",
        };

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct).ConfigureAwait(false);

        if (credential?.Files is { Count: > 0 } files)
        {
            foreach (var (path, contents) in files)
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    await sandbox.ExecAsync(new SandboxExec { Argv = ["mkdir", "-p", dir] }, ct).ConfigureAwait(false);
                await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["tee", path],
                    Stdin = contents,
                }, ct).ConfigureAwait(false);
            }
        }

        var result = await runner.RunAsync(sandbox, "/work", prompt, credential: null, opts.GeneratorModelId, reasoningMode: null, ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException(
                $"PR description agent returned no output: {result.Summary}");

        return result.Stdout.Trim();
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxBytes"/>
    /// UTF-8 bytes by removing bytes from the middle. Inserts a
    /// "[… N bytes truncated …]" marker at the removal point.
    /// </summary>
    public static string TruncateMiddle(string text, int maxBytes) =>
        PrDescriptionPrompt.TruncateMiddle(text, maxBytes);
}
