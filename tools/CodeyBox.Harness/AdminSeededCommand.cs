using System.Diagnostics;
using System.Globalization;
using CodeyBox.AdminSeed;

namespace CodeyBox.Harness;

/// <summary>
/// <c>admin-seeded</c> subcommand: owns the seeded, self-contained CodeyBox
/// admin instance (orchestrator API + Blazor Admin.Web on a throwaway SQLite
/// database with fake agents). <c>seed</c> writes the deterministic database;
/// <c>serve</c> starts both servers in the foreground for the E2E recipe's
/// run step, manual demos, and exploratory runs.
/// </summary>
public static class AdminSeededCommand
{
    public const int DefaultSeed = 42;
    public const string DefaultDbFileName = "admin-seed.db";
    public const string DefaultApiUrl = "http://localhost:5050";
    public const string DefaultAdminUrl = "http://localhost:5070";

    public enum ParseStatus { Ok, Usage, Invalid }

    public sealed record Parsed(
        ParseStatus Status,
        string Verb,
        int Seed,
        string Db,
        string ApiUrl,
        string AdminUrl,
        string RepoRoot,
        bool FreezeQueue,
        TimeSpan ReadyTimeout,
        string? Error);

    public static Parsed Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
            return Usage("admin-seeded seed|serve [options]");

        var verb = args[0].ToLowerInvariant();
        if (verb is not ("seed" or "serve"))
            return Usage($"Unknown admin-seeded verb '{args[0]}'. Expected 'seed' or 'serve'.");

