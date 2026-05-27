using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// Tests for R8-core adoption: <see cref="MultipassSandboxProvider.WaitForAdoptedAgentCompletionAsync"/>
/// (post-resume polling loop) and the <c>IsValidAgentLogPath</c> security
/// validator. The orchestrator's <c>SandboxResumeOnStartupService</c> calls
/// these after every <c>multipass start</c> to re-tail the in-VM agent log and
/// observe the wrapper's <c>.exit</c> sidecar marker.
///
/// <para>The validator is security-sensitive: a DB-tamper attacker who flips
/// <c>work_items.agent_log_path</c> to (say) <c>/home/ubuntu/.ssh/id_ed25519</c>
/// would otherwise coerce the resume handler into streaming the contents back
/// through the adopted-agent log forwarder. The anchor-under-AgentLogDir rule
/// enforced here is defence-in-depth — orchestrator-side selection already
/// constrains the path, but the provider re-checks because the persistence
/// layer is between the orchestrator and the multipass call.</para>
/// </summary>
public sealed class MultipassAdoptionTests
{
    private const string ValidLogPath = "/work/.codeybox/agent-logs/wi123-work-i0.log";

    // ── IsValidAgentLogPath ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/work/.codeybox/agent-logs/foo.log", true)]
    [InlineData("/work/.codeybox/agent-logs/sub/nested.log", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("relative/path.log", false)]
    [InlineData("/tmp/sneaky.log", false)] // not under AgentLogDir
    [InlineData("/work/.codeybox/agent-logs", false)] // no trailing slash, this is the dir itself
    [InlineData("/work/.codeybox/agent-logs-other/x.log", false)] // sibling dir not anchored
    [InlineData("/work/.codeybox/agent-logs/../../etc/passwd", false)] // dot-dot
    [InlineData("/work/.codeybox/agent-logs/a$b.log", false)] // $
    [InlineData("/work/.codeybox/agent-logs/a`b.log", false)] // backtick
    [InlineData("/work/.codeybox/agent-logs/a\"b.log", false)] // double quote
    [InlineData("/work/.codeybox/agent-logs/a'b.log", false)] // single quote
    [InlineData("/work/.codeybox/agent-logs/a\\b.log", false)] // backslash
    [InlineData("/work/.codeybox/agent-logs/a\nb.log", false)] // newline
    [InlineData("/work/.codeybox/agent-logs/a\rb.log", false)] // carriage return
    [InlineData("/work/.codeybox/agent-logs/a\0b.log", false)] // NUL
    public void IsValidAgentLogPath_AcceptsLegitimatePaths_RejectsShellAndPathInjection(
        string path, bool expected)
    {
        Assert.Equal(expected, MultipassSandboxProvider.IsValidAgentLogPath(path));
    }

    [Fact]
    public void IsValidAgentLogPath_RejectsControlCharactersBelowSpace()
    {
        // Defence-in-depth: any control byte (0x00-0x1f, 0x7f) could be used
        // to inject newlines / bell / escape sequences into shell-rendered
        // output. We reject the whole class rather than relying on quoting.
        for (var ch = 0; ch < 0x20; ch++)
        {
            var path = "/work/.codeybox/agent-logs/x" + (char)ch + ".log";
            Assert.False(MultipassSandboxProvider.IsValidAgentLogPath(path),
                $"control char 0x{ch:x2} must be rejected");
        }
        Assert.False(MultipassSandboxProvider.IsValidAgentLogPath(
            "/work/.codeybox/agent-logs/x.log"));
    }

    [Fact]
    public void IsValidAgentLogPath_AnchorMatchesSandboxConventions()
    {
        // Sanity that the validator's anchor agrees with the path the
        // orchestrator computes. Decoupling them silently would put us in
        // a state where every legitimate persisted path is rejected.
        Assert.True(MultipassSandboxProvider.IsValidAgentLogPath(
            SandboxConventions.AgentLogDir + "/sample.log"));
    }

    // ── WaitForAdoptedAgentCompletionAsync ───────────────────────────────────

    [Fact]
    public async Task WaitForAdopted_StreamsAppendedBytes_AndReturnsExitCode()
    {
        // Happy path: the in-VM file grows by two chunks, then the .exit
        // sidecar appears with code 0. We verify the runner is asked to
        // tail each chunk with an advancing byte offset and that the final
        // flush after the exit marker also picks up trailing bytes.
        var script = new VmScript()
            .AddTailRead(offset: 1, bytes: "first chunk\n")
            .AddExitMissing()
            .AddTailRead(offset: 13, bytes: "second chunk\n")
            .AddExitMissing()
            .AddTailRead(offset: 27, bytes: "")
            .AddExitPresent(0)
            // Final flush after the marker is observed.
            .AddTailRead(offset: 27, bytes: "post-exit trailing\n");

        var runner = script.Build();
        var provider = NewProviderWithRunner(runner);
        var emitted = new List<string>();

        var exit = await provider.WaitForAdoptedAgentCompletionAsync(
            "codeybox-test", ValidLogPath,
            chunk => emitted.Add(chunk),
            deadline: TimeSpan.FromSeconds(5),
            ct: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["first chunk\n", "second chunk\n", "post-exit trailing\n"], emitted);
    }

    [Fact]
    public async Task WaitForAdopted_ReturnsNegativeOne_WhenExitMarkerIsCorrupt()
    {
        // The wrapper writes the exit code as a single integer line. A
        // corrupted marker file (truncated to half a byte, written by a
        // misbehaving cron, etc.) is rare but must still terminate the
        // poll loop so the orchestrator can fall through to recovery
        // rather than spin forever.
        var script = new VmScript()
            .AddTailRead(offset: 1, bytes: "hello\n")
            .AddExitPresentRaw("not-an-integer")
            .AddTailRead(offset: 7, bytes: "");

        var runner = script.Build();
        var provider = NewProviderWithRunner(runner);

        var exit = await provider.WaitForAdoptedAgentCompletionAsync(
            "codeybox-test", ValidLogPath,
            logSink: null,
            deadline: TimeSpan.FromSeconds(5),
            ct: CancellationToken.None);

        Assert.Equal(-1, exit);
    }

    [Fact]
    public async Task WaitForAdopted_ReturnsNull_WhenCancelled()
    {
        // Cancelling the wait (host shutdown during the resume sweep) must
        // resolve immediately with null and NOT throw — the caller already
        // logs the deadline-elapsed case via the same return signal.
        var script = new VmScript()
            // Many empty tails + missing markers; never converges.
            .AddTailRead(offset: 1, bytes: "")
            .AddExitMissing();
        for (var i = 0; i < 50; i++)
        {
            script.AddTailRead(offset: 1, bytes: "");
            script.AddExitMissing();
        }

        var runner = script.Build();
        var provider = NewProviderWithRunner(runner);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var exit = await provider.WaitForAdoptedAgentCompletionAsync(
            "codeybox-test", ValidLogPath,
            logSink: null,
            deadline: TimeSpan.FromSeconds(10),
            ct: cts.Token);

        Assert.Null(exit);
    }

    [Fact]
    public async Task WaitForAdopted_RejectsInvalidVmName()
    {
        var provider = NewProviderWithRunner(new VmScript().Build());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.WaitForAdoptedAgentCompletionAsync(
                "../escape", ValidLogPath, null,
                deadline: TimeSpan.FromSeconds(1), ct: CancellationToken.None));
    }

