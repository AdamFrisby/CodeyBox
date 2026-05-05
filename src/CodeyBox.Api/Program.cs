using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Gemini;
using CodeyBox.Api;
using CodeyBox.Api.Hubs;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Formatting.Compact;
// Disambiguate: both Serilog and MEL expose an ILogger interface.
// Program.cs only uses Serilog.Log (the static class) and MEL's ILogger<T>;
// declaring the alias keeps local function signatures unambiguous.
using ILogger = Microsoft.Extensions.Logging.ILogger;

var builder = WebApplication.CreateBuilder(args);

// Optional extra-config file pointed at by CODEYBOX_EXTRA_CONFIG. Lets
// operator-side configuration (e.g. dev/test setups in a gitignored
// local/ directory) layer on top of the committed appsettings.json
// without copying files into the API project. Loaded LAST so it wins.
{
    var extra = Environment.GetEnvironmentVariable("CODEYBOX_EXTRA_CONFIG");
    if (!string.IsNullOrEmpty(extra))
        builder.Configuration.AddJsonFile(extra, optional: false, reloadOnChange: false);
}

// Default to loopback-only. Operators putting a TLS-terminating reverse
// proxy in front should override via ASPNETCORE_URLS or appsettings.
// Anything beyond localhost MUST be intentional, since the API can spawn
// sandboxes and trigger merges.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && builder.Configuration["urls"] is null
    && builder.Configuration["Kestrel:Endpoints:Default:Url"] is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:5000");
}

// ── Serilog bootstrap ─────────────────────────────────────────────────────
// Configured from CodeyBox:AuditLog section before the host builds so the
// logger is available from startup. UseSerilog() replaces the default MEL
// providers, so all ILogger<T> call sites continue to work unchanged.
{
    var cbConf = builder.Configuration.GetSection("CodeyBox").Get<CodeyBoxOptions>()
        ?? new CodeyBoxOptions();
    var auditOpts = cbConf.AuditLog;

    if (auditOpts.RetainedDays < 1)
        throw new InvalidOperationException("CodeyBox:AuditLog:RetainedDays must be >= 1");
    if (string.IsNullOrWhiteSpace(auditOpts.Path))
        throw new InvalidOperationException("CodeyBox:AuditLog:Path must be non-empty");
    if (string.IsNullOrWhiteSpace(auditOpts.AuditPath))
        throw new InvalidOperationException("CodeyBox:AuditLog:AuditPath must be non-empty");

    // Ensure log directories exist and are writable before handing control
    // to Serilog, so misconfigured paths surface at startup rather than
    // silently dropping events.
    foreach (var logPath in new[] { auditOpts.Path, auditOpts.AuditPath })
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (string.IsNullOrEmpty(dir)) continue;
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Audit log directory '{dir}' (from path '{logPath}') is not writable: {ex.Message}", ex);
        }
    }

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "CodeyBox")
        .Enrich.With<SensitiveDataRedactionEnricher>()
        .WriteTo.Console()
        .WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: auditOpts.Path,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: auditOpts.RetainedDays,
            fileSizeLimitBytes: auditOpts.MaxFileSizeBytes,
            rollOnFileSizeLimit: true,
            shared: false)
        .WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(Matching.WithProperty<bool>("Audit", v => v))
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: auditOpts.AuditPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: auditOpts.RetainedDays,
                fileSizeLimitBytes: auditOpts.MaxFileSizeBytes,
                rollOnFileSizeLimit: true,
                shared: false))
        .CreateLogger();

    builder.Host.UseSerilog();
}

// ── OpenTelemetry ─────────────────────────────────────────────────────────
// Off by default (OtelOptions.Enabled = false). Operators opt in by setting
// CodeyBox:Otel:Enabled=true and CodeyBox:Otel:OtlpEndpoint. When disabled,
// no OTel types are registered — zero overhead in the default configuration.
{
    var cbConf = builder.Configuration.GetSection("CodeyBox").Get<CodeyBoxOptions>()
        ?? new CodeyBoxOptions();
    var otelOpts = cbConf.Otel;
    OtelOptions.Validate(otelOpts);

    if (otelOpts.Enabled)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(otelOpts.ServiceName, serviceVersion: otelOpts.ServiceVersion)
                .AddAttributes(otelOpts.ResourceAttributes.Select(
                    kv => new KeyValuePair<string, object>(kv.Key, kv.Value))))
            .WithTracing(t => t
                .AddSource("CodeyBox.Pipeline")
                .AddSource("CodeyBox.Sandbox")
                .AddSource("CodeyBox.Upstream")
                .AddSource("CodeyBox.Audit")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, otelOpts)))
            .WithMetrics(m => m
                .AddMeter("CodeyBox.Pipeline")
                .AddMeter("CodeyBox.Sandbox")
                .AddMeter("CodeyBox.Audit")
                .AddMeter("CodeyBox.Upstream")
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, otelOpts)));
    }
}

builder.Services.Configure<CodeyBoxOptions>(builder.Configuration.GetSection("CodeyBox"));
builder.Services.Configure<ProjectsOptions>(builder.Configuration.GetSection("CodeyBox"));

ApiKeyAuth.Configure(builder);

// --- Sandbox provider --------------------------------------------------------
// Selected by CodeyBox:SandboxProvider in config. Each option has a different
// security/setup trade-off — see docs/sandbox-providers.md.
//
//   process     — UNSAFE. No isolation. Dev only; refuses to load outside
//                 Development env unless DangerouslyAllowProcessSandbox=true.
//   bubblewrap  — Namespace + seccomp isolation, no daemon. Single package
//                 install. Shares the host kernel.
//   multipass   — Real Ubuntu VMs via Canonical's snap. Separate guest
//                 kernel. Single 'snap install multipass' on Ubuntu, no
//                 podman / OCI runtime / /etc edits. ~10-30s VM launch.
builder.Services.AddSingleton<ISandboxProvider>(SelectSandboxProvider);

static void ConfigureOtlp(OtlpExporterOptions o, OtelOptions opts)
{
    o.Endpoint = new Uri(opts.OtlpEndpoint!);
    o.Protocol = opts.ExportProtocol == "httpprotobuf"
        ? OtlpExportProtocol.HttpProtobuf
        : OtlpExportProtocol.Grpc;
    if (!string.IsNullOrEmpty(opts.OtlpHeaders))
        o.Headers = opts.OtlpHeaders;
}

