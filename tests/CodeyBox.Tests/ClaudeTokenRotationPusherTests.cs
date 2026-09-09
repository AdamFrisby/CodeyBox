using System.Collections.Concurrent;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClaudeTokenRotationPusher"/>: the bridge that pushes
/// a freshly-rotated host-side Claude access token into every VM currently
/// running a Claude agent so the in-VM CLI doesn't 401 on its next API call.
/// The ambient Serilog logger is swapped (under the <c>GlobalSerilog</c>
/// collection) so the per-VM audit events can be observed.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ClaudeTokenRotationPusherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestSink _sink = new();

    public ClaudeTokenRotationPusherTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "codeybox-token-pusher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteCredFile(string content)
    {
        var path = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(path, content);
        return path;
    }

    // ── Push behaviour ────────────────────────────────────────────────────────

    [Fact]
    public async Task PushToAll_WritesSanitisedBundleIntoEveryRegisteredSandbox()
    {
        // The push pathway is the production fix for the residual rotation
        // gap left by PR #98: when the host rotates the access_token while
        // multiple VMs are mid-iteration, every one of them must receive the
        // fresh bundle so the next in-VM Anthropic call doesn't 401.
        var path = WriteCredFile(
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-new","refreshToken":"rt-secret","expiresAt":9999999999}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vmA = new RecordingSandbox("codeybox-vm-a");
        var vmB = new RecordingSandbox("codeybox-vm-b");
        using var regA = pusher.RegisterActiveSandbox(vmA);
        using var regB = pusher.RegisterActiveSandbox(vmB);

        await pusher.PushToAllAsync();

        Assert.Single(vmA.Execs);
        Assert.Single(vmB.Execs);
        AssertBundleWrittenViaStdin(vmA.Execs[0]);
        AssertBundleWrittenViaStdin(vmB.Execs[0]);
    }

    [Fact]
    public async Task PushToAll_BundleOmitsRefreshTokenAndIsNotOnArgv()
    {
        // The PR #98 invariant — only the host can refresh — must hold through
        // the runtime push. The bundle MUST NOT carry the refresh_token, and
        // MUST NOT appear on argv (which would expose the secret on multipass
        // exec's host-side process command line).
        var path = WriteCredFile(
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-new","refreshToken":"rt-secret","expiresAt":9999999999}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm");
        using var reg = pusher.RegisterActiveSandbox(vm);
        await pusher.PushToAllAsync();

        var exec = Assert.Single(vm.Execs);
        Assert.NotNull(exec.Stdin);
        Assert.DoesNotContain("refreshToken", exec.Stdin!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rt-secret", exec.Stdin!);
        Assert.Contains("sk-ant-oat01-new", exec.Stdin!);

        // The bundle/secret must not appear anywhere on the argv.
        foreach (var arg in exec.Argv)
        {
            Assert.DoesNotContain("rt-secret", arg);
            Assert.DoesNotContain("sk-ant-oat01-new", arg);
        }
        // Argv ExtraEnvironment carries no copy of the bundle either.
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (_, v) in exec.ExtraEnvironment)
                Assert.DoesNotContain("sk-ant-oat01-new", v);
        }
    }

    [Fact]
    public async Task PushToAll_NoRegistrations_IsNoOp()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        await pusher.PushToAllAsync();

        // Sanity: no audit events emitted because no sandbox was registered.
        Assert.DoesNotContain(_sink.Events, e => GetEventName(e) == "agent.claude_token_pushed_to_vm");
    }

    [Fact]
    public async Task PushToAll_MalformedCredentialsFile_SkipsWithoutAudit()
    {
        // If the rotated file fails to parse we don't push garbage into a VM
        // (which would invalidate the still-good in-VM token) and we don't
        // emit a misleading "pushed" audit event.
        var path = WriteCredFile("not json");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm");
        using var reg = pusher.RegisterActiveSandbox(vm);
        await pusher.PushToAllAsync();

        Assert.Empty(vm.Execs);
        Assert.DoesNotContain(_sink.Events, e => GetEventName(e) == "agent.claude_token_pushed_to_vm");
    }

    // ── Registration lifecycle ────────────────────────────────────────────────

    [Fact]
    public async Task DisposingRegistration_RemovesSandboxFromActiveSet()
    {
        // The runner unregisters when the Claude invocation completes, so a
        // subsequent rotation must not push into the now-idle sandbox.
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm");
        var reg = pusher.RegisterActiveSandbox(vm);
        Assert.Single(pusher.ActiveSandboxes);

        reg.Dispose();
        Assert.Empty(pusher.ActiveSandboxes);

        await pusher.PushToAllAsync();
        Assert.Empty(vm.Execs);
    }

    [Fact]
    public void RegisterActiveSandbox_TwiceForSameSandbox_RegistersIndependentTokens()
    {
        // Each Register call returns its own disposable; disposing one must
        // not retire the other. This is the structural guarantee that allows
        // reentrant agent invocations against the same sandbox.
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm");
        var regA = pusher.RegisterActiveSandbox(vm);
        var regB = pusher.RegisterActiveSandbox(vm);
        Assert.Equal(2, pusher.ActiveSandboxes.Count);
        regA.Dispose();
        Assert.Single(pusher.ActiveSandboxes);
        regB.Dispose();
        Assert.Empty(pusher.ActiveSandboxes);
    }

    [Fact]
    public void RegisterActiveSandbox_NullSandbox_Throws()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        Assert.Throws<ArgumentNullException>(() => pusher.RegisterActiveSandbox(null!));
    }

    // ── Audit emission ────────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulPush_EmitsClaudeTokenPushedToVmAuditEvent()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vmA = new RecordingSandbox("codeybox-vm-a");
        var vmB = new RecordingSandbox("codeybox-vm-b");
        using var regA = pusher.RegisterActiveSandbox(vmA);
        using var regB = pusher.RegisterActiveSandbox(vmB);

        await pusher.PushToAllAsync();

        var pushedEvents = _sink.Events
            .Where(e => GetEventName(e) == "agent.claude_token_pushed_to_vm")
            .ToList();
        Assert.Equal(2, pushedEvents.Count);
        var sandboxNames = pushedEvents
            .Select(e => GetScalar<string>(e, "SandboxName"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "codeybox-vm-a", "codeybox-vm-b" }, sandboxNames);
        foreach (var e in pushedEvents)
            Assert.Equal(LogEventLevel.Information, e.Level);
    }

    [Fact]
    public async Task FailedPush_EmitsClaudeTokenPushFailedAuditEventAndDoesNotEmitSuccess()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm", failingExec: true, stderr: "permission denied");
        using var reg = pusher.RegisterActiveSandbox(vm);

        await pusher.PushToAllAsync();

        var failed = Assert.Single(_sink.Events, e => GetEventName(e) == "agent.claude_token_push_failed");
        Assert.Equal(LogEventLevel.Warning, failed.Level);
        Assert.Equal("codeybox-vm", GetScalar<string>(failed, "SandboxName"));
        Assert.Contains("exit code 1", GetScalar<string>(failed, "Reason") ?? "");

        Assert.DoesNotContain(_sink.Events, e => GetEventName(e) == "agent.claude_token_pushed_to_vm");
    }

    [Fact]
    public async Task ThrowingSandbox_EmitsClaudeTokenPushFailedAuditEvent()
    {
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm-throw", throwOnExec: new IOException("disk failure"));
        using var reg = pusher.RegisterActiveSandbox(vm);

        await pusher.PushToAllAsync();

        var failed = Assert.Single(_sink.Events, e => GetEventName(e) == "agent.claude_token_push_failed");
        Assert.Equal("codeybox-vm-throw", GetScalar<string>(failed, "SandboxName"));
        Assert.Contains("disk failure", GetScalar<string>(failed, "Reason") ?? "");
    }

    [Fact]
    public async Task RotationPush_DoesNotEmitClaudeUnauthorizedAuditEvent()
    {
        // PR #98's agent.claude_unauthorized signal must only fire when
        // Anthropic genuinely rejects the latest token (real 401 in agent
        // stderr/stdout) — not as a side-effect of a normal rotation push.
        // This test asserts the structural property: the rotation pathway
        // never invokes ClaudeUnauthorizedObserved.
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm");
        using var reg = pusher.RegisterActiveSandbox(vm);

        await pusher.PushToAllAsync();

        Assert.DoesNotContain(_sink.Events, e => GetEventName(e) == "agent.claude_unauthorized");
    }

    // ── End-to-end via FileSystemWatcher ──────────────────────────────────────

    [Fact]
    public async Task RotationDetectedByWatcher_TriggersPushIntoActiveSandbox()
    {
        // Acceptance criterion #1: when ~/.claude/.credentials.json rotates
        // while a VM is running, the pusher must observe the rotation via
        // the file watcher and push the fresh bundle into the VM. This
        // exercises the full TokenUpdated → OnTokenUpdated → PushToAllAsync
        // path end-to-end.
        var path = WriteCredFile("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-old"}}""");
        using var source = new ClaudeCredentialFileSource(path);
        using var pusher = new ClaudeTokenRotationPusher(source);

        var vm = new RecordingSandbox("codeybox-vm");
        using var reg = pusher.RegisterActiveSandbox(vm);

        // Rewrite the file with a fresh token — simulates the host CLI
        // refreshing while the VM iteration is mid-flight.
        File.WriteAllText(path, """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-new"}}""");

        // The watcher delivery is async; poll until the push is observed or
        // a generous timeout elapses.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && vm.Execs.Count == 0)
            await Task.Delay(50);

        var exec = Assert.Single(vm.Execs);
        Assert.NotNull(exec.Stdin);
        Assert.Contains("sk-ant-oat01-new", exec.Stdin!);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertBundleWrittenViaStdin(SandboxExec exec)
    {
        Assert.True(exec.Argv.Count >= 9);
        Assert.Equal("bash", exec.Argv[0]);
        Assert.Equal("-c", exec.Argv[1]);
        Assert.Equal("$HOME", exec.Argv[4]);
        Assert.Equal(".claude/.credentials.json", exec.Argv[5]);
        // Bundle must be piped in via stdin, not via env-var or argv.
        Assert.NotNull(exec.Stdin);
        using var doc = JsonDocument.Parse(exec.Stdin!);
        Assert.True(doc.RootElement
            .GetProperty("claudeAiOauth")
            .TryGetProperty("accessToken", out _));
    }

    private static string? GetEventName(LogEvent evt) => GetScalar<string>(evt, "EventName");

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t) return t;
        return default;
    }

    /// <summary>
    /// Test sandbox that records every <see cref="SandboxExec"/> it receives
    /// and can be configured to fail or throw on exec to exercise the
    /// pusher's error pathways.
    /// </summary>
    internal sealed class RecordingSandbox : ISandbox
    {
        private readonly bool _failingExec;
        private readonly string _stderr;
        private readonly Exception? _throwOnExec;

        public RecordingSandbox(string id, bool failingExec = false, string stderr = "", Exception? throwOnExec = null)
        {
            Id = id;
            _failingExec = failingExec;
            _stderr = stderr;
            _throwOnExec = throwOnExec;
        }

        public string Id { get; }
        public ConcurrentBag<SandboxExec> ExecsBag { get; } = new();
        public IReadOnlyList<SandboxExec> Execs => ExecsBag.ToArray();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ExecsBag.Add(exec);
            if (_throwOnExec is not null) throw _throwOnExec;
            return Task.FromResult(_failingExec
                ? new SandboxExecResult(1, "", _stderr)
                : new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
