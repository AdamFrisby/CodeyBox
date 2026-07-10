using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Bridge between the host-side credential watcher
/// (<see cref="ClaudeCredentialFileSource"/>) and the VMs that are currently
/// running a Claude agent. PR #98 stripped the refresh_token from the bundle
/// shipped into each VM so only the host CLI can refresh, eliminating the
/// shared single-use refresh-token race. That fix left one residual gap: when
/// the host rotates the access_token while a VM is mid-iteration, the VM
/// continues holding the (now-invalidated) prior access_token and the next
/// Anthropic API call from that VM returns 401, classifying the iteration
/// as <c>agent.claude_unauthorized</c> and failing the work item. This
/// service closes that gap by:
///
/// <list type="number">
///   <item><description>Tracking active Claude-running sandboxes via
///   <see cref="RegisterActiveSandbox"/>; the Claude runner
///   registers each sandbox for the duration of <c>RunAsync</c> /
///   <c>RunResumedAsync</c>.</description></item>
///   <item><description>Subscribing to
///   <see cref="ClaudeCredentialFileSource.TokenUpdated"/> and, on each
///   rotation, building the same sanitised bundle the provider would have
///   shipped to a fresh sandbox and writing it via
///   <see cref="ISandbox.ExecAsync"/> into each registered VM's
///   <c>~/.claude/.credentials.json</c>. The bundle still omits the
///   refresh_token, preserving PR #98's "only the host can refresh"
///   invariant.</description></item>
/// </list>
///
/// <para><b>Audit trail:</b> every per-VM push emits
/// <c>agent.claude_token_pushed_to_vm</c> (or
/// <c>agent.claude_token_push_failed</c>) so operators can correlate a host
/// rotation event with the subsequent in-VM credential refresh.</para>
///
/// <para><b>Secrets handling:</b> the sanitised bundle is piped via stdin
/// rather than passed as an env var or argv element, so it never appears on
/// the <c>multipass exec</c> command line.</para>
/// </summary>
public sealed class ClaudeTokenRotationPusher : IClaudeTokenRotationPusher, IDisposable
{
    private readonly ClaudeCredentialFileSource _source;
    private readonly ILogger<ClaudeTokenRotationPusher>? _log;
    private readonly ConcurrentDictionary<string, ISandbox> _active = new(StringComparer.Ordinal);
    private bool _disposed;

    public ClaudeTokenRotationPusher(
        ClaudeCredentialFileSource source,
        ILogger<ClaudeTokenRotationPusher>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log;
        _source.TokenUpdated += OnTokenUpdated;
    }

    public IDisposable RegisterActiveSandbox(ISandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        // The key is the sandbox identity + a GUID so multiple concurrent
        // registrations of the same sandbox (e.g. reentrant agent calls in
        // tests) each own a distinct key and dispose independently.
        var key = $"{sandbox.Id}:{Guid.NewGuid():N}";
        _active[key] = sandbox;
        return new Registration(this, key);
    }

    /// <summary>
    /// Snapshot of currently-registered sandboxes. Exposed for tests; the
    /// production code path uses <see cref="OnTokenUpdated"/> directly.
    /// </summary>
    internal IReadOnlyCollection<ISandbox> ActiveSandboxes => _active.Values.ToArray();

    /// <summary>
    /// Synchronous entry point used by the rotation handler and by tests
    /// that need to drive a push deterministically. Awaits all in-flight
    /// pushes and never throws — failures are audit-logged per-VM.
    /// </summary>
    internal async Task PushToAllAsync(CancellationToken ct = default)
    {
        var raw = _source.GetRaw();
        if (!CredentialFileTokenExtractor.TryBuildClaudeSanitisedBundle(raw, out _, out var bundle))
        {
            _log?.LogWarning(
                "Claude credentials file rotated but did not parse into a sanitised bundle; skipping VM push");
            return;
        }

        var snapshot = _active.ToArray();
        if (snapshot.Length == 0)
            return;

        var pushes = new List<Task>(snapshot.Length);
        foreach (var (_, sandbox) in snapshot)
            pushes.Add(PushToSandboxAsync(sandbox, bundle, ct));
        await Task.WhenAll(pushes).ConfigureAwait(false);
    }

    private void OnTokenUpdated()
    {
        if (_disposed) return;
        // Fire-and-forget on the watcher thread; PushToAllAsync swallows its
        // own errors via per-VM audit logging.
        _ = Task.Run(() => PushToAllAsync(CancellationToken.None));
    }

    private async Task PushToSandboxAsync(ISandbox sandbox, string bundle, CancellationToken ct)
    {
        try
        {
            await SandboxCredentialFileWriter.WriteAsync(
                sandbox,
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    ".claude/.credentials.json"),
                bundle,
                SandboxCredentialOverwritePolicy.Overwrite,
                ct).ConfigureAwait(false);
            AuditLog.ClaudeTokenPushedToVm(sandbox.Id);
        }
        catch (OperationCanceledException)
        {
            // Don't audit-log a cancellation — the sandbox is presumably
            // tearing down and the rotation push race-loses against disposal.
        }
        catch (Exception ex)
        {
            AuditLog.ClaudeTokenPushFailed(sandbox.Id, ex.Message);
            _log?.LogWarning(ex,
                "Failed to push rotated Claude token into sandbox {Sandbox}",
                sandbox.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.TokenUpdated -= OnTokenUpdated;
    }

    private sealed class Registration : IDisposable
    {
        private readonly ClaudeTokenRotationPusher _owner;
        private readonly string _key;
        private int _disposed;

        public Registration(ClaudeTokenRotationPusher owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _owner._active.TryRemove(_key, out _);
        }
    }
}
