using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.HostProcess;
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

    // ── IsValidAbsolutePath ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/work", true)]
    [InlineData("/work/", true)]
    [InlineData("/work/subdir", true)]
    [InlineData("/work/a/b/c", true)]
    [InlineData("/", true)]
    [InlineData("", false)]                  // empty
    [InlineData(" ", false)]                 // not absolute (no leading slash)
    [InlineData("work", false)]              // relative
    [InlineData("./work", false)]            // relative-dot
    [InlineData("../work", false)]           // contains ..
    [InlineData("/work/..", false)]          // contains ..
    [InlineData("/work/../etc", false)]      // contains ..
    [InlineData("/work/a..b", false)]        // even an inert ".." substring is rejected
    [InlineData("/work/a$b", false)]         // $
    [InlineData("/work/a`b", false)]         // backtick
    [InlineData("/work/a\"b", false)]        // double quote
    [InlineData("/work/a'b", false)]         // single quote
    [InlineData("/work/a\\b", false)]        // backslash
    [InlineData("/work/a\nb", false)]        // newline
    [InlineData("/work/a\rb", false)]        // carriage return
    [InlineData("/work/a\0b", false)]        // NUL
    public void IsValidAbsolutePath_AcceptsLegitimate_RejectsShellAndPathInjection(
        string path, bool expected)
    {
        Assert.Equal(expected, MultipassSandboxProvider.IsValidAbsolutePath(path));
    }

    [Fact]
    public void IsValidAbsolutePath_RejectsAllControlBytes()
    {
        // Each control byte (0x00-0x1f, 0x7f) could be used to inject newlines,
        // bell, escape sequences, etc. into the in-VM sh -c. Reject the whole
        // class — defence-in-depth even though we single-quote the value.
        for (var ch = 0; ch < 0x20; ch++)
        {
            var path = "/work/x" + (char)ch + "y";
            Assert.False(MultipassSandboxProvider.IsValidAbsolutePath(path),
                $"control char 0x{ch:x2} must be rejected");
        }
        Assert.False(MultipassSandboxProvider.IsValidAbsolutePath("/work/xy"),
            "0x7f (DEL) must be rejected");
    }

    // ── IsValidPreemptCheckpointRef ──────────────────────────────────────────

    [Theory]
    [InlineData("refs/heads/codeybox/preempt/abc", true)]
    [InlineData("refs/heads/codeybox/preempt/01234567-89ab-cdef-0123-456789abcdef", true)]
    [InlineData("refs/heads/codeybox/preempt/A", true)]
    [InlineData("refs/heads/codeybox/preempt/-", true)]
    [InlineData("", false)]                                              // empty
    [InlineData("refs/heads/codeybox/preempt/", false)]                  // empty suffix
    [InlineData("refs/heads/codeybox/preempts/abc", false)]              // wrong prefix
    [InlineData("refs/heads/codeybox/preempt", false)]                   // missing trailing slash → no suffix
    [InlineData("refs/heads/codebox/preempt/abc", false)]                // misspelled prefix
    [InlineData("efs/heads/codeybox/preempt/abc", false)]                // off-by-one prefix
    [InlineData("refs/tags/codeybox/preempt/abc", false)]                // wrong namespace
    [InlineData("codeybox/preempt/abc", false)]                          // not fully-qualified
    [InlineData("refs/heads/codeybox/preempt/abc def", false)]           // whitespace in suffix
    [InlineData("refs/heads/codeybox/preempt/abc/def", false)]           // slash in suffix
    [InlineData("refs/heads/codeybox/preempt/abc.def", false)]           // dot in suffix
    [InlineData("refs/heads/codeybox/preempt/abc_def", false)]           // underscore in suffix
    [InlineData("refs/heads/codeybox/preempt/abc$def", false)]           // shell metachar in suffix
    [InlineData("refs/heads/codeybox/preempt/abc;rm -rf /", false)]      // injection attempt
    [InlineData("refs/heads/codeybox/preempt/abc`whoami`", false)]       // backtick injection
    [InlineData("refs/heads/codeybox/preempt/abc\ndef", false)]          // newline
    public void IsValidPreemptCheckpointRef_AcceptsCodeyboxShape_RejectsEverythingElse(
        string refName, bool expected)
    {
        Assert.Equal(expected, MultipassSandboxProvider.IsValidPreemptCheckpointRef(refName));
    }

    [Fact]
    public void IsValidPreemptCheckpointRef_AcceptsGuidShape()
    {
        // The orchestrator builds refs of the form
        // refs/heads/codeybox/preempt/<guid>. Sanity that a real Guid (the
        // typical case) is accepted.
        var refName = "refs/heads/codeybox/preempt/" + Guid.NewGuid().ToString("D");
        Assert.True(MultipassSandboxProvider.IsValidPreemptCheckpointRef(refName));
    }

    // ── IsValidCheckpointCommitMessage ───────────────────────────────────────

    [Theory]
    [InlineData("CodeyBox preempt checkpoint", true)]
    [InlineData("multi\nline\nmessage", true)]                // LF allowed
    [InlineData("col1\tcol2", true)]                          // TAB allowed
    [InlineData("", false)]                                   // empty
    [InlineData("contains\rCR", false)]                       // CR rejected
    [InlineData("contains\0NUL", false)]                      // NUL rejected
    [InlineData("containsBELL", false)]                 // BEL (0x07) rejected
    [InlineData("containsESC", false)]                  // ESC (0x1b) rejected
    [InlineData("containsDEL", false)]                  // DEL (0x7f) rejected
    public void IsValidCheckpointCommitMessage_AcceptsTextLfTab_RejectsControlAndCr(
        string message, bool expected)
    {
        Assert.Equal(expected, MultipassSandboxProvider.IsValidCheckpointCommitMessage(message));
    }

    [Fact]
    public void IsValidCheckpointCommitMessage_RejectsAllControlBytesExceptLfAndTab()
    {
        // Defence-in-depth: every control byte (0x00-0x1f and 0x7f) is rejected
        // except 0x09 (TAB) and 0x0a (LF), which are common in real commit
        // messages. CR (0x0d) is rejected because it can collide with the
        // single-quoting on macOS/BSD shells.
        for (var ch = 0; ch < 0x20; ch++)
        {
            var msg = "msg" + (char)ch + "trailing";
            var expected = ch == '\n' || ch == '\t';
            Assert.Equal(expected, MultipassSandboxProvider.IsValidCheckpointCommitMessage(msg));
        }
        Assert.False(MultipassSandboxProvider.IsValidCheckpointCommitMessage("msgtrailing"));
    }

    [Fact]
    public void IsValidCheckpointCommitMessage_CapsAt1024Chars()
    {
        // The cap protects the in-VM `git commit -m '...'` argv length against
        // a pathological persisted value. 1024 is generous for a synthesised
        // preempt-checkpoint commit message ("CodeyBox preempt checkpoint for
        // <guid>"). The boundary should be inclusive at 1024 and exclusive at
        // 1025 — verify both sides.
        var exactlyAtCap = new string('a', 1024);
        var oneOver = new string('a', 1025);

        Assert.True(MultipassSandboxProvider.IsValidCheckpointCommitMessage(exactlyAtCap),
            "1024-char message must be accepted (boundary)");
        Assert.False(MultipassSandboxProvider.IsValidCheckpointCommitMessage(oneOver),
            "1025-char message must be rejected (over cap)");
    }

    // ── PushSuspendedVmCheckpointRefAsync ────────────────────────────────────

    [Fact]
    public async Task PushCheckpoint_HappyPath_RunsExpectedShScriptAndReturnsTrue()
    {
        // Verifies the real provider composes the exact `sh -c` script that
        // the in-VM git push relies on: set -e for short-circuit, scratchpad
        // creation, git add -A / commit --allow-empty / push origin HEAD:<ref>.
        // The fake provider used in SandboxSuspendResumeTests exercises the
        // surface but not this script — a regression that dropped `set -e` or
        // changed the push target would slip past those tests.
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProviderWithRunner(runner);

        var ok = await provider.PushSuspendedVmCheckpointRefAsync(
            vmName: "codeybox-test",
            workingDir: "/work",
            refName: "refs/heads/codeybox/preempt/abc-123",
            commitMessage: "CodeyBox preempt checkpoint for wi42",
            ct: CancellationToken.None);

        Assert.True(ok);
        Assert.Single(runner.Calls);

        var call = runner.Calls.ToArray()[0];
        Assert.Equal("exec", call.Argv[1]);
        Assert.Equal("codeybox-test", call.Argv[2]);
        Assert.Equal("--", call.Argv[3]);
        Assert.Equal("sh", call.Argv[4]);
        Assert.Equal("-c", call.Argv[5]);

        var script = call.Argv[6];
        // set -e is load-bearing: a mid-script failure (e.g. commit refusing
        // an empty author) must short-circuit before `git push` runs and
        // promotes a stale HEAD.
        Assert.StartsWith("set -e", script, StringComparison.Ordinal);
        Assert.Contains("cd '/work'", script);
        Assert.Contains("mkdir -p .codeybox", script);
        Assert.Contains(".codeybox/preempt-scratchpad.md", script);
        Assert.Contains("git add -A", script);
        Assert.Contains("git commit --allow-empty -m 'CodeyBox preempt checkpoint for wi42'", script);
        // Ref is inlined unquoted (git rejects single-quoted refs on push as
        // ambiguous) — the validator is what makes this safe.
        Assert.Contains("git push origin HEAD:refs/heads/codeybox/preempt/abc-123", script);
    }

    [Fact]
    public async Task PushCheckpoint_RunnerNonZeroExit_ReturnsFalse()
    {
        // A non-zero exit from the in-VM script (e.g. push rejected by remote,
        // commit refused) must surface as `false` so the orchestrator records
        // the promotion failure rather than silently advancing the work item
        // as if the checkpoint succeeded.
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(1, "", "push rejected")));
        var provider = NewProviderWithRunner(runner);

        var ok = await provider.PushSuspendedVmCheckpointRefAsync(
            "codeybox-test", "/work",
            "refs/heads/codeybox/preempt/abc",
            "msg",
            CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task PushCheckpoint_RunnerThrows_ReturnsFalse()
    {
        // An exception out of the runner (e.g. multipassd hiccup, transient
        // network error) is logged and converted to `false` — never lets a
        // throw propagate up into the orchestrator's resume promotion path.
        var runner = new RecordingMultipassRunner((_, _, _) =>
            throw new InvalidOperationException("multipassd unreachable"));
        var provider = NewProviderWithRunner(runner);

        var ok = await provider.PushSuspendedVmCheckpointRefAsync(
            "codeybox-test", "/work",
            "refs/heads/codeybox/preempt/abc",
            "msg",
            CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task PushCheckpoint_RunnerCancellation_Propagates()
    {
        // Cancellation must propagate (host shutdown during the resume sweep)
        // rather than being swallowed into a false return. The orchestrator
        // distinguishes "push failed" from "host shutting down" via the
        // OperationCanceledException.
        var runner = new RecordingMultipassRunner((_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProviderWithRunner(runner);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.PushSuspendedVmCheckpointRefAsync(
                "codeybox-test", "/work",
                "refs/heads/codeybox/preempt/abc",
                "msg",
                cts.Token));
    }

    [Theory]
    [InlineData("../escape", "/work", "refs/heads/codeybox/preempt/abc", "msg", "vmName")]
    [InlineData("codeybox-test", "relative/path", "refs/heads/codeybox/preempt/abc", "msg", "workingDir")]
    [InlineData("codeybox-test", "/work/../etc", "refs/heads/codeybox/preempt/abc", "msg", "workingDir")]
    [InlineData("codeybox-test", "/work", "refs/heads/wrong/shape", "msg", "refName")]
    [InlineData("codeybox-test", "/work", "refs/heads/codeybox/preempt/abc;rm", "msg", "refName")]
    [InlineData("codeybox-test", "/work", "refs/heads/codeybox/preempt/abc", "", "commitMessage")]
    [InlineData("codeybox-test", "/work", "refs/heads/codeybox/preempt/abc", "bad\rmsg", "commitMessage")]
    public async Task PushCheckpoint_RejectsInvalidArguments(
        string vmName, string workingDir, string refName, string commitMessage, string expectedParam)
    {
        // The four validators (IsValidSandboxName, IsValidAbsolutePath,
        // IsValidPreemptCheckpointRef, IsValidCheckpointCommitMessage) gate
        // the in-VM sh -c construction. An ArgumentException here means an
        // attacker who flipped the DB-stored value can't reach the runner.
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProviderWithRunner(runner);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.PushSuspendedVmCheckpointRefAsync(
                vmName, workingDir, refName, commitMessage, CancellationToken.None));
        Assert.Equal(expectedParam, ex.ParamName);
        // Critical: the runner is never invoked when validation fails — the
        // in-VM sh -c construction is unreachable for tampered inputs.
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task PushCheckpoint_SingleQuotesEscapeApostropheInCommitMessage()
    {
        // ShellSingleQuote uses the classic '"'"' trick to embed apostrophes.
        // A regression that dropped that escape would split the -m argument
        // and either fail noisily or (worse) interpret the trailing text as
        // a subsequent shell command. Verify the script encodes the
        // apostrophe correctly.
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProviderWithRunner(runner);

        var ok = await provider.PushSuspendedVmCheckpointRefAsync(
            "codeybox-test", "/work",
            "refs/heads/codeybox/preempt/abc",
            "agent's checkpoint",
            CancellationToken.None);

        Assert.True(ok);
        var script = runner.Calls.ToArray()[0].Argv[6];
        // '"'"' is the canonical shell-safe way to embed a single quote
        // inside a single-quoted string.
        Assert.Contains("'agent'\"'\"'s checkpoint'", script);
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
            .AddTailRead(offset: 26, bytes: "")
            .AddExitPresent(0)
            // Final flush after the marker is observed.
            .AddTailRead(offset: 26, bytes: "post-exit trailing\n");

        var runner = script.Build();
        var provider = NewProviderWithRunner(runner);
        var emitted = new List<string>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await provider.WaitForAdoptedAgentCompletionAsync(
            "codeybox-test", ValidLogPath,
            chunk => emitted.Add(chunk),
            deadline: null,
            ct: cts.Token);

        Assert.Equal(0, exit);
        Assert.Equal(["first chunk\n", "second chunk\n", "post-exit trailing\n"], emitted);
        AssertTailOffsets(runner, 1, 13, 26, 26);
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
                    return Task.FromResult(new ProcessRunResult(1, "", "transient failure"));
                return Task.FromResult(new ProcessRunResult(0, "agent output\n", ""));
            }
            if (IsExitCall(argv))
            {
                return ticks >= 4
                    ? Task.FromResult(new ProcessRunResult(0, "0\n", ""))
                    : Task.FromResult(new ProcessRunResult(1, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv"));
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

    private static void AssertTailOffsets(RecordingMultipassRunner runner, params long[] expectedOffsets)
    {
        var actual = runner.Calls
            .Select(call => call.Argv)
            .Where(IsTailCall)
            .Select(argv => ExtractTailOffset(argv[6]))
            .ToArray();

        Assert.Equal(expectedOffsets, actual);
    }

    private static long ExtractTailOffset(string script)
    {
        const string prefix = "tail -c +";
        var start = script.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"tail script did not contain '{prefix}': {script}");
        start += prefix.Length;
        var end = script.IndexOf(' ', start);
        Assert.True(end > start, $"tail script did not contain an offset terminator: {script}");
        return long.Parse(script[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

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
            daemonRetryPolicy: policy)
        {
            // Keep the adoption poll loop off the wall clock: a real 2s
            // Task.Delay can drift past these tests' short deadlines under a
            // loaded test host, producing a spurious timeout (null) instead of
            // the scripted exit code.
            AdoptionPollIntervalOverride = TimeSpan.FromMilliseconds(5),
        };
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

        public RecordingMultipassRunner Build()
        {
            return new RecordingMultipassRunner((argv, _, _) =>
            {
                if (!_steps.TryDequeue(out var step))
                {
                    // Default: behave as "nothing new, no marker" so the loop
                    // can spin until the caller's cancellation/deadline fires.
                    return Task.FromResult(IsExitCall(argv)
                        ? new ProcessRunResult(1, "", "")
                        : new ProcessRunResult(0, "", ""));
                }
                return step.Kind switch
                {
                    StepKind.Tail => Task.FromResult(new ProcessRunResult(0, step.Body, "")),
                    StepKind.ExitMissing => Task.FromResult(new ProcessRunResult(1, "", "")),
                    StepKind.ExitPresent => Task.FromResult(new ProcessRunResult(0, step.Body + "\n", "")),
                    _ => Task.FromResult(new ProcessRunResult(0, "", "")),
                };
            });
        }

        private enum StepKind { Tail, ExitMissing, ExitPresent }
        private sealed record ScriptStep(StepKind Kind, long Offset, string Body, int Code);
    }
}
