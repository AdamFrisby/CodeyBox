using System.Net;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Tests;

/// <summary>
/// In-memory Blaxel API double: control plane (<c>/v0/sandboxes</c> CRUD) plus
/// the sandbox data-plane process API (<c>/process</c> start/poll/logs/kill).
/// Scripts the recorded-shape responses the provider needs, emulates a tiny
/// guest filesystem (base64 write/read, <c>find</c> listing, <c>printf</c> echo
/// so the resume probe verifies for real), and supports failure injection plus
/// create gates for the concurrency tests. Never touches the network.
/// </summary>
internal sealed class FakeBlaxelHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, SandboxRecord> _sandboxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessRecord> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _guestFiles = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _createGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _processGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _processSequence;
    private int _enteredCreates;
    private int _concurrentCreates;
    private int _maxConcurrentCreates;

    public sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body,
        string? BlaxelAuthorization,
        string? BlaxelWorkspace,
        Dictionary<string, string>? ProcessEnv);

    public sealed record ProcessStart(string Command, Dictionary<string, string> Env);

    private sealed class SandboxRecord
    {
        public string Name = string.Empty;
        public int GetPolls;
        public bool Woken;
    }

    private sealed class ProcessRecord
    {
        public string Id = string.Empty;
        public string Command = string.Empty;
        public Dictionary<string, string> Env = new(StringComparer.Ordinal);
        public int Polls;
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_sync) return _requests.ToList(); }
    }

    public IReadOnlyList<ProcessStart> StartedProcesses
    {
        get
        {
            lock (_sync)
            {
                return _processes.Values.Select(p => new ProcessStart(p.Command, new Dictionary<string, string>(p.Env))).ToList();
            }
        }
    }

    public HttpStatusCode? FailCreateStatus { get; set; }

    public bool ThrowOnCreate { get; set; }

    public HttpStatusCode? FailProcessStatus { get; set; }

    public HttpStatusCode? FailDeleteStatus { get; set; }

    public int RunningAfterPolls { get; set; }

    public int StandbyAfterPolls { get; set; } = int.MaxValue;

    public string InitialState { get; set; } = "RUNNING";

    public bool BareArrayList { get; set; }

    public bool BreakResumeProbe { get; set; }

    public bool GateCreates { get; set; }

    public bool BlockProcesses { get; set; }

    public Func<string, IReadOnlyDictionary<string, string>, (int Exit, string Stdout, string Stderr)>? ExecutionResponder { get; set; }

    public int EnteredCreates => Volatile.Read(ref _enteredCreates);

    public int MaxConcurrentCreates
    {
        get { lock (_sync) return _maxConcurrentCreates; }
    }

    public void ReleaseCreates() => _createGate.TrySetResult();

    public void ReleaseProcesses() => _processGate.TrySetResult();


    /// <summary>Seeds a sandbox record without HTTP, for resume flows that start from standby.</summary>
    public void SeedSandbox(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            _sandboxes[name] = new SandboxRecord { Name = name };
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : string.Empty;

        Dictionary<string, string>? processEnv = null;
        if (path.StartsWith("/process", StringComparison.Ordinal) && request.Method == HttpMethod.Post && path == "/process")
        {
            processEnv = ParseEnv(body);
        }

        string? authorization = null;
        string? workspace = null;
        if (request.Headers.TryGetValues("x-blaxel-authorization", out var authValues))
        {
            authorization = string.Join(",", authValues);
        }

        if (request.Headers.TryGetValues("x-blaxel-workspace", out var workspaceValues))
        {
            workspace = string.Join(",", workspaceValues);
        }

        lock (_sync)
        {
            _requests.Add(new RecordedRequest(request.Method, path, body, authorization, workspace, processEnv));
        }

        if (path == "/v0/sandboxes" && request.Method == HttpMethod.Post)
        {
            return await HandleCreateAsync(body, ct).ConfigureAwait(false);
        }

        if (path == "/v0/sandboxes" && request.Method == HttpMethod.Get)
        {
            return HandleList();
        }

        if (path.StartsWith("/v0/sandboxes/", StringComparison.Ordinal))
        {
            var name = Uri.UnescapeDataString(path.Substring("/v0/sandboxes/".Length));
            if (request.Method == HttpMethod.Get)
            {
                return HandleGet(name);
            }

            if (request.Method == HttpMethod.Delete)
            {
                return HandleDelete(name);
            }
        }

        if (path == "/process" && request.Method == HttpMethod.Post)
        {
            return HandleStartProcess(body);
        }

        if (path.StartsWith("/process/", StringComparison.Ordinal))
        {
            var rest = path.Substring("/process/".Length);
            var slash = rest.IndexOf('/');
            var id = slash < 0 ? rest : rest.Substring(0, slash);
            var tail = slash < 0 ? string.Empty : rest.Substring(slash);
            if (request.Method == HttpMethod.Get && tail.Length == 0)
            {
                return await HandleGetProcessAsync(id, ct).ConfigureAwait(false);
            }

            if (request.Method == HttpMethod.Get && tail == "/logs")
            {
                return HandleGetLogs(id);
            }

            if (request.Method == HttpMethod.Delete && tail == "/kill")
            {
                return HandleKill(id);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"{{\"error\":\"no fake route for {request.Method} {path}\"}}", Encoding.UTF8, "application/json"),
        };
    }

    private async Task<HttpResponseMessage> HandleCreateAsync(string body, CancellationToken ct)
    {
        if (ThrowOnCreate)
        {
            throw new HttpRequestException("simulated transport failure");
        }

        if (FailCreateStatus.HasValue)
        {
            return Error(FailCreateStatus.Value, "injected create failure");
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

            string name;
            try
            {
                using var document = JsonDocument.Parse(body);
                name = document.RootElement.GetProperty("metadata").GetProperty("name").GetString() ?? $"sbx_{Guid.NewGuid():N}";
            }
            catch (JsonException)
            {
                name = $"sbx_{Guid.NewGuid():N}";
            }

            lock (_sync)
            {
                _sandboxes[name] = new SandboxRecord { Name = name };
            }

            return Json(JsonSerializer.Serialize(SandboxJson(name, "RUNNING", "DEPLOYING", includeUrl: true)));
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCreates);
        }
    }

    private HttpResponseMessage HandleList()
    {
        List<string> names;
        lock (_sync)
        {
            names = _sandboxes.Keys.ToList();
        }

        if (BareArrayList)
        {
            var items = names.Select(n => (object)SandboxJson(n, "RUNNING", "DEPLOYED", includeUrl: false)).ToList();
            items.Add(ForeignSandboxJson());
            return Json(JsonSerializer.Serialize(items));
        }

        var data = names.Select(n => (object)SandboxJson(n, "RUNNING", "DEPLOYED", includeUrl: false)).ToList();
        data.Add(ForeignSandboxJson());
        return Json(JsonSerializer.Serialize(new
        {
            data,
            meta = new { hasMore = false, cursor = (string?)null },
        }));
    }

    private HttpResponseMessage HandleGet(string name)
    {
        SandboxRecord? record;
        lock (_sync)
        {
            _sandboxes.TryGetValue(name, out record);
        }

        if (record is null)
        {
            return Error(HttpStatusCode.NotFound, "sandbox not found");
        }

        var polls = Interlocked.Increment(ref record.GetPolls) - 1;
        string state;
        string status;
        if (record.Woken)
        {
            // A data-plane touch woke the sandbox; it stays up in the fake.
            state = "RUNNING";
            status = "DEPLOYED";
        }
        else if (string.Equals(InitialState, "RUNNING", StringComparison.Ordinal))
        {
            if (polls < RunningAfterPolls)
            {
                state = "RUNNING";
                status = "DEPLOYING";
            }
            else if (polls - RunningAfterPolls >= StandbyAfterPolls)
            {
                state = "STANDBY";
                status = "DEPLOYED";
            }
            else
            {
                state = "RUNNING";
                status = "DEPLOYED";
            }
        }
        else
        {
            state = InitialState;
            status = "DEPLOYED";
        }

        return Json(JsonSerializer.Serialize(SandboxJson(name, state, status, includeUrl: true)));
    }

    private HttpResponseMessage HandleDelete(string name)
    {
        if (FailDeleteStatus.HasValue)
        {
            return Error(FailDeleteStatus.Value, "injected delete failure");
        }

        lock (_sync)
        {
            if (!_sandboxes.Remove(name))
            {
                return Error(HttpStatusCode.NotFound, "sandbox not found");
            }
        }

        return Json("{}");
    }

    private HttpResponseMessage HandleStartProcess(string body)
    {
        if (FailProcessStatus.HasValue)
        {
            return Error(FailProcessStatus.Value, "injected process failure");
        }

        string command;
        string? name;
        Dictionary<string, string> env;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            command = root.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? string.Empty : string.Empty;
            name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in envElement.EnumerateObject())
                {
                    env[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            command = string.Empty;
            name = null;
            env = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        ApplyGuestWriteEmulation(command);

        var id = $"proc_{Interlocked.Increment(ref _processSequence)}";
        lock (_sync)
        {
            _processes[id] = new ProcessRecord { Id = id, Command = command, Env = env };
        }

        _ = name;
        return Json(JsonSerializer.Serialize(new
        {
            pid = id,
            name = $"process-{id}",
            command,
            status = "running",
            exitCode = (int?)null,
            stdout = "",
            stderr = "",
            logs = "",
        }));
    }

    private async Task<HttpResponseMessage> HandleGetProcessAsync(string id, CancellationToken ct)
    {
        if (BlockProcesses)
        {
            await _processGate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        ProcessRecord? record;
        lock (_sync)
        {
            _processes.TryGetValue(id, out record);
        }

        if (record is null)
        {
            TouchWake();
            return Error(HttpStatusCode.NotFound, "process not found");
        }

        var polls = Interlocked.Increment(ref record.Polls);
        var (exit, stdout, stderr) = ResolveProcessResult(record);
        var completed = polls > 0;
        return Json(JsonSerializer.Serialize(new
        {
            pid = record.Id,
            name = $"process-{record.Id}",
            command = record.Command,
            status = completed ? TerminalStatus(exit) : "running",
            exitCode = completed ? exit : (int?)null,
            stdout = completed ? stdout : "",
            stderr = completed ? stderr : "",
            logs = completed ? stdout + stderr : "",
        }));
    }

    private HttpResponseMessage HandleGetLogs(string id)
    {
        ProcessRecord? record;
        lock (_sync)
        {
            _processes.TryGetValue(id, out record);
        }

        if (record is null)
        {
            return Error(HttpStatusCode.NotFound, "process not found");
        }

        var (exit, stdout, stderr) = ResolveProcessResult(record);
        _ = exit;
        return Json(JsonSerializer.Serialize(new
        {
            logs = stdout + stderr,
            stdout,
            stderr,
        }));
    }

    private HttpResponseMessage HandleKill(string id)
    {
        lock (_sync)
        {
            _processes.Remove(id);
        }

        return Json(JsonSerializer.Serialize(new { success = true }));
    }

    private (int Exit, string Stdout, string Stderr) ResolveProcessResult(ProcessRecord record)
    {
        var responder = ExecutionResponder;
        if (responder is not null)
        {
            return responder(record.Command, record.Env);
        }

        if (IsResumeProbe(record.Command, out var nonce))
        {
            return BreakResumeProbe ? (0, "wrong-answer", string.Empty) : (0, nonce, string.Empty);
        }

        if (IsBase64Read(record.Command, out var readPath) && readPath is not null)
        {
            string content;
            lock (_sync)
            {
                _guestFiles.TryGetValue(readPath, out content!);
            }

            return (0, Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? string.Empty)), string.Empty);
        }

        if (IsFindListing(record.Command, out var findRoot) && findRoot is not null)
        {
            List<string> matches;
            lock (_sync)
            {
                matches = _guestFiles.Keys
                    .Where(p => p.StartsWith(findRoot + "/", StringComparison.Ordinal))
                    .OrderBy(static p => p, StringComparer.Ordinal)
                    .ToList();
            }

            return (0, string.Join("\n", matches) + (matches.Count == 0 ? string.Empty : "\n"), string.Empty);
        }

        return (0, string.Empty, string.Empty);
    }

    private void ApplyGuestWriteEmulation(string command)
    {
        // The provider wraps scripts in POSIX single quotes, so every inner
        // quote arrives escaped. Parse the normalized form.
        const string Q = "\u0001";
        var normalized = command.Replace("'\\''", Q, StringComparison.Ordinal);
        var marker = "|base64 -d ";
        var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return;
        }

        var printfMarker = "printf %s " + Q;
        var printfIndex = normalized.IndexOf(printfMarker, StringComparison.Ordinal);
        if (printfIndex < 0 || printfIndex > markerIndex)
        {
            return;
        }

        var payloadStart = printfIndex + printfMarker.Length;
        var payloadEnd = normalized.IndexOf(Q + "|base64", payloadStart, StringComparison.Ordinal);
        if (payloadEnd < 0)
        {
            return;
        }

        var redirect = normalized.Substring(markerIndex + marker.Length).TrimStart();
        var append = redirect.StartsWith(">>", StringComparison.Ordinal);
        var pathQuote = (append ? redirect.Substring(2) : redirect.Substring(1)).TrimStart();
        if (!pathQuote.StartsWith(Q, StringComparison.Ordinal) || pathQuote.Length < 2)
        {
            return;
        }

        var pathEnd = pathQuote.IndexOf(Q, 1, StringComparison.Ordinal);
        if (pathEnd < 0)
        {
            return;
        }

        var path = pathQuote.Substring(1, pathEnd - 1);
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized.Substring(payloadStart, payloadEnd - payloadStart)));
        }
        catch (FormatException)
        {
            return;
        }

        lock (_sync)
        {
            if (append && _guestFiles.TryGetValue(path, out var existing))
            {
                _guestFiles[path] = existing + decoded;
            }
            else
            {
                _guestFiles[path] = decoded;
            }
        }
    }

    private static bool IsResumeProbe(string command, out string nonce)
    {
        nonce = string.Empty;
        const string prefix = "printf %s '";
        if (!command.StartsWith(prefix, StringComparison.Ordinal) || !command.EndsWith("'", StringComparison.Ordinal))
        {
            return false;
        }

        nonce = command.Substring(prefix.Length, command.Length - prefix.Length - 1);
        return nonce.StartsWith("resume-probe-", StringComparison.Ordinal);
    }

    private static bool IsBase64Read(string command, out string? path)
    {
        path = null;
        const string Q = "\u0001";
        var normalized = command.Replace("'\\''", Q, StringComparison.Ordinal);
        var marker = "base64 -- " + Q;
        var index = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var start = index + marker.Length;
        var end = normalized.IndexOf(Q, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        path = normalized.Substring(start, end - start);
        return true;
    }

    private static bool IsFindListing(string command, out string? root)
    {
        root = null;
        var tokens = Tokenize(command);
        var findIndex = tokens.FindIndex(static t => string.Equals(t, "find", StringComparison.Ordinal));
        if (findIndex < 0 || findIndex + 1 >= tokens.Count)
        {
            return false;
        }

        root = tokens[findIndex + 1].TrimEnd('/');
        return true;
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (inQuote)
            {
                if (c == '\'')
                {
                    if (i + 3 < command.Length && command[i + 1] == '\\' && command[i + 2] == '\'' && command[i + 3] == '\'')
                    {
                        current.Append('\'');
                        i += 3;
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '\'')
            {
                inQuote = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static Dictionary<string, string> ParseEnv(string body)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("env", out var envElement)
                && envElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in envElement.EnumerateObject())
                {
                    env[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        return env;
    }

    private void TouchWake()
    {
        lock (_sync)
        {
            foreach (var record in _sandboxes.Values)
            {
                record.Woken = true;
            }
        }
    }

    private static string TerminalStatus(int exit) => exit == 0 ? "completed" : "failed";

    private static object SandboxJson(string name, string state, string status, bool includeUrl) => new
    {
        metadata = new
        {
            name,
            url = includeUrl ? $"https://sbx-{name}-testws.us-pdx-1.bl.run" : null,
            workspace = "testws",
            labels = new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = "blaxel",
            },
        },
        spec = new { region = "us-pdx-1" },
        state,
        status,
    };

    private static object ForeignSandboxJson() => new
    {
        metadata = new
        {
            name = "foreign-box",
            url = "https://sbx-foreign-box-testws.us-pdx-1.bl.run",
            workspace = "testws",
            labels = new Dictionary<string, string>
            {
                ["other"] = "yes",
            },
        },
        spec = new { region = "us-pdx-1" },
        state = "RUNNING",
        status = "DEPLOYED",
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string message) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = message }), Encoding.UTF8, "application/json"),
    };
}
