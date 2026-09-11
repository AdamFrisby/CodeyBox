using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Template-method base for every <see cref="IPullRequestDescriptionGenerator"/>
/// strategy. Owns the safeguards no strategy may omit so a future third
/// strategy inherits them rather than re-implementing them:
/// <list type="bullet">
/// <item>inputs are passed through <see cref="RawOutputRedactor.Redact"/> before
/// prompt construction, so accidentally-committed secrets never reach the model;</item>
/// <item>the diff is truncated from the middle at
/// <see cref="PrDescriptionOptions.MaxDiffBytes"/> via
/// <see cref="PrDescriptionPrompt.TruncateMiddle"/>, keeping the first and last
/// hunks with a truncation marker;</item>
/// <item>empty generations throw (the caller falls back to the static template);</item>
/// <item>generated output is passed through <see cref="RawOutputRedactor.Redact"/>
/// before it is returned, so secrets echoed by the model never reach the PR body.</item>
/// </list>
/// Strategies implement only <see cref="GenerateCoreAsync"/>: turn the prepared
/// prompt into raw prose. They must not create their own prompt pipeline.
/// </summary>
public abstract class PullRequestDescriptionGeneratorBase : IPullRequestDescriptionGenerator
{
    /// <summary>Maximum agent commit messages forwarded to the prompt.</summary>
    protected const int MaxCommitMessages = PrDescriptionPrompt.MaxCommitMessages;

    /// <summary>Maximum UTF-8 bytes kept per agent commit message.</summary>
    protected const int MaxCommitMessageBytes = 2048;

    private readonly PrDescriptionOptions _opts;
    private readonly ILogger _log;

    protected PullRequestDescriptionGeneratorBase(PrDescriptionOptions opts, ILogger log)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Snapshot of the strategy configuration.</summary>
    protected PrDescriptionOptions Options => _opts;

    /// <summary>Strategy logger.</summary>
    protected ILogger Log => _log;

    /// <inheritdoc />
    public async Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Defence-in-depth: redact inputs before building the prompt; caller should also redact.
        var safeRequest = request with
        {
            FullDiff = RawOutputRedactor.Redact(request.FullDiff),
            DiffSummary = RawOutputRedactor.Redact(request.DiffSummary),
            Prompt = RawOutputRedactor.Redact(request.Prompt),
            CommitMessages = RedactCommitMessages(request.CommitMessages),
            AgentReasoningTail = request.AgentReasoningTail is null
                ? null
                : RawOutputRedactor.Redact(request.AgentReasoningTail),
        };
        var truncatedDiff = PrDescriptionPrompt.TruncateMiddle(safeRequest.FullDiff, _opts.MaxDiffBytes);
        var prompt = PrDescriptionPrompt.BuildPrompt(safeRequest, truncatedDiff);

        var text = await GenerateCoreAsync(safeRequest, prompt, truncatedDiff, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"{GetType().Name} returned no output; falling back to the static template");

        // Defence-in-depth output redaction; caller (BuildDescriptionAsync) also redacts the return value.
        return RawOutputRedactor.Redact(text.Trim());
    }

    /// <summary>
    /// Turns the prepared <paramref name="prompt"/> into raw generated prose.
    /// <paramref name="safeRequest"/> carries the redacted inputs and
    /// <paramref name="truncatedDiff"/> the middle-truncated diff for strategies
    /// that need the pieces separately. Throw on any failure (including empty
    /// output — though the base also guards); the caller falls back to the
    /// static template and the pull request still opens.
    /// </summary>
    protected abstract Task<string> GenerateCoreAsync(
        PullRequestDescriptionRequest safeRequest,
        string prompt,
        string truncatedDiff,
        CancellationToken ct);

    private static IReadOnlyList<string> RedactCommitMessages(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0) return messages;
        var redacted = new List<string>(Math.Min(messages.Count, MaxCommitMessages));
        foreach (var message in messages.Take(MaxCommitMessages))
        {
            if (string.IsNullOrWhiteSpace(message)) continue;
            redacted.Add(RawOutputRedactor.TruncateToBytes(
                RawOutputRedactor.Redact(message), MaxCommitMessageBytes));
        }
        return redacted;
    }
}