static ISandboxProvider SelectSandboxProvider(IServiceProvider sp)
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var startupLog = loggerFactory.CreateLogger("CodeyBox.Sandbox");

    var kind = (opts.SandboxProvider ?? "").Trim().ToLowerInvariant();
    var environment = sp.GetRequiredService<IHostEnvironment>();

    if (string.IsNullOrEmpty(kind))
    {
        if (environment.IsDevelopment())
        {
            startupLog.LogWarning(
                "CodeyBox:SandboxProvider not set; defaulting to 'process' because environment is Development. " +
                "DO NOT do this in production.");
            kind = "process";
        }
        else
        {
            throw new InvalidOperationException(
                "CodeyBox:SandboxProvider must be set in non-Development environments. " +
                "Choose one of: multipass, bubblewrap, process " +
                "(see docs/sandbox-providers.md for trade-offs).");
        }
    }

    return kind switch
    {
        "process" => BuildProcess(opts, environment, startupLog, loggerFactory),
        "bubblewrap" => new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions(),
            loggerFactory.CreateLogger<BubblewrapSandboxProvider>(),
            sp.GetService<ITimingStore>()),
        "multipass" => new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                ExtraCloudInit = opts.MultipassExtraCloudInit,
                ExtraRuncmd = opts.MultipassExtraRuncmd,
                NetworkProfiles = opts.SandboxNetworkProfiles,
                UseBaselineImages = opts.MultipassUseBaselineImages,
            },
            loggerFactory.CreateLogger<MultipassSandboxProvider>(),
            sp.GetService<ITimingStore>()),
        _ => throw new InvalidOperationException(
            $"Unknown CodeyBox:SandboxProvider '{kind}'. Valid: multipass, bubblewrap, process"),
    };
}

static IReadOnlyList<AgentClass> BuildAndValidateAgentClasses(
    List<AgentClassOptions> options, ILogger log)
{
    var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var result = new List<AgentClass>();

    foreach (var classOpts in options)
    {
        if (string.IsNullOrWhiteSpace(classOpts.Id))
            throw new InvalidOperationException("Each AgentClass must have a non-empty Id");
        if (!seenIds.Add(classOpts.Id))
            throw new InvalidOperationException($"AgentClass Id '{classOpts.Id}' is not unique");
        if (classOpts.Members.Count == 0)
            throw new InvalidOperationException($"AgentClass '{classOpts.Id}' must have at least one member");

        var members = new List<AgentMembership>();
        foreach (var m in classOpts.Members)
        {
            if (string.IsNullOrWhiteSpace(m.Agent))
                throw new InvalidOperationException($"AgentClass '{classOpts.Id}': member Agent must be non-empty");
            if (!Enum.TryParse<AgentBilling>(m.Billing, ignoreCase: true, out var billing))
                throw new InvalidOperationException(
                    $"AgentClass '{classOpts.Id}': unknown Billing '{m.Billing}'. Expected Subscription or PayPerApi");
            if (m.QualityScore is null)
                throw new InvalidOperationException(
                    $"AgentClass '{classOpts.Id}': member '{m.Agent}' is missing QualityScore. " +
                    $"Add QualityScore=N (0–200); see docs/agent-classes.md for recommended values.");
            var score = m.QualityScore.Value;
            if (score < 0 || score > 200)
                throw new InvalidOperationException(
                    $"AgentClass '{classOpts.Id}': member '{m.Agent}' has QualityScore={score} which is outside the valid range 0–200.");
            // Gemini at frontier-adjacent tier REQUIRES ReasoningMode="high" — running
            // standard-reasoning Gemini in a >=90 slot misrepresents its capability.
            var agentKind = new AgentKind(m.Agent);
            if (agentKind == AgentKind.Gemini && score >= 90 &&
                !string.Equals(m.ReasoningMode, "high", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"AgentClass '{classOpts.Id}': Gemini member with QualityScore={score} (≥90) requires " +
                    $"ReasoningMode=\"high\". Either set ReasoningMode=\"high\" (requires @google/gemini-cli ≥0.1.9 " +
                    $"with --thinking support; install via MultipassExtraRuncmd) or lower QualityScore below 90.");
            members.Add(new AgentMembership
            {
                Agent = agentKind,
                Billing = billing,
                ModelId = m.ModelId,
                QualityScore = score,
                ReasoningMode = m.ReasoningMode,
            });
        }

        var hasOnlySubscription = members.All(m => m.Billing == AgentBilling.Subscription);
        if (hasOnlySubscription)
            log.LogWarning(
                "AgentClass '{ClassId}' has no PayPerApi fallback — items may wait indefinitely if all subscriptions are exhausted",
                classOpts.Id);

        result.Add(new AgentClass
        {
            Id = classOpts.Id,
            DisplayName = string.IsNullOrWhiteSpace(classOpts.DisplayName)
                ? classOpts.Id
                : classOpts.DisplayName,
            Members = members,
        });
    }

    return result;
}

static IReadOnlyList<ParsedTodModifier> BuildAndValidateTodModifiers(
    AgentScoreModifiersOptions opts, ILogger log)
{
    // Allowed day-code → DayOfWeek mapping (three-letter abbreviations).
    var dayMap = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday,
    };

    var result = new List<ParsedTodModifier>();
    foreach (var entry in opts.ByTimeOfDay)
    {
        if (string.IsNullOrWhiteSpace(entry.Agent))
            throw new InvalidOperationException("AgentScoreModifiers.ByTimeOfDay: entry is missing Agent value");

        if (Math.Abs(entry.Modifier) > 5)
            throw new InvalidOperationException(
                $"AgentScoreModifiers.ByTimeOfDay: modifier for '{entry.Agent}' is {entry.Modifier}; " +
                $"absolute value must be ≤ 5. Modifiers are tiebreakers, not eligibility gates. " +
                $"Use MinModelScore on the work item to gate by capability.");

        var parsedWindows = new List<ParsedTimeWindow>();
        foreach (var w in entry.Windows)
        {
            var days = new HashSet<DayOfWeek>();
            foreach (var d in w.Days)
            {
                if (!dayMap.TryGetValue(d, out var dow))
                    throw new InvalidOperationException(
                        $"AgentScoreModifiers.ByTimeOfDay: unknown day code '{d}' for agent '{entry.Agent}'. " +
                        $"Valid codes: Mon, Tue, Wed, Thu, Fri, Sat, Sun.");
                days.Add(dow);
            }
            if (days.Count == 0)
                throw new InvalidOperationException(
                    $"AgentScoreModifiers.ByTimeOfDay: window for agent '{entry.Agent}' has no days.");

            if (!TimeSpan.TryParseExact(w.StartUtc, @"hh\:mm", null, out var start))
                throw new InvalidOperationException(
                    $"AgentScoreModifiers.ByTimeOfDay: StartUtc '{w.StartUtc}' for agent '{entry.Agent}' is not a valid HH:mm time.");
            if (!TimeSpan.TryParseExact(w.EndUtc, @"hh\:mm", null, out var end))
                throw new InvalidOperationException(
                    $"AgentScoreModifiers.ByTimeOfDay: EndUtc '{w.EndUtc}' for agent '{entry.Agent}' is not a valid HH:mm time.");

            parsedWindows.Add(new ParsedTimeWindow(days, start, end));
        }

        result.Add(new ParsedTodModifier(new AgentKind(entry.Agent), entry.Modifier, parsedWindows));
    }

    // Log active windows so operators can audit the schedule at startup.
    if (result.Count > 0)
    {
        foreach (var mod in result)
        {
            var windowDescs = mod.Windows.Select(w =>
                $"[{string.Join(",", w.Days)} {w.Start:hh\\:mm}–{w.End:hh\\:mm} UTC]");
            log.LogInformation(
                "AgentScoreModifiers: agent={Agent} modifier={Modifier:+0;-0} windows={Windows}",
                mod.Agent.Value, mod.Modifier, string.Join(", ", windowDescs));
        }
    }

    return result;
}

