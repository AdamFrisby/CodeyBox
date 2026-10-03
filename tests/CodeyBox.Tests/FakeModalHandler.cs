using System.Net;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Tests;

/// <summary>
/// In-memory Modal control-plane double. Scripts the recorded-shape responses
/// the provider needs (create, get, list, terminate, snapshot, exec poll,
/// kill, files) and supports failure injection plus create gates for the
/// concurrency tests. Never touches the network.
/// </summary>
internal sealed class FakeModalHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, SandboxState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecState> _execs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SandboxId, string Path), byte[]> _files = new();
    private readonly TaskCompletionSource _createGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _execGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _pollGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sandboxCounter;
    private int _execCounter;
    private int _snapshotCounter;
    private int _enteredCreates;
    private int _concurrentCreates;
    private int _maxConcurrentCreates;

    public sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body,
        string? TokenId,
        string? TokenSecret);

    public sealed record PollScriptStep(string Stdout, string Stderr, bool Completed, int Exit);

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_sync) return _requests.ToList(); }
    }

    public HttpStatusCode? FailCreateStatus { get; set; }

    public int? FailRetryAfterSeconds { get; set; }

    public bool ThrowOnCreate { get; set; }

    public HttpStatusCode? FailExecStatus { get; set; }

    public HttpStatusCode? FailWriteStatus { get; set; }

    public HttpStatusCode? FailSnapshotStatus { get; set; }

    public string SandboxStatus { get; set; } = "running";

    public bool GateCreates { get; set; }

    public bool BlockExecutions { get; set; }

    public bool GatePolls { get; set; }

    public Func<string, string, (int Exit, string Stdout, string Stderr)>? ExecutionResponder { get; set; }

    /// <summary>
    /// Scripted poll responses, dequeued one per exec poll. Each step's
    /// stdout/stderr is a NEW delta (mirroring the incremental poll contract),
    /// not a cumulative snapshot; the final step should set Completed.
    /// </summary>
    public Queue<PollScriptStep> ExecPollScript { get; } = new();

    public int EnteredCreates => Volatile.Read(ref _enteredCreates);

    public int MaxConcurrentCreates
    {
        get { lock (_sync) return _maxConcurrentCreates; }
    }

    public void ReleaseCreates() => _createGate.TrySetResult();

    public void ReleaseExecutions() => _execGate.TrySetResult();

    public void ReleasePolls() => _pollGate.TrySetResult();

    public IReadOnlyList<string> KilledExecs
    {
        get { lock (_sync) return _execs.Values.Where(static e => e.Killed).Select(static e => e.ExecId).ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? new Uri("http://localhost/");
        var path = uri.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string? tokenId = null;
        string? tokenSecret = null;
        if (request.Headers.TryGetValues("X-Modal-Token-Id", out var ids))
        {
            tokenId = ids.FirstOrDefault();
        }

        if (request.Headers.TryGetValues("X-Modal-Token-Secret", out var secrets))
        {
            tokenSecret = secrets.FirstOrDefault();
        }

        lock (_sync)
        {
            _requests.Add(new RecordedRequest(request.Method, path, body, tokenId, tokenSecret));
        }

        if (request.Method == HttpMethod.Post && path == "/v1/sandboxes")
        {
            return await HandleCreateAsync(body, ct).ConfigureAwait(false);
        }

        if (request.Method == HttpMethod.Get && path == "/v1/sandboxes")
        {
            return HandleList(uri.Query);
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && segments[0] == "v1" && segments[1] == "sandboxes")
        {
            var sandboxId = Uri.UnescapeDataString(segments[2]);
            var rest = segments.Skip(3).ToArray();
            if (request.Method == HttpMethod.Get && rest.Length == 0)
            {
                return HandleGet(sandboxId);
            }

            if (request.Method == HttpMethod.Post && rest is ["terminate"])
            {
                return HandleTerminate(sandboxId);
            }

            if (request.Method == HttpMethod.Post && rest is ["snapshot"])
            {
                return HandleSnapshot(sandboxId);
            }

            if (request.Method == HttpMethod.Post && rest is ["exec"])
            {
                return await HandleStartExecAsync(sandboxId, body, ct).ConfigureAwait(false);
            }

            if (rest.Length == 2 && rest[0] == "exec")
            {
                var execId = Uri.UnescapeDataString(rest[1]);
                if (request.Method == HttpMethod.Get)
                {
                    return await HandlePollExecAsync(sandboxId, execId, uri.Query, ct).ConfigureAwait(false);
                }
            }

            if (rest.Length == 3 && rest[0] == "exec" && rest[2] == "kill" && request.Method == HttpMethod.Post)
            {
                return HandleKillExec(sandboxId, Uri.UnescapeDataString(rest[1]));
            }

            if (request.Method == HttpMethod.Post && rest is ["files:write"])
            {
                return HandleWriteFile(sandboxId, body);
            }

            if (request.Method == HttpMethod.Get && rest is ["files"])
            {
                return HandleReadFile(sandboxId, uri.Query);
            }
        }

        return Json(HttpStatusCode.NotFound, new { error = "unknown route: " + path });
    }

    private async Task<HttpResponseMessage> HandleCreateAsync(string body, CancellationToken ct)
    {
        Interlocked.Increment(ref _enteredCreates);
        lock (_sync)
        {
            _concurrentCreates++;
            _maxConcurrentCreates = Math.Max(_maxConcurrentCreates, _concurrentCreates);
        }

        try
        {
            if (ThrowOnCreate)
            {
                throw new HttpRequestException("connection refused");
            }

            if (FailCreateStatus is { } fail)
            {
                var failure = Json(fail, new { error = "create failed" });
                if (fail == HttpStatusCode.TooManyRequests && FailRetryAfterSeconds is { } retrySeconds)
                {
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retrySeconds));
                }

                return failure;
            }

            if (GateCreates)
            {
                await _createGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }

            var id = $"sb_{Interlocked.Increment(ref _sandboxCounter):D6}";
            string name;
            try
            {
                using var doc = JsonDocument.Parse(body);
                name = doc.RootElement.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? id
                    : id;
            }
            catch (JsonException)
            {
                name = id;
            }

            lock (_sync)
            {
                _sandboxes[id] = new SandboxState(id, name);
            }

            return Json(HttpStatusCode.OK, new { sandbox_id = id, name, status = "creating", created_at_unix = 1_700_000_000L });
        }
        finally
        {
            lock (_sync)
            {
                _concurrentCreates--;
            }
        }
    }

    private HttpResponseMessage HandleGet(string sandboxId)
    {
        lock (_sync)
        {
            if (!_sandboxes.TryGetValue(sandboxId, out var state) || state.Terminated)
            {
                return Json(HttpStatusCode.NotFound, new { error = "no such sandbox" });
            }

            return Json(HttpStatusCode.OK, new
            {
                sandbox_id = state.Id,
                name = state.Name,
                status = SandboxStatus,
                created_at_unix = 1_700_000_000L,
                metadata = new Dictionary<string, string>
                {
                    ["codeybox-managed"] = "true",
                    ["codeybox-provider"] = "modal",
                },
            });
        }
    }

    private HttpResponseMessage HandleList(string query)
    {
        List<object> items;
        lock (_sync)
        {
            items = _sandboxes.Values
                .Where(static s => !s.Terminated)
                .Select(static s => (object)new
                {
                    sandbox_id = s.Id,
                    name = s.Name,
                    status = "running",
                    created_at_unix = 1_700_000_000L,
                    metadata = new Dictionary<string, string>
                    {
                        ["codeybox-managed"] = "true",
                        ["codeybox-provider"] = "modal",
                    },
                })
                .ToList();
        }

        _ = query;
        return Json(HttpStatusCode.OK, new { sandboxes = items, has_more = false, next_cursor = (string?)null });
    }

    private HttpResponseMessage HandleTerminate(string sandboxId)
    {
        lock (_sync)
        {
            if (!_sandboxes.TryGetValue(sandboxId, out var state) || state.Terminated)
            {
                return Json(HttpStatusCode.NotFound, new { error = "no such sandbox" });
            }

            state.Terminated = true;
            return Json(HttpStatusCode.OK, new { sandbox_id = sandboxId, status = "terminated" });
        }
    }

    private HttpResponseMessage HandleSnapshot(string sandboxId)
    {
        if (FailSnapshotStatus is { } fail)
        {
            return Json(fail, new { error = "snapshot failed" });
        }

        lock (_sync)
        {
            if (!_sandboxes.TryGetValue(sandboxId, out var state) || state.Terminated)
            {
                return Json(HttpStatusCode.NotFound, new { error = "no such sandbox" });
            }

            var snapshotId = $"snap_{Interlocked.Increment(ref _snapshotCounter):D6}";
            state.Snapshots.Add(snapshotId);
            return Json(HttpStatusCode.OK, new { snapshot_id = snapshotId, name = "snapshot" });
        }
    }

    private async Task<HttpResponseMessage> HandleStartExecAsync(string sandboxId, string body, CancellationToken ct)
    {
        if (FailExecStatus is { } fail)
        {
            return Json(fail, new { error = "exec failed" });
        }

        string command;
        try
        {
            using var doc = JsonDocument.Parse(body);
            command = doc.RootElement.TryGetProperty("command", out var commandProp)
                ? commandProp.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            command = string.Empty;
        }

        var execId = $"exec_{Interlocked.Increment(ref _execCounter):D6}";
        lock (_sync)
        {
            _execs[execId] = new ExecState(execId, sandboxId, command);
        }

        if (BlockExecutions)
        {
            try
            {
                await _execGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        return Json(HttpStatusCode.OK, new { exec_id = execId, status = "running" });
    }

    private async Task<HttpResponseMessage> HandlePollExecAsync(string sandboxId, string execId, string query, CancellationToken ct)
    {
        if (GatePolls && !_pollGate.Task.IsCompleted)
        {
            await _pollGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return ExecJson(execId, "completed", 0, string.Empty, string.Empty, 0, 0);
        }

        if (BlockExecutions && !_execGate.Task.IsCompleted)
        {
            try
            {
                await _execGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            if (!_execGate.Task.IsCompleted)
            {
                return ExecJson(execId, "running", null, string.Empty, string.Empty, 0, 0);
            }
        }

        PollScriptStep? scripted;
        lock (_sync)
        {
            scripted = ExecPollScript.Count > 0 ? ExecPollScript.Dequeue() : null;
        }

        if (scripted is not null)
        {
            var stdoutBytes = Encoding.UTF8.GetByteCount(scripted.Stdout);
            var stderrBytes = Encoding.UTF8.GetByteCount(scripted.Stderr);
            return ExecJson(
                execId,
                scripted.Completed ? "completed" : "running",
                scripted.Completed ? scripted.Exit : null,
                scripted.Stdout,
                scripted.Stderr,
                stdoutBytes,
                stderrBytes);
        }

        ExecState? state;
        lock (_sync)
        {
            _execs.TryGetValue(execId, out state);
        }

        if (state is null || !string.Equals(state.SandboxId, sandboxId, StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.NotFound, new { error = "no such exec" });
        }

        if (state.Killed)
        {
            return ExecJson(execId, "completed", 137, string.Empty, string.Empty, 0, 0);
        }

        var responder = ExecutionResponder;
        if (IsFindListing(state.Command))
        {
            var listing = FindOutput(sandboxId, FindRoot(state.Command));
            return ExecJson(
                execId, "completed", 0, listing, string.Empty,
                Encoding.UTF8.GetByteCount(listing), 0);
        }

        if (responder is not null)
        {
            var (exit, stdout, stderr) = responder(sandboxId, state.Command);
            if (stdout.Contains('\0'))
            {
                stdout = stdout.Replace("\0", string.Empty, StringComparison.Ordinal);
            }

            return ExecJson(
                execId, "completed", exit, stdout, stderr,
                Encoding.UTF8.GetByteCount(stdout), Encoding.UTF8.GetByteCount(stderr));
        }

        _ = query;
        return ExecJson(execId, "completed", 0, string.Empty, string.Empty, 0, 0);
    }

    private HttpResponseMessage HandleKillExec(string sandboxId, string execId)
    {
        lock (_sync)
        {
            if (_execs.TryGetValue(execId, out var state)
                && string.Equals(state.SandboxId, sandboxId, StringComparison.Ordinal))
            {
                state.Killed = true;
                return Json(HttpStatusCode.OK, new { exec_id = execId, status = "killed" });
            }

            return Json(HttpStatusCode.NotFound, new { error = "no such exec" });
        }
    }

    private HttpResponseMessage HandleWriteFile(string sandboxId, string body)
    {
        if (FailWriteStatus is { } fail)
        {
            return Json(fail, new { error = "write failed" });
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var path = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;
            var content = Convert.FromBase64String(doc.RootElement.GetProperty("content_b64").GetString() ?? string.Empty);
            lock (_sync)
            {
                _files[(sandboxId, path)] = content;
            }

            return Json(HttpStatusCode.OK, new { path });
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Json(HttpStatusCode.BadRequest, new { error = "malformed write" });
        }
    }

    private HttpResponseMessage HandleReadFile(string sandboxId, string query)
    {
        var path = string.Empty;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "path")
            {
                path = Uri.UnescapeDataString(kv[1]);
            }
        }

        lock (_sync)
        {
            if (_files.TryGetValue((sandboxId, path), out var content))
            {
                return Json(HttpStatusCode.OK, new { path, content_b64 = Convert.ToBase64String(content) });
            }

            return Json(HttpStatusCode.NotFound, new { error = "no such file" });
        }
    }

    private static bool IsFindListing(string command) =>
        command.Contains("'find'", StringComparison.Ordinal);

    private string FindOutput(string sandboxId, string? root)
    {
        lock (_sync)
        {
            var paths = _files.Keys
                .Where(k => string.Equals(k.SandboxId, sandboxId, StringComparison.Ordinal))
                .Select(static k => k.Path)
                .Where(p => root is null || string.Equals(p, root, StringComparison.Ordinal) || p.StartsWith(root + "/", StringComparison.Ordinal))
                .OrderBy(static p => p, StringComparer.Ordinal)
                .ToList();
            return string.Join('\n', paths);
        }
    }

    private static string? FindRoot(string command)
    {
        var find = command.IndexOf("'find'", StringComparison.Ordinal);
        if (find < 0)
        {
            return null;
        }

        var open = command.IndexOf('\'', find + 6);
        if (open < 0)
        {
            return null;
        }

        var close = command.IndexOf('\'', open + 1);
        if (close < 0)
        {
            return null;
        }

        return command.Substring(open + 1, close - open - 1);
    }

    private static HttpResponseMessage ExecJson(
        string execId, string status, int? exit, string stdout, string stderr, long stdoutLength, long stderrLength) =>
        Json(HttpStatusCode.OK, new
        {
            exec_id = execId,
            status,
            exit_code = exit,
            stdout,
            stderr,
            stdout_length = stdoutLength,
            stderr_length = stderrLength,
            stdout_truncated = false,
            stderr_truncated = false,
        });

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T payload)
    {
        var response = new HttpResponseMessage(status);
        response.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        return response;
    }

    private sealed class SandboxState(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public bool Terminated { get; set; }

        public List<string> Snapshots { get; } = [];
    }

    private sealed class ExecState(string execId, string sandboxId, string command)
    {
        public string ExecId { get; } = execId;

        public string SandboxId { get; } = sandboxId;

        public string Command { get; } = command;

        public bool Killed { get; set; }
    }
}
