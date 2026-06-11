using System.Net;
using System.Net.Http;
using CodeyBox.Agents.Codex;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class CodexAgentRunnerTests
{
    // ── GetTextOnlyUnavailabilityReason ───────────────────────────────────────
    //
    // The rebase-resolver router consults the probe BEFORE invoking
    // RunTextOnlyAsync. The two must agree on what 'viable' means, including
    // the CODEX_AUTH_JSON fallback path (when the env var carries the parsed
    // OAuth blob with an OPENAI_API_KEY field). A drift here would either
    // misroute past Codex when it would in fact work, or pick Codex and then
    // fail at call-time with the misleading-error bug-shape the routing fix
    // was supposed to eliminate.

    [Fact]
    public void GetTextOnlyUnavailabilityReason_NullCredential_ReturnsReason()
    {
        var runner = new CodexAgentRunner();
        Assert.Equal("OPENAI_API_KEY is required for text-only calls",
            runner.GetTextOnlyUnavailabilityReason(credential: null));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_EmptyEnvironment_ReturnsReason()
    {
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.Equal("OPENAI_API_KEY is required for text-only calls",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_OpenAiApiKeyPresent_ReturnsNull()
    {
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-openai" },
            new Dictionary<string, string>());

        Assert.Null(runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_CodexAuthJsonWithEmbeddedKey_ReturnsNull()
    {
        // Operators with OAuth-only setups inject the parsed auth blob via
        // CODEX_AUTH_JSON. When the blob carries an OPENAI_API_KEY, the probe
        // must report viable — otherwise the router silently routes past
        // Codex in a configuration where it would in fact serve.
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string>
            {
                ["CODEX_AUTH_JSON"] = """{"OPENAI_API_KEY":"sk-from-oauth"}""",
            },
            new Dictionary<string, string>());

        Assert.Null(runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_CodexAuthJsonWithoutKey_ReturnsReason()
    {
        // Per the original task, Codex-OAuth-only-with-no-API-key is out of
        // scope of this fix — the auth JSON without a usable OPENAI_API_KEY
        // does NOT count as text-only viable. Locks in the documented gap.
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string>
            {
                ["CODEX_AUTH_JSON"] = """{"tokens":{"access_token":"abc"}}""",
            },
            new Dictionary<string, string>());

        Assert.Equal("OPENAI_API_KEY is required for text-only calls",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_CodexAuthJsonInvalidJson_ReturnsReason()
    {
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = "not json" },
            new Dictionary<string, string>());

        Assert.Equal("OPENAI_API_KEY is required for text-only calls",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public async Task GetTextOnlyUnavailabilityReason_AgreesWithRunTextOnlyAsync_OnMissingCredentials()
    {
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.NotNull(runner.GetTextOnlyUnavailabilityReason(cred));
        var result = await runner.RunTextOnlyAsync("hello", cred);
        Assert.False(result.Success);
        Assert.Contains("OPENAI_API_KEY", result.Error);
    }

    [Fact]
    public async Task RunResumedAsync_MaterialisesAuthBeforeInvokingCodex()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CodexAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.True(result.Success);
        // The codex CLI invocation must be preceded by the in-sandbox auth
        // materialisation bash command that references CODEX_AUTH_JSON.
        var authIdx = sandbox.Execs.FindIndex(e =>
            e.Argv.Count >= 3
            && e.Argv[0] == "bash"
            && e.Argv[2].Contains("CODEX_AUTH_JSON", StringComparison.Ordinal)
            && e.Argv[2].Contains("$HOME/.codex/auth.json", StringComparison.Ordinal));
        var codexIdx = sandbox.Execs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "codex");
        Assert.True(authIdx >= 0, "auth materialisation bash command was not invoked");
        Assert.True(codexIdx >= 0, "codex CLI was not invoked");
        Assert.True(authIdx < codexIdx, "auth materialisation must run before codex CLI");
        Assert.Equal("exec", sandbox.Execs[codexIdx].Argv[1]);
    }

    [Fact]
    public async Task RunResumedAsync_WhenAuthMaterialisationFails_DoesNotInvokeCodex()
    {
        var sandbox = new RecordingSandbox(authWriteExitCode: 7);
        var runner = new CodexAgentRunner();

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "resume prompt",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        Assert.False(result.Success);
        Assert.Equal("failed to materialise codex auth: exit 7", result.Summary);
        Assert.DoesNotContain(sandbox.Execs, exec => exec.Argv.Count > 0 && exec.Argv[0] == "codex");
    }

    [Fact]
    public async Task PrepareSandboxScript_PreservesExistingSandboxAuthJson()
    {
        // The materialisation script must short-circuit when auth.json is
        // already non-empty inside the sandbox. That preserves restored
        // sandbox home state while still avoiding host credential mounts.
        var sandbox = new RecordingSandbox();
        var runner = new CodexAgentRunner();

        // RunResumedAsync drives PrepareSandboxAsync as a side effect.
        _ = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "p",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        var prepExec = sandbox.Execs.FirstOrDefault(e =>
            e.Argv.Count >= 3
            && e.Argv[0] == "bash"
            && e.Argv[2].Contains("CODEX_AUTH_JSON", StringComparison.Ordinal));
        Assert.NotNull(prepExec);
        var script = prepExec!.Argv[2];

        // Guard must check existence (or non-empty) of auth.json BEFORE the
        // env-var write block, and exit early when present.
        var stillExistsIdx = script.IndexOf("$HOME/.codex/auth.json", StringComparison.Ordinal);
        var earlyExitIdx = script.IndexOf("exit 0", StringComparison.Ordinal);
        var writeIdx = script.IndexOf("printf", StringComparison.Ordinal);
        Assert.True(stillExistsIdx >= 0, "script must reference $HOME/.codex/auth.json");
        Assert.True(earlyExitIdx >= 0, "script must short-circuit when file is present (exit 0)");
        Assert.True(writeIdx >= 0, "script must still have a printf-from-env fallback");
        Assert.True(earlyExitIdx < writeIdx,
            "early-exit guard must come before the env-var write so an existing auth.json is preserved");
    }

    // ── Default model from config ─────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoModelIdOverride_PassesDefaultModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = "gpt-5.5",
            });
        var runner = new CodexAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "argv must contain --model when DefaultModelId is set");
        Assert.Equal("gpt-5.5", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_WithExplicitModelId_UsesOverrideNotDefault()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = "gpt-5.5",
            });
        var runner = new CodexAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: "gpt-4o-mini");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("gpt-4o-mini", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_NullWhenNoDefaultConfigured_NoModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new CodexAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    // ── Text-only model plumbing ──────────────────────────────────────────────

    [Fact]
    public async Task RunTextOnlyAsync_ConfiguredDefault_SetsModelInRequestBody()
    {
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = "gpt-5.5",
            });
        var handler = new CapturingCodexHandler(HttpStatusCode.Unauthorized,
            """{"error":{"message":"placeholder rejected"}}""");
        var runner = new CodexAgentRunner(defaults, networkTolerance: null, textOnlyHttp: new HttpClient(handler));
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("hello", cred);

        Assert.False(result.Success);
        // The "no default configured" guard would have returned before any HTTP
        // call — assert we got past it and into the request-construction path.
        Assert.DoesNotContain("no default configured", result.Summary);
        Assert.DoesNotContain("no default configured", result.Error);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"model\":\"gpt-5.5\"", handler.LastRequestBody);
    }

    private sealed class CapturingCodexHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingCodexHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            };
        }
    }

    [Fact]
    public async Task RunTextOnlyAsync_MissingDefault_ReturnsError()
    {
        var runner = new CodexAgentRunner();
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("hello", cred);

        Assert.False(result.Success);
        Assert.Contains("no default configured", result.Error);
    }

    private sealed class RecordingSandbox : ISandbox
    {
        private readonly int _authWriteExitCode;

        public RecordingSandbox(int authWriteExitCode = 0)
        {
            _authWriteExitCode = authWriteExitCode;
        }

        public string Id => "recording";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            // The auth materialisation script is a single-arg bash -c invocation
            // referencing CODEX_AUTH_JSON; surface the configured exit code so we
            // can simulate a failed write.
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[2].Contains("CODEX_AUTH_JSON", StringComparison.Ordinal)
                && exec.Argv[2].Contains(".codex/auth.json", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(_authWriteExitCode, "", "auth stderr"));
            }

            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
