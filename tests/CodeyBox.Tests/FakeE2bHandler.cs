using System.Net;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Tests;

/// <summary>
/// In-memory E2B API double covering both planes the provider needs: the
/// REST control plane (<c>/v2/sandboxes</c>, <c>/sandboxes/{id}</c>,
/// <c>/pause</c>, <c>/connect</c>, <c>/timeout</c>, <c>/snapshots</c>) and the
/// per-sandbox envd gateway (<c>/health</c>, <c>/commands</c>,
/// <c>/files</c>). Scripts the recorded-shape responses, supports failure
/// injection plus create/command gates for the concurrency tests, and records
/// which auth header each plane carried. Never touches the network.
/// </summary>
internal sealed class FakeE2bHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, SandboxState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _createGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _commandGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _created;
    private int _enteredCreates;
    private int _enteredCommands;
    private int _concurrentCreates;
    private int _maxConcurrentCreates;

    public sealed record RecordedRequest(
        HttpMethod Method,
        string Host,
        string Path,
        string Query,
        string Body,
        string? ApiKey,
        string? AccessToken)
    {
        public string FullPath => Path + Query;
    }

    public sealed record CommandInvocation(string Path, string Body);

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_sync) return _requests.ToList(); }
    }

    public List<CommandInvocation> Commands
    {
        get
        {
            lock (_sync)
            {
                return _requests
                    .Where(static r => r.Path == "/commands")
                    .Select(static r => new CommandInvocation(r.Path, r.Body))
                    .ToList();
            }
        }
    }

    public HttpStatusCode? FailCreateStatus { get; set; }

    public bool ThrowOnCreate { get; set; }

    public HttpStatusCode? FailCommandStatus { get; set; }

    public HttpStatusCode? FailWriteStatus { get; set; }

    public HttpStatusCode? FailReadStatus { get; set; }

    public string DefaultSandboxState { get; set; } = "running";

    public HttpStatusCode SandboxStatusCode { get; set; } = HttpStatusCode.OK;

    public byte[] ReadFileBody { get; set; } = [];

    public bool GateCreates { get; set; }

    public bool BlockCommands { get; set; }

    /// <summary>When true, create responses omit the envd access token (tests the provider's fail-closed path).</summary>
    public bool OmitAccessToken { get; set; }

    public Func<string, (int Exit, string Stdout, string Stderr)>? CommandResponder { get; set; }

    public int EnteredCreates => Volatile.Read(ref _enteredCreates);

    public int EnteredCommands => Volatile.Read(ref _enteredCommands);

    public int MaxConcurrentCreates
    {
        get { lock (_sync) return _maxConcurrentCreates; }
    }

    public void ReleaseCreates() => _createGate.TrySetResult();

    public void ReleaseCommands() => _commandGate.TrySetResult();

    /// <summary>Flips a known sandbox's lifecycle state (e.g. to stage a paused sandbox for resume).</summary>
    public void SetState(string sandboxId, string state)
    {
        lock (_sync)
        {
            if (_sandboxes.TryGetValue(sandboxId, out var existing))
            {
                _sandboxes[sandboxId] = existing with { State = state };
            }
        }
    }

    /// <summary>Adds a sandbox the provider must not claim (no managed metadata).</summary>
    public void AddUnmanaged(string sandboxId)
    {
        lock (_sync)
        {
            _unmanaged.Add(sandboxId);
        }
    }

    private readonly HashSet<string> _unmanaged = new(StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var query = request.RequestUri?.Query ?? string.Empty;
        var host = request.RequestUri?.Host ?? string.Empty;
        string body;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            body = string.Empty;
        }

        request.Headers.TryGetValues("X-API-KEY", out var apiKeys);
        request.Headers.TryGetValues("X-Access-Token", out var accessTokens);
        lock (_sync)
        {
            _requests.Add(new RecordedRequest(
                request.Method, host, path, query, body,
                apiKeys?.FirstOrDefault(), accessTokens?.FirstOrDefault()));
        }

        if (request.Method == HttpMethod.Post && path == "/v2/sandboxes")
        {
            return await HandleCreateAsync(ct).ConfigureAwait(false);
        }

        if (request.Method == HttpMethod.Get && path == "/v2/sandboxes")
        {
            return await HandleListAsync(ct).ConfigureAwait(false);
        }

        if (path.StartsWith("/sandboxes/", StringComparison.Ordinal)
            || path.StartsWith("/v2/sandboxes/", StringComparison.Ordinal))
        {
            return await HandleSandboxAsync(request.Method, path, body, ct).ConfigureAwait(false);
        }

        // Envd data plane (per-sandbox host).
        if (path == "/health")
        {
            return JsonResponse(HttpStatusCode.OK, "{}");
        }

        if (request.Method == HttpMethod.Post && path == "/commands")
        {
            return await HandleCommandAsync(body, ct).ConfigureAwait(false);
        }

        if (path == "/files")
        {
            return await HandleFilesAsync(request, body, ct).ConfigureAwait(false);
        }

        return JsonResponse(HttpStatusCode.NotFound, """{"message":"unknown path"}""");
    }

    private async Task<HttpResponseMessage> HandleCreateAsync(CancellationToken ct)
    {
        if (ThrowOnCreate)
        {
            throw new HttpRequestException("connection refused");
        }

        if (FailCreateStatus.HasValue)
        {
            return JsonResponse(FailCreateStatus.Value, """{"message":"injected create failure"}""");
        }

        Interlocked.Increment(ref _enteredCreates);
        if (GateCreates)
        {
            lock (_sync)
            {
                _concurrentCreates++;
                _maxConcurrentCreates = Math.Max(_maxConcurrentCreates, _concurrentCreates);
            }

            try
            {
                await _createGate.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _concurrentCreates--;
                }
            }
        }

        var id = $"sb_test_{Interlocked.Increment(ref _created)}";
        lock (_sync)
        {
            _sandboxes[id] = new SandboxState(DefaultSandboxState, $"envd_token_{id}");
        }

        return JsonResponse(HttpStatusCode.Created, JsonSerializer.Serialize(new
        {
            sandboxID = id,
            templateID = "base",
            domain = "e2b.app",
            envdVersion = "1.0.0",
            envdAccessToken = OmitAccessToken ? null : $"envd_token_{id}",
            trafficAccessToken = (string?)null,
            state = DefaultSandboxState,
            metadata = new Dictionary<string, string>(),
        }));
    }

    private Task<HttpResponseMessage> HandleListAsync(CancellationToken ct)
    {
        _ = ct;
        List<object> items;
        List<object> unmanaged;
        lock (_sync)
        {
            items = _sandboxes.Select(kvp => (object)new
            {
                sandboxID = kvp.Key,
                templateID = "base",
                domain = "e2b.app",
                state = kvp.Value.State,
                metadata = new Dictionary<string, string>
                {
                    ["codeybox-managed"] = "true",
                    ["codeybox-provider"] = "e2b",
                },
            }).ToList();
            unmanaged = _unmanaged.Select(id => (object)new
            {
                sandboxID = id,
                templateID = "base",
                domain = "e2b.app",
                state = "running",
                metadata = new Dictionary<string, string>(),
            }).ToList();
        }

        items.AddRange(unmanaged);
        return Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { sandboxes = items })));
    }

    private Task<HttpResponseMessage> HandleSandboxAsync(HttpMethod method, string path, string body, CancellationToken ct)
    {
        _ = body;
        _ = ct;
        var rest = path.StartsWith("/v2/sandboxes/", StringComparison.Ordinal)
            ? path.Substring("/v2/sandboxes/".Length)
            : path.Substring("/sandboxes/".Length);
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest.Substring(0, slash);
        var action = slash < 0 ? string.Empty : rest.Substring(slash);

        SandboxState? state;
        lock (_sync)
        {
            _sandboxes.TryGetValue(id, out state);
        }

        if (method == HttpMethod.Get && action == string.Empty)
        {
            if (SandboxStatusCode == HttpStatusCode.NotFound || state is null)
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """{"message":"sandbox not found"}"""));
            }

            return Task.FromResult(JsonResponse(SandboxStatusCode, JsonSerializer.Serialize(new
            {
                sandboxID = id,
                templateID = "base",
                domain = "e2b.app",
                state = state?.State ?? DefaultSandboxState,
                envdAccessToken = state?.AccessToken,
            })));
        }

        if (method == HttpMethod.Delete && action == string.Empty)
        {
            lock (_sync)
            {
                _sandboxes.Remove(id);
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        }

        if (method == HttpMethod.Post && action == "/pause")
        {
            lock (_sync)
            {
                if (_sandboxes.TryGetValue(id, out var existing))
                {
                    _sandboxes[id] = existing with { State = "paused" };
                }
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        }

        if (method == HttpMethod.Post && action == "/connect")
        {
            lock (_sync)
            {
                if (_sandboxes.TryGetValue(id, out var existing))
                {
                    _sandboxes[id] = existing with { State = "running", AccessToken = $"envd_token_{id}_resumed" };
                }
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                sandboxID = id,
                domain = "e2b.app",
                state = "running",
                envdAccessToken = $"envd_token_{id}_resumed",
            })));
        }

        if (method == HttpMethod.Post && action == "/timeout")
        {
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        }

        if (method == HttpMethod.Post && action == "/snapshots")
        {
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                snapshotID = "snap_test",
                name = "snap_test",
            })));
        }

        return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """{"message":"unknown sandbox action"}"""));
    }

    private async Task<HttpResponseMessage> HandleCommandAsync(string body, CancellationToken ct)
    {
        if (FailCommandStatus.HasValue)
        {
            return JsonResponse(FailCommandStatus.Value, """{"message":"injected command failure"}""");
        }

        Interlocked.Increment(ref _enteredCommands);
        if (BlockCommands)
        {
            await _commandGate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        var responder = CommandResponder ?? (_ => (0, string.Empty, string.Empty));
        var (exit, stdout, stderr) = responder(body);
        return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            exitCode = exit,
            stdout,
            stderr,
        }));
    }

    private async Task<HttpResponseMessage> HandleFilesAsync(HttpRequestMessage request, string body, CancellationToken ct)
    {
        _ = ct;
        var query = request.RequestUri?.Query ?? string.Empty;
        var filePath = GetQueryParameter(query, "path") ?? "/unknown";
        await Task.Yield();

        if (request.Method == HttpMethod.Post)
        {
            if (FailWriteStatus.HasValue)
            {
                return JsonResponse(FailWriteStatus.Value, """{"message":"injected write failure"}""");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var base64 = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
                lock (_sync)
                {
                    _files[filePath] = Convert.FromBase64String(base64);
                }
            }
            catch (Exception)
            {
                return JsonResponse(HttpStatusCode.BadRequest, """{"message":"bad file payload"}""");
            }

            return JsonResponse(HttpStatusCode.OK, "{}");
        }

        if (request.Method == HttpMethod.Get)
        {
            if (FailReadStatus.HasValue)
            {
                return JsonResponse(FailReadStatus.Value, """{"message":"injected read failure"}""");
            }

            byte[] bytes;
            lock (_sync)
            {
                bytes = _files.TryGetValue(filePath, out var stored) ? stored : ReadFileBody;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            return response;
        }

        return JsonResponse(HttpStatusCode.MethodNotAllowed, """{"message":"method not allowed"}""");
    }

    private static string? GetQueryParameter(string query, string name)
    {
        foreach (var piece in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = piece.IndexOf('=');
            var key = equals < 0 ? piece : piece.Substring(0, equals);
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(equals < 0 ? string.Empty : piece.Substring(equals + 1));
            }
        }

        return null;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed record SandboxState(string State, string AccessToken);
}
