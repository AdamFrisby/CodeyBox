using System.Diagnostics;
using System.Text;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.Tests.E2eAuthoring;

internal static class DemoLoginSandboxExec
{
    public static bool IsDnsLookup(SandboxExec exec)
        => exec.Argv.Count >= 3 && exec.Argv[0] == "getent" && exec.Argv[1] == "ahosts";

    public static bool IsShellScript(SandboxExec exec)
        => exec.Argv.SequenceEqual(["sh", "-s"]);

    public static bool IsReadinessProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "curl"
            && exec.Argv.Any(arg => arg.Contains("/healthz", StringComparison.Ordinal));

    public static async Task<SandboxExecResult> RunShellScriptAsync(string? script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start sh");
        if (!string.IsNullOrEmpty(script))
            await process.StandardInput.WriteAsync(script.AsMemory(), ct);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new SandboxExecResult(process.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// Graphical sandbox that models the demo login fixture for cheap-model CUA
/// exploration. Returns real accessibility trees and accepts computer-use
/// input without a live VM.
/// </summary>
public sealed class DemoLoginCuaSandbox : ISandbox
{
    private readonly DemoLoginPageModel _page = new();
    private string? _focusedField;
    public const string BaseUrl = "http://app.local";

    public string Id { get; } = "demo-login-cua-" + Guid.NewGuid().ToString("N")[..8];

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (DemoLoginSandboxExec.IsDnsLookup(exec))
            return Task.FromResult(new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty));

        if (DemoLoginSandboxExec.IsShellScript(exec))
            return DemoLoginSandboxExec.RunShellScriptAsync(exec.Stdin, ct);

        if (DemoLoginSandboxExec.IsReadinessProbe(exec))
            return Task.FromResult(new SandboxExecResult(0, ReadHealthzFixture(), string.Empty));

        return Task.FromResult(new SandboxExecResult(127, string.Empty, $"unsupported exec: {string.Join(' ', exec.Argv)}"));
    }

    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
        => Task.FromResult(Encoding.UTF8.GetBytes("demo-login-screenshot"));

    public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
    {
        var descriptor = _page.DescribePoint(x, y);
        if (descriptor is null)
            return Task.FromResult<SandboxAccessibilitySnapshot?>(null);

        return Task.FromResult<SandboxAccessibilitySnapshot?>(new SandboxAccessibilitySnapshot
        {
            Role = descriptor.Role,
            Name = descriptor.Name,
            Text = descriptor.Text,
            ElementType = descriptor.ElementType,
        });
    }

    public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(_page.AccessibilityTreeJson());

    public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
    {
        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case SandboxInputEventType.Click:
                    HandleClick(evt.X ?? 0, evt.Y ?? 0);
                    break;
                case SandboxInputEventType.Type:
                    ApplyText(evt.Text ?? string.Empty);
                    break;
                case SandboxInputEventType.Key:
                    if (string.Equals(evt.Key, "Enter", StringComparison.OrdinalIgnoreCase))
                        _page.TryLogin();
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private void HandleClick(int x, int y)
    {
        var descriptor = _page.DescribePoint(x, y);
        if (descriptor?.ElementType?.StartsWith("css:", StringComparison.Ordinal) == true)
        {
            var selector = descriptor.ElementType["css:".Length..];
            _focusedField = selector;
            if (selector is "#login-btn")
                _page.TryLogin();
        }
    }

    private void ApplyText(string text)
    {
        switch (_focusedField)
        {
            case "#email":
                _page.Email = text;
                break;
            case "#password":
                _page.Password = text;
                break;
        }
    }

    public ValueTask DisposeAsync() => default;

    private static string ReadHealthzFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-login-app", "healthz");
        return File.Exists(path) ? File.ReadAllText(path) : "ok\n";
    }
}

/// <summary>
/// Replay sandbox that runs the real <see cref="CodeyBox.Orchestrator.E2eReplayRuntime"/>
/// embedded driver against a Playwright stub backed by <see cref="DemoLoginPageModel"/>.
/// Firewall install scripts are executed for real (not silently short-circuited).
/// </summary>
public sealed class DemoLoginReplaySandbox : ISandbox
{
    private readonly string _root;
    private readonly DemoLoginPageModel _page = new();
    private readonly List<SandboxExec> _firewallExecs = [];