    [Fact]
    public async Task WaitForAdopted_RejectsInvalidAgentLogPath()
    {
        var provider = NewProviderWithRunner(new VmScript().Build());

        // Path is not anchored under AgentLogDir → ArgumentException, not
        // a silent multipass-exec of an attacker-chosen file.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.WaitForAdoptedAgentCompletionAsync(
                "codeybox-test", "/etc/passwd", null,
                deadline: TimeSpan.FromSeconds(1), ct: CancellationToken.None));
    }

    [Fact]
    public async Task WaitForAdopted_ReturnsNull_WhenAgentLogPathIsWhitespace()
    {
        // Whitespace-only path is treated as "no log path persisted" so the
        // adoption is a no-op rather than throwing. The resume service uses
        // this branch when an item's AgentLogPath column is blank (older row
        // pre-dating the column, or never set).
        var provider = NewProviderWithRunner(new VmScript().Build());

        var exit = await provider.WaitForAdoptedAgentCompletionAsync(
            "codeybox-test", "   ", null,
            deadline: TimeSpan.FromSeconds(1), ct: CancellationToken.None);

        Assert.Null(exit);
    }

    [Fact]
    public async Task WaitForAdopted_ContinuesPolling_WhenTailReadFails()
    {
        // A transient `multipass exec` failure (e.g. multipassd hiccup) must
        // not abort the wait — the loop retries on the next tick. The
        // ReadLogTailAsync returns offset+empty on non-zero exit so the next
        // poll re-issues the same tail.
        var calls = new List<IReadOnlyList<string>>();
        var ticks = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            calls.Add(argv.ToArray());
            // First two ticks fail; then we observe the agent's output and exit.
            ticks++;
            if (IsTailCall(argv))
            {
                if (ticks <= 2)
                    return Task.FromResult(new RunResult(1, "", "transient failure"));
                return Task.FromResult(new RunResult(0, "agent output\n", ""));
            }
            if (IsExitCall(argv))
            {
                return ticks >= 4
                    ? Task.FromResult(new RunResult(0, "0\n", ""))
                    : Task.FromResult(new RunResult(1, "", ""));
            }
            return Task.FromResult(new RunResult(99, "", "unexpected argv"));
        });

        var provider = NewProviderWithRunner(runner);
        var emitted = new List<string>();
        var exit = await provider.WaitForAdoptedAgentCompletionAsync(
            "codeybox-test", ValidLogPath,
            chunk => emitted.Add(chunk),
            deadline: TimeSpan.FromSeconds(10),
            ct: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("agent output\n", emitted);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsTailCall(IReadOnlyList<string> argv) =>
        argv.Count >= 6
        && argv[1] == "exec"
        && argv[3] == "--"
        && argv[4] == "sh"
        && argv[5] == "-c"
        && argv.Count > 6
        && argv[6].StartsWith("tail -c +", StringComparison.Ordinal);

    private static bool IsExitCall(IReadOnlyList<string> argv) =>
        argv.Count >= 7
        && argv[1] == "exec"
        && argv[3] == "--"
        && argv[4] == "sh"
        && argv[5] == "-c"
        && argv[6].StartsWith("test -f", StringComparison.Ordinal);

    private static MultipassSandboxProvider NewProviderWithRunner(IProcessRunner runner)
    {
        var staging = Directory.CreateTempSubdirectory("codeybox-adopt-").FullName;
        var options = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/false",
            StagingDirectory = staging,
        };
        var policy = new MultipassDaemonRetryPolicy
        {
            Delay = (_, _) => Task.CompletedTask,
            HealthProbeTimeout = TimeSpan.FromMilliseconds(50),
        };
        return new MultipassSandboxProvider(
            options,
            NullLogger<MultipassSandboxProvider>.Instance,
            timings: null,
            runner: runner,
            daemonRetryPolicy: policy);
    }

    /// <summary>
    /// Scripted in-VM responses for the adoption poll loop. Models each tick
    /// as a (tail-read, exit-read) pair. The runner consumes one entry per
    /// call in arrival order so tests describe the expected sequence rather
    /// than juggling state machines.
    /// </summary>
    private sealed class VmScript
    {
        private readonly ConcurrentQueue<ScriptStep> _steps = new();

        public VmScript AddTailRead(long offset, string bytes)
        {
            _steps.Enqueue(new ScriptStep(StepKind.Tail, offset, bytes, 0));
            return this;
        }
        public VmScript AddExitMissing()
        {
            _steps.Enqueue(new ScriptStep(StepKind.ExitMissing, 0, "", 0));
            return this;
        }
        public VmScript AddExitPresent(int code)
        {
            _steps.Enqueue(new ScriptStep(StepKind.ExitPresent, 0, code.ToString(), code));
            return this;
        }
        public VmScript AddExitPresentRaw(string body)
        {
            _steps.Enqueue(new ScriptStep(StepKind.ExitPresent, 0, body, 0));
            return this;
        }

        public IProcessRunner Build()
        {
            return new RecordingMultipassRunner((argv, _, _) =>
            {
                if (!_steps.TryDequeue(out var step))
                {
                    // Default: behave as "nothing new, no marker" so the loop
                    // can spin until the caller's cancellation/deadline fires.
                    return Task.FromResult(IsExitCall(argv)
                        ? new RunResult(1, "", "")
                        : new RunResult(0, "", ""));
                }
                return step.Kind switch
                {
                    StepKind.Tail => Task.FromResult(new RunResult(0, step.Body, "")),
                    StepKind.ExitMissing => Task.FromResult(new RunResult(1, "", "")),
                    StepKind.ExitPresent => Task.FromResult(new RunResult(0, step.Body + "\n", "")),
                    _ => Task.FromResult(new RunResult(0, "", "")),
                };
            });
        }

        private enum StepKind { Tail, ExitMissing, ExitPresent }
        private sealed record ScriptStep(StepKind Kind, long Offset, string Body, int Code);
    }
}
