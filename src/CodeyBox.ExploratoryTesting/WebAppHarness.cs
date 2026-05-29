using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Graphical;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Web-app implementation of <see cref="IAppUnderTestHarness"/>. Uses any
/// <see cref="ISandboxProvider"/> that supports graphical sandboxes — in
/// practice the Multipass provider — so the harness itself is unit-testable
/// against a stub provider.
/// </summary>
public sealed class WebAppHarness : IAppUnderTestHarness
{
    private const string InVmLogDir = "/var/log/codeybox-harness";

    private readonly ISandboxProvider _provider;
    private readonly Func<ISandbox, ComputerUseBridge> _computerUseFactory;
    private readonly ILogger<WebAppHarness> _log;
    private readonly TimeProvider _timeProvider;

    public WebAppHarness(
        ISandboxProvider provider,
        ILogger<WebAppHarness>? log = null,
        Func<ISandbox, ComputerUseBridge>? computerUseFactory = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _computerUseFactory = computerUseFactory ?? (_ => new ComputerUseBridge());
        _log = log ?? NullLogger<WebAppHarness>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AppUnderTestSession> LaunchAsync(WebAppRecipe recipe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ValidateRecipe(recipe);
        var web = recipe;

        _log.LogInformation(
            "WebAppHarness.LaunchAsync: target={Target} entryUrl={EntryUrl} buildSteps={BuildCount} seedSteps={SeedCount}",
            web.TargetName, web.EntryUrl, web.BuildSteps.Count, web.SeedSteps.Count);

        var spec = BuildSandboxSpec(web);
        var sandbox = await _provider.CreateAsync(spec, ct);
        try
        {
            await PrepareInVmLogDirAsync(sandbox, ct);
            await RunSerialAsync(sandbox, web, web.BuildSteps, "build", ct);
            await RunSerialAsync(sandbox, web, web.SeedSteps, "seed", ct);
            await StartRunCommandAsync(sandbox, web, ct);
            await WaitForHttpReachableAsync(sandbox, web, ct);
            await OpenBrowserAsync(sandbox, web, ct);
            var screenshot = await WaitForRenderedUiAsync(sandbox, web, ct);
            var bridge = _computerUseFactory(sandbox);
            _log.LogInformation("WebAppHarness.LaunchAsync: target={Target} ready ({Bytes} byte screenshot)",
                web.TargetName, screenshot.Length);
            return new AppUnderTestSession(sandbox, bridge, web.EntryUrl, screenshot);
        }
        catch
        {
            // Best-effort teardown if any post-create step throws; otherwise
            // the multipass VM lingers until the leak reaper picks it up.
            try { await sandbox.DisposeAsync(); }
            catch (Exception disposeEx)
            {
                _log.LogWarning(disposeEx,
                    "WebAppHarness.LaunchAsync: target={Target} dispose-after-failure threw; sandbox may leak",
                    web.TargetName);
            }
            throw;
        }
    }

    private SandboxSpec BuildSandboxSpec(WebAppRecipe recipe) => new()
    {
        ImageReference = recipe.ImageReference,
        Mounts = recipe.Mounts,
        Environment = recipe.Environment,
        Limits = recipe.Limits ?? SandboxResourceLimits.Default,
        Flavor = SandboxProfileFlavor.Graphical,
        Network = new SandboxNetworkPolicy { ProfileName = recipe.NetworkProfile },
        WorkingDirectory = SandboxConventions.WorkDir,
    };

    private async Task PrepareInVmLogDirAsync(ISandbox sandbox, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mkdir", "-p", InVmLogDir],
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"WebAppHarness: failed to create in-VM log directory {InVmLogDir} " +
                $"(exit {result.ExitCode}): {result.Stderr.Trim()}");
    }