    public DemoLoginReplaySandbox()
    {
        _root = Path.Combine(Path.GetTempPath(), $"codeybox-demo-login-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "playwright"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "playwright", "index.js"), BuildPlaywrightStub());
        File.WriteAllText(Path.Combine(_root, "dns-hook.js"), DnsHook);
    }

    public string Id { get; } = "demo-login-replay-" + Guid.NewGuid().ToString("N")[..8];

    public IReadOnlyList<SandboxExec> FirewallExecs => _firewallExecs;

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (DemoLoginSandboxExec.IsDnsLookup(exec) && exec.Argv[2] == "app.local")
            return new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty);

        if (DemoLoginSandboxExec.IsShellScript(exec))
        {
            if (exec.Stdin?.Contains("iptables", StringComparison.Ordinal) == true)
                _firewallExecs.Add(exec);

            return await DemoLoginSandboxExec.RunShellScriptAsync(exec.Stdin, ct);
        }

        if (DemoLoginSandboxExec.IsReadinessProbe(exec))
            return new SandboxExecResult(0, ReadHealthzFixture(), string.Empty);

        var argv = StripReplayDriverWrapper(exec.Argv);
        var psi = new ProcessStartInfo(argv[0])
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in argv.Skip(1))
            psi.ArgumentList.Add(arg);
        psi.Environment["NODE_PATH"] = Path.Combine(_root, "node_modules");
        psi.Environment["NODE_OPTIONS"] = $"--require {Path.Combine(_root, "dns-hook.js")}";
        psi.Environment["DEMO_LOGIN_HTML"] = ResolveHtmlFixturePath();
        psi.Environment["DEMO_LOGIN_STATE_JSON"] = System.Text.Json.JsonSerializer.Serialize(new
        {
            email = _page.Email,
            password = _page.Password,
            loggedIn = _page.LoggedIn,
        });

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start node");
        if (exec.Stdin is not null)
            await process.StandardInput.WriteAsync(exec.Stdin.AsMemory(), ct);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        SyncPageStateFromDriver(stdout);