static ISandboxProvider BuildProcess(CodeyBoxOptions opts, IHostEnvironment env, ILogger startupLog, ILoggerFactory loggerFactory)
{
    if (!env.IsDevelopment() && !opts.DangerouslyAllowProcessSandbox)
    {
        throw new InvalidOperationException(
            "CodeyBox:SandboxProvider=process is UNSAFE outside Development. " +
            "Set CodeyBox:DangerouslyAllowProcessSandbox=true to override (NOT recommended), " +
            "or pick multipass | bubblewrap.");
    }
    startupLog.LogWarning("Using Process sandbox provider — NO ISOLATION. Dev only.");
    return new ProcessSandboxProvider(loggerFactory.CreateLogger<ProcessSandboxProvider>());
}

// --- Git host ----------------------------------------------------------------
builder.Services.AddSingleton<LocalGitHost>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new LocalGitHost(
        new LocalGitHostOptions { RootDirectory = opts.GitRootDirectory },
        sp.GetRequiredService<ILogger<LocalGitHost>>());
});
builder.Services.AddSingleton<IGitHost>(sp => sp.GetRequiredService<LocalGitHost>());

// --- Pull request service (in-memory by default) -----------------------------
builder.Services.AddSingleton<IPullRequestService, InMemoryPullRequestService>();

// --- Agents ------------------------------------------------------------------
builder.Services.AddSingleton<IAgentRunner, ClaudeAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, CopilotAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, CodexAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, GeminiAgentRunner>();
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();

// Plugin discovery result captured before builder.Build() so the credential
// provider factory below can reference the list directly without any async
// blocking. Populated by AddCodeyBoxPlugins (called in the plugin-foundation
// section near the end of service registration).
IReadOnlyList<LoadedPlugin>? preDiscoveredPlugins = null;

// --- Credentials -------------------------------------------------------------
// Each agent's API key has a per-agent host env var that maps to the
// canonical sandbox env var the agent CLI reads. Operators add new agents
// by appending to this list (or registering a different ICredentialProvider).
//
// Chain order: BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV.
//
// 1. ClaudeOAuthFileCredentialProvider — reads Claude's token fresh from a
//    JSON file (default ~/.claude/.credentials.json, the path the local
//    `claude` CLI refreshes in-place) on every pickup, so a host-side token
//    rotation is picked up without an orchestrator restart.
// 2. Plugin ICredentialProvider implementations — inserted in discovery order
//    (between OAuth-file and env-var). Vault-issued short-lived credentials
//    are preferred over env-var fallbacks. Per-project ordering is expressed
//    via Project.CredentialProviderPriority; see docs/credential-plugins.md.
// 3. EnvironmentCredentialProvider — catch-all fallback reading host env vars.
//
// Operators with no credential plugins see zero behaviour change: the chain
// is identical to the pre-plugin OAuth-file → env-var behaviour.
//
// ChainedCredentialProvider is registered under three service types so that:
//   - ICredentialProvider resolves the global chain (smoke gates, startup validation).
//   - IProjectAwareCredentialProvider lets PipelineRunner apply per-project
//     CredentialProviderPriority at agent pickup time.
//   - ChainedCredentialProvider is directly resolvable for callers that need both.
builder.Services.AddSingleton<ChainedCredentialProvider>(sp =>
{
    var builtInFirst = new List<ICredentialProvider>();
    var namedPlugins = new List<(string Id, ICredentialProvider Provider)>();
    var builtInLast = new List<ICredentialProvider>();

    var oauthFile =
        Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_OAUTH_FILE")
        ?? builder.Configuration["CodeyBox:ClaudeOAuthFile"];

    if (!string.IsNullOrWhiteSpace(oauthFile))
    {
        // Expand a leading ~ to $HOME for ergonomic config like
        // "~/.claude/.credentials.json".
        if (oauthFile.StartsWith("~/", StringComparison.Ordinal))
            oauthFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                oauthFile[2..]);
        builtInFirst.Add(new ClaudeOAuthFileCredentialProvider(
            oauthFile,
            sandboxEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
            sp.GetService<ILogger<ClaudeOAuthFileCredentialProvider>>()));
    }

    // Enumerate plugin-registered ICredentialProvider types using the list captured
    // from AddCodeyBoxPlugins (called before builder.Build()). Each plugin type is
    // registered in DI under its concrete type by PluginLoader.RegisterPlugins;
    // we resolve by concrete type (not by ICredentialProvider) to avoid resolving
    // the ChainedCredentialProvider itself and causing infinite recursion.
    // Plugin IDs are stored alongside providers so per-project
    // CredentialProviderPriority can filter and reorder them at pickup time.
    var credentialProviderType = typeof(ICredentialProvider);
    foreach (var plugin in preDiscoveredPlugins ?? [])
    {
        foreach (var type in plugin.RegisteredTypes)
        {
            if (credentialProviderType.IsAssignableFrom(type))
                namedPlugins.Add((plugin.PluginId, (ICredentialProvider)sp.GetRequiredService(type)));
        }
    }

    builtInLast.Add(new EnvironmentCredentialProvider(new[]
    {
        // Claude Code accepts either ANTHROPIC_API_KEY (real API key, format
        // sk-ant-api03-…) or CLAUDE_CODE_OAUTH_TOKEN (OAuth access token,
        // format sk-ant-oat01-…). Default mapping is OAuth so subscription
        // users (Pro/Max/Team/Enterprise) can run without a separate API
        // key. Operators with a raw API key can change the in-sandbox name
        // to ANTHROPIC_API_KEY here.
        new AgentCredentialMapping(AgentKind.Claude, "CODEYBOX_CLAUDE_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"),
        new AgentCredentialMapping(AgentKind.Copilot, "CODEYBOX_COPILOT_TOKEN", "GH_TOKEN"),
        new AgentCredentialMapping(AgentKind.Codex, "CODEYBOX_CODEX_API_KEY", "OPENAI_API_KEY"),
        new AgentCredentialMapping(AgentKind.Gemini, "CODEYBOX_GEMINI_API_KEY", "GEMINI_API_KEY"),
    }));

    return new ChainedCredentialProvider(
        builtInFirst,
        namedPlugins,
        builtInLast,
        log: sp.GetService<ILogger<ChainedCredentialProvider>>());
});
builder.Services.AddSingleton<ICredentialProvider>(sp => sp.GetRequiredService<ChainedCredentialProvider>());
builder.Services.AddSingleton<IProjectAwareCredentialProvider>(sp => sp.GetRequiredService<ChainedCredentialProvider>());

