using System.Net;
using System.Net.WebSockets;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Sprites;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class SpritesSandboxProviderTests
{
    [Fact]
    public async Task CreateAsync_UsesRc30CreateSchema_AppliesNetworkPolicy_AndDeletesOnDispose()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites")
                return JsonResponse("""{"name":"created"}""", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Post && request.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal) &&
                request.PathAndQuery.EndsWith("/policy/network", StringComparison.Ordinal))
            {
                return JsonResponse("""{"rules":[]}""");
            }
            if (request.Method == HttpMethod.Delete && request.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var provider = NewProvider(
            handler,
            new EmptySpritesWebSocketFactory(),
            new SpritesSandboxOptions
            {
                Token = "sprite-token",
                WaitForCapacity = true,
                NetworkProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["agents"] = ["api.openai.com"],
                },
            });

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy
            {
                AllowedHosts = ["https://github.com:443"],
                HostGitEndpoint = "git.internal:9418",
                ProfileName = "agents",
            },
        });

        Assert.StartsWith("codeybox-", sandbox.Id, StringComparison.Ordinal);
        await sandbox.DisposeAsync();

        var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites");
        Assert.Equal("Bearer", create.AuthorizationScheme);
        Assert.Equal("sprite-token", create.AuthorizationParameter);
        using (var doc = JsonDocument.Parse(create.Body))
        {
            var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(["name", "url_settings", "wait_for_capacity"], properties);
            Assert.StartsWith("codeybox-", doc.RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);
            Assert.True(doc.RootElement.GetProperty("wait_for_capacity").GetBoolean());
            Assert.Equal("sprite", doc.RootElement.GetProperty("url_settings").GetProperty("auth").GetString());
            Assert.False(doc.RootElement.TryGetProperty("cpu", out _));
            Assert.False(doc.RootElement.TryGetProperty("memory", out _));
            Assert.False(doc.RootElement.TryGetProperty("region", out _));
        }

        var policy = Assert.Single(handler.Requests, r => r.PathAndQuery.EndsWith("/policy/network", StringComparison.Ordinal));
        using (var doc = JsonDocument.Parse(policy.Body))
        {
            var rules = doc.RootElement.GetProperty("rules")
                .EnumerateArray()
                .Select(r => (Domain: r.GetProperty("domain").GetString(), Action: r.GetProperty("action").GetString()))
                .ToArray();
            Assert.Contains(("api.openai.com", "allow"), rules);
            Assert.Contains(("github.com", "allow"), rules);
            Assert.Contains(("git.internal", "allow"), rules);
            Assert.Equal(("*", "deny"), rules[^1]);
        }

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecAsync_SendsEnvironmentOverStdin_DemultiplexesFrames_AndSendsStdinFrames()
    {
        var socket = new FakeSpritesWebSocket();
        socket.EnqueueText("""{"type":"session_info","session_id":42,"command":"bash","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
        socket.EnqueueBinary([1, (byte)'o', (byte)'u', (byte)'t']);
        socket.EnqueueBinary([2, (byte)'e', (byte)'r', (byte)'r']);
        socket.EnqueueBinary([3, 7]);

        var sandbox = NewSandbox(socket, new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            Environment = new Dictionary<string, string> { ["BASE"] = "1" },
        });

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-lc", "cat"],
            WorkingDirectory = "/work",
            Stdin = "hello",
            ExtraEnvironment = new Dictionary<string, string> { ["SECRET"] = "env-value" },
        });

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.Stdout);
        Assert.Equal("err", result.Stderr);

        Assert.NotNull(socket.ConnectedUri);
        var query = ParseQuery(socket.ConnectedUri!);
        AssertWrappedCommand(query, ["bash", "-lc", "cat"]);
        Assert.Equal(["/work"], query["dir"]);
        Assert.Equal(["false"], query["tty"]);
        Assert.False(query.ContainsKey("env"));
        Assert.DoesNotContain("env-value", socket.ConnectedUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(query.Keys, k => k.Equals("stdin", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("sprite-token", socket.BearerToken);
        Assert.Contains(socket.SentFrames, frame =>
            frame.Length > 1 &&
            frame[0] == 0 &&
            Encoding.UTF8.GetString(frame[1..]).EndsWith("hello", StringComparison.Ordinal));
        Assert.Contains(socket.SentFrames, frame => frame.SequenceEqual(new byte[] { 4 }));
    }

    [Fact]
    public async Task CreateAsync_AllowsCredentialTmpfsPlaceholderWithoutPersistingDirectory()
    {
        var handler = SuccessfulLifecycleHandler();
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = SandboxConventions.CredentialsDir, Tmpfs = true }],
        });

        await sandbox.DisposeAsync();

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites");
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_AllowsDefaultWorkTmpfsMount()
    {
        var sockets = new QueueSpritesWebSocketFactory(SuccessfulSocket(sessionId: 1));
        var provider = NewProvider(SuccessfulLifecycleHandler(), sockets, new SpritesSandboxOptions { Token = "sprite-token" });

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true }],
        });

        await sandbox.DisposeAsync();

        Assert.Single(sockets.Created);
        AssertWrappedCommand(ParseQuery(sockets.Created[0].ConnectedUri!), ["mkdir", "-p", SandboxConventions.WorkDir]);
    }

    [Fact]
    public async Task CreateAsync_RejectsCredentialHostFileMount_BeforeProvisioning()
    {
        var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });
        using var temp = new TempDirectory();
        var authFile = Path.Combine(temp.Path, "auth.json");
        File.WriteAllText(authFile, "{}");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount
                {
                    SandboxPath = $"{SandboxConventions.CredentialsDir}/auth.json",
                    HostPath = authFile,
                    ReadOnly = false,
                },
            ],
        }));

        Assert.Contains("refusing credential mount", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateAsync_CreatesNonSecretTmpfsDirectory_WhenDowngradeExplicitlyAllowed()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites")
                return JsonResponse("""{"name":"created"}""", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Post && request.PathAndQuery.EndsWith("/policy/network", StringComparison.Ordinal))
                return JsonResponse("""{"rules":[]}""");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var socket = new FakeSpritesWebSocket();
        socket.EnqueueText("""{"type":"session_info","session_id":1,"command":"mkdir","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
        socket.EnqueueText("""{"type":"exit","exit_code":0}""");
        var provider = NewProvider(
            handler,
            new SingleSpritesWebSocketFactory(socket),
            new SpritesSandboxOptions
            {
                Token = "sprite-token",
                AllowPersistentTmpfsDowngrade = true,
            });

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = "/scratch", Tmpfs = true, ReadOnly = false }],
        });

        await sandbox.DisposeAsync();

        var query = ParseQuery(socket.ConnectedUri!);
        AssertWrappedCommand(query, ["mkdir", "-p", "/scratch"]);
    }

    [Fact]
    public async Task CreateAsync_RunsSetupCommandsInOrder_AndSkipsBlankCommands()
    {
        var sockets = new QueueSpritesWebSocketFactory(
            SuccessfulSocket(sessionId: 1),
            SuccessfulSocket(sessionId: 2));
        var provider = NewProvider(
            SuccessfulLifecycleHandler(),
            sockets,
            new SpritesSandboxOptions
            {
                Token = "sprite-token",
                SetupCommands = ["first", "   ", "second"],
            });

        var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        await sandbox.DisposeAsync();

        Assert.Equal(2, sockets.Created.Count);
        AssertWrappedCommand(ParseQuery(sockets.Created[0].ConnectedUri!), ["bash", "-lc", "first"]);
        AssertWrappedCommand(ParseQuery(sockets.Created[1].ConnectedUri!), ["bash", "-lc", "second"]);
    }

    [Fact]
    public async Task CreateAsync_SetupCommandFailureDeletesSpriteAndSurfacesStderrTail()
    {
        var handler = SuccessfulLifecycleHandler();
        var sockets = new QueueSpritesWebSocketFactory(
            ExitingSocket(sessionId: 1, exitCode: 12, stderr: "setup failed"));
        var provider = NewProvider(
            handler,
            sockets,
            new SpritesSandboxOptions
            {
                Token = "sprite-token",
                SetupCommands = ["bad"],
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" }));

        Assert.Contains("Sprites setup command failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("setup failed", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecAsync_RefusesStdinWritesToCredentialDirectory()
    {
        var socket = new FakeSpritesWebSocket();
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" });

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash",
                "-c",
                "cat > /run/codeybox/creds/auth.json",
            ],
            Stdin = "{}",
        }));

        Assert.Contains("does not expose tmpfs credential storage", ex.Message, StringComparison.Ordinal);
        Assert.Null(socket.ConnectedUri);
    }

    [Fact]
    public async Task FileBackedCredentialRunner_FailsBeforePrepareScript()
    {
        var socket = new FakeSpritesWebSocket();
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" });
        var runner = new CodexAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = "{}" },
            new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.False(result.Success);
        Assert.Contains("file-backed credentials are not supported", result.Summary, StringComparison.Ordinal);
        Assert.Null(socket.ConnectedUri);
    }

    [Fact]
    public async Task RunResumedAsync_FileBackedCredentialRunner_FailsBeforeScratchpadRestore()
    {
        var socket = new FakeSpritesWebSocket();
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" });
        var runner = new CodexAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = "{}" },
            new Dictionary<string, string>());

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "prompt",
            credential,
            new AgentResumeContext("checkpoint-ref"));

        Assert.False(result.Success);
        Assert.Contains("file-backed credentials are not supported", result.Summary, StringComparison.Ordinal);
        Assert.Null(socket.ConnectedUri);
    }

    [Fact]
    public async Task RunTextOnlyAsync_FileBackedCredentialRunner_ReturnsFailureBeforePrepareScript()
    {
        var socket = new FakeSpritesWebSocket();
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" });
        var runner = new CursorAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{}" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync(
            "prompt",
            credential,
            sandbox: sandbox,
            workingDirectory: "/work");

        Assert.False(result.Success);
        Assert.Contains("file-backed credentials are not supported", result.Summary, StringComparison.Ordinal);
        Assert.Null(socket.ConnectedUri);
    }

    [Fact]
    public async Task CreateAsync_StagesReadOnlyMountsWithoutSyncBack()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "mirror.txt"), "mirror");
        var sockets = new QueueSpritesWebSocketFactory(SuccessfulSocket(sessionId: 1));
        var provider = NewProvider(SuccessfulLifecycleHandler(), sockets, new SpritesSandboxOptions { Token = "sprite-token" });

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = temp.Path, ReadOnly = true }],
        });

        await sandbox.DisposeAsync();

        Assert.Single(sockets.Created);
    }

    [Fact]
    public async Task CreateAsync_RejectsUnknownNetworkProfile_AndDeletesCreatedSprite()
    {
        var handler = SuccessfulLifecycleHandler();
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "missing" },
        }));

        Assert.Contains("network profile 'missing' is not configured", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites");
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_DeleteFailureAfterCreate_ThrowsDeferredWithRetainedSprite()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites")
                return JsonResponse("""{"name":"created"}""", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Post && request.PathAndQuery.EndsWith("/policy/network", StringComparison.Ordinal))
                return JsonResponse("""{"error":"policy failed"}""", HttpStatusCode.InternalServerError);
            if (request.Method == HttpMethod.Delete && request.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal))
                return JsonResponse("""{"error":"delete failed"}""", HttpStatusCode.InternalServerError);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
        }));

        Assert.Equal("sprites", ex.Provider);
        Assert.Equal("create-cleanup", ex.Operation);
        Assert.StartsWith("codeybox-", ex.RetainedSandboxName, StringComparison.Ordinal);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActiveSnapshots_ReportSpriteUntilDispose()
    {
        var handler = SuccessfulLifecycleHandler();
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });
        var workItemId = new WorkItemId(Guid.NewGuid());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });

        var active = Assert.Single(provider.SnapshotActiveSandboxes());
        Assert.Equal(workItemId, active.WorkItemId);
        Assert.Same(sandbox, active.Sandbox);
        var progress = Assert.Single(provider.SnapshotActiveSandboxProgress());
        Assert.Equal(workItemId, progress.WorkItemId);
        Assert.Equal(sandbox.Id, progress.SandboxId);

        await sandbox.DisposeAsync();

        Assert.Empty(provider.SnapshotActiveSandboxes());
        Assert.Empty(provider.SnapshotActiveSandboxProgress());
    }

    [Fact]
    public async Task ReadValidatedOptions_ResolvesTokenFromEnvironment_AndRejectsMissingToken()
    {
        using var env = new EnvironmentVariableScope("SPRITES_TEST_TOKEN", "from-env");
        var handler = SuccessfulLifecycleHandler();
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions
        {
            Token = null,
            TokenEnvironmentVariable = "SPRITES_TEST_TOKEN",
        });

        var envSandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        await envSandbox.DisposeAsync();

        var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites");
        Assert.Equal("from-env", create.AuthorizationParameter);

        using var missing = new EnvironmentVariableScope("SPRITES_MISSING_TOKEN", null);
        var missingProvider = NewProvider(
            new RecordingHttpHandler(_ => throw new InvalidOperationException("HTTP should not be called")),
            new EmptySpritesWebSocketFactory(),
            new SpritesSandboxOptions { Token = null, TokenEnvironmentVariable = "SPRITES_MISSING_TOKEN" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missingProvider.CreateAsync(new SandboxSpec { ImageReference = "ignored" }));
        Assert.Contains("SPRITES_MISSING_TOKEN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadValidatedOptions_RejectsHttpBaseUrl_UnlessExplicitlyAllowed()
    {
        var provider = NewProvider(
            new RecordingHttpHandler(_ => throw new InvalidOperationException("HTTP should not be called")),
            new EmptySpritesWebSocketFactory(),
            new SpritesSandboxOptions { Token = "sprite-token", ApiBaseUrl = "http://api.sprites.test" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" }));
        Assert.Contains("must use https", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url", "SPRITES_TOKEN", "codeybox-", "sprite", "ApiBaseUrl")]
    [InlineData("https://api.sprites.dev", "", "codeybox-", "sprite", "TokenEnvironmentVariable")]
    [InlineData("https://api.sprites.dev", "SPRITES_TOKEN", "bad_prefix", "sprite", "NamePrefix")]
    [InlineData("https://api.sprites.dev", "SPRITES_TOKEN", "codeybox-", "invalid", "UrlAuth")]
    public async Task ReadValidatedOptions_RejectsInvalidConfiguration(
        string apiBaseUrl,
        string tokenEnvironmentVariable,
        string namePrefix,
        string urlAuth,
        string expectedMessage)
    {
        var provider = NewProvider(
            new RecordingHttpHandler(_ => throw new InvalidOperationException("HTTP should not be called")),
            new EmptySpritesWebSocketFactory(),
            new SpritesSandboxOptions
            {
                ApiBaseUrl = apiBaseUrl,
                Token = "sprite-token",
                TokenEnvironmentVariable = tokenEnvironmentVariable,
                NamePrefix = namePrefix,
                UrlAuth = urlAuth,
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" }));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeLeakedAsync_RejectsUnsafeNames()
    {
        var provider = NewProvider(
            new RecordingHttpHandler(_ => throw new InvalidOperationException("HTTP should not be called")),
            new EmptySpritesWebSocketFactory(),
            new SpritesSandboxOptions { Token = "sprite-token" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.DisposeLeakedAsync("not-codeybox", CancellationToken.None));
    }

    [Fact]
    public async Task WritableHostMount_IsUploaded_AndSyncedBackAfterExec()
    {
        using var host = new TempDirectory();
        File.WriteAllText(Path.Combine(host.Path, "before.txt"), "before");
        var changedArchive = CreateDirectoryArchiveBase64(("after.txt", "after"));
        var sockets = new QueueSpritesWebSocketFactory(
            SuccessfulSocket(sessionId: 1),
            SuccessfulSocket(sessionId: 2),
            SuccessfulSocket(sessionId: 3, stdout: changedArchive));
        var provider = NewProvider(SuccessfulLifecycleHandler(), sockets, new SpritesSandboxOptions { Token = "sprite-token" });

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = host.Path, ReadOnly = false }],
        });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(host.Path, "before.txt")));

        await sandbox.DisposeAsync();

        Assert.False(File.Exists(Path.Combine(host.Path, "before.txt")));
        Assert.Equal("after", File.ReadAllText(Path.Combine(host.Path, "after.txt")));
        var upload = sockets.Created[0];
        Assert.Contains(upload.SentFrames, frame => frame[0] == 0 && frame.Length > 1);
    }

    [Fact]
    public async Task WritableHostFileMount_IsUploaded_AndSyncedBackOnDispose()
    {
        using var host = new TempDirectory();
        var hostFile = Path.Combine(host.Path, "state.txt");
        File.WriteAllText(hostFile, "before");
        var changedFile = Convert.ToBase64String(Encoding.UTF8.GetBytes("after"));
        var sockets = new QueueSpritesWebSocketFactory(
            SuccessfulSocket(sessionId: 1),
            SuccessfulSocket(sessionId: 2),
            SuccessfulSocket(sessionId: 3, stdout: changedFile));
        var provider = NewProvider(SuccessfulLifecycleHandler(), sockets, new SpritesSandboxOptions { Token = "sprite-token" });

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = "/tmp/state.txt", HostPath = hostFile, ReadOnly = false }],
        });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });
        Assert.True(result.Success);
        Assert.Equal("before", File.ReadAllText(hostFile));

        await sandbox.DisposeAsync();

        Assert.Equal("after", File.ReadAllText(hostFile));
        AssertWrappedCommand(ParseQuery(sockets.Created[0].ConnectedUri!), ["sh", "-c", "set -eu; mkdir -p \"$(dirname \"$1\")\"; base64 -d > \"$1\"", "_", "/tmp/state.txt"]);
        AssertWrappedCommand(ParseQuery(sockets.Created[2].ConnectedUri!), ["base64", "-w0", "/tmp/state.txt"]);
    }

    [Fact]
    public async Task CorruptSyncArchive_DoesNotDeleteHostMount()
    {
        using var host = new TempDirectory();
        File.WriteAllText(Path.Combine(host.Path, "keep.txt"), "keep");
        var sockets = new QueueSpritesWebSocketFactory(
            SuccessfulSocket(sessionId: 1),
            SuccessfulSocket(sessionId: 2),
            SuccessfulSocket(sessionId: 3, stdout: "not-base64"));
        var provider = NewProvider(SuccessfulLifecycleHandler(), sockets, new SpritesSandboxOptions { Token = "sprite-token" });

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = host.Path, ReadOnly = false }],
        });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });
        Assert.True(result.Success);

        await sandbox.DisposeAsync();

        Assert.True(Directory.Exists(host.Path));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(host.Path, "keep.txt")));
    }

    [Fact]
    public async Task ExecAsync_CancellationAfterSessionInfo_PostsKillForParsedSession()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"type":"signal"}
                        {"type":"exited"}
                        {"type":"complete","exit_code":143}
                        """,
                        Encoding.UTF8,
                        "application/x-ndjson"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var socket = new BlockingAfterSessionInfoWebSocket(sessionId: 42);
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" }, httpHandler: handler);
        using var cts = new CancellationTokenSource();

        var exec = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "100"] }, cts.Token);
        await socket.SessionInfoDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exec);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill");
    }

    [Fact]
    public async Task ExecAsync_CancellationBeforeSessionInfo_DoesNotPostKill()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var socket = new BlockingBeforeSessionInfoWebSocket();
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" }, httpHandler: handler);
        using var cts = new CancellationTokenSource();

        var exec = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "100"] }, cts.Token);
        await socket.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exec);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecAsync_NonCancellationExceptionAfterSessionInfo_PostsKill()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"type":"signal"}
                        {"type":"complete","exit_code":143}
                        """,
                        Encoding.UTF8,
                        "application/x-ndjson"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var socket = new FakeSpritesWebSocket();
        socket.EnqueueText("""{"type":"session_info","session_id":42,"command":"bash","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
        socket.EnqueueText("""{"type":""");
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" }, httpHandler: handler);

        await Assert.ThrowsAnyAsync<JsonException>(() => sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill");
    }

    [Fact]
    public async Task KillExecAsync_ThrowsWhenEventsEndBeforeComplete()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"type":"signal"}
                        {"type":"exited"}
                        """,
                        Encoding.UTF8,
                        "application/x-ndjson"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new SpritesApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.KillExecAsync(new SpritesSandboxOptions { Token = "sprite-token" }, "codeybox-test", 42, CancellationToken.None));

        Assert.Contains("ended before a complete event", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillExecAsync_EmptyBodyReturnsWithoutThrowing()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "application/x-ndjson"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new SpritesApiClient(new HttpClient(handler));

        await client.KillExecAsync(new SpritesSandboxOptions { Token = "sprite-token" }, "codeybox-test", 42, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/v1/sprites/codeybox-test/exec/42/kill");
    }

    [Fact]
    public async Task ListAllManagedAsync_RepeatsPrefixOnEachPage_AndDisposeLeakedDeletesByName()
    {
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.PathAndQuery == "/v1/sprites?prefix=codeybox-&max_results=50")
            {
                return JsonResponse(
                    """{"sprites":[{"name":"codeybox-a","updated_at":"2026-01-02T00:00:00Z"}],"has_more":true,"next_continuation_token":"next"}""");
            }
            if (request.Method == HttpMethod.Get && request.PathAndQuery == "/v1/sprites/codeybox-a")
                return JsonResponse("""{"created_at":"2026-01-01T00:00:00Z"}""");
            if (request.Method == HttpMethod.Get && request.PathAndQuery == "/v1/sprites?prefix=codeybox-&max_results=50&continuation_token=next")
            {
                return JsonResponse(
                    """{"sprites":[{"name":"codeybox-b","updated_at":"2026-02-02T00:00:00Z"},{"name":"other","updated_at":"2026-03-03T00:00:00Z"}],"has_more":false}""");
            }
            if (request.Method == HttpMethod.Get && request.PathAndQuery == "/v1/sprites/codeybox-b")
                return JsonResponse("""{"created_at":"2026-02-01T00:00:00Z"}""");
            if (request.Method == HttpMethod.Delete && request.PathAndQuery == "/v1/sprites/codeybox-b")
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var provider = NewProvider(handler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);
        await provider.DisposeLeakedAsync("codeybox-b", CancellationToken.None);

        Assert.Equal(["codeybox-a", "codeybox-b"], managed.Select(s => s.Name).ToArray());
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), managed[0].CreatedAt);
        Assert.All(
            handler.Requests.Where(r => r.Method == HttpMethod.Get && r.PathAndQuery.StartsWith("/v1/sprites?", StringComparison.Ordinal)),
            r => Assert.Contains("prefix=codeybox-", r.PathAndQuery, StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            r => r.Method == HttpMethod.Get &&
                 r.PathAndQuery == "/v1/sprites?prefix=codeybox-&max_results=50&continuation_token=next");
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/v1/sprites/codeybox-b");
    }

    [Fact]
    public async Task ApiFailures_SurfaceDetail_AndDeleteNotFoundIsTolerated()
    {
        var listHandler = new RecordingHttpHandler(request =>
            request.Method == HttpMethod.Get
                ? JsonResponse("""{"error":"list failed"}""", HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var listProvider = NewProvider(listHandler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listProvider.ListAllManagedAsync(CancellationToken.None));

        Assert.Contains("list sprites failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("list failed", ex.Message, StringComparison.Ordinal);

        var deleteHandler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var deleteProvider = NewProvider(deleteHandler, new EmptySpritesWebSocketFactory(), new SpritesSandboxOptions { Token = "sprite-token" });

        await deleteProvider.DisposeLeakedAsync("codeybox-missing", CancellationToken.None);

        Assert.Single(deleteHandler.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/v1/sprites/codeybox-missing");
    }

    private static RecordingHttpHandler SuccessfulLifecycleHandler() =>
        new(request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/v1/sprites")
                return JsonResponse("""{"name":"created"}""", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Post && request.PathAndQuery.EndsWith("/policy/network", StringComparison.Ordinal))
                return JsonResponse("""{"rules":[]}""");
            if (request.Method == HttpMethod.Delete && request.PathAndQuery.StartsWith("/v1/sprites/codeybox-", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static FakeSpritesWebSocket SuccessfulSocket(int sessionId, string? stdout = null)
    {
        var socket = new FakeSpritesWebSocket();
        socket.EnqueueText($$"""{"type":"session_info","session_id":{{sessionId}},"command":"cmd","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
        if (stdout is not null)
            socket.EnqueueBinary([1, .. Encoding.UTF8.GetBytes(stdout)]);
        socket.EnqueueText("""{"type":"exit","exit_code":0}""");
        return socket;
    }

    private static FakeSpritesWebSocket ExitingSocket(int sessionId, int exitCode, string? stdout = null, string? stderr = null)
    {
        var socket = new FakeSpritesWebSocket();
        socket.EnqueueText($$"""{"type":"session_info","session_id":{{sessionId}},"command":"cmd","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
        if (stdout is not null)
            socket.EnqueueBinary([1, .. Encoding.UTF8.GetBytes(stdout)]);
        if (stderr is not null)
            socket.EnqueueBinary([2, .. Encoding.UTF8.GetBytes(stderr)]);
        socket.EnqueueText($$"""{"type":"exit","exit_code":{{exitCode}}}""");
        return socket;
    }

    private static string CreateDirectoryArchiveBase64(params (string RelativePath, string Contents)[] files)
    {
        using var temp = new TempDirectory();
        foreach (var (relativePath, contents) in files)
        {
            var fullPath = Path.Combine(temp.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            TarFile.CreateFromDirectory(temp.Path, gzip, includeBaseDirectory: false);
        return Convert.ToBase64String(output.ToArray());
    }

    private static SpritesSandboxProvider NewProvider(
        RecordingHttpHandler handler,
        ISpritesWebSocketFactory webSocketFactory,
        SpritesSandboxOptions options) =>
        new(
            () => options,
            new HttpClient(handler),
            webSocketFactory,
            NullLogger<SpritesSandboxProvider>.Instance);

    private static SpritesSandbox NewSandbox(
        ISpritesWebSocket socket,
        SandboxSpec spec,
        RecordingHttpHandler? httpHandler = null)
    {
        var client = new SpritesApiClient(new HttpClient(httpHandler ?? new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))));
        return new SpritesSandbox(
            "codeybox-test",
            spec,
            new SpritesSandboxOptions { Token = "sprite-token" },
            client,
            new SingleSpritesWebSocketFactory(socket),
            [],
            () => { },
            NullLogger<SpritesSandboxProvider>.Instance);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codeybox-sprites-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static Dictionary<string, List<string>> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = separator >= 0 ? part[..separator] : part;
            var value = separator >= 0 ? part[(separator + 1)..] : "";
            key = Uri.UnescapeDataString(key);
            value = Uri.UnescapeDataString(value);
            if (!result.TryGetValue(key, out var values))
            {
                values = [];
                result[key] = values;
            }
            values.Add(value);
        }
        return result;
    }

    private static void AssertWrappedCommand(Dictionary<string, List<string>> query, IReadOnlyList<string> expectedOriginalArgv)
    {
        var cmd = query["cmd"];
        Assert.True(cmd.Count >= expectedOriginalArgv.Count + 5, $"wrapped command had too few argv entries: {string.Join(" ", cmd)}");
        Assert.Equal("sh", cmd[0]);
        Assert.Equal("-c", cmd[1]);
        Assert.Contains("exec \"$@\"", cmd[2], StringComparison.Ordinal);
        Assert.Equal("_", cmd[3]);
        Assert.True(int.TryParse(cmd[4], NumberStyles.None, CultureInfo.InvariantCulture, out var envBytes), "env byte count should be numeric");
        Assert.True(envBytes > 0);
        Assert.Equal(expectedOriginalArgv, cmd.Skip(5).ToArray());
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<RequestSnapshot, HttpResponseMessage> _respond;

        public RecordingHttpHandler(Func<RequestSnapshot, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = new RequestSnapshot(
                request.Method,
                request.RequestUri?.PathAndQuery ?? "",
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body);
            Requests.Add(snapshot);
            return _respond(snapshot);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string PathAndQuery,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);

    private sealed class EmptySpritesWebSocketFactory : ISpritesWebSocketFactory
    {
        public ISpritesWebSocket Create() => throw new InvalidOperationException("WebSocket should not be opened");
    }

    private sealed class SingleSpritesWebSocketFactory : ISpritesWebSocketFactory
    {
        private readonly ISpritesWebSocket _socket;

        public SingleSpritesWebSocketFactory(ISpritesWebSocket socket)
        {
            _socket = socket;
        }

        public ISpritesWebSocket Create() => _socket;
    }

    private sealed class QueueSpritesWebSocketFactory : ISpritesWebSocketFactory
    {
        private readonly Queue<FakeSpritesWebSocket> _sockets;

        public QueueSpritesWebSocketFactory(params FakeSpritesWebSocket[] sockets)
        {
            _sockets = new Queue<FakeSpritesWebSocket>(sockets);
        }

        public List<FakeSpritesWebSocket> Created { get; } = [];

        public ISpritesWebSocket Create()
        {
            if (_sockets.Count == 0)
                throw new InvalidOperationException("No fake sprites websocket queued");
            var socket = _sockets.Dequeue();
            Created.Add(socket);
            return socket;
        }
    }

    private sealed class BlockingAfterSessionInfoWebSocket : ISpritesWebSocket
    {
        private readonly int _sessionId;
        private bool _sentSessionInfo;

        public BlockingAfterSessionInfoWebSocket(int sessionId)
        {
            _sessionId = sessionId;
        }

        public TaskCompletionSource SessionInfoDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri? ConnectedUri { get; private set; }
        public string? BearerToken { get; private set; }
        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken ct)
        {
            ConnectedUri = uri;
            BearerToken = bearerToken;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task<WebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_sentSessionInfo)
            {
                _sentSessionInfo = true;
                var payload = Encoding.UTF8.GetBytes(
                    $$"""{"type":"session_info","session_id":{{_sessionId}},"command":"sleep","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
                payload.CopyTo(buffer);
                SessionInfoDelivered.TrySetResult();
                return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, endOfMessage: true);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingBeforeSessionInfoWebSocket : ISpritesWebSocket
    {
        public TaskCompletionSource ReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken ct)
        {
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task<WebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            ReceiveStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSpritesWebSocket : ISpritesWebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Payload)> _incoming = new();

        public Uri? ConnectedUri { get; private set; }
        public string? BearerToken { get; private set; }
        public List<byte[]> SentFrames { get; } = [];
        public WebSocketState State { get; private set; } = WebSocketState.None;

        public void EnqueueText(string json) =>
            _incoming.Enqueue((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json)));

        public void EnqueueBinary(byte[] payload) =>
            _incoming.Enqueue((WebSocketMessageType.Binary, payload));

        public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken ct)
        {
            ConnectedUri = uri;
            BearerToken = bearerToken;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            Assert.Equal(WebSocketMessageType.Binary, messageType);
            Assert.True(endOfMessage);
            SentFrames.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_incoming.Count == 0)
            {
                State = WebSocketState.Closed;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));
            }

            var message = _incoming.Dequeue();
            message.Payload.CopyTo(buffer);
            return Task.FromResult(new WebSocketReceiveResult(message.Payload.Length, message.Type, endOfMessage: true));
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }
}
