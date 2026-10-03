using System.Text;
using CodeyBox.TartSandboxPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// In-memory Tart CLI + guest SSH double. Emulates `tart clone/set/list/ip/
/// stop/delete/run` plus the SSH remote-command shapes the provider sends
/// (readiness probe, base64 file write/read, find, mkdir, status-file
/// protocol), with failure injection and clone gating for the concurrency
/// tests. Never spawns a process. Secrets are never recorded: only argv,
/// stdin length, and the guest filesystem are observable.
/// </summary>
internal sealed class FakeTartProcessRunner : ITartProcessRunner
{
    private readonly object _sync = new();
    private readonly List<RecordedInvocation> _invocations = [];
    private readonly Dictionary<string, FakeVm> _vms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _statusFiles = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _cloneGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _execGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _enteredClones;
    private int _concurrentClones;
    private int _maxConcurrentClones;
    private int _nextIpOctet = 11;

    public sealed record RecordedInvocation(
        string Executable,
        IReadOnlyList<string> Argv,
        int StdinBytes,
        IReadOnlyDictionary<string, string> EnvironmentKeys);

    public sealed record FakeVm(string Image, bool Running, string Ip, Dictionary<string, byte[]> Files);

    public IReadOnlyList<RecordedInvocation> Invocations
    {
        get { lock (_sync) return _invocations.ToList(); }
    }

    public int EnteredClones => Volatile.Read(ref _enteredClones);

    public int MaxConcurrentClones
    {
        get { lock (_sync) return _maxConcurrentClones; }
    }

    public bool GateClones { get; set; }

    public bool BlockExecutions { get; set; }

    public bool FailSshTransport { get; set; }

    public bool FailWrites { get; set; }

    public bool FailProbeAuth { get; set; }

    public string? FailCloneStderr { get; set; }

    public int FailCloneExit { get; set; }

    public string? FailSetStderr { get; set; }

    public bool FailList { get; set; }

    public Func<string, (int Exit, string Stdout, string Stderr)>? ExecutionResponder { get; set; }

    public void ReleaseClones() => _cloneGate.TrySetResult();

    public void ReleaseExecutions() => _execGate.TrySetResult();

    public void SeedForeignVm(string name, bool running = true)
    {
        lock (_sync)
            _vms[name] = new FakeVm("foreign-image", running, "192.0.2.99", new Dictionary<string, byte[]>(StringComparer.Ordinal));
    }

    public byte[]? GetGuestFile(string vm, string path)
    {
        lock (_sync)
            return _vms.TryGetValue(vm, out var entry) && entry.Files.TryGetValue(path, out var bytes) ? bytes : null;
    }

    public bool VmExists(string vm)
    {
        lock (_sync) return _vms.ContainsKey(vm);
    }

    public bool VmRunning(string vm)
    {
        lock (_sync) return _vms.TryGetValue(vm, out var entry) && entry.Running;
    }

    public Task<TartProcessResult> RunAsync(
        TartProcessSpec spec,
        Action<string>? stdoutChunk,
        Action<string>? stderrChunk,
        int maxOutputBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_sync)
        {
            _invocations.Add(new RecordedInvocation(
                spec.Executable,
                spec.Argv.ToList(),
                spec.Stdin?.Length ?? 0,
                (spec.ExtraEnvironment ?? new Dictionary<string, string>()).ToDictionary(
                    kvp => kvp.Key, kvp => kvp.Value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparer.Ordinal)));
        }

        if (string.Equals(spec.Executable, "tart", StringComparison.Ordinal))
            return RunTartAsync(spec, ct);
        if (string.Equals(spec.Executable, "ssh", StringComparison.Ordinal)
            || string.Equals(spec.Executable, "sshpass", StringComparison.Ordinal))
            return RunSshAsync(spec, stdoutChunk, stderrChunk, ct);