// --- HTTP clients ------------------------------------------------------------
// Named client for GitHub upstream. GitHub requires a User-Agent header.
// Authorization is added per-request in GitHubUpstreamRemote (per-project PAT).
builder.Services.AddHttpClient("github-upstream", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("codeybox");
    // Shorter timeout than the 100 s .NET default: bounds the stall window per
    // attempt given the orchestrator retries up to UpstreamPushMaxAttempts times.
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Named client for quota probes. Short timeout — quota probes are on the hot
// path and must not stall the worker pickup loop. Authorization is added per-
// request; headers are never logged (see SensitiveDataRedactionEnricher).
builder.Services.AddHttpClient("agent-quota", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Named client for credential smoke probes. Authorization is added per-request
// from the credential bundle; the header is never logged. Timeout is generous
// (15 s) since the probe runs at most once per credential fingerprint per TTL.
builder.Services.AddHttpClient("agent-smoke", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

// --- Quota probes ------------------------------------------------------------
// Registered as IEnumerable<IAgentQuotaProbe>; the router resolves by Kind.
// Tokens are read from host env vars here (not in the probes) to keep the
// probe implementations independently testable.
builder.Services.AddSingleton<QuotaRouterOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var qr = cbOpts.QuotaRouter;
    return new QuotaRouterOptions
    {
        MinQuotaPct = qr.MinQuotaPct,
        QuotaRecheckInterval = TimeSpan.FromSeconds(qr.QuotaRecheckIntervalSeconds),
        QuotaCacheTtl = TimeSpan.FromSeconds(qr.QuotaCacheTtlSeconds),
    };
});
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
    new ClaudeQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY"),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClaudeQuotaProbe>()));
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
    new CodexQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        Environment.GetEnvironmentVariable("CODEYBOX_CODEX_API_KEY"),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CodexQuotaProbe>()));
// No GeminiQuotaProbe: Gemini uses PayPerApi billing (no subscription quota endpoint).
// The router treats a missing probe as unlimited — intentional. See docs/agents.md.

// --- Agent class router ------------------------------------------------------
builder.Services.AddSingleton<AgentClassRouter>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentClasses");

    // Build and validate the catalog.
    var catalog = BuildAndValidateAgentClasses(cbOpts.AgentClasses, startupLog);

    // Build and validate time-of-day score modifiers.
    var todModifiers = BuildAndValidateTodModifiers(cbOpts.AgentScoreModifiers, startupLog);

    return new AgentClassRouter(
        catalog,
        sp.GetServices<IAgentQuotaProbe>(),
        sp.GetRequiredService<QuotaRouterOptions>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentClassRouter>(),
        TimeProvider.System,
        todModifiers);
});

// --- Credential smoke probes -------------------------------------------------
// Registered as IEnumerable<IAgentSmokeProbe>; the gate resolves by Kind.
// Copilot has no smoke probe: its auth surface is not directly probeable.
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new ClaudeSmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClaudeSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new CodexSmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CodexSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new GeminiSmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GeminiSmokeProbe>()));

builder.Services.AddSingleton<SmokeOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var s = cbOpts.Smoke;
    return new SmokeOptions
    {
        Enabled = s.Enabled,
        CacheTtlMinutes = s.CacheTtlMinutes,
        StartupTimeoutSeconds = s.StartupTimeoutSeconds,
    };
});
builder.Services.AddSingleton<IAgentSmokeCache>(sp =>
{
    var opts = sp.GetRequiredService<SmokeOptions>();
    return new AgentSmokeCache(TimeSpan.FromMinutes(opts.CacheTtlMinutes));
});
builder.Services.AddSingleton<CredentialSmokeGate>(sp =>
    new CredentialSmokeGate(
        sp.GetRequiredService<ICredentialProvider>(),
        sp.GetServices<IAgentSmokeProbe>(),
        sp.GetRequiredService<IAgentSmokeCache>(),
        sp.GetRequiredService<SmokeOptions>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CredentialSmokeGate>()));

// --- Projects + per-project upstream + audit composer ------------------------
builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
builder.Services.AddSingleton<IUpstreamRemoteFactory, UpstreamRemoteFactory>();
builder.Services.AddSingleton<IPresetCatalog, PresetCatalog>();
builder.Services.AddSingleton<ProjectAuditorComposer>();

// --- Webhooks ----------------------------------------------------------------
// AllowAutoRedirect=false prevents SSRF via HTTP 3xx redirects to private
// addresses that bypass the blocklist in ValidateWebhookUrl.
builder.Services.AddHttpClient("webhook")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    });
builder.Services.AddSingleton<IWebhookDispatcher>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    if (opts.Webhooks.Count == 0)
        return new NullWebhookDispatcher();

    var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var endpointConfigs = opts.Webhooks.Select(w =>
    {
        if (string.IsNullOrWhiteSpace(w.Name))
            throw new InvalidOperationException("Each webhook endpoint must have a non-empty Name");
        if (w.Name.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new InvalidOperationException($"Webhook endpoint Name '{w.Name}' must not contain control characters");
        if (!seenNames.Add(w.Name))
            throw new InvalidOperationException($"Webhook endpoint Name '{w.Name}' is not unique");
        if (w.SecretEnvVar is not null && w.SecretEnvVar.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new InvalidOperationException($"Webhooks[{w.Name}].SecretEnvVar must not contain control characters");
        Validation.ValidateWebhookUrl(w.Url, $"Webhooks[{w.Name}].Url");
        if (w.MaxAttempts < 1)
            throw new InvalidOperationException($"Webhooks[{w.Name}].MaxAttempts must be >= 1");
        if (w.InitialBackoffSeconds < 0)
            throw new InvalidOperationException($"Webhooks[{w.Name}].InitialBackoffSeconds must be >= 0");
        if (w.TimeoutSeconds < 1)
            throw new InvalidOperationException($"Webhooks[{w.Name}].TimeoutSeconds must be >= 1");
        return new WebhookEndpointConfig
        {
            Name = w.Name,
            Url = w.Url,
            SecretEnvVar = w.SecretEnvVar,
            EventFilter = w.EventFilter,
            MaxAttempts = w.MaxAttempts,
            InitialBackoffSeconds = w.InitialBackoffSeconds,
            TimeoutSeconds = w.TimeoutSeconds,
        };
    }).ToList();

    return new HttpWebhookDispatcher(
        new WebhookDispatcherOptions { Endpoints = endpointConfigs },
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<HttpWebhookDispatcher>>());
});

