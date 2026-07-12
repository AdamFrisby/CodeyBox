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

    public static Task<SandboxExecResult> RunShellScriptAsync(string? script, CancellationToken ct)
        => RunShellScriptAsync(script, pathPrefix: null, ct);

    /// <summary>
    /// Runs a <c>sh -s</c> script through the real shell. <paramref name="pathPrefix"/> is
    /// prepended to <c>PATH</c> so privileged tools (iptables, sudo, …) resolve to recording
    /// shims — modeling the replay VM's exec surface deterministically without host root.
    /// </summary>
    public static async Task<SandboxExecResult> RunShellScriptAsync(string? script, string? pathPrefix, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrEmpty(pathPrefix))
        {
            var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = pathPrefix + Path.PathSeparator + existing;
        }

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
/// embedded driver against a Playwright stub backed by linkedom loading the committed
/// <c>Fixtures/demo-login-app/index.html</c> fixture (including its inline script).
/// DOM queries and the post-login show/hide transition come from the real markup, so
/// replay-green cannot pass on fabricated state the fixture does not produce.
/// Firewall install scripts are executed for real (not silently short-circuited).
/// </summary>
public sealed class DemoLoginReplaySandbox : ISandbox
{
    private readonly string _root;
    private readonly string _shimBin;
    private readonly List<SandboxExec> _firewallExecs = [];

    public DemoLoginReplaySandbox()
    {
        _root = Path.Combine(Path.GetTempPath(), $"codeybox-demo-login-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "playwright"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "playwright", "index.js"), BuildPlaywrightStub());
        File.WriteAllText(Path.Combine(_root, "dns-hook.js"), DnsHook);
        _shimBin = Path.Combine(_root, "fw-shim");
        WriteFirewallShims(_shimBin);
        CopyLinkedomBundle();
    }

    public string Id { get; } = "demo-login-replay-" + Guid.NewGuid().ToString("N")[..8];

    public IReadOnlyList<SandboxExec> FirewallExecs => _firewallExecs;

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (DemoLoginSandboxExec.IsDnsLookup(exec) && exec.Argv[2] == "app.local")
            return new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty);

        if (DemoLoginSandboxExec.IsShellScript(exec))
        {
            // The replay runtime installs/removes an egress firewall via `sh -s`. Model the
            // replay VM by executing that real script with iptables/sudo/id shimmed on PATH,
            // so its control flow runs to completion deterministically without host root.
            if (exec.Stdin?.Contains("iptables", StringComparison.Ordinal) == true)
            {
                _firewallExecs.Add(exec);
                return await DemoLoginSandboxExec.RunShellScriptAsync(exec.Stdin, _shimBin, ct);
            }

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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start node");
        if (exec.Stdin is not null)
            await process.StandardInput.WriteAsync(exec.Stdin.AsMemory(), ct);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

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

    /// <summary>
    /// Writes recording shims for the privileged tools the egress-firewall script requires.
    /// Each shim models the replay VM's tool surface: <c>iptables</c>/<c>ip6tables</c> accept
    /// rule additions (returning non-zero for <c>-D</c> deletes so cleanup while-loops
    /// terminate), <c>sudo</c> drops its options and execs the wrapped command, <c>id</c>
    /// resolves the synthetic run-user to a fixed uid/gid, and the rest are no-ops. The
    /// script's real branching thus runs to a clean exit without host root or netfilter.
    /// </summary>
    private static void WriteFirewallShims(string shimBin)
    {
        Directory.CreateDirectory(shimBin);
        WriteShim(shimBin, "iptables", IptablesShim);
        WriteShim(shimBin, "ip6tables", IptablesShim);
        WriteShim(shimBin, "sudo", SudoShim);
        WriteShim(shimBin, "id", IdShim);
        WriteShim(shimBin, "chown", NoopShim);
        WriteShim(shimBin, "useradd", NoopShim);
        WriteShim(shimBin, "adduser", NoopShim);
        WriteShim(shimBin, "userdel", NoopShim);
    }

    private static void WriteShim(string shimBin, string name, string body)
    {
        var path = Path.Combine(shimBin, name);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    // Non-zero for `-D` (delete) so `while run ... -D ...; do :; done` cleanup loops break;
    // exit 0 for chain create/append/insert so the install path records rules and succeeds.
    private const string IptablesShim =
        "#!/bin/sh\nfor a in \"$@\"; do [ \"$a\" = \"-D\" ] && exit 1; done\nexit 0\n";

    // Drop sudo's own options (-n, -H, -u user, …) and exec the wrapped command via PATH.
    private const string SudoShim =
        "#!/bin/sh\nwhile [ $# -gt 0 ]; do case \"$1\" in -u) shift 2 ;; --) shift; break ;; -*) shift ;; *) break ;; esac; done\nexec \"$@\"\n";

    // `id -u` (current user, one arg) delegates to the real id for the root check; a two-arg
    // form (`id -u USER` / `id -g USER`) resolves the synthetic run-user to a stable id.
    private const string IdShim =
        "#!/bin/sh\nif [ $# -le 1 ]; then exec /usr/bin/id \"$@\"; fi\necho 483\n";

    private const string NoopShim = "#!/bin/sh\nexit 0\n";

    private void CopyLinkedomBundle()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vendor", "linkedom-bundle.cjs");
        if (!File.Exists(source))
            throw new FileNotFoundException("linkedom bundle missing from test output; rebuild CodeyBox.Tests.", source);

        File.Copy(source, Path.Combine(_root, "linkedom-bundle.cjs"), overwrite: true);
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
            const path = require('path');
            const { parseHTML } = require(path.join(__dirname, '..', '..', 'linkedom-bundle.cjs'));

            function loadFixtureDom() {
              const htmlFixture = process.env.DEMO_LOGIN_HTML;
              if (!htmlFixture || !fs.existsSync(htmlFixture)) {
                throw new Error('demo login HTML fixture not found: ' + htmlFixture);
              }
              const html = fs.readFileSync(htmlFixture, 'utf8');
              const { document, window } = parseHTML(html);
              const inline = html.match(/<script>([\s\S]*?)<\/script>/i);
              if (inline) {
                // Run the fixture's own submit handler so show/hide transitions
                // match the committed markup.
                new Function('document', 'window', inline[1])(document, window);
              }
              return { document, window };
            }

            const { document, window } = loadFixtureDom();

            function isVisible(selector) {
              const el = document.querySelector(selector);
              if (!el) return false;
              return !el.hidden;
            }

            function textContent(selector) {
              const el = document.querySelector(selector);
              return el ? (el.textContent || '').trim() : '';
            }

            function makeLocator(selector) {
              return {
                first() { return this; },
                async isVisible() { return isVisible(selector); },
                async textContent() { return textContent(selector); },
                async click() {
                  const el = document.querySelector(selector);
                  if (!el) return;
                  if (selector === '#login-btn' || el.id === 'login-btn') {
                    const form = document.getElementById('login-form');
                    if (form) {
                      form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
                    }
                  } else {
                    el.click();
                  }
                },
                async fill(value) {
                  const el = document.querySelector(selector);
                  if (el) el.value = value;
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
                url() { return this.currentUrl; },
                async title() { return document.title; },
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
                Kind = "selectorHidden",
                Selector = "#login-form",
                Description = "login form hidden after successful login",
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
