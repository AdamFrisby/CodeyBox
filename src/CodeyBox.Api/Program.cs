using Microsoft.Extensions.Options;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Gemini;
using CodeyBox.Api;
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
            loggerFactory.CreateLogger<BubblewrapSandboxProvider>()),
        "multipass" => new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                ExtraCloudInit = opts.MultipassExtraCloudInit,
                ExtraRuncmd = opts.MultipassExtraRuncmd,
                NetworkProfiles = opts.SandboxNetworkProfiles,
                UseBaselineImages = opts.MultipassUseBaselineImages,
            },
            loggerFactory.CreateLogger<MultipassSandboxProvider>()),
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
            members.Add(new AgentMembership
            {
                Agent = new AgentKind(m.Agent),
                Billing = billing,
                ModelId = m.ModelId,
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

// --- Credentials -------------------------------------------------------------
// Each agent's API key has a per-agent host env var that maps to the
// canonical sandbox env var the agent CLI reads. Operators add new agents
// by appending to this list (or registering a different ICredentialProvider).
//
// The chain reads Claude's OAuth token fresh from a JSON file (default
// ~/.claude/.credentials.json, the path the local `claude` CLI refreshes
// in-place) on every pickup, so a host-side token rotation is picked up
// without an orchestrator restart. If the file is absent or empty, the
// env-var provider supplies the value the host launcher exported.
builder.Services.AddSingleton<ICredentialProvider>(sp =>
{
    var providers = new List<ICredentialProvider>();

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
        providers.Add(new ClaudeOAuthFileCredentialProvider(
            oauthFile,
            sandboxEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
            sp.GetService<ILogger<ClaudeOAuthFileCredentialProvider>>()));
    }

    providers.Add(new EnvironmentCredentialProvider(new[]
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

    return new ChainedCredentialProvider(providers);
});

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

// --- Agent class router ------------------------------------------------------
builder.Services.AddSingleton<AgentClassRouter>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentClasses");

    // Build and validate the catalog.
    var catalog = BuildAndValidateAgentClasses(cbOpts.AgentClasses, startupLog);

    return new AgentClassRouter(
        catalog,
        sp.GetServices<IAgentQuotaProbe>(),
        sp.GetRequiredService<QuotaRouterOptions>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentClassRouter>());
});

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

// --- Persistence + queue + pipeline + worker pool ----------------------------
builder.Services.AddSingleton<IWorkItemStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<InMemoryTaskQueue>();
builder.Services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<InMemoryTaskQueue>());

builder.Services.AddSingleton<PipelineOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new PipelineOptions
    {
        SandboxImageReference = opts.SandboxImageReference,
        AgentAllowedHosts = opts.AgentAllowedHosts,
        UpstreamPushMaxAttempts = opts.UpstreamPushMaxAttempts,
        UpstreamPushBackoff = TimeSpan.FromSeconds(opts.UpstreamPushBackoffSeconds),
    };
});
builder.Services.AddSingleton<PipelineRunner>();
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
    sp.GetRequiredService<IProjectRepository>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());

var app = builder.Build();

app.UseApiKeyAuth(anonymousPrefixes: ["/healthz"]);

WorkItemEndpoints.Map(app);

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