// --- Changelog automation ----------------------------------------------------
// Named HTTP client for direct Anthropic Messages API calls (changelog generation).
// Reuses api.anthropic.com which is already in AgentAllowedHosts; this client is
// used only from the API process, not from sandbox agents.
builder.Services.AddHttpClient("changelog-claude", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddSingleton<IPullRequestEnumerator>(sp =>
    new CodeyBox.Upstream.GitHub.GitHubPullRequestEnumerator(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<CodeyBox.Upstream.GitHub.GitHubPullRequestEnumerator>>()));

builder.Services.AddSingleton<IChangelogGenerator>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.Changelog;
    return new ClaudeChangelogGenerator(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<ClaudeChangelogGenerator>>(),
        opts);
});

// Changelog webhook HMAC secret — mirrors the SandboxProvider enforcement pattern.
// In non-Development environments the secret env-var MUST be configured so that
// the POST /webhooks/github/release endpoint always validates HMAC signatures.
{
    var changelogCfg = builder.Configuration.GetSection("CodeyBox:Changelog");
    var changelogEnabled = changelogCfg.GetValue<bool>("Enabled", true);
    var webhookSecretEnvVar = changelogCfg["GitHubWebhookSecretEnvVar"];
    if (changelogEnabled && string.IsNullOrEmpty(webhookSecretEnvVar))
    {
        if (builder.Environment.IsDevelopment())
        {
            Log.Warning(
                "CodeyBox:Changelog:GitHubWebhookSecretEnvVar is not configured. " +
                "GitHub release webhooks will be rejected with 401 until a secret is set. " +
                "This is a configuration error in non-Development environments.");
        }
        else
        {
            throw new InvalidOperationException(
                "CodeyBox:Changelog:GitHubWebhookSecretEnvVar must be configured in non-Development environments. " +
                "Set it to the name of the environment variable holding the HMAC-SHA256 webhook secret " +
                "(see docs/changelog-automation.md).");
        }
    }
}

// --- SignalR (live agent stdout) ----------------------------------------------
// AgentStdoutHub requires no additional packages on ASP.NET Core 8+.
// Auth is enforced by the existing ApiKeyAuth middleware on the HTTP upgrade
// request — the hub itself needs no [Authorize] attribute because no ASP.NET
// Core authentication scheme is registered.
builder.Services.AddSignalR();
builder.Services.AddSingleton<AgentStdoutBroadcastService>();
builder.Services.AddSingleton<IStdoutBroadcaster>(sp =>
    sp.GetRequiredService<AgentStdoutBroadcastService>());

// --- Audit timeline reader ---------------------------------------------------
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AuditLog;
    return new AuditLogTimelineReader(opts);
});

// --- Persistence + queue + pipeline + worker pool ----------------------------
builder.Services.AddSingleton<IWorkItemStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<ISuggestionStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteSuggestionStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IWorkItemQuestionStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemQuestionStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IAuditReportStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAuditReportStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<ITimingStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteTimingStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IWorkItemCostStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemCostStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IQueueController>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteQueueController(opts.StateDatabasePath, sp.GetRequiredService<ILogger<SqliteQueueController>>());
});
builder.Services.AddSingleton<InMemoryTaskQueue>();
builder.Services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<InMemoryTaskQueue>());

// --- Dead-worker registry + reaper -------------------------------------------
builder.Services.AddSingleton<IWorkerRegistry>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkerRegistry(opts.StateDatabasePath, sp.GetRequiredService<ILogger<SqliteWorkerRegistry>>());
});
builder.Services.AddSingleton<DeadWorkerOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.DeadWorker;
    opts.Validate();
    return opts;
});
builder.Services.AddSingleton<DeadWorkerReaper>(sp => new DeadWorkerReaper(
    sp.GetRequiredService<IWorkerRegistry>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ITaskQueue>(),
    sp.GetRequiredService<DeadWorkerOptions>(),
    sp.GetRequiredService<ILogger<DeadWorkerReaper>>(),
    sp.GetRequiredService<IWebhookDispatcher>()));

// --- Agent cost extractors + calculator ------------------------------------
builder.Services.AddSingleton<AgentCostCalculator>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentPricing");
    var pricing = opts.AgentPricing;
    AgentCostCalculator.ValidateAtStartup(pricing,
        sp.GetRequiredService<IAgentRegistry>().Available, startupLog);
    return new AgentCostCalculator(pricing);
});
builder.Services.AddSingleton<IReadOnlyDictionary<AgentKind, IAgentCostExtractor>>(sp =>
{
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentCosts");
    var registry = sp.GetRequiredService<IAgentRegistry>();
    var extractors = new Dictionary<AgentKind, IAgentCostExtractor>
    {
        [AgentKind.Claude] = new ClaudeCostExtractor(),
        [AgentKind.Codex] = new CodexCostExtractor(),
        [AgentKind.Gemini] = new GeminiCostExtractor(),
    };
    // Warn once at startup for registered agents with no extractor.
    foreach (var kind in registry.Available)
    {
        if (!extractors.ContainsKey(kind))
            startupLog.LogWarning(
                "No cost extractor registered for agent '{Agent}'; cost data will not be captured for this agent", kind.Value);
    }
    return extractors;
});

