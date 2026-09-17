using CodeyBox.Agents.Cmd;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdQuotaFailureDetector"/> over recorded real
/// command-code 1.54.2 failure shapes (key-id tails redacted — assertions
/// only touch the stable refusal text): the spend-limit refusal, the
/// CLI-side spend-cap refusal, the no-auth gate, and the config errors that
/// must NOT park as quota. Also pins that the headless permission-gate
/// diagnostic the runner lifts is quota-clean (it is a dispatch-
/// configuration failure, not spend).
/// </summary>
public sealed class CmdQuotaFailureDetectorTests
{
    private static readonly CmdQuotaFailureDetector Detector = new();

    [Fact]
    public void Kind_IsCmd()
    {
        Assert.Equal(AgentKind.Cmd, Detector.Kind);
    }

    [Fact]
    public void Detect_SpendLimitRefusal_ParksLimitReached()
    {
        // Recorded real shape (paid model on a $0-spend-limit key, exit 4):
        // the provider body relayed verbatim in the terminal result line.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 403\"}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"isError\":true,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":2202,\"finalText\":\"\",\"error\":\"Error: 403 Key limit exceeded (total limit).\"}";

        var detection = Detector.Detect(stderr: string.Empty, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_SpendCapRefusal_ParksLimitReached()
    {
        // Recorded real shape (API-key spend cap tripped, exit 4): the
        // CLI's own cap check on stderr with empty stdout.
        const string stderr = "Error: API key spend cap reached.";

        var detection = Detector.Detect(stderr: stderr, stdout: string.Empty);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_MissingAuthPlaceholder_ParksUnauthorized()
    {
        // Recorded real shape (no auth.json / COMMAND_CODE_API_KEY, exit 3).
        const string stderr = "Error: No auth credentials found. Run cmd login or set COMMAND_CODE_API_KEY.";

        var detection = Detector.Detect(stderr: stderr, stdout: string.Empty);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_MissingProviderKeyEnv_ParksUnauthorized()
    {
        // Recorded real shape (providers.json apiKey reference unresolvable,
        // exit 1): names the exact env variable the shipped mapping wires.
        const string stderr = "Error: API key environment variable OPENROUTER_API_KEY is not set";

        var detection = Detector.Detect(stderr: stderr, stdout: string.Empty);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_UnknownModel400_IsNotQuota()
    {
        // Recorded real shape (exit 1): a 400 for a model the provider does
        // not serve is a configuration error (wrong id), not spend — it must
        // not park the member as quota-exhausted.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 400\"}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"isError\":true,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":2206,\"finalText\":\"\",\"error\":\"Error: 400\"}";

        Assert.Null(Detector.Detect(stderr: string.Empty, stdout: stdout));
    }

    [Fact]
    public void Detect_UndeclaredModelAdvisory_IsNotQuota()
    {
        // The "isn't declared under provider" note fires on healthy runs too
        // (the CLI sends the id anyway) — never a failure.
        const string stderr = "\"deepseek/deepseek-v3\" isn't declared under provider 'openrouter' in providers.json — sending it anyway.";

        Assert.Null(Detector.Detect(stderr: stderr, stdout: string.Empty));
    }

    [Fact]
    public void Detect_PermissionGateDiagnostic_IsNotQuota()
    {
        // The tool_hook_blocked lift the runner emits on a permission-gated
        // success is a dispatch-configuration failure (missing --yolo), not
        // spend — it must never park the member as quota-exhausted.
        const string diagnostic =
            "cmd blocked 2 write/shell tool call(s) behind the headless permission gate " +
            "(tool_hook_blocked — headless mode without --yolo); no file writes or shell commands could run";

        Assert.Null(Detector.Detect(stderr: diagnostic, stdout: string.Empty));
    }

    [Fact]
    public void Detect_ModelOutputCitingStatusCode_IsNotQuota()
    {
        // Prompts and repo content under review can cite bare status codes;
        // patterns stay anchored to provider-shaped phrases.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"message_end\",\"content\":[{\"type\":\"text\"," +
            "\"text\":\"The API returned 403 for missing scopes; fix the auth header.\"}]}}";

        Assert.Null(Detector.Detect(stderr: string.Empty, stdout: stdout));
    }

    [Fact]
    public void Detect_EmptyStreams_ReturnsNull()
    {
        Assert.Null(Detector.Detect(stderr: null, stdout: null));
        Assert.Null(Detector.Detect(stderr: string.Empty, stdout: string.Empty));
    }

    [Fact]
    public void Ctor_OperatorExtras_AppendedAfterDefaults_BlanksIgnored()
    {
        var detector = new CmdQuotaFailureDetector(
        [
            new QuotaFailurePattern("", QuotaFailureKind.LimitReached),
            new QuotaFailurePattern("operator-canary-exhaustion", QuotaFailureKind.LimitReached),
        ]);

        var detection = detector.Detect(stderr: "operator-canary-exhaustion in provider body", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
        // Defaults still fire first.
        Assert.NotNull(detector.Detect(stderr: "Key limit exceeded", stdout: null));
    }
}
