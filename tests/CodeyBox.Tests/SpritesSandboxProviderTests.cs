using System.Net;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    public async Task ExecAsync_UsesWebSocketQueryEnv_DemultiplexesFrames_AndSendsStdinFrames()
    {
        var socket = new FakeSpritesWebSocket();
        socket.EnqueueText("""{"type":"session_info","session_id":42,"command":"bash","created":0,"cols":0,"rows":0,"is_owner":true,"tty":false}""");
        socket.EnqueueBinary([1, (byte)'o', (byte)'u', (byte)'t']);
        socket.EnqueueBinary([2, (byte)'e', (byte)'r', (byte)'r']);
        socket.EnqueueText("""{"type":"exit","exit_code":7}""");

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
        Assert.Equal(["bash", "-lc", "cat"], query["cmd"]);
        Assert.Equal(["/work"], query["dir"]);
        Assert.Contains("BASE=1", query["env"]);
        Assert.Contains("SECRET=env-value", query["env"]);
        Assert.DoesNotContain(query.Keys, k => k.Equals("stdin", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("sprite-token", socket.BearerToken);
        Assert.Contains(socket.SentFrames, frame => frame.SequenceEqual(new byte[] { 0, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }));
        Assert.Contains(socket.SentFrames, frame => frame.SequenceEqual(new byte[] { 4 }));
    }

    [Fact]
    public async Task CreateAsync_AllowsCredentialTmpfsDirectory_ForEnvironmentOnlyCredentials()
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
        var provider = NewProvider(handler, new SingleSpritesWebSocketFactory(socket), new SpritesSandboxOptions { Token = "sprite-token" });

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = SandboxConventions.CredentialsDir, Tmpfs = true }],
        });

        await sandbox.DisposeAsync();

        var query = ParseQuery(socket.ConnectedUri!);
        Assert.Equal(["mkdir", "-p", SandboxConventions.CredentialsDir], query["cmd"]);
    }

    [Fact]
    public async Task ExecAsync_RefusesCredentialFileMaterialization()
    {
        var socket = new FakeSpritesWebSocket();
        var sandbox = NewSandbox(socket, new SandboxSpec { ImageReference = "ignored" });

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "umask 077 && cat > \"$0\"", $"{SandboxConventions.CredentialsDir}/auth.json"],
            Stdin = """{"secret":true}""",
        }));

        Assert.Contains("does not expose tmpfs credential storage", ex.Message, StringComparison.Ordinal);
        Assert.Null(socket.ConnectedUri);
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

    private static SpritesSandboxProvider NewProvider(
        RecordingHttpHandler handler,
        ISpritesWebSocketFactory webSocketFactory,
        SpritesSandboxOptions options) =>
        new(
            () => options,
            new HttpClient(handler),
            webSocketFactory,
            NullLogger<SpritesSandboxProvider>.Instance);

    private static SpritesSandbox NewSandbox(FakeSpritesWebSocket socket, SandboxSpec spec)
    {
        var client = new SpritesApiClient(new HttpClient(new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))));
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
