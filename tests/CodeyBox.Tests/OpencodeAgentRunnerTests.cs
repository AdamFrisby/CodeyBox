using CodeyBox.Agents.Opencode;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="OpencodeAgentRunner"/>. Uses the shared
/// CapturingSandbox to inspect the argv and stdin the runner forwards.
/// </summary>
public sealed class OpencodeAgentRunnerTests
{
    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, new OpencodeAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Opencode_RoundTrips()
    {
        Assert.Equal(AgentKind.Opencode, new AgentKind("opencode"));
    }

    [Fact]
    public async Task RunAsync_Argv_StartsWithBinaryAndRunSubcommand()
    {
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Equal("opencode", argv[0]);
        Assert.Equal("run", argv[1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModel_IsDeepSeekVariant()
    {
        // The default agent-class slot ships routed at a DeepSeek model id
        // because that is the differentiating capability opencode adds vs
        // the other registered agents. Operators retune the exact id via
        // `opencode models`; this test pins the prefix only.
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["opencode"] = "deepseek-v4-flash",
            });
        var runner = new OpencodeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Contains("--model", argv);
        Assert.Contains("deepseek-v4-flash", argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_OverridesDefault()
    {
        // Confirms the multi-provider routing slot: opencode is also usable
        // as an Anthropic/OpenAI fallback when the DeepSeek path is gated.
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "anthropic/claude-sonnet-4-6");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-sonnet-4-6", argv[modelIdx + 1]);
        Assert.DoesNotContain(argv, a => a.StartsWith("opencode-go/deepseek-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_OmitsModelFlag_WhenDefaultModelClearedAndNoOverride()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new OpencodeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_PromptArrivesOnStdin_NotArgv()
    {
        // MAX_ARG_STRLEN is 128 KiB; rework prompts can exceed it. stdin
        // bypasses the ceiling and matches Codex/Gemini's pattern.
        const string prompt = "write a fizzbuzz in Go";
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(prompt, argv);
        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
    }

    [Fact]
    public async Task RunAsync_NoReasoningFlag_WhenEnvOverrideUnset()
    {
        // No OPENCODE_REASONING_FLAG → reasoning mode is dropped on the
        // floor rather than guessed at, per the vendor-api-drift rule.
        var prior = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", null);
            var sandbox = new CapturingSandbox();
            var runner = new OpencodeAgentRunner();
            await runner.RunAsync(sandbox, "/work", "x", credential: null,
                reasoningMode: "high");
            Assert.DoesNotContain("high", sandbox.CapturedExec!.Argv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", prior);
        }
    }

    [Fact]
    public async Task RunAsync_ReasoningFlag_AppendedWhenEnvOverrideSet()
    {
        var prior = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", "--reasoning-effort");
            var sandbox = new CapturingSandbox();
            var runner = new OpencodeAgentRunner();
            await runner.RunAsync(sandbox, "/work", "x", credential: null,
                reasoningMode: "high");
            var argv = sandbox.CapturedExec!.Argv.ToList();
            var idx = argv.IndexOf("--reasoning-effort");
            Assert.True(idx >= 0);
            Assert.Equal("high", argv[idx + 1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", prior);
        }
    }

    // --- PrepareSandboxAsync credential-materialisation -------------------
    // The runner's PrepareSandboxAsync writes the opencode auth.json inside
    // the sandbox from the OPENCODE_AUTH_JSON credential env var. These
    // tests pin:
    //   - the materialisation script runs BEFORE the opencode CLI invocation;
    //   - it references the correct default destination;
    //   - it honours OPENCODE_AUTH_DEST_PATH for non-XDG destinations;
    //   - it is skipped entirely when no credential is supplied;
    //   - a failed write fails the run with a meaningful summary and
    //     prevents the opencode CLI from being invoked.

    private static AgentCredential OpencodeCred(string authJson, string? destPath = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_AUTH_JSON"] = authJson,
        };
        if (destPath is not null) env["OPENCODE_AUTH_DEST_PATH"] = destPath;
        return new AgentCredential(AgentKind.Opencode, env, new Dictionary<string, string>());
    }

    private static int FindOpencodeExecIndex(IReadOnlyList<SandboxExec> execs)
    {
        for (var i = 0; i < execs.Count; i++)
        {
            var argv = execs[i].Argv;
            if (argv.Count > 0 && argv[0] == "opencode") return i;
        }
        return -1;
    }

    private static int FindMaterialisationScriptIndex(IReadOnlyList<SandboxExec> execs)
    {
        for (var i = 0; i < execs.Count; i++)
        {
            var argv = execs[i].Argv;
            if (argv.Count >= 3
                && argv[0] == "bash"
                && argv[1] == "-c"
                && (argv[2].Contains("OPENCODE_AUTH_JSON", StringComparison.Ordinal)
                    || (argv.Count >= 5 && argv[4] == ".local/share/opencode/auth.json")))
                return i;
        }
        return -1;
    }

    [Fact]
    public async Task RunAsync_NoCredential_DoesNotRunMaterialisationScript()
    {
        // Short-circuit when no opencode credential is supplied: the runner
        // must not emit a no-op bash heredoc just for ceremony.
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.Equal(-1, FindMaterialisationScriptIndex(sandbox.Execs));
        Assert.True(FindOpencodeExecIndex(sandbox.Execs) >= 0);
    }

    [Fact]
    public async Task RunAsync_CredentialWithoutAuthJsonKey_DoesNotRunMaterialisationScript()
    {
        // Distinct branch from the null-credential case: a non-null credential
        // whose env dict does NOT contain OPENCODE_AUTH_JSON (e.g. a bundle
        // assembled from an unrelated env-var mapping) must also skip
        // materialisation rather than write empty content to the destination
        // file. Pins the OR shape in PrepareSandboxAsync so a regression that
        // swapped it for AND, or renamed the OPENCODE_AUTH_JSON key, would
        // be caught here.
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Opencode,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Intentionally a different key — represents a bundle that
                // somehow reached the runner without the auth file payload.
                ["SOMETHING_ELSE"] = "value",
            },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        Assert.Equal(-1, FindMaterialisationScriptIndex(sandbox.Execs));
        Assert.True(FindOpencodeExecIndex(sandbox.Execs) >= 0);
    }

    [Fact]
    public async Task RunAsync_WithCredential_MaterialisationRunsBeforeOpencodeCli()
    {
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"providers":{"deepseek":{"apiKey":"sk-test"}}}""");

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        var matIdx = FindMaterialisationScriptIndex(sandbox.Execs);
        var cliIdx = FindOpencodeExecIndex(sandbox.Execs);
        Assert.True(matIdx >= 0, "materialisation script must be executed");
        Assert.True(cliIdx >= 0, "opencode CLI must be invoked");
        Assert.True(matIdx < cliIdx, "materialisation must run before the opencode CLI");
    }

    [Fact]
    public async Task RunAsync_MaterialisationScript_UsesXdgDefaultWhenDestPathUnset()
    {
        // No OPENCODE_AUTH_DEST_PATH supplied ⇒ the in-sandbox script must
        // fall back to the XDG default at $HOME/.local/share/opencode/auth.json.
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"x":1}""");

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        var matIdx = FindMaterialisationScriptIndex(sandbox.Execs);
        Assert.True(matIdx >= 0);
        var exec = sandbox.Execs[matIdx];
        Assert.Equal(".local/share/opencode/auth.json", exec.Argv[4]);
        Assert.Equal("", exec.Argv[5]);
    }

    [Fact]
    public async Task RunAsync_MaterialisationScript_UsesCredentialDestinationOverride()
    {
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"x":1}""", "$HOME/.config/opencode/auth.json");

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        var matIdx = FindMaterialisationScriptIndex(sandbox.Execs);
        Assert.True(matIdx >= 0);
        var exec = sandbox.Execs[matIdx];
        Assert.Equal(".local/share/opencode/auth.json", exec.Argv[4]);
        Assert.Equal("$HOME/.config/opencode/auth.json", exec.Argv[5]);
    }

    [Fact]
    public async Task RunAsync_MaterialisationScript_WritesViaPrivateTempFile()
    {
        // Auth file (and parent dir) must end up at 0700/0600 without
        // following a pre-created symlink. The shared writer creates parents
        // with mode 0700, writes stdin to a private temp file, then renames it
        // over the destination.
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"x":1}""");

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        var script = sandbox.Execs[FindMaterialisationScriptIndex(sandbox.Execs)].Argv[2];
        Assert.Contains("mkdir -m 700", script, StringComparison.Ordinal);
        Assert.Contains("mktemp", script, StringComparison.Ordinal);
        Assert.Contains("cat > \"$tmp\"", script, StringComparison.Ordinal);
        Assert.Contains("mv -f -T", script, StringComparison.Ordinal);
        Assert.Contains("credential destination parent is a symlink", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MaterialisationScript_ChmodsAuthFileTo600()
    {
        // chmod 600 is a defense-in-depth backstop after the temp-file write
        // and atomic rename. It pins the final mode regardless of filesystem
        // defaults or a pre-existing destination.
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"x":1}""");

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        var script = sandbox.Execs[FindMaterialisationScriptIndex(sandbox.Execs)].Argv[2];
        var catIdx = script.IndexOf("cat > \"$tmp\"", StringComparison.Ordinal);
        var chmodIdx = script.IndexOf("chmod 600", StringComparison.Ordinal);
        Assert.True(chmodIdx >= 0, "script must chmod the auth file to 0600");
        Assert.True(catIdx >= 0, "script must write stdin into the private temp file");
        Assert.True(catIdx < chmodIdx, "chmod must run after the stdin write");
    }

    [Fact]
    public async Task RunAsync_MaterialisationFailure_FailsRunAndDoesNotInvokeOpencode()
    {
        // If the bash heredoc fails (e.g. sandbox FS readonly), the runner
        // must surface a meaningful Summary and skip the opencode CLI rather
        // than charge ahead with no auth file in place.
        var sandbox = new RecordingSandbox(authWriteExitCode: 13);
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"x":1}""");

        var result = await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        Assert.False(result.Success);
        Assert.Contains("opencode auth", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13", result.Summary);
        Assert.Equal(-1, FindOpencodeExecIndex(sandbox.Execs));
    }

    [Fact]
    public async Task RunAsync_MaterialisationScript_DoesNotEmbedCredentialBytesInArgv()
    {
        // The credential bytes must flow via stdin, NOT be interpolated into
        // the bash heredoc or argv — otherwise the secret would appear in any
        // command-line audit log the orchestrator captures.
        var sandbox = new RecordingSandbox();
        var runner = new OpencodeAgentRunner();
        const string secret = "sk-deepseek-supersecretvalue-do-not-leak";
        var cred = OpencodeCred("{\"providers\":{\"deepseek\":{\"apiKey\":\"" + secret + "\"}}}");

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        var script = sandbox.Execs[FindMaterialisationScriptIndex(sandbox.Execs)].Argv[2];
        Assert.DoesNotContain(secret, script);
        var authExec = sandbox.Execs[FindMaterialisationScriptIndex(sandbox.Execs)];
        Assert.DoesNotContain(secret, authExec.Argv);
        Assert.Equal(cred.EnvironmentVariables["OPENCODE_AUTH_JSON"], authExec.Stdin);
        Assert.DoesNotContain("OPENCODE_AUTH_JSON", script);
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_MissingAuth_ReturnsNull()
    {
        var runner = new OpencodeAgentRunner();
        Assert.Null(runner.GetTextOnlyUnavailabilityReason(credential: null));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_EmptyCredentialBundle_ReturnsReason()
    {
        var runner = new OpencodeAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Opencode,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
        Assert.Equal(
            "OPENCODE_AUTH_JSON is required when a credential bundle is supplied",
            runner.GetTextOnlyUnavailabilityReason(cred));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_WithAuthJson_ReturnsNull()
    {
        var runner = new OpencodeAgentRunner();
        Assert.Null(runner.GetTextOnlyUnavailabilityReason(OpencodeCred("""{"token":"x"}""")));
    }

    [Fact]
    public void TextOnlyRequiresSandbox_IsTrue()
    {
        ITextOnlyAgentRunner runner = new OpencodeAgentRunner();

        Assert.True(runner.TextOnlyRequiresSandbox);
    }

    [Fact]
    public async Task RunTextOnlyAsync_InvokesOpencodeRunWithModelAndStdin()
    {
        const string prompt = "resolve this conflict";
        var sandbox = new TextOnlyRecordingSandbox("resolved json");
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["opencode"] = "deepseek-v4-flash",
            });
        var runner = new OpencodeAgentRunner(defaults);
        var cred = OpencodeCred("""{"token":"x"}""");

        var result = await runner.RunTextOnlyAsync(prompt, cred, sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        Assert.Equal("resolved json", result.Output);
        var agentExec = sandbox.Execs.Last();
        Assert.Equal("opencode", agentExec.Argv[0]);
        Assert.Equal("run", agentExec.Argv[1]);
        Assert.Contains("--model", agentExec.Argv);
        Assert.Contains("deepseek-v4-flash", agentExec.Argv);
        Assert.Equal(prompt, agentExec.Stdin);
        Assert.Equal(SandboxAgentOutputTransportPreference.ExecPipe, agentExec.AgentOutputTransport);
    }

    [Fact]
    public async Task RunTextOnlyAsync_HttpIngestSandboxPrefersDetachedBatchLaunch()
    {
        var sandbox = new TextOnlyRecordingSandbox(
            "resolved json",
            SandboxAgentOutputTransportKind.HttpIngest);
        var runner = new OpencodeAgentRunner();
        var cred = OpencodeCred("""{"token":"x"}""");

        var result = await runner.RunTextOnlyAsync("prompt", cred, sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        var agentExec = sandbox.Execs.Last(e => e.Argv.Count >= 2 && e.Argv[0] == "opencode" && e.Argv[1] == "run");
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, agentExec.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, agentExec.LaunchMode);
    }

    [Fact]
    public async Task RunTextOnlyAsync_WithReasoningFlag_AppendsFlagToArgv()
    {
        var prior = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
        Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", "--reasoning-effort");
        try
        {
            var sandbox = new TextOnlyRecordingSandbox("ok");
            var runner = new OpencodeAgentRunner();
            var cred = OpencodeCred("""{"token":"x"}""");

            var result = await runner.RunTextOnlyAsync(
                "prompt", cred, sandbox: sandbox, workingDirectory: "/work", reasoningMode: "high");

            Assert.True(result.Success);
            var agentExec = sandbox.Execs.Last();
            Assert.Contains("--reasoning-effort", agentExec.Argv);
            Assert.Contains("high", agentExec.Argv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", prior);
        }
    }

    /// <summary>
    /// Sandbox that records every exec invocation and lets the test stub a
    /// specific exit code for the auth-materialisation bash script.
    /// </summary>
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
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[1] == "-c"
                && (exec.Argv[2].Contains("OPENCODE_AUTH_JSON", StringComparison.Ordinal)
                    || (exec.Argv.Count >= 5 && exec.Argv[4] == ".local/share/opencode/auth.json")))
            {
                return Task.FromResult(new SandboxExecResult(_authWriteExitCode, "", "auth stderr"));
            }
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TextOnlyRecordingSandbox(
        string agentStdout,
        SandboxAgentOutputTransportKind transportKind = SandboxAgentOutputTransportKind.ExecPipe) : ISandbox
    {
        public string Id => "recording-text-only";
        public SandboxAgentOutputTransportKind AgentOutputTransportKind { get; } = transportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => AgentOutputTransportKind == SandboxAgentOutputTransportKind.HttpIngest
            ? SandboxBatchLaunchMode.Detached
            : SandboxBatchLaunchMode.Attached;
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[1] == "-c"
                && (exec.Argv[2].Contains("OPENCODE_AUTH_JSON", StringComparison.Ordinal)
                    || (exec.Argv.Count >= 5 && exec.Argv[4] == ".local/share/opencode/auth.json")))
            {
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            if (exec.Argv.Count >= 2 && exec.Argv[0] == "opencode" && exec.Argv[1] == "run")
                return Task.FromResult(new SandboxExecResult(0, agentStdout, ""));

            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