builder.Services.AddSingleton<PipelineOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.GitIdentity");
    var hostIdentity = HostGitIdentityReader.Read(startupLog);
    return new PipelineOptions
    {
        SandboxImageReference = opts.SandboxImageReference,
        AgentAllowedHosts = opts.AgentAllowedHosts,
        UpstreamPushMaxAttempts = opts.UpstreamPushMaxAttempts,
        UpstreamPushBackoff = TimeSpan.FromSeconds(opts.UpstreamPushBackoffSeconds),
        HostGitIdentity = hostIdentity,
    };
});
builder.Services.AddSingleton<PipelineRunner>(sp => new PipelineRunner(
    sp.GetRequiredService<ISandboxProvider>(),
    sp.GetRequiredService<IGitHost>(),
    sp.GetRequiredService<IAgentRegistry>(),
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetRequiredService<IPullRequestService>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IUpstreamRemoteFactory>(),
    sp.GetRequiredService<ProjectAuditorComposer>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<PipelineOptions>(),
    sp.GetRequiredService<ILogger<PipelineRunner>>(),
    sp.GetService<CredentialSmokeGate>(),
    sp.GetService<ISuggestionStore>(),
    sp.GetServices<IAgentQuotaProbe>(),
    sp.GetService<QuotaRouterOptions>(),
    sp.GetRequiredService<IAuditReportStore>(),
    null,
    sp.GetRequiredService<IWorkItemCostStore>(),
    sp.GetRequiredService<IReadOnlyDictionary<AgentKind, IAgentCostExtractor>>(),
    sp.GetRequiredService<AgentCostCalculator>(),
    sp.GetService<IWorkItemQuestionStore>(),
    sp.GetRequiredService<IStdoutBroadcaster>()));
