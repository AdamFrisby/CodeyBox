using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Api;

/// <summary>
/// Generates a CHANGELOG.md section by calling a configured completion endpoint
/// (Anthropic Messages shape by default) with a structured summary of merged
/// pull requests.
///
/// Handles batching: if the combined PR title+body payload would exceed 100 KB,
/// the PRs are split into batches, each batch summarised independently, and the
/// partial summaries are merged in a final pass.
///
/// PR bodies are run through <see cref="RawOutputRedactor"/> before being sent
/// to the LLM to prevent accidental credential leakage.
/// </summary>
public sealed class ClaudeChangelogGenerator : IChangelogGenerator
{
    private const int MaxPayloadBytes = 100 * 1024;
    private const int MaxPerPrBodyBytes = 4 * 1024;
    private const int MaxResponseTokens = 4096;

    private readonly ICompletionClient _completionClient;
    private readonly ILogger<ClaudeChangelogGenerator> _log;
    private readonly Func<ChangelogOptions> _opts;

    public ClaudeChangelogGenerator(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeChangelogGenerator> log,
        ChangelogOptions opts)
        : this(
            new CompletionClient(
                httpClientFactory,
                () => new CompletionClientOptions { HttpClientName = "changelog-claude" },
                NullLogger<CompletionClient>.Instance),
            log,
            () => opts)
    {
    }

    public ClaudeChangelogGenerator(
        ICompletionClient completionClient,
        ILogger<ClaudeChangelogGenerator> log,
        Func<ChangelogOptions> opts)
    {
        _completionClient = completionClient;
        _log = log;
        _opts = opts;
    }

    public async Task<ChangelogEntry> GenerateAsync(ChangelogRequest request, CancellationToken ct)
    {
        var redacted = request.PullRequests.Select(RedactPr).ToList();
        var formatOverride = request.SectionHeaderFormat;

        string markdown;
        if (redacted.Count == 0)
        {
            var header = FormatSectionHeader(request.ToTag, DateOnly.FromDateTime(DateTime.UtcNow), formatOverride);
            markdown = $"{header}\n\n*(no pull requests found between {request.FromTag} and {request.ToTag})*\n";
        }
        else if (EstimateBytes(redacted) <= MaxPayloadBytes)
        {
            markdown = await GenerateSinglePassAsync(request.ToTag, redacted, formatOverride, ct);
        }
        else
        {
            markdown = await GenerateBatchedAsync(request.ToTag, redacted, ct);
        }

        var categoryMap = ParseCategories(markdown);
        return new ChangelogEntry
        {
            ToTag = request.ToTag,
            Markdown = markdown,
            CategoryToPrNumbers = categoryMap,
        };
    }

    private async Task<string> GenerateSinglePassAsync(
        string toTag, IReadOnlyList<MergedPullRequest> prs, string? formatOverride, CancellationToken ct)
    {
        var header = FormatSectionHeader(toTag, DateOnly.FromDateTime(DateTime.UtcNow), formatOverride);
        var prompt = BuildPrompt(toTag, header, prs);
        return await CallLlmAsync(prompt, ct);
    }

    private async Task<string> GenerateBatchedAsync(
        string toTag, IReadOnlyList<MergedPullRequest> prs, CancellationToken ct)
    {
        _log.LogInformation(
            "Changelog payload for {Tag} exceeds {Max} bytes with {Count} PRs; batching",
            toTag, MaxPayloadBytes, prs.Count);

        // Split PRs into batches of at most MaxPayloadBytes each.
        var batches = SplitIntoBatches(prs);
        var partials = new List<string>(batches.Count);
        foreach (var batch in batches)
        {
            var partialHeader = $"## Partial ({partials.Count + 1}/{batches.Count})";
            var prompt = BuildPrompt(toTag, partialHeader, batch);
            var partial = await CallLlmAsync(prompt, ct);
            partials.Add(partial);
        }

        // Merge partials into a final changelog.
        var mergePrompt = BuildMergePrompt(toTag, partials);
        return await CallLlmAsync(mergePrompt, ct);
    }

    private async Task<string> CallLlmAsync(string userPrompt, CancellationToken ct)
    {
        var opts = _opts();
        var token = string.IsNullOrWhiteSpace(opts.GeneratorApiKey)
            ? Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY")
            : opts.GeneratorApiKey;
        if (string.IsNullOrEmpty(token))
        {
            _log.LogWarning("CODEYBOX_CLAUDE_API_KEY is not set; cannot generate changelog");
            throw new InvalidOperationException("CODEYBOX_CLAUDE_API_KEY is not set");
        }

        var model = opts.GeneratorModelId ?? "claude-opus-4-7";
        var wireApi = opts.GeneratorWireApi;
        var completion = new CompletionRequest
        {
            Endpoint = opts.GeneratorBaseUrl,
            Model = model,
            Messages = [new CompletionMessage("user", userPrompt)],
            WireApi = wireApi,
            BearerToken = wireApi == CompletionWireApi.AnthropicMessages ? null : token,
            ExtraHeaders = wireApi == CompletionWireApi.AnthropicMessages
                ? new Dictionary<string, string>
                {
                    ["x-api-key"] = token,
                    ["anthropic-version"] = opts.GeneratorAnthropicVersion,
                }
                : null,
            MaxOutputTokens = MaxResponseTokens,
        };

        var result = await _completionClient.CompleteAsync(completion, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                _log.LogWarning("Changelog completion returned no text (model {Model})", model);
                return "*(changelog generation failed: unexpected response shape)*";
            }
            return result.Text;
        }
        if (result.Status is CompletionStatus.EmptyCompletion or CompletionStatus.Refused)
        {
            _log.LogWarning("Changelog completion returned no usable text (model {Model}, status {Status})", model, result.Status);
            return "*(changelog generation failed: unexpected response shape)*";
        }

