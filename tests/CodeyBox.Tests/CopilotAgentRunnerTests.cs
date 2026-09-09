using CodeyBox.Agents.Copilot;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the GitHub Copilot CLI runner: the non-interactive argv it builds, model selection, and the
/// BYOK environment mapping. Behaviours here were verified against copilot v1.0.82 — see the runner's
/// remarks for the specific captures.
/// </summary>
public sealed class CopilotAgentRunnerTests
{
    private static CopilotOptions Byok(Action<CopilotProviderOptions>? tweak = null)
    {
        var options = new CopilotOptions
        {
            Provider = new CopilotProviderOptions { BaseUrl = "http://model-host.internal:13305/v1" },
        };
        tweak?.Invoke(options.Provider);
        return options;
    }

    private static IReadOnlyList<string> Argv(CopilotAgentRunner runner, string? modelId = null)
    {
        var sandbox = new CapturingSandbox();
        runner.RunAsync(sandbox, "/work", "do the thing", credential: null, modelId: modelId)
            .GetAwaiter().GetResult();
        return sandbox.CapturedExec!.Argv;
    }

    [Fact]
    public void Kind_IsCopilot() => Assert.Equal(AgentKind.Copilot, new CopilotAgentRunner().Kind);

    [Fact]
    public void Argv_AlwaysAllowsAllTools_BecauseNonInteractiveModeRequiresIt()
    {
        // copilot 1.0.82 documents --allow-all-tools as "required for non-interactive mode"; without it
        // the CLI blocks on a permission prompt nothing will answer.
        var argv = Argv(new CopilotAgentRunner());

        Assert.Equal("copilot", argv[0]);
        Assert.Contains("-p", argv);
        Assert.Contains("do the thing", argv);
        Assert.Contains("--allow-all-tools", argv);
        Assert.Contains("--allow-all-paths", argv);
        // Egress stays governed by the sandbox network profile, not waived here.
        Assert.DoesNotContain("--allow-all-urls", argv);
        Assert.DoesNotContain("--allow-all", argv);
    }

    [Fact]
    public void Argv_PassesModelId()
    {
        // The CLI does expose --model (an older comment in this runner claimed otherwise), and it is the
        // only thing that changes the wire model in -p mode.
        var argv = Argv(new CopilotAgentRunner(), modelId: "gpt-5.6-sol");

        var idx = argv.ToList().IndexOf("--model");
        Assert.True(idx >= 0, "expected --model in argv");
        Assert.Equal("gpt-5.6-sol", argv[idx + 1]);
    }

    [Fact]
    public void Argv_UnderByok_ExcludesApplyPatchByDefault()
    {
        // apply_patch is offered as an OpenAI *custom* tool with a Lark grammar; a server implementing
        // only function tools rejects the entire tools array and no turn can start.
        var argv = Argv(new CopilotAgentRunner { Options = Byok() });

        var idx = argv.ToList().IndexOf("--excluded-tools");
        Assert.True(idx >= 0, "expected --excluded-tools under BYOK");
        Assert.Equal("apply_patch", argv[idx + 1]);
    }

    [Fact]
    public void Argv_SubscriptionMode_ExcludesNothing()
    {
        // On GitHub's own routing the custom tool works, so withholding it would cost an editing path
        // for nothing.
        Assert.DoesNotContain("--excluded-tools", Argv(new CopilotAgentRunner()));
    }

    [Fact]
    public void Argv_ExplicitEmptyExcludedTools_OptsOutOfTheByokDefault()
    {
        var options = Byok();
        options.ExcludedTools = [];

        Assert.DoesNotContain("--excluded-tools", Argv(new CopilotAgentRunner { Options = options }));
    }

    [Fact]
    public void ProviderEnvironment_IsEmptyWithoutBaseUrl()
    {
        // Every other provider variable is inert without COPILOT_PROVIDER_BASE_URL, so emitting them
        // would be noise the CLI ignores.
        var env = CopilotAgentRunner.BuildProviderEnvironment(new CopilotOptions { Offline = true });

        Assert.Empty(env);
    }

