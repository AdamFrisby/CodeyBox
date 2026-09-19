using System.Net;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Tests;

/// <summary>
/// In-memory Runloop API double. Scripts the recorded-shape responses the
/// provider needs (create, wait-for-status, async execution, files, snapshot,
/// suspend/resume, shutdown, list) and supports failure injection plus
/// create/execution gates for the concurrency tests. Never touches the network.
/// </summary>
internal sealed class FakeRunloopHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, string> _executionCommands = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _createGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _execGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _devboxes;
    private int _executions;
    private int _enteredCreates;
    private int _concurrentCreates;
    private int _maxConcurrentCreates;

    public sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body,
        string? AuthorizationScheme,
        string? AuthorizationParameter);

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_sync) return _requests.ToList(); }
    }

    public List<string> StartedExecutionIds
    {
        get { lock (_sync) return _executionCommands.Keys.ToList(); }
    }

    public HttpStatusCode? FailCreateStatus { get; set; }

    public bool ThrowOnCreate { get; set; }

    public HttpStatusCode? FailExecStatus { get; set; }

    public HttpStatusCode? FailWriteStatus { get; set; }

    public string DevboxStatus { get; set; } = "running";

    public HttpStatusCode DevboxStatusCode { get; set; } = HttpStatusCode.OK;

    public string ReadFileBody { get; set; } = string.Empty;

    public bool GateCreates { get; set; }

    public bool BlockExecutions { get; set; }

    public Func<string, (int Exit, string Stdout, string Stderr)>? ExecutionResponder { get; set; }

    public int EnteredCreates => Volatile.Read(ref _enteredCreates);

    public int MaxConcurrentCreates
    {
        get { lock (_sync) return _maxConcurrentCreates; }
    }

    public void ReleaseCreates() => _createGate.TrySetResult();

    public void ReleaseExecutions() => _execGate.TrySetResult();

    public static string CompletedExecution(string devboxId, string executionId, int exit, string stdout, string stderr) =>
        JsonSerializer.Serialize(new
        {
            devbox_id = devboxId,
            execution_id = executionId,
            status = "completed",
            stdout,
            stderr,
            exit_status = exit,
            stdout_truncated = false,
            stderr_truncated = false,
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? "/";
        string body;
        if (request.Content is MultipartFormDataContent)
        {
            body = "(multipart)";
        }
        else if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            body = string.Empty;
        }

        lock (_sync)
        {
            _requests.Add(new RecordedRequest(
                request.Method,
                path,
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
        }

        if (request.Method == HttpMethod.Post && path == "/v1/devboxes")
        {
            return await HandleCreateAsync(ct).ConfigureAwait(false);
        }

        if (request.Method == HttpMethod.Get && path == "/v1/devboxes")
        {
            return JsonList();
        }

        if (path.EndsWith("/execute_async", StringComparison.Ordinal))
        {
            return HandleStartExecution(path, body);
        }

        if (path.Contains("/executions/", StringComparison.Ordinal) && path.EndsWith("/wait_for_status", StringComparison.Ordinal))
        {
            return await HandleWaitForExecutionAsync(path, ct).ConfigureAwait(false);
        }

        if (path.Contains("/executions/", StringComparison.Ordinal) && path.EndsWith("/kill", StringComparison.Ordinal))
        {
            return Json("{}");
        }

        if (path.Contains("/executions/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            return ExecutionView(path);
        }

        if (path.EndsWith("/wait_for_status", StringComparison.Ordinal))
        {
            return DevboxView(DevboxIdFrom(path));
        }

        if (path.EndsWith("/write_file_contents", StringComparison.Ordinal))
        {
            if (FailWriteStatus.HasValue)
            {
                return new HttpResponseMessage(FailWriteStatus.Value)
                {
                    Content = new StringContent("""{"error":"injected write failure"}""", Encoding.UTF8, "application/json"),
                };
            }

            return Json("""{"devbox_id":"dbx_1","stdout":"","stderr":"","exit_status":0}""");
        }

        if (path.EndsWith("/read_file_contents", StringComparison.Ordinal))
        {
            var content = ReadFileBody;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/plain"),
            };
        }

        if (path.EndsWith("/download_file", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ReadFileBody)),
            };
        }

        if (path.EndsWith("/upload_file", StringComparison.Ordinal))
        {
            return Json("""{"devbox_id":"dbx_1","stdout":"","stderr":"","exit_status":0}""");
        }

        if (path.EndsWith("/snapshot_disk", StringComparison.Ordinal))
        {
            return Json(JsonSerializer.Serialize(new
            {
                id = "snap_test",
                name = "baseline",
                create_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                source_devbox_id = DevboxIdFrom(path),
                metadata = new Dictionary<string, string>(),
            }));
        }

        if (path.EndsWith("/suspend", StringComparison.Ordinal))
        {
            return DevboxView(DevboxIdFrom(path), "suspended");
        }

        if (path.EndsWith("/resume", StringComparison.Ordinal))
        {
            DevboxStatus = "running";
            return DevboxView(DevboxIdFrom(path), "running");
        }

        if (path.EndsWith("/shutdown", StringComparison.Ordinal))
        {
            return Json("{}");
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/devboxes/", StringComparison.Ordinal))
        {
            if (DevboxStatusCode != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(DevboxStatusCode)
                {
                    Content = new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json"),
                };
            }

            return DevboxView(DevboxIdFrom(path));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"{{\"error\":\"no fake route for {request.Method} {path}\"}}", Encoding.UTF8, "application/json"),
        };
    }

    private async Task<HttpResponseMessage> HandleCreateAsync(CancellationToken ct)
    {
        if (ThrowOnCreate)
        {
            throw new HttpRequestException("simulated transport failure");
        }

        if (FailCreateStatus.HasValue)
        {
            return new HttpResponseMessage(FailCreateStatus.Value)
            {
                Content = new StringContent("""{"error":"injected create failure"}""", Encoding.UTF8, "application/json"),
            };
        }

        var entered = Interlocked.Increment(ref _enteredCreates);
        _ = entered;
        var concurrent = Interlocked.Increment(ref _concurrentCreates);
        lock (_sync)
        {
            _maxConcurrentCreates = Math.Max(_maxConcurrentCreates, concurrent);
        }

        try
        {
            if (GateCreates)
            {
                await _createGate.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            var id = $"dbx_{Interlocked.Increment(ref _devboxes)}";
            return DevboxView(id, "provisioning");
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCreates);
        }
    }

    private HttpResponseMessage HandleStartExecution(string path, string body)
    {
        if (FailExecStatus.HasValue)
        {
            return new HttpResponseMessage(FailExecStatus.Value)
            {
                Content = new StringContent("""{"error":"injected exec failure"}""", Encoding.UTF8, "application/json"),
            };
        }

        var devboxId = DevboxIdFrom(path);
        var executionId = $"exec_{Interlocked.Increment(ref _executions)}";
        string command;
        try
        {
            using var doc = JsonDocument.Parse(body);
            command = doc.RootElement.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            command = string.Empty;
        }

        lock (_sync)
        {
            _executionCommands[executionId] = command;
        }

        return Json(JsonSerializer.Serialize(new
        {
            devbox_id = devboxId,
            execution_id = executionId,
            status = "running",
        }));
    }

    private async Task<HttpResponseMessage> HandleWaitForExecutionAsync(string path, CancellationToken ct)
    {
        if (BlockExecutions)
        {
            await _execGate.Task.WaitAsync(ct).ConfigureAwait(false);
            return ExecutionView(path);
        }

        return ExecutionView(path);
    }

    private HttpResponseMessage ExecutionView(string path)
    {
        var segments = path.Split('/');
        var executionId = segments.Length >= 2 ? segments[^2] : "exec_0";
        string command;
        lock (_sync)
        {
            _executionCommands.TryGetValue(executionId, out command!);
        }

        var responder = ExecutionResponder;
        if (responder is not null)
        {
            var (exit, stdout, stderr) = responder(command ?? string.Empty);
            return Json(CompletedExecution("dbx_1", executionId, exit, stdout, stderr));
        }

        return Json(CompletedExecution("dbx_1", executionId, 0, string.Empty, string.Empty));
    }

    private HttpResponseMessage DevboxView(string devboxId, string? statusOverride = null) =>
        Json(JsonSerializer.Serialize(new
        {
            id = devboxId,
            name = $"codeybox-test-{devboxId}",
            status = statusOverride ?? DevboxStatus,
            create_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            metadata = new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = "runloop",
            },
        }));

    private HttpResponseMessage JsonList()
    {
        List<string> ids;
        lock (_sync)
        {
            ids = Enumerable.Range(1, Volatile.Read(ref _devboxes)).Select(i => $"dbx_{i}").ToList();
        }

        var devboxes = ids.Select(id => new
        {
            id,
            name = $"codeybox-test-{id}",
            status = "running",
            create_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            metadata = new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = "runloop",
            },
        }).ToList<object>();
        devboxes.Add(new
        {
            id = "other_1",
            name = "someone-else",
            status = "running",
            create_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            metadata = new Dictionary<string, string>(),
        });

        return Json(JsonSerializer.Serialize(new { devboxes, has_more = false }));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string DevboxIdFrom(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "devboxes", StringComparison.Ordinal))
            {
                return segments[i + 1];
            }
        }

        return "dbx_1";
    }
}
