using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// <see cref="IPullRequestDescriptionGenerator"/> built on the configurable
/// <see cref="ICompletionClient"/>. Makes a single tool-less completion call
/// against <see cref="PrDescriptionOptions.CompletionEndpoint"/> with the
/// shared <see cref="PrDescriptionPrompt"/> prompt. Creates no sandbox and
/// uses no agent runner, so description generation never contends for sandbox
/// permits on the merge critical path.
///
/// A tool-less completion call cannot act on injected instructions in the
/// diff, but its output is published where reviewers and downstream automation
/// may treat it as authoritative — so the rendered PR body keeps the
/// deterministic facts (work item id, changed-file list) adjacent to the
/// generated prose and marks that section as machine-generated (see
/// <see cref="PrDescriptionBody"/>).
///
/// Any failure — timeout, transport failure, authentication failure, or empty
/// completion — throws so the caller falls back to the static template and
/// the pull request still opens. The caller bounds the whole round trip with
/// <see cref="PrDescriptionOptions.Timeout"/> (default 30 seconds).
/// </summary>
public sealed class CompletionPullRequestDescriptionGenerator : PullRequestDescriptionGeneratorBase
{
    private readonly ICompletionClient _completion;

    public CompletionPullRequestDescriptionGenerator(
        ICompletionClient completion,
        PrDescriptionOptions opts,
        ILogger<CompletionPullRequestDescriptionGenerator> log)
        : base(opts, log)
    {
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    /// <inheritdoc />
    protected override async Task<string> GenerateCoreAsync(
        PullRequestDescriptionRequest safeRequest,
        string prompt,
        string truncatedDiff,
        CancellationToken ct)
    {
        var opts = Options;
        if (string.IsNullOrWhiteSpace(opts.CompletionEndpoint))
            throw new InvalidOperationException(
                "PR description generator: CompletionEndpoint is not configured for the completion strategy");

        var token = ResolveApiKey(opts);
        var completionRequest = new CompletionRequest
        {
            Endpoint = opts.CompletionEndpoint,
            Model = opts.CompletionModel,
            Messages = [new CompletionMessage("user", prompt)],
            WireApi = opts.CompletionWireApi,
            BearerToken = opts.CompletionWireApi == CompletionWireApi.AnthropicMessages ? null : token,
            ExtraHeaders = opts.CompletionWireApi == CompletionWireApi.AnthropicMessages && !string.IsNullOrEmpty(token)
                ? new Dictionary<string, string>
                {
                    ["x-api-key"] = token,
                    ["anthropic-version"] = opts.CompletionAnthropicVersion,
                }
                : null,
            MaxOutputTokens = opts.CompletionMaxOutputTokens,
        };

        var result = await _completion.CompleteAsync(completionRequest, ct).ConfigureAwait(false);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text))
        {
            Log.LogWarning(
                "PR description completion failed with {Status} for model {Model}; using static template",
                result.Status, opts.CompletionModel);
            throw new InvalidOperationException(
                $"PR description completion failed with {result.Status} for model '{opts.CompletionModel}'");
        }

        Log.LogInformation("Completion-generated PR description produced ({Chars} chars)", result.Text.Length);
        return result.Text;
    }

    private static string? ResolveApiKey(PrDescriptionOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.CompletionApiKey))
            return opts.CompletionApiKey;
        foreach (var name in opts.CompletionApiKeyEnvVars)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }
}