    [Fact]
    public void ProviderEnvironment_RendersConfiguredEndpoint()
    {
        var options = Byok(p =>
        {
            p.Headers = ["X-Tenant: acme", "  "];
            p.MaxPromptTokens = 120_000;
        });
        options.Offline = true;

        var env = CopilotAgentRunner.BuildProviderEnvironment(options);

        Assert.Equal("http://model-host.internal:13305/v1", env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("openai", env["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("completions", env["COPILOT_PROVIDER_WIRE_API"]);
        Assert.Equal("http", env["COPILOT_PROVIDER_TRANSPORT"]);
        Assert.Equal("true", env["COPILOT_OFFLINE"]);
        Assert.Equal("120000", env["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"]);
        // Blank header entries are dropped rather than emitted as an empty line.
        Assert.Equal("X-Tenant: acme", env["COPILOT_PROVIDER_HEADERS"]);
        Assert.False(env.ContainsKey("COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"));
    }

    [Fact]
    public void ProviderEnvironment_NeverCarriesTheCredential()
    {
        // The key/bearer reach the CLI through the credential chain's environment injection. If they
        // ever leaked into config-derived output they would be persisted wherever config is.
        var env = CopilotAgentRunner.BuildProviderEnvironment(Byok());

        Assert.DoesNotContain(CopilotAgentRunner.ProviderApiKeyEnvironmentVariable, env.Keys);
        Assert.DoesNotContain(CopilotAgentRunner.ProviderBearerTokenEnvironmentVariable, env.Keys);
    }

    [Fact]
    public void ProviderEnvironment_OfflineIgnoredWithoutProvider()
    {
        // Copilot refuses offline mode without a provider — it could neither authenticate nor infer —
        // so the flag must not be forwarded to fail at launch.
        var env = CopilotAgentRunner.BuildProviderEnvironment(
            new CopilotOptions { Offline = true, Provider = new CopilotProviderOptions() });

        Assert.DoesNotContain("COPILOT_OFFLINE", env.Keys);
    }

    [Theory]
    [InlineData("Azure", "azure")]
    [InlineData("ANTHROPIC", "anthropic")]
    [InlineData("nonsense", "openai")]   // unrecognised falls back to the CLI's own default
    [InlineData(null, "openai")]
    public void ProviderEnvironment_NormalisesProviderType(string? configured, string expected)
    {
        var options = Byok(p => p.Type = configured!);

        Assert.Equal(expected, CopilotAgentRunner.BuildProviderEnvironment(options)["COPILOT_PROVIDER_TYPE"]);
    }

    [Fact]
    public void CredentialEnvironmentVariables_CoverBothAuthModes()
    {
        // Every credential env var must be classified or SandboxEnvironmentVariablePolicy rejects it.
        var declared = CopilotAgentRunner.CredentialEnvironmentVariables;

        Assert.Contains("GH_TOKEN", declared);
        Assert.Contains("GITHUB_TOKEN", declared);
        Assert.Contains(CopilotAgentRunner.ProviderApiKeyEnvironmentVariable, declared);
        Assert.Contains(CopilotAgentRunner.ProviderBearerTokenEnvironmentVariable, declared);
    }

    private static IReadOnlyDictionary<string, string> RunEnv(CopilotAgentRunner runner)
    {
        var sandbox = new CapturingSandbox();
        runner.RunAsync(sandbox, "/work", "do the thing", credential: null)
            .GetAwaiter().GetResult();
        return sandbox.CapturedExec!.ExtraEnvironment!;
    }

    private static string HeaderLine(IReadOnlyDictionary<string, string> env, string name)
    {
        var lines = env["COPILOT_PROVIDER_HEADERS"].Split('\n');
        var line = Assert.Single(lines, l => l.StartsWith(name + ":", StringComparison.Ordinal));
        return line[(name.Length + 1)..].Trim();
    }

    [Fact]
    public void ProviderHeaders_SessionPlaceholder_DiffersPerInvocation_WhileStaticHeaderIsIdentical()
    {
        // Two real invocations: the per-invocation header must vary (no shared session collapsing
        // concurrent sandboxes onto one provider session) while the static header is byte-identical.
        var runner = new CopilotAgentRunner
        {
            Options = Byok(p => p.Headers =
            [
                "X-Tenant: acme",
                "x-opencode-session: {{codeybox.session_id}}",
            ]),
        };

        var first = RunEnv(runner);
        var second = RunEnv(runner);

        Assert.Equal("acme", HeaderLine(first, "X-Tenant"));
        Assert.Equal(HeaderLine(first, "X-Tenant"), HeaderLine(second, "X-Tenant"));
        var firstSession = HeaderLine(first, "x-opencode-session");
        var secondSession = HeaderLine(second, "x-opencode-session");
        Assert.NotEqual(secondSession, firstSession);
        Assert.True(Guid.TryParseExact(firstSession, "D", out _), $"not a UUID: {firstSession}");
        Assert.True(Guid.TryParseExact(secondSession, "D", out _), $"not a UUID: {secondSession}");
        // The diagnostic variable correlates each run with the session id it used.
        Assert.Equal(firstSession, first[CopilotAgentRunner.ProviderSessionIdEnvironmentVariable]);
        Assert.Equal(secondSession, second[CopilotAgentRunner.ProviderSessionIdEnvironmentVariable]);
    }

    [Fact]
    public void ProviderHeaders_GeneratedValue_ReachesAgentEnvironment_InExpectedHeaderFormat()
    {
        // What the agent process receives must be a well-formed "Name: Value" line in the
        // newline-separated format Copilot parses — not the raw placeholder.
        var runner = new CopilotAgentRunner
        {
            Options = Byok(p => p.Headers = ["x-opencode-session: {{codeybox.session_id}}"]),
        };

        var env = RunEnv(runner);

        var raw = env["COPILOT_PROVIDER_HEADERS"];
        Assert.DoesNotContain(CopilotAgentRunner.ProviderSessionIdPlaceholder, raw);
        Assert.Matches(@"^x-opencode-session: [0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", raw);
    }

    [Fact]
    public void ProviderHeaders_StaticOnly_IsByteIdenticalAcrossInvocations_AndEmitsNoSessionId()
    {
        // Regression: other BYOK providers with only static headers see zero behaviour change —
        // identical bytes across invocations and no diagnostic variable.
        var runner = new CopilotAgentRunner
        {
            Options = Byok(p => p.Headers = ["X-Tenant: acme", "X-Other: 1"]),
        };

        var first = RunEnv(runner);
        var second = RunEnv(runner);

        Assert.Equal("X-Tenant: acme\nX-Other: 1", first["COPILOT_PROVIDER_HEADERS"]);
        Assert.Equal(first["COPILOT_PROVIDER_HEADERS"], second["COPILOT_PROVIDER_HEADERS"]);
        Assert.False(first.ContainsKey(CopilotAgentRunner.ProviderSessionIdEnvironmentVariable));
        Assert.False(second.ContainsKey(CopilotAgentRunner.ProviderSessionIdEnvironmentVariable));
    }

    [Fact]
    public void BuildProviderEnvironment_ResolvesEveryOccurrenceOncePerCall_WithInjectedGenerator()
    {
        // One generator call per invocation, shared by every occurrence so the whole invocation
        // correlates to a single provider session.
        var options = Byok(p => p.Headers =
        [
            "x-opencode-session: {{codeybox.session_id}}",
            "x-trace: {{codeybox.session_id}}",
        ]);
        var calls = 0;
        var env = CopilotAgentRunner.BuildProviderEnvironment(options, () => { calls++; return "fixed-id"; });

        Assert.Equal(1, calls);
        Assert.Equal("x-opencode-session: fixed-id\nx-trace: fixed-id", env["COPILOT_PROVIDER_HEADERS"]);
        Assert.Equal("fixed-id", env[CopilotAgentRunner.ProviderSessionIdEnvironmentVariable]);
    }

    [Fact]
    public void BuildProviderEnvironment_NeverInvokesGenerator_WithoutPlaceholder()
    {
        // Static headers must not touch the generator at all.
        var options = Byok(p => p.Headers = ["X-Tenant: acme"]);
        var env = CopilotAgentRunner.BuildProviderEnvironment(
            options,
            () => throw new InvalidOperationException("generator must not be invoked"));

        Assert.Equal("X-Tenant: acme", env["COPILOT_PROVIDER_HEADERS"]);
        Assert.False(env.ContainsKey(CopilotAgentRunner.ProviderSessionIdEnvironmentVariable));
    }
}