        return Task.FromResult(new TartProcessResult(127, string.Empty, $"{spec.Executable}: command not found"));
    }

    public ITartDetachedProcess StartDetached(TartProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_sync)
            _invocations.Add(new RecordedInvocation(spec.Executable, spec.Argv.ToList(), 0, new Dictionary<string, string>(StringComparer.Ordinal)));

        if (FailRun)
            throw new TartCliException(spec.Executable, spec.Argv, 1, "run-failed", "fake tart run refused");

        var name = spec.Argv[^1];
        lock (_sync)
        {
            if (!_vms.TryGetValue(name, out var vm))
                throw new TartCliException(spec.Executable, spec.Argv, 1, "not-found", $"VM \"{name}\" not found");
            var ip = vm.Ip.Length == 0 ? $"192.0.2.{_nextIpOctet++}" : vm.Ip;
            _vms[name] = vm with { Running = true, Ip = ip };
        }
        return new FakeDetachedProcess();
    }

    public bool FailRun { get; set; }

    private async Task<TartProcessResult> RunTartAsync(TartProcessSpec spec, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var sub = spec.Argv.Count > 0 ? spec.Argv[0] : string.Empty;
        if (string.Equals(sub, "clone", StringComparison.Ordinal))
            return await CloneAsync(spec, ct).ConfigureAwait(false);
        return sub switch
        {
            "set" => HandleSet(),
            "list" => HandleList(spec),
            "ip" => HandleIp(spec),
            "stop" => HandleStop(spec),
            "delete" => HandleDelete(spec),
            _ => new TartProcessResult(1, string.Empty, $"unknown command: {sub}"),
        };
    }

    private async Task<TartProcessResult> CloneAsync(TartProcessSpec spec, CancellationToken ct)
    {
        Interlocked.Increment(ref _enteredClones);
        var concurrent = Interlocked.Increment(ref _concurrentClones);
        try
        {
            lock (_sync)
                _maxConcurrentClones = Math.Max(_maxConcurrentClones, concurrent);
            if (GateClones)
                await _cloneGate.Task.WaitAsync(ct).ConfigureAwait(false);
            if (FailCloneStderr is not null)
                return new TartProcessResult(FailCloneExit == 0 ? 1 : FailCloneExit, string.Empty, FailCloneStderr);
            var name = spec.Argv[^1];
            lock (_sync)
            {
                if (_vms.ContainsKey(name))
                    return new TartProcessResult(1, string.Empty, $"VM \"{name}\" already exists");
                _vms[name] = new FakeVm(spec.Argv[^2], Running: false, string.Empty, new Dictionary<string, byte[]>(StringComparer.Ordinal));
            }
            return new TartProcessResult(0, string.Empty, string.Empty);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentClones);
        }
    }

    private TartProcessResult HandleSet()
    {
        if (FailSetStderr is not null)
            return new TartProcessResult(1, string.Empty, FailSetStderr);
        return new TartProcessResult(0, string.Empty, string.Empty);
    }

    private TartProcessResult HandleList(TartProcessSpec spec)
    {
        if (FailList)
            return new TartProcessResult(1, string.Empty, "list failed");
        lock (_sync)
        {
            if (spec.Argv.Contains("--format"))
            {
                var rows = _vms.Select(kvp =>
                    $"{{\"name\": \"{kvp.Key}\", \"state\": \"{(kvp.Value.Running ? "running" : "stopped")}\", \"size\": 20.0}}");
                return new TartProcessResult(0, "[" + string.Join(",", rows) + "]", string.Empty);
            }
            var lines = new List<string> { "Name\tState" };
            lines.AddRange(_vms.Select(kvp => $"{kvp.Key}\t{(kvp.Value.Running ? "running" : "stopped")}"));
            return new TartProcessResult(0, string.Join("\n", lines) + "\n", string.Empty);
        }
    }

    private TartProcessResult HandleIp(TartProcessSpec spec)
    {
        var name = spec.Argv[^1];
        lock (_sync)
        {
            if (!_vms.TryGetValue(name, out var vm))
                return new TartProcessResult(1, string.Empty, $"VM \"{name}\" not found");
            if (!vm.Running || vm.Ip.Length == 0)
                return new TartProcessResult(1, string.Empty, $"VM \"{name}\" is not running");
            return new TartProcessResult(0, vm.Ip + "\n", string.Empty);
        }
    }

    private TartProcessResult HandleStop(TartProcessSpec spec)
    {
        var name = spec.Argv[^1];
        lock (_sync)
        {
            if (!_vms.TryGetValue(name, out var vm))
                return new TartProcessResult(1, string.Empty, $"VM \"{name}\" not found");
            _vms[name] = vm with { Running = false };
        }
        return new TartProcessResult(0, string.Empty, string.Empty);
    }

    private TartProcessResult HandleDelete(TartProcessSpec spec)
    {
        var name = spec.Argv[^1];
        lock (_sync)
        {
            if (!_vms.Remove(name))
                return new TartProcessResult(1, string.Empty, $"VM \"{name}\" not found");
        }
        return new TartProcessResult(0, string.Empty, string.Empty);
    }

    private async Task<TartProcessResult> RunSshAsync(
        TartProcessSpec spec,
        Action<string>? stdoutChunk,
        Action<string>? stderrChunk,
        CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var command = spec.Argv[^1];
        var target = spec.Argv.Count >= 2 ? spec.Argv[^2] : string.Empty;
        var vm = VmForTarget(target);

        if (BlockExecutions && command.Contains("__cb_code", StringComparison.Ordinal))
            await _execGate.Task.WaitAsync(ct).ConfigureAwait(false);

        TartProcessResult result = command switch
        {
            // Wrapped user execs start with `mkdir -p` too — the status
            // marker must win over the prefix arms below.
            _ when command.Contains("__cb_code", StringComparison.Ordinal) => UserExec(vm, command),
            "echo codeybox-ready" => Probe(vm),
            _ when command.StartsWith("umask 077", StringComparison.Ordinal) => WriteFile(vm, command, spec.Stdin),
            _ when command.StartsWith("base64 ", StringComparison.Ordinal) => ReadFile(vm, command),
            _ when command.StartsWith("find ", StringComparison.Ordinal) => ListFiles(vm, command),
            _ when command.StartsWith("mkdir -p", StringComparison.Ordinal) => new TartProcessResult(0, string.Empty, string.Empty),
            _ when command.StartsWith("cat -- '/tmp/.codeybox-exec-status/", StringComparison.Ordinal) => ReadStatus(command),
            _ when command.StartsWith("rm -f -- ", StringComparison.Ordinal) => RemoveStaging(command),
            _ => new TartProcessResult(0, string.Empty, string.Empty),
        };

        if (result.Stdout.Length > 0)
            stdoutChunk?.Invoke(result.Stdout);
        if (result.Stderr.Length > 0)
            stderrChunk?.Invoke(result.Stderr);
        return result;

        TartProcessResult Probe(string? name)
        {
            if (FailProbeAuth)
                return new TartProcessResult(255, string.Empty, "admin@192.0.2.11: Permission denied (publickey,password).");
            if (name is null)
                return new TartProcessResult(255, string.Empty, "ssh: connect to host 192.0.2.11 port 22: Connection refused");
            return new TartProcessResult(0, "codeybox-ready\n", string.Empty);
        }

        TartProcessResult WriteFile(string? name, string cmd, string? stdin)
        {
            if (name is null)
                return new TartProcessResult(255, string.Empty, "ssh: Connection refused");
            if (FailWrites)
                return new TartProcessResult(1, string.Empty, "write failed");
            var path = Unquote(cmd[(cmd.IndexOf('>') + 1)..].Trim());
            var bytes = Convert.FromBase64String((stdin ?? string.Empty).Trim());
            lock (_sync)
                _vms[name].Files[path] = bytes;
            return new TartProcessResult(0, string.Empty, string.Empty);
        }

        TartProcessResult ReadFile(string? name, string cmd)
        {
            if (name is null)
                return new TartProcessResult(255, string.Empty, "ssh: Connection refused");
            var path = Unquote(cmd["base64 ".Length..].Trim());
            lock (_sync)
                return _vms[name].Files.TryGetValue(path, out var bytes)
                    ? new TartProcessResult(0, Convert.ToBase64String(bytes) + "\n", string.Empty)
                    : new TartProcessResult(1, string.Empty, $"base64: {path}: No such file or directory");
        }

        TartProcessResult ListFiles(string? name, string cmd)
        {
            if (name is null)
                return new TartProcessResult(255, string.Empty, "ssh: Connection refused");
            var dir = ExtractFirstQuoted(cmd).TrimEnd('/') + "/";
            lock (_sync)
            {
                var rows = _vms[name].Files.Keys
                    .Where(k => k.StartsWith(dir, StringComparison.Ordinal))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();
                return new TartProcessResult(0, string.Join('\0', rows) + (rows.Length == 0 ? string.Empty : "\0"), string.Empty);
            }
        }

        TartProcessResult ReadStatus(string cmd)
        {
            var path = Unquote(cmd["cat -- ".Length..].Trim());
            lock (_sync)
                return _statusFiles.TryGetValue(path, out var exit)
                    ? new TartProcessResult(0, exit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", string.Empty)
                    : new TartProcessResult(1, string.Empty, $"cat: {path}: No such file or directory");
        }

        TartProcessResult RemoveStaging(string cmd)
        {
            var path = Unquote(cmd["rm -f -- ".Length..].Trim());
            lock (_sync)
            {
                _statusFiles.Remove(path);
                foreach (var entry in _vms.Values)
                    entry.Files.Remove(path);
            }
            return new TartProcessResult(0, string.Empty, string.Empty);
        }

        TartProcessResult UserExec(string? name, string cmd)
        {
            if (FailSshTransport || name is null)
                return new TartProcessResult(255, string.Empty, "ssh: connect to host 192.0.2.11 port 22: Connection refused");
            var responder = ExecutionResponder ?? (_ => (0, string.Empty, string.Empty));
            var (exit, stdout, stderr) = responder(cmd);
            var statusPath = ExtractStatusPath(cmd);
            if (statusPath is not null)
            {
                lock (_sync)
                    _statusFiles[statusPath] = exit;
            }
            return new TartProcessResult(exit, stdout, stderr);
        }
    }

    private string? VmForTarget(string target)
    {
        var at = target.LastIndexOf('@');
        var ip = at >= 0 ? target[(at + 1)..] : target;
        lock (_sync)
            return _vms.FirstOrDefault(kvp => kvp.Value.Ip == ip).Key;
    }

    private static string? ExtractStatusPath(string command)
    {
        const string marker = "> '";
        var index = command.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var rest = command[(index + marker.Length)..];
        var end = rest.IndexOf('\'');
        if (end < 0)
            return null;
        var path = rest[..end];
        return path.StartsWith("/tmp/.codeybox-exec-status/", StringComparison.Ordinal) ? path : null;
    }

    private static string ExtractFirstQuoted(string command)
    {
        var first = command.IndexOf('\'');
        if (first < 0)
            return string.Empty;
        var rest = command[(first + 1)..];
        var end = rest.IndexOf('\'');
        if (end < 0)
            return rest;
        return rest[..end].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    private static string Unquote(string quoted)
    {
        var text = quoted.Trim();
        if (text.Length >= 2 && text.StartsWith('\'') && text.EndsWith('\''))
            text = text[1..^1];
        return text.Replace("'\\''", "'", StringComparison.Ordinal);
    }

    private sealed class FakeDetachedProcess : ITartDetachedProcess
    {
        public int Id => 4242;

        public bool HasExited { get; private set; }

        public void Kill() => HasExited = true;

        public void Dispose() { }
    }
}