        return new SandboxExecResult(process.ExitCode, stdout, stderr);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return default;
    }

    private void SyncPageStateFromDriver(string stdout)
    {
        if (!stdout.Contains("\"loggedIn\":true", StringComparison.Ordinal))
            return;

        _page.Email = "alice@example.com";
        _page.Password = DemoLoginPageModel.ResolvePassword("secret");
        _page.TryLogin();
    }

    private static string ResolveHtmlFixturePath()
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-login-app", "index.html");

    private static string ReadHealthzFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-login-app", "healthz");
        return File.Exists(path) ? File.ReadAllText(path) : "ok\n";
    }

    private static IReadOnlyList<string> StripReplayDriverWrapper(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || argv[0] != "sudo")
            return argv;
        var nodeIndex = argv.ToList().IndexOf("node");
        return nodeIndex >= 0 ? argv.Skip(nodeIndex).ToArray() : argv;
    }

    private static string BuildPlaywrightStub()
    {
        return """
            const fs = require('fs');
            let pageState = { email: '', password: '', loggedIn: false };

            try {
              const seeded = process.env.DEMO_LOGIN_STATE_JSON;
              if (seeded) pageState = JSON.parse(seeded);
            } catch {}

            const htmlFixture = process.env.DEMO_LOGIN_HTML;
            if (htmlFixture && fs.existsSync(htmlFixture)) {
              fs.readFileSync(htmlFixture, 'utf8');
            }

            const knownSelectors = new Set([
              '#welcome', '[data-testid="welcome-banner"]', '#hidden',
              '#email', '#password', '#login-btn', '[data-testid="login-form"]', '#ready'
            ]);

            function resolvePassword(value) {
              if (value === '<redacted-password>') return 'secret';
              return value;
            }

            function isVisible(selector) {
              if (selector === '#welcome' || selector === '[data-testid="welcome-banner"]') return pageState.loggedIn;
              if (selector === '#hidden') return false;
              if (selector === '#email' || selector === '#password' || selector === '#login-btn') return !pageState.loggedIn;
              if (selector === '#ready') return true;
              return knownSelectors.has(selector) ? false : false;
            }

            function textContent(selector) {
              if (selector === '#welcome' || selector === '[data-testid="welcome-banner"]') return 'Welcome, alice@example.com';
              return '';
            }

            function makeLocator(selector) {
              return {
                first() { return this; },
                async isVisible() { return isVisible(selector); },
                async textContent() { return textContent(selector); },
                async click() {
                  if (selector === '#login-btn') {
                    const password = resolvePassword(pageState.password);
                    if (pageState.email.trim() === 'alice@example.com' && password === 'secret') {
                      pageState.loggedIn = true;
                      console.log(JSON.stringify({ loggedIn: true }));
                    }
                  }
                },
                async fill(value) {
                  if (selector === '#email') pageState.email = value;
                  if (selector === '#password') pageState.password = value;
                },
                async dblclick() {},
                async press() {},
                async selectOption() {},
                async check() {},
                async uncheck() {},
                async hover() {},
                async waitFor() {}
              };
            }

            function makePage() {
              return {
                currentUrl: 'http://app.local/',
                locator: makeLocator,
                async goto(url) { this.currentUrl = url; },
                url() { return pageState.loggedIn ? 'http://app.local/dashboard' : this.currentUrl; },
                async title() { return pageState.loggedIn ? 'Demo Dashboard' : 'Demo Login'; },
                async waitForTimeout() {}
              };
            }

            exports.chromium = {
              async launch() {
                return {
                  async newContext(options) {
                    if (!options || options.serviceWorkers !== 'block') throw new Error('service workers not blocked');
                    return {
                      async route(_pattern, handler) {},
                      async routeWebSocket(_pattern, handler) {},
                      async newPage() { return makePage(); }
                    };
                  },
                  async close() {}
                };
              }
            };
            """;
    }

    private const string DnsHook =
        """
        const dns = require('dns');
        const originalLookup = dns.promises.lookup.bind(dns.promises);
        dns.promises.lookup = async function(host, options) {
          if (host === 'app.local') {
            if (options && options.all) return [{ address: '127.0.0.1', family: 4 }];
            return { address: '127.0.0.1', family: 4 };
          }
          return originalLookup(host, options);
        };
        """;
}

public static class DemoLoginExploration
{
    public const string WorkItemId = "94e3d549f2ed4f76987f6ce882a9f745";
    public const string TestCaseId = "e2e-replay-demo-login-happy-path";

    public static E2eExplorationPlan Plan() => new()
    {
        TargetName = "demo-login",
        EntryUrl = DemoLoginCuaSandbox.BaseUrl + "/",
        Actions =
        [
            new E2eExplorationAction { Kind = "click", X = 120, Y = 116 },
            new E2eExplorationAction { Kind = "type", Text = "alice@example.com" },
            new E2eExplorationAction { Kind = "click", X = 120, Y = 176 },
            new E2eExplorationAction { Kind = "type", Text = "secret" },
            new E2eExplorationAction { Kind = "click", X = 90, Y = 228 },
        ],
        Assertions =
        [
            new E2eReplayAssertion
            {
                Kind = "selectorVisible",
                Selector = "#welcome",
                Description = "welcome banner visible after login",
            },
            new E2eReplayAssertion
            {
                Kind = "selectorTextContains",
                Selector = "#welcome",
                Value = "alice@example.com",
                Description = "welcome banner shows the signed-in email",
            },
            new E2eReplayAssertion
            {
                Kind = "titleContains",
                Value = "Dashboard",
                Description = "page title reflects post-login dashboard",
            },
        ],
        EmitOptions = new E2eReplayEmitOptions
        {
            Name = "demo-login-happy-path",
            Readiness = new E2eReadinessProbe
            {
                Url = DemoLoginCuaSandbox.BaseUrl + "/healthz",
                MaxAttempts = 10,
                DelayMs = 100,
            },
            StepDelayAfterMs = 50,
        },
    };

    public static AppUnderTestSession CreateSession(ISandbox sandbox)
    {
        var bridge = new ComputerUseBridge();
        return new AppUnderTestSession(
            sandbox,
            bridge,
            DemoLoginCuaSandbox.BaseUrl + "/",
            Encoding.UTF8.GetBytes("readiness"));
    }
}