builder.Services.AddSingleton<IPipelineRunner>(sp => sp.GetRequiredService<PipelineRunner>());
builder.Services.AddSingleton<OrchestratorOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.Orchestrator");
    return OrchestratorOptionsFactory.Build(cbOpts.Concurrency, cbOpts.WorkerPool, startupLog);
});
builder.Services.AddSingleton<CancellationRegistry>(sp =>
    new CancellationRegistry(sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
builder.Services.AddSingleton<OrchestratorService>(sp => new OrchestratorService(
    sp.GetRequiredService<ITaskQueue>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<IPipelineRunner>(),
    sp.GetRequiredService<CancellationRegistry>(),
    sp.GetRequiredService<OrchestratorOptions>(),
    sp.GetRequiredService<ILogger<OrchestratorService>>(),
    sp.GetRequiredService<AgentClassRouter>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IQueueController>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IWorkerRegistry>(),
    sp.GetRequiredService<DeadWorkerOptions>(),
    sp.GetRequiredService<DeadWorkerReaper>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadWorkerReaper>());
builder.Services.AddHostedService(sp => new StartupSmokeProbeService(
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetServices<IAgentSmokeProbe>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<SmokeOptions>(),
    sp.GetRequiredService<ILogger<StartupSmokeProbeService>>()));
builder.Services.AddHostedService(sp => new AuditAgentStartupValidationService(
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetRequiredService<ILogger<AuditAgentStartupValidationService>>()));
builder.Services.AddHostedService(sp => new AuditReportRetentionService(
    sp.GetRequiredService<IAuditReportStore>(),
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AuditLog.RetainedDays,
    sp.GetRequiredService<ILogger<AuditReportRetentionService>>()));
builder.Services.AddHostedService(sp => new BudgetAlertService(
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IWorkItemCostStore>(),
    sp.GetRequiredService<IQueueController>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.BudgetAlerts,
    sp.GetRequiredService<ILogger<BudgetAlertService>>()));

builder.Services.AddSingleton<SandboxLeakReaper>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.SandboxLeak;
    return new SandboxLeakReaper(
        sp.GetRequiredService<ISandboxProvider>(),
        sp.GetRequiredService<IWebhookDispatcher>(),
        opts,
        sp.GetRequiredService<ILogger<SandboxLeakReaper>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<SandboxLeakReaper>());

// --- Plugin foundation -------------------------------------------------------
// Discovers assemblies from CodeyBox:Plugins, registers plugin types under
// their Core interfaces before the container is frozen, then runs
// IPluginInitializer.InitializeAsync at startup via PluginInitializationService.
// See docs/plugins.md for author guidance, allowlist config, and threat model.
preDiscoveredPlugins = builder.Services.AddCodeyBoxPlugins(builder.Configuration);

var app = builder.Build();

app.UseApiKeyAuth(anonymousPrefixes: ["/healthz", "/webhooks/"]);

WorkItemEndpoints.Map(app);
WorkItemTimingsEndpoints.Map(app);
WorkItemCostsEndpoints.Map(app);
ProjectBudgetEndpoints.Map(app);
WorkItemDiffEndpoints.Map(app);
SuggestionEndpoints.Map(app);
AuditReportEndpoints.Map(app);
ChangelogEndpoints.Map(app);
FleetEndpoints.Map(app);
PluginEndpoints.Map(app);
WorkerRegistryEndpoints.Map(app);
SandboxEndpoints.Map(app);

app.MapHub<AgentStdoutHub>("/hubs/agent-stdout");
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

namespace CodeyBox.Api
{
    public sealed class CodeyBoxOptions
    {
        public string GitRootDirectory { get; set; } = "/var/lib/codeybox/repos";
        public string StateDatabasePath { get; set; } = "/var/lib/codeybox/state.db";
        public string SandboxImageReference { get; set; } = "codeybox/agent:latest";
        public string[] AgentAllowedHosts { get; set; } = ["api.anthropic.com", "api.openai.com", "api.githubcopilot.com", "generativelanguage.googleapis.com"];
        /// <summary>
        /// Legacy concurrency knob. If set, treated as
        /// <see cref="WorkerPool"/>.<see cref="WorkerPoolOptions.MaxConcurrentWorkers"/>
        /// and a deprecation warning is emitted. Prefer WorkerPool instead.
        /// </summary>
        public int? Concurrency { get; set; }

        /// <summary>Worker pool sizing and spawn-pacing configuration.</summary>
        public WorkerPoolOptions WorkerPool { get; set; } = new();

        /// <summary>Heartbeat and dead-worker reaper configuration.</summary>
        public DeadWorkerOptions DeadWorker { get; set; } = new();

        public int UpstreamPushMaxAttempts { get; set; } = 5;
        public int UpstreamPushBackoffSeconds { get; set; } = 15;

        /// <summary>
        /// Which sandbox provider to use. One of: <c>multipass</c>,
        /// <c>bubblewrap</c>, <c>process</c>.
        /// Default is empty — startup defaults to 'process' in Development
        /// and refuses to start in other environments.
        /// </summary>
        public string? SandboxProvider { get; set; }

        /// <summary>
        /// Override that lets <c>process</c> sandbox load outside Development.
        /// Don't.
        /// </summary>
        public bool DangerouslyAllowProcessSandbox { get; set; }

        /// <summary>
        /// Extra cloud-init YAML appended to the auto-generated network policy
        /// when SandboxProvider=multipass. Use to install agent CLIs in the
        /// VM at first boot (e.g. apt-installing nodejs and npm-installing
        /// the agent CLI).
        /// </summary>
        public string? MultipassExtraCloudInit { get; set; }

        /// <summary>
        /// Maps logical network-profile names → host bridge names. Operators
        /// configure these bridges once via scripts/setup-host-networks.sh;
        /// the orchestrator then attaches each VM to the matching bridge,
        /// where host-side nftables rules enforce egress.
        ///
        /// Empty → no host-enforced profile is selectable; sandboxes
        /// fall back to Multipass's default bridge, which
        /// setup-host-networks.sh blocks at the host. For functional
        /// egress, populate this and run setup-host-networks.sh.
        ///
        /// Example:
        /// <code>
        /// "SandboxNetworkProfiles": {
        ///   "isolated":  "cb-iso",
        ///   "claude":    "cb-claude",
        ///   "multi-llm": "cb-multi-llm"
        /// }
        /// </code>
        /// Bridge names are limited to 15 characters by Linux IFNAMSIZ.
        /// Profile names (the keys) have no such limit.
        /// </summary>
        public Dictionary<string, string> SandboxNetworkProfiles { get; set; } = [];

        /// <summary>
        /// Shell commands run inside the sandbox VM at first boot, after
        /// the orchestrator's route swap (so they have working egress).
        /// Use for one-shot setup the project needs in the sandbox —
        /// installing the agent CLI, the language toolchain, any auditor
        /// tools the audit policy expects to be present. Each entry is a
        /// single shell command (multi-line OK).
        /// </summary>
        public List<string> MultipassExtraRuncmd { get; set; } = [];

        /// <summary>
        /// When true, the Multipass provider lazily bakes a per-profile
        /// baseline VM on first use (running the standard cloud-init +
        /// MultipassExtraRuncmd install once), then clones it for every
        /// subsequent sandbox of that profile. Cuts each VM cold-start from
        /// ~5-10 min to ~10s. The baselines stay stopped at rest.
        ///
        /// Delete the baselines (<c>multipass delete --purge cb-baseline-*</c>)
        /// to force a re-bake after changing MultipassExtraRuncmd.
        /// </summary>
        public bool MultipassUseBaselineImages { get; set; } = false;

        /// <summary>
        /// Outbound webhook endpoints. Empty list disables webhooks entirely.
        /// Each entry configures one HTTPS target that receives pipeline events.
        /// </summary>
        public List<WebhookEndpointOptions> Webhooks { get; set; } = [];

        /// <summary>
        /// Audit log configuration: rolling file paths, retention, and size caps.
        /// </summary>
        public AuditLogOptions AuditLog { get; set; } = new();

        /// <summary>
        /// Agent class definitions for quota-aware routing. Each class lists one or
        /// more agent members in preference order. See docs/agent-classes.md.
        /// </summary>
        public List<AgentClassOptions> AgentClasses { get; set; } = [];

        /// <summary>Quota router tuning knobs.</summary>
        public QuotaRouterConfig QuotaRouter { get; set; } = new();

        /// <summary>
        /// Time-of-day score modifiers. Applied as small effective-score adjustments
        /// to act as tiebreakers between near-equivalent models during peak cost windows.
        /// See docs/configuration.md for the schedule schema.
        /// </summary>
        public AgentScoreModifiersOptions AgentScoreModifiers { get; set; } = new();

        /// <summary>Credential smoke test tuning knobs.</summary>
        public SmokeConfig Smoke { get; set; } = new();

        /// <summary>Agent token pricing for cost estimation. See docs/cost-reporting.md.</summary>
        public AgentPricingOptions AgentPricing { get; set; } = new();

        /// <summary>Monthly cost-budget alert sweep configuration. See docs/budget-alerts.md.</summary>
        public BudgetAlertOptions BudgetAlerts { get; set; } = new();
        /// <summary>OpenTelemetry export configuration. See docs/observability.md.</summary>
        public OtelOptions Otel { get; set; } = new();

        /// <summary>Changelog automation configuration. See docs/changelog-automation.md.</summary>
        public ChangelogOptions Changelog { get; set; } = new();

        /// <summary>
        /// Sandbox leak reaper configuration. The reaper periodically scans for
        /// <c>codeybox-*</c> Multipass VMs that outlived their work item and logs
        /// (or optionally auto-disposes) them. See docs/sandbox-leaks.md.
        /// </summary>
        public SandboxLeakOptions SandboxLeak { get; set; } = new();
    }

    /// <summary>
    /// OpenTelemetry export configuration. Bound from <c>CodeyBox:Otel</c>.
    /// Off by default — set <see cref="Enabled"/> to true to opt in.
    /// </summary>
    public sealed class OtelOptions
    {
        /// <summary>Enable OTel export. Default false. Nothing is registered when false.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>OTel service.name resource attribute. Default "codeybox".</summary>
        public string ServiceName { get; set; } = "codeybox";

        /// <summary>OTel service.version — typically a git SHA or release tag.</summary>
        public string? ServiceVersion { get; set; }

        /// <summary>OTLP collector endpoint, e.g. http://localhost:4317.</summary>
        public string? OtlpEndpoint { get; set; }

        /// <summary>
        /// Optional CSV of extra headers forwarded to the OTLP collector,
        /// e.g. <c>x-honeycomb-team=abc,x-dataset=prod</c>.
        /// </summary>
        public string? OtlpHeaders { get; set; }

        /// <summary>OTLP wire format. Either <c>grpc</c> (default) or <c>httpprotobuf</c>.</summary>
        public string ExportProtocol { get; set; } = "grpc";

        /// <summary>Extra OTel resource attributes merged into every span and metric point.</summary>
        public Dictionary<string, string> ResourceAttributes { get; set; } = [];

        /// <summary>
        /// Validates the options, throwing <see cref="InvalidOperationException"/> when
        /// <see cref="Enabled"/> is true and the configuration is incomplete or invalid.
        /// Safe to call when disabled — no-ops immediately.
        /// </summary>
        public static void Validate(OtelOptions opts)
        {
            if (!opts.Enabled) return;

            if (string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
                throw new InvalidOperationException(
                    "CodeyBox:Otel:OtlpEndpoint must be set when CodeyBox:Otel:Enabled=true.");

            if (!Uri.TryCreate(opts.OtlpEndpoint, UriKind.Absolute, out var endpointUri)
                || endpointUri.Scheme is not "http" and not "https")
                throw new InvalidOperationException(
                    $"CodeyBox:Otel:OtlpEndpoint '{opts.OtlpEndpoint}' is not a valid http/https URL.");

            if (opts.ExportProtocol is not "grpc" and not "httpprotobuf")
                throw new InvalidOperationException(
                    $"CodeyBox:Otel:ExportProtocol '{opts.ExportProtocol}' is not valid. " +
                    "Expected 'grpc' or 'httpprotobuf'.");
        }
    }

    /// <summary>
    /// Global changelog automation options. Bound from <c>CodeyBox:Changelog</c>.
    /// Per-project overrides are applied via <c>Project.Changelog</c>.
    /// </summary>
    public sealed class ChangelogOptions
    {
        /// <summary>Enable or disable changelog automation globally. Default true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// LLM agent to use for generation. Currently only "claude" is supported.
        /// Default "claude".
        /// </summary>
        public string GeneratorAgent { get; set; } = "claude";

        /// <summary>
        /// Optional model override for the generator LLM call, e.g. "claude-opus-4-7".
        /// Defaults to "claude-opus-4-7".
        /// </summary>
        public string? GeneratorModelId { get; set; }

        /// <summary>
        /// Path to CHANGELOG.md within the project repo. Default "CHANGELOG.md".
        /// </summary>
        public string ChangelogPath { get; set; } = "CHANGELOG.md";

        /// <summary>
        /// Section header format. Supports {tag} and {date:yyyy-MM-dd} placeholders.
        /// Default: "## [{tag}] - {date:yyyy-MM-dd}".
        /// </summary>
        public string SectionHeaderFormat { get; set; } = "## [{tag}] - {date:yyyy-MM-dd}";

        /// <summary>
        /// Name of the environment variable holding the HMAC-SHA256 secret for
        /// validating incoming GitHub release webhooks. Must be set in non-Development
        /// environments; the webhook endpoint rejects all requests with 401 if not configured.
        /// </summary>
        public string? GitHubWebhookSecretEnvVar { get; set; }
    }

    /// <summary>
    /// Credential smoke test options. Bound from <c>CodeyBox:Smoke</c>.
    /// </summary>
    public sealed class SmokeConfig
    {
        /// <summary>Enable or disable the smoke gate. Default true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Result cache TTL in minutes. Default 15.</summary>
        public int CacheTtlMinutes { get; set; } = 15;

        /// <summary>Per-agent timeout for the startup probe in seconds. Default 10.</summary>
        public int StartupTimeoutSeconds { get; set; } = 10;
    }

    /// <summary>Config binding for a single agent class (see CodeyBox:AgentClasses).</summary>
    public sealed class AgentClassOptions
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<AgentMembershipOptions> Members { get; set; } = [];
    }

    /// <summary>Config binding for one member of an agent class.</summary>
    public sealed class AgentMembershipOptions
    {
        /// <summary>Agent kind value, e.g. "claude", "codex".</summary>
        public string Agent { get; set; } = string.Empty;
        /// <summary>"Subscription" or "PayPerApi".</summary>
        public string Billing { get; set; } = "Subscription";
        /// <summary>Optional model override, e.g. "claude-opus-4-7".</summary>
        public string? ModelId { get; set; }
        /// <summary>
        /// Operator-curated capability score (0–200). Required; no silent default.
        /// See docs/agent-classes.md for recommended seed values.
        /// </summary>
        public int? QualityScore { get; set; }
        /// <summary>
        /// Optional reasoning-effort knob, e.g. "high". Required for Gemini
        /// members with QualityScore >= 90.
        /// </summary>
        public string? ReasoningMode { get; set; }
    }

    /// <summary>Quota router tuning. Bound from CodeyBox:QuotaRouter.</summary>
    public sealed class QuotaRouterConfig
    {
        /// <summary>Minimum available-quota percentage to consider a member viable. Default 10.</summary>
        public double MinQuotaPct { get; set; } = 10.0;
        /// <summary>Seconds to wait before re-probing when all subscription members are exhausted. Default 300 (5 min).</summary>
        public int QuotaRecheckIntervalSeconds { get; set; } = 300;
        /// <summary>Seconds to cache a probe result. Default 60.</summary>
        public int QuotaCacheTtlSeconds { get; set; } = 60;
    }

    /// <summary>
    /// Top-level score-modifier config. Bound from <c>CodeyBox:AgentScoreModifiers</c>.
    /// </summary>
    public sealed class AgentScoreModifiersOptions
    {
        /// <summary>Time-of-day modifier entries.</summary>
        public List<TimeOfDayModifierOptions> ByTimeOfDay { get; set; } = [];
    }

    /// <summary>One time-of-day modifier entry.</summary>
    public sealed class TimeOfDayModifierOptions
    {
        /// <summary>Agent kind value, e.g. "claude".</summary>
        public string Agent { get; set; } = string.Empty;
        /// <summary>
        /// Score delta applied during matching windows. Negative values reduce
        /// effective score; positive values increase it. Bounded to ±5 at startup.
        /// </summary>
        public int Modifier { get; set; }
        /// <summary>Human annotation for config readability; not used at runtime.</summary>
        public string? Comment { get; set; }
        /// <summary>Windows during which the modifier is active.</summary>
        public List<TimeWindowOptions> Windows { get; set; } = [];
    }

    /// <summary>A single UTC time window within a week.</summary>
    public sealed class TimeWindowOptions
    {
        /// <summary>Three-letter day codes: Mon, Tue, Wed, Thu, Fri, Sat, Sun.</summary>
        public List<string> Days { get; set; } = [];
        /// <summary>Window start time in HH:mm UTC (inclusive).</summary>
        public string StartUtc { get; set; } = "00:00";
        /// <summary>Window end time in HH:mm UTC (exclusive). Wrap-around is supported (e.g. 22:00–02:00).</summary>
        public string EndUtc { get; set; } = "00:00";
    }

    /// <summary>
    /// Per-endpoint webhook options, bound from the CodeyBox:Webhooks config array.
    /// </summary>
    public sealed class WebhookEndpointOptions
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? SecretEnvVar { get; set; }
        public List<string> EventFilter { get; set; } = [];
        public int MaxAttempts { get; set; } = 3;
        public int InitialBackoffSeconds { get; set; } = 1;
        public int TimeoutSeconds { get; set; } = 10;
    }

    /// <summary>
    /// Rolling file log configuration. Paths are resolved relative to the
    /// API process's working directory when they are not absolute.
    /// See <c>docs/audit-logging.md</c> for details.
    /// </summary>
    public sealed class AuditLogOptions
    {
        /// <summary>
        /// Path template for the main rolling log (all severity levels).
        /// Default: <c>logs/codeybox-.json</c> — the date is inserted before
        /// the trailing dot by Serilog's file sink.
        /// </summary>
        public string Path { get; set; } = "logs/codeybox-.json";

        /// <summary>
        /// Path template for the audit-only rolling log (<c>Audit=true</c>
        /// events only). Default: <c>logs/audit-.json</c>.
        /// </summary>
        public string AuditPath { get; set; } = "logs/audit-.json";

        /// <summary>Days of rolled files to keep. Must be >= 1. Default: 30.</summary>
        public int RetainedDays { get; set; } = 30;

        /// <summary>Per-file size cap before rolling. Default: 100 MiB.</summary>
        public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;
    }
}

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