        var seed = DefaultSeed;
        string? db = null;
        var apiUrl = DefaultApiUrl;
        var adminUrl = DefaultAdminUrl;
        string? repoRoot = null;
        var freezeQueue = true;
        var readyTimeout = TimeSpan.FromMinutes(2);

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed) || seed < 0)
                        return Usage("--seed must be a non-negative integer.");
                    break;
                case "--db" when i + 1 < args.Length:
                    db = args[++i];
                    break;
                case "--api-url" when i + 1 < args.Length:
                    apiUrl = args[++i];
                    break;
                case "--admin-url" when i + 1 < args.Length:
                    adminUrl = args[++i];
                    break;
                case "--repo-root" when i + 1 < args.Length:
                    repoRoot = args[++i];
                    break;
                case "--live":
                    freezeQueue = false;
                    break;
                case "--ready-timeout-sec" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) || secs <= 0 || secs > 1800)
                        return Usage("--ready-timeout-sec must be 1..1800.");
                    readyTimeout = TimeSpan.FromSeconds(secs);
                    break;
                case "-h":
                case "--help":
                    return Usage("admin-seeded seed|serve [options]");
                default:
                    return Usage($"Unknown option: {args[i]}");
            }
        }

        if (verb == "serve")
        {
            if (!TryValidateLoopbackUrl(apiUrl, out var apiErr))
                return Invalid($"--api-url invalid: {apiErr}");
            if (!TryValidateLoopbackUrl(adminUrl, out var adminErr))
                return Invalid($"--admin-url invalid: {adminErr}");
            repoRoot = string.IsNullOrWhiteSpace(repoRoot)
                ? Directory.GetCurrentDirectory()
                : repoRoot;
            if (repoRoot.IndexOf('\0') >= 0 || !Directory.Exists(repoRoot))
                return Invalid($"--repo-root does not exist: {repoRoot}");
        }

        db = string.IsNullOrWhiteSpace(db)
            ? Path.Combine(Path.GetTempPath(), "codeybox-admin-seed", DefaultDbFileName)
            : db;
        if (db.IndexOf('\0') >= 0)
            return Invalid("--db path contains NUL.");

        return new Parsed(ParseStatus.Ok, verb, seed, db, apiUrl, adminUrl, repoRoot ?? "", freezeQueue, readyTimeout, null);

        static Parsed Usage(string? error) =>
            new(ParseStatus.Usage, "", DefaultSeed, "", DefaultApiUrl, DefaultAdminUrl, "", true, TimeSpan.Zero, error);
        static Parsed Invalid(string error) =>
            new(ParseStatus.Invalid, "", DefaultSeed, "", DefaultApiUrl, DefaultAdminUrl, "", true, TimeSpan.Zero, error);
    }

    internal static bool TryValidateLoopbackUrl(string value, out string error)
    {
        error = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = $"'{value}' is not an absolute URL.";
            return false;
        }
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            error = "only http:// loopback listeners are supported for the seeded instance.";
            return false;
        }
        if (uri.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            error = "host must be localhost, 127.0.0.1, or ::1.";
            return false;
        }
        return true;
    }

    public static async Task<int> RunAsync(Parsed parsed, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        return parsed.Verb switch
        {
            "seed" => await RunSeedAsync(parsed, output, error, ct).ConfigureAwait(false),
            "serve" => await RunServeAsync(parsed, output, error, ct).ConfigureAwait(false),
            _ => 2,
        };
    }

    private static async Task<int> RunSeedAsync(Parsed parsed, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(parsed.Db)) ?? Path.GetTempPath();
            var seeder = new AdminSeeder();
            var summary = await seeder.SeedAsync(
                parsed.Db, root,
                new AdminSeedSpec { Seed = parsed.Seed, Now = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
            output.WriteLine(
                $"Seeded admin instance: seed={summary.Seed} db={summary.DbPath} " +
                $"work-items={summary.WorkItemCount} audits={summary.AuditReportCount} " +
                $"releases={summary.ReleaseCount} suggestions={summary.SuggestionCount}");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Seed failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunServeAsync(Parsed parsed, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var logDir = Environment.GetEnvironmentVariable("CODEYBOX_SEED_LOG_DIR");
        if (string.IsNullOrWhiteSpace(logDir))
            logDir = Path.Combine(Path.GetTempPath(), "codeybox-admin-seed", "logs");
        Directory.CreateDirectory(logDir);

        var seedText = parsed.Seed.ToString(CultureInfo.InvariantCulture);
        var apiEnv = SeededInstanceEnv(
            parsed.Db, seedText,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ASPNETCORE_URLS"] = parsed.ApiUrl,
            });
        var adminEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = parsed.AdminUrl,
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_LAUNCH_PROFILE"] = "",
            ["CodeyBoxAdmin__ApiBaseUrl"] = parsed.ApiUrl,
        };

        var apiLog = Path.Combine(logDir, "codeybox-api.log");
        var adminLog = Path.Combine(logDir, "codeybox-admin-web.log");
        using var api = StartChild("codeybox-api", "src/CodeyBox.Api", parsed.RepoRoot, apiEnv, apiLog);
        using var admin = StartChild(
            "codeybox-admin-web", Path.Combine("tools", "CodeyBox.Admin", "src", "CodeyBox.Admin.Web"),
            parsed.RepoRoot, adminEnv, adminLog);

        try
        {
            using var timeout = new CancellationTokenSource(parsed.ReadyTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await WaitForReadyAsync(parsed.ApiUrl, parsed.AdminUrl, output, linked.Token).ConfigureAwait(false);

            if (parsed.FreezeQueue)
                await FreezeQueueAsync(parsed.ApiUrl, output).ConfigureAwait(false);

            output.WriteLine($"Seeded admin ready: api={parsed.ApiUrl} admin={parsed.AdminUrl} (seed {parsed.Seed})");
            output.WriteLine("Press Ctrl+C to stop.");
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error.WriteLine($"Serve timed out waiting for readiness within {parsed.ReadyTimeout}.");
            return 1;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Serve failed: {ex.Message}");
            return 1;
        }
        finally
        {
            StopChild(admin);
            StopChild(api);
        }
    }

    internal static IReadOnlyDictionary<string, string> SeededInstanceEnv(
        string dbPath, string seedText, IDictionary<string, string> extra)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_LAUNCH_PROFILE"] = "",
            ["CodeyBox__StateDatabasePath"] = dbPath,
            ["CodeyBox__SeededFakeAgents__Enabled"] = "true",
            ["CodeyBox__SeededFakeAgents__Seed"] = seedText,
            ["CodeyBox__DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox__GitRootDirectory"] = Path.Combine(Path.GetTempPath(), "codeybox-admin-seed", "repos"),
            ["CodeyBox__Projects__0__Id"] = "seeded-shop",
            ["CodeyBox__Projects__0__DisplayName"] = "Seeded Shop",
            ["CodeyBox__Projects__0__BaseBranch"] = "main",
            ["CodeyBox__Projects__0__DefaultAgentClass"] = "seeded",
            ["CodeyBox__Projects__1__Id"] = "seeded-portal",
            ["CodeyBox__Projects__1__DisplayName"] = "Seeded Portal",
            ["CodeyBox__Projects__1__BaseBranch"] = "main",
            ["CodeyBox__Projects__1__DefaultAgentClass"] = "seeded",
            ["CodeyBox__AgentClasses__0__Id"] = "seeded",
            ["CodeyBox__AgentClasses__0__DisplayName"] = "Seeded fake agents",
            ["CodeyBox__AgentClasses__0__Members__0__Agent"] = "seeded-fake",
            ["CodeyBox__AgentClasses__0__Members__0__Billing"] = "Subscription",
            ["CodeyBox__AgentClasses__0__Members__0__QualityScore"] = "50",
        };
        foreach (var kv in extra)
            env[kv.Key] = kv.Value;
        return env;
    }

    private static ChildProcess StartChild(
        string name, string projectRelPath, string repoRoot,
        IReadOnlyDictionary<string, string> env, string logPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(projectRelPath);
        foreach (var kv in env)
            start.Environment[kv.Key] = kv.Value;

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {name}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ChildProcess(name, process, log, logPath);
    }

    private static void StopChild(ChildProcess child)
    {
        try
        {
            if (!child.Process.HasExited)
            {
                child.Process.Kill(entireProcessTree: true);
                child.Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill; nothing to do.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Process is exiting; teardown is best-effort.
        }
        finally
        {
            child.Dispose();
        }
    }

    private static async Task WaitForReadyAsync(string apiUrl, string adminUrl, TextWriter output, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            var apiOk = await ProbeAsync(http, $"{apiUrl.TrimEnd('/')}/healthz", ct).ConfigureAwait(false);
            var adminOk = await ProbeAsync(http, adminUrl, ct).ConfigureAwait(false);
            if (apiOk && adminOk)
                return;
            if (attempt % 6 == 0)
                output.WriteLine($"Waiting for seeded instance (attempt {attempt}): api={apiOk} admin={adminOk} …");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ProbeAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task FreezeQueueAsync(string apiUrl, TextWriter output)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var content = new StringContent(
                """{"reason":"seeded E2E freeze: keep seeded states deterministic"}""",
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{apiUrl.TrimEnd('/')}/queue/pause", content).ConfigureAwait(false);
            output.WriteLine(resp.IsSuccessStatusCode
                ? "Queue frozen for deterministic E2E."
                : $"Queue freeze returned {(int)resp.StatusCode}; continuing unfrozen.");
        }
        catch (HttpRequestException ex)
        {
            output.WriteLine($"Queue freeze failed ({ex.Message}); continuing unfrozen.");
        }
        catch (TaskCanceledException)
        {
            output.WriteLine("Queue freeze timed out; continuing unfrozen.");
        }
    }

    private sealed class ChildProcess : IDisposable
    {
        public ChildProcess(string name, Process process, StreamWriter log, string logPath)
        {
            Name = name;
            Process = process;
            Log = log;
            LogPath = logPath;
        }

        public string Name { get; }
        public Process Process { get; }
        public StreamWriter Log { get; }
        public string LogPath { get; }

        public void Dispose()
        {
            try { Log.Dispose(); } catch (ObjectDisposedException) { }
            Process.Dispose();
        }
    }

    public static void PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: codeybox-harness admin-seeded seed|serve [options]");
        error.WriteLine();
        error.WriteLine("Verbs:");
        error.WriteLine("  seed    Write the deterministic seeded SQLite database (no servers).");
        error.WriteLine("  serve   Start the orchestrator API + Admin.Web on the seeded database.");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  --seed <n>              Determinism seed (default: 42).");
        error.WriteLine("  --db <path>             SQLite path (default: temp/codeybox-admin-seed/admin-seed.db).");
        error.WriteLine("  --api-url <url>         API listener, loopback http only (serve; default: http://localhost:5050).");
        error.WriteLine("  --admin-url <url>       Admin.Web listener, loopback http only (serve; default: http://localhost:5070).");
        error.WriteLine("  --repo-root <path>      Repository checkout to run from (serve; default: cwd).");
        error.WriteLine("  --live                  Do not freeze the queue on boot (serve freezes by default).");
        error.WriteLine("  --ready-timeout-sec <n> Readiness budget 1..1800 (serve; default: 120).");
    }
}