        _log.LogWarning("Changelog completion failed with {Status} for model {Model}", result.Status, model);
        throw new HttpRequestException($"Changelog completion failed with {result.Status} for model {model}");
    }

    private string FormatSectionHeader(string tag, DateOnly date, string? formatOverride = null)
    {
        var format = formatOverride ?? _opts().SectionHeaderFormat;
        return format
            .Replace("{tag}", tag, StringComparison.Ordinal)
            .Replace("{date:yyyy-MM-dd}", date.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
    }

    private static string BuildPrompt(string toTag, string sectionHeader, IReadOnlyList<MergedPullRequest> prs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a technical writer generating a CHANGELOG.md entry.");
        sb.AppendLine("Categorise each PR into exactly one of: Added, Changed, Fixed, Internal.");
        sb.AppendLine("Output ONLY the Markdown section — no preamble, no explanation.");
        sb.AppendLine("Format:");
        sb.AppendLine($"  {sectionHeader}");
        sb.AppendLine();
        sb.AppendLine("  ### Added");
        sb.AppendLine("  - Short description ([#N])");
        sb.AppendLine();
        sb.AppendLine("  ### Changed");
        sb.AppendLine("  - ...");
        sb.AppendLine();
        sb.AppendLine("  ### Fixed");
        sb.AppendLine("  - ...");
        sb.AppendLine();
        sb.AppendLine("  ### Internal");
        sb.AppendLine("  - ...");
        sb.AppendLine();
        sb.AppendLine("Omit sections that have no entries.");
        sb.AppendLine($"Release tag: {toTag}");
        sb.AppendLine();
        sb.AppendLine("Pull requests to summarise:");
        sb.AppendLine();

        foreach (var pr in prs)
        {
            sb.AppendLine($"### PR #{pr.Number}: {pr.Title}");
            if (!string.IsNullOrWhiteSpace(pr.Body))
            {
                var truncatedBody = RawOutputRedactor.TruncateToBytes(pr.Body, MaxPerPrBodyBytes);
                sb.AppendLine(truncatedBody);
            }
            if (pr.ChangedFiles.Count > 0)
            {
                sb.AppendLine($"Files changed: {string.Join(", ", pr.ChangedFiles.Take(20))}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildMergePrompt(string toTag, IReadOnlyList<string> partials)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a technical writer merging partial CHANGELOG entries into one final entry.");
        sb.AppendLine("Merge the following partial changelog sections into a single clean section.");
        sb.AppendLine("Deduplicate any entries that appear more than once.");
        sb.AppendLine("Use these categories: Added, Changed, Fixed, Internal.");
        sb.AppendLine("Output ONLY the merged Markdown — no preamble.");
        sb.AppendLine($"Release tag: {toTag}");
        sb.AppendLine();

        for (int i = 0; i < partials.Count; i++)
        {
            sb.AppendLine($"--- Partial {i + 1} ---");
            sb.AppendLine(partials[i]);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int EstimateBytes(IReadOnlyList<MergedPullRequest> prs)
    {
        var total = 0;
        foreach (var pr in prs)
            total += Encoding.UTF8.GetByteCount(pr.Title) + Encoding.UTF8.GetByteCount(pr.Body);
        return total;
    }

    private static List<List<MergedPullRequest>> SplitIntoBatches(IReadOnlyList<MergedPullRequest> prs)
    {
        var batches = new List<List<MergedPullRequest>>();
        var current = new List<MergedPullRequest>();
        int currentBytes = 0;

        foreach (var pr in prs)
        {
            var prBytes = Encoding.UTF8.GetByteCount(pr.Title) + Encoding.UTF8.GetByteCount(pr.Body);
            if (current.Count > 0 && currentBytes + prBytes > MaxPayloadBytes)
            {
                batches.Add(current);
                current = [];
                currentBytes = 0;
            }
            current.Add(pr);
            currentBytes += prBytes;
        }
        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    private static MergedPullRequest RedactPr(MergedPullRequest pr) =>
        pr with { Title = RawOutputRedactor.Redact(pr.Title), Body = RawOutputRedactor.Redact(pr.Body) };

    // Parses category sections from the generated markdown and maps them to PR numbers.
    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ParseCategories(string markdown)
    {
        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        var prRef = new Regex(@"\[#(\d+)\]", RegexOptions.Compiled);

        string? currentCategory = null;
        var currentPrs = new List<int>();

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                if (currentCategory is not null)
                    result[currentCategory] = currentPrs.ToList();
                currentCategory = trimmed[4..].Trim();
                currentPrs = [];
            }
            else if (currentCategory is not null)
            {
                foreach (Match m in prRef.Matches(trimmed))
                {
                    if (int.TryParse(m.Groups[1].Value, out var n))
                        currentPrs.Add(n);
                }
            }
        }
        if (currentCategory is not null)
            result[currentCategory] = currentPrs.ToList();

        return result;
    }
}