    private async Task RunSerialAsync(
        ISandbox sandbox,
        WebAppRecipe recipe,
        IReadOnlyList<RecipeStep> steps,
        string phase,
        CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var label = step.Label ?? (step.Command.Count > 0 ? step.Command[0] : "(empty)");
            _log.LogInformation(
                "WebAppHarness[{Target}]: {Phase} step {Index}/{Total}: {Label}",
                recipe.TargetName, phase, i + 1, steps.Count, label);
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = step.Command,
                WorkingDirectory = step.WorkingDirectory ?? SandboxConventions.WorkDir,
                ExtraEnvironment = step.Environment,
            }, ct);
            if (!result.Success)
                throw new HarnessRecipeStepFailedException(
                    recipe.TargetName, phase, label, result.ExitCode, result.Stderr);
        }
    }

    private async Task StartRunCommandAsync(ISandbox sandbox, WebAppRecipe recipe, CancellationToken ct)
    {
        // Background the long-running app process. We do NOT use a shell to
        // interpolate the recipe's argv — the recipe author already passed
        // a clean argv. Instead we spawn `setsid` with the argv directly and
        // redirect stdout/stderr to a per-target log via `sh -c` whose
        // command-line we compose from a fixed template. The argv positional
        // params ($@) keep the recipe author's argv quoted exactly as given;
        // there is no opportunity for an argv element to be re-parsed as
        // shell syntax.
        var logFile = $"{InVmLogDir}/{recipe.TargetName}.log";
        var pidFile = $"{InVmLogDir}/{recipe.TargetName}.pid";
        var shellScript = """
            cd "${HARNESS_RUN_CWD:-/work}" || exit 1
            nohup setsid "$@" >"$HARNESS_RUN_LOG" 2>&1 </dev/null &
            echo $! > "$HARNESS_RUN_PID"
            disown $! 2>/dev/null || true
            """;

        var argv = new List<string>
        {
            "sh", "-c", shellScript, "harness-run",
        };
        argv.AddRange(recipe.RunCommand.Command);

        // WHY: recipe env first, harness bookkeeping vars last. If a recipe
        // happens to declare HARNESS_RUN_LOG / PID / CWD the harness's values
        // win — the recipe must not be able to redirect our log / pid file
        // by accident or on purpose.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (recipe.RunCommand.Environment is { } extra)
            foreach (var kv in extra)
                env[kv.Key] = kv.Value;
        env["HARNESS_RUN_LOG"] = logFile;
        env["HARNESS_RUN_PID"] = pidFile;
        env["HARNESS_RUN_CWD"] = recipe.RunCommand.WorkingDirectory ?? SandboxConventions.WorkDir;

        _log.LogInformation(
            "WebAppHarness[{Target}]: starting run command (logged to {LogFile})",
            recipe.TargetName, logFile);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            WorkingDirectory = SandboxConventions.WorkDir,
            ExtraEnvironment = env,
        }, ct);
        if (!result.Success)
            throw new HarnessRecipeStepFailedException(
                recipe.TargetName,
                phase: "run",
                label: recipe.RunCommand.Label ?? "run",
                exitCode: result.ExitCode,
                stderr: result.Stderr);
    }

    private async Task WaitForHttpReachableAsync(ISandbox sandbox, WebAppRecipe recipe, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + recipe.ReadinessTimeout;
        var lastErr = "";
        while (_timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "curl", "--silent", "--show-error", "--fail",
                    "--max-time", "5",
                    "--output", "/dev/null",
                    "--write-out", "%{http_code}",
                    recipe.EntryUrl,
                ],
            }, ct);
            if (probe.Success)
            {
                _log.LogInformation(
                    "WebAppHarness[{Target}]: entry URL {EntryUrl} reachable (HTTP {Status})",
                    recipe.TargetName, recipe.EntryUrl, probe.Stdout.Trim());
                return;
            }
            lastErr = string.IsNullOrWhiteSpace(probe.Stderr) ? probe.Stdout : probe.Stderr;
            await Task.Delay(recipe.ReadinessPollInterval, _timeProvider, ct);
        }

        throw new HarnessReadinessTimeoutException(
            recipe.TargetName,
            $"app did not respond at {recipe.EntryUrl} within {recipe.ReadinessTimeout}. " +
            $"last curl error: {lastErr.Trim()}");
    }

    private async Task OpenBrowserAsync(ISandbox sandbox, WebAppRecipe recipe, CancellationToken ct)
    {
        var browserArgv = SubstituteUrl(recipe.BrowserCommand, recipe.EntryUrl);
        var logFile = $"{InVmLogDir}/{recipe.TargetName}-browser.log";

        // Same backgrounding pattern as the run command: argv positional
        // params keep the recipe's argv exact; only the template-literal
        // shell script and env vars are constants we control.
        var shellScript = """
            export DISPLAY="${HARNESS_DISPLAY:-:0}"
            nohup setsid "$@" >"$HARNESS_BROWSER_LOG" 2>&1 </dev/null &
            disown $! 2>/dev/null || true
            """;
        var argv = new List<string> { "sh", "-c", shellScript, "harness-browser" };
        argv.AddRange(browserArgv);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HARNESS_BROWSER_LOG"] = logFile,
            ["HARNESS_DISPLAY"] = SandboxConventions.GraphicalDisplay,
            ["DISPLAY"] = SandboxConventions.GraphicalDisplay,
        };

        _log.LogInformation(
            "WebAppHarness[{Target}]: launching in-VM browser ({Argv})",
            recipe.TargetName, string.Join(' ', browserArgv));
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            ExtraEnvironment = env,
        }, ct);
        if (!result.Success)
            throw new HarnessRecipeStepFailedException(
                recipe.TargetName,
                phase: "browser",
                label: "open",
                exitCode: result.ExitCode,
                stderr: result.Stderr);
    }

    private async Task<byte[]> WaitForRenderedUiAsync(ISandbox sandbox, WebAppRecipe recipe, CancellationToken ct)
    {
        if (recipe.BrowserSettleDelay > TimeSpan.Zero)
            await Task.Delay(recipe.BrowserSettleDelay, _timeProvider, ct);

        var deadline = _timeProvider.GetUtcNow() + recipe.ReadinessTimeout;
        byte[]? lastScreenshot = null;
        Exception? lastError = null;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lastScreenshot = await sandbox.GetScreenshotAsync(ct);
                lastError = null;
                if (PngRenderedUiReadiness.LooksLikeRenderedUi(lastScreenshot))
                {
                    _log.LogInformation(
                        "WebAppHarness[{Target}]: UI rendered ({Bytes} byte screenshot)",
                        recipe.TargetName, lastScreenshot.Length);
                    return lastScreenshot;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
            await Task.Delay(recipe.ReadinessPollInterval, _timeProvider, ct);
        }

        var detail = lastError is not null
            ? $"last screenshot threw: {lastError.GetType().Name}: {lastError.Message}"
            : "last screenshot did not pass rendered-UI pixel-diversity check";
        throw new HarnessReadinessTimeoutException(
            recipe.TargetName,
            $"UI did not render within {recipe.ReadinessTimeout}. {detail}");
    }

    private static IReadOnlyList<string> SubstituteUrl(IReadOnlyList<string> argv, string url)
    {
        var result = new List<string>(argv.Count);
        foreach (var arg in argv)
            result.Add(arg.Replace("$URL", url, StringComparison.Ordinal));
        return result;
    }

    private static void ValidateRecipe(WebAppRecipe recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe.TargetName))
            throw new ArgumentException("WebAppRecipe.TargetName is required.", nameof(recipe));
        foreach (var ch in recipe.TargetName)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-'))
                throw new ArgumentException(
                    $"WebAppRecipe.TargetName '{recipe.TargetName}' must be lowercase ASCII letters / digits / dashes.",
                    nameof(recipe));
            if (char.IsUpper(ch))
                throw new ArgumentException(
                    $"WebAppRecipe.TargetName '{recipe.TargetName}' must be lowercase.",
                    nameof(recipe));
        }
        if (string.IsNullOrWhiteSpace(recipe.EntryUrl))
            throw new ArgumentException("WebAppRecipe.EntryUrl is required.", nameof(recipe));
        if (string.IsNullOrWhiteSpace(recipe.NetworkProfile))
            throw new ArgumentException("WebAppRecipe.NetworkProfile is required.", nameof(recipe));
        if (recipe.RunCommand is null || recipe.RunCommand.Command is null || recipe.RunCommand.Command.Count == 0)
            throw new ArgumentException("WebAppRecipe.RunCommand.Command must be non-empty.", nameof(recipe));
        if (recipe.BrowserCommand is null || recipe.BrowserCommand.Count == 0)
            throw new ArgumentException("WebAppRecipe.BrowserCommand must be non-empty.", nameof(recipe));
        if (recipe.ReadinessTimeout <= TimeSpan.Zero)
            throw new ArgumentException("WebAppRecipe.ReadinessTimeout must be positive.", nameof(recipe));
        if (recipe.ReadinessPollInterval <= TimeSpan.Zero)
            throw new ArgumentException("WebAppRecipe.ReadinessPollInterval must be positive.", nameof(recipe));
        foreach (var step in recipe.BuildSteps.Concat(recipe.SeedSteps))
        {
            if (step.Command is null || step.Command.Count == 0)
                throw new ArgumentException("Every RecipeStep.Command must be non-empty.", nameof(recipe));
        }
    }
}

public sealed class HarnessRecipeStepFailedException : Exception
{
    public HarnessRecipeStepFailedException(string targetName, string phase, string label, int exitCode, string stderr)
        : base($"WebAppHarness[{targetName}]: {phase} step '{label}' failed (exit {exitCode}): {stderr.Trim()}")
    {
        TargetName = targetName;
        Phase = phase;
        Label = label;
        ExitCode = exitCode;
    }

    public string TargetName { get; }
    public string Phase { get; }
    public string Label { get; }
    public int ExitCode { get; }
}

public sealed class HarnessReadinessTimeoutException : Exception
{
    public HarnessReadinessTimeoutException(string targetName, string detail)
        : base($"WebAppHarness[{targetName}]: {detail}")
    {
        TargetName = targetName;
    }

    public string TargetName { get; }
}
