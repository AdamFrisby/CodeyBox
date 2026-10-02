using CodeyBox.Core;
using CodeyBox.HostProcess;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// The producer's command-invocation helper over the shared
/// <see cref="IProcessRunner"/>: argv arrays only (never a shell string), a
/// working directory, capped stdout/stderr, a per-command timeout, and a
/// per-run <c>DOTNET_CLI_HOME</c> pinned outside the measured checkout.
/// </summary>
public sealed class HostCommands : IDisposable
{
    private const int TailChars = 2000;

    private readonly IProcessRunner _runner;
    private readonly object _cliHomeSync = new();
    private string? _cliHome;

    public HostCommands(IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>
    /// Runs <paramref name="argv"/> in <paramref name="workingDirectory"/>
    /// and returns capped stdout/stderr. The char caps are applied as byte
    /// caps — the stricter bound. Output that outlives the child's exit plus
    /// the runner's post-exit drain grace arrives with the limit-exceeded
    /// flags set, so a silently truncated test list cannot masquerade as
    /// complete. Throws <see cref="TestSelectionBaselineProduceException"/>
    /// on timeout.
    /// </summary>
    public async Task<ProcessRunResult> CappedAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        int maxStdoutChars,
        int maxStderrChars,
        TimeSpan timeout,
        string label,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
            throw new ArgumentException("Process argv must contain an executable.", nameof(argv));
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (maxStdoutChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStdoutChars));
        if (maxStderrChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStderrChars));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _runner.RunAsync(
                argv,
                stdin: null,
                timeoutCts.Token,
                maxStdoutBytes: maxStdoutChars,
                maxStderrBytes: maxStderrChars,
                environment: BuildEnvironment(argv),
                killOnOutputLimit: false,
                workingDirectory: workingDirectory).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TestSelectionBaselineProduceException(
                $"{label} exceeded the {timeout.TotalSeconds:0}s timeout.");
        }
    }

    /// <summary>
    /// The last <see cref="TailChars"/> characters of process output, for
    /// error messages. Control characters are replaced BEFORE the tail is
    /// taken — child output is checkout-authored content and reaches the
    /// operator's terminal, so raw ESC/OSC bytes (fake hyperlinks,
    /// clipboard writes, screen clears) must not pass through.
    /// Untrusted output is never logged wholesale.
    /// </summary>
    public static string Tail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var sanitized = SanitizeForMessage(value, keepLineBreaks: true).Trim();
        return sanitized.Length <= TailChars ? sanitized : sanitized[^TailChars..];
    }

    /// <summary>
    /// Replaces control characters with '_'. When
    /// <paramref name="keepLineBreaks"/> is set, '\n' and '\t' survive
    /// (multi-line tails); everything else — including ESC, carriage return
    /// and DEL — is neutralised.
    /// </summary>
    internal static string SanitizeForMessage(string value, bool keepLineBreaks)
    {
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            chars[i] = char.IsControl(ch) && !(keepLineBreaks && ch is '\n' or '\t')
                ? '_'
                : ch;
        }

        return new string(chars);
    }

    /// <summary>
    /// The environment for one invocation: ambient variables plus, for
    /// <c>dotnet</c> commands, a pinned CLI home. Supplied to the shared
    /// runner as a full REPLACEMENT set, so ambient variables are merged in
    /// here explicitly — <see cref="IProcessRunner"/> replace semantics mean
    /// nothing is inherited implicitly.
    /// </summary>
    private IReadOnlyDictionary<string, string>? BuildEnvironment(IReadOnlyList<string> argv)
    {
        if (!DotnetCliHomeConventions.IsDotnetInvocation(argv))
            return null;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                environment[key] = value;
        }

        DotnetCliHomeConventions.ApplyIfDotnetInvocation(argv, CliHomeDirectory(), environment);
        return environment;
    }

    /// <summary>
    /// A fresh, unpredictable CLI-home directory per producer run. A fixed
    /// shared name under the temp dir could be pre-created by another local
    /// user to control the NuGet cache these dotnet invocations resolve.
    /// Also pinned under the temp dir rather than inside the measured
    /// checkout — <see cref="DotnetCliHomeConventions"/> would otherwise
    /// create <c>&lt;repo&gt;/.dotnet-cli-home</c> as a produce side effect.
    /// </summary>
    private string CliHomeDirectory()
    {
        lock (_cliHomeSync)
        {
            _cliHome ??= Path.Combine(
                Path.GetTempPath(),
                "codeybox-test-selection-dotnet-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cliHome);
            return _cliHome;
        }
    }

    /// <summary>
    /// Best-effort removal of the per-run CLI home — it can hold a restored
    /// NuGet package cache. A subsequent invocation lazily recreates it.
    /// </summary>
    public void Dispose()
    {
        string? home;
        lock (_cliHomeSync)
        {
            home = _cliHome;
            _cliHome = null;
        }

        if (home is null)
            return;
        try
        {
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
