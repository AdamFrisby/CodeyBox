using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Opencode;
using CodeyBox.Api;
using CodeyBox.Api.Hubs;
using CodeyBox.Audit;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using CodeyBox.Notifications;
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
// reloadOnChange:true so an operator can edit this file without restarting
// CodeyBox — IOptionsMonitor<T> consumers will observe the new values within
// the framework's debounce window (~1 s).
{
    var extra = Environment.GetEnvironmentVariable("CODEYBOX_EXTRA_CONFIG");
    if (!string.IsNullOrEmpty(extra))
        builder.Configuration.AddJsonFile(extra, optional: false, reloadOnChange: true);
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
    var agentStreamOpts = cbConf.AgentStreams;

    AuditLogStartup.ValidateAndPrepare(auditOpts);

    using var bootstrapLoggerFactory = LoggerFactory.Create(static b => b.AddSerilog(dispose: false));
    AgentStreamsOptions.ValidateAtStartup(
        agentStreamOpts,
        bootstrapLoggerFactory.CreateLogger("CodeyBox.AgentStreams"));

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
builder.Services.Configure<NotificationsOptions>(builder.Configuration.GetSection("CodeyBox:Notifications"));
// Register ProjectsOptions through AddOptions so IOptionsMonitor<ProjectsOptions>
// is wired into the framework's reload pipeline. PostConfigure layers our custom
// map-shaped binding (audit-type / language overrides / profile inheritance) on
// top of the framework's section.Bind() — these dictionaries don't bind from the
// standard Bind() path because their keys are dynamic JSON property names. On
// reload, both run again automatically.
builder.Services.AddOptions<ProjectsOptions>()
    .Bind(builder.Configuration.GetSection("CodeyBox"))
    .PostConfigure(opts => ProjectsOptionsBinder.ApplyCustomMaps(opts, builder.Configuration.GetSection("CodeyBox")));

// Immutable-field guard for CodeyBoxOptions. The startup snapshot is bound from
// IConfiguration when the service provider resolves it, after test/host
// ConfigureAppConfiguration hooks have had a chance to add their final sources.
// The retaining monitor cache is deliberate: stock IOptionsMonitor drops its
// prior cached value before validating a reload candidate, so a rejected edit
// would make CurrentValue throw until the next successful reload.
builder.Services.AddSingleton(sp => new CodeyBoxOptionsStartupSnapshot(
    sp.GetRequiredService<IConfiguration>().GetSection("CodeyBox").Get<CodeyBoxOptions>()
    ?? new CodeyBoxOptions()));
builder.Services.AddSingleton<IOptionsMonitorCache<CodeyBoxOptions>>(
    sp => new RetainingOptionsMonitorCache<CodeyBoxOptions>(
        sp.GetRequiredService<CodeyBoxOptionsStartupSnapshot>().Value));
builder.Services.AddSingleton<IValidateOptions<CodeyBoxOptions>>(
    sp => new ImmutableCodeyBoxOptionsValidator(
        sp.GetRequiredService<CodeyBoxOptionsStartupSnapshot>().Value));
builder.Services.AddSingleton<IValidateOptions<CodeyBoxOptions>, CodeyBoxOptionsValidator>();

// Rejects ProjectsOptions reloads that remove a project still holding
// non-terminal work items. Adding new projects passes cleanly.
builder.Services.AddSingleton<IValidateOptions<ProjectsOptions>, ProjectsOptionsRemovalValidator>();

// Sized from the resolved sandbox provider's capability, not its config name:
// a provider that implements ISuspendingSandboxProvider freezes VMs on shutdown
// and needs the raised ceiling; everything else keeps the tighter grace. Using
// the DI-resolved provider keeps the deployment knowledge (name → provider) in
// the composition root and out of the Core policy. See ComputeHostShutdownTimeout.
builder.Services.AddOptions<HostOptions>()
    .Configure<IOptions<CodeyBoxOptions>, ISandboxProvider, ILoggerFactory>(
        (o, cbOptsAccessor, sandboxProvider, loggerFactory) =>
        {
            var providerSuspendsOnShutdown = sandboxProvider is ISuspendingSandboxProvider;
            o.ShutdownTimeout = Program.ComputeHostShutdownTimeout(
                cbOptsAccessor.Value,
                providerSuspendsOnShutdown,
                loggerFactory.CreateLogger("CodeyBox.HostShutdown"));
        });

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

// B1: register the baseline-image resolver capability as a derived view of
// the registered sandbox provider. The factory returns null when the
// provider does not implement IBaselineImageResolver (process / bubblewrap);
// consumers must use GetService (not GetRequiredService) and handle null.
// The factory is gated on the resolved provider — non-multipass setups get a
// null factory hit, which the consumer treats as "no baseline pinning".
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<ISandboxProvider>() as IBaselineImageResolver
        ?? NullBaselineImageResolver.Instance);

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
        "multipass" => BuildMultipass(opts, sp, loggerFactory, startupLog, sp.GetService<ITimingStore>()),
        _ => throw new InvalidOperationException(
            $"Unknown CodeyBox:SandboxProvider '{kind}'. Valid: multipass, bubblewrap, process"),
    };
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

static MultipassSandboxProvider BuildMultipass(CodeyBoxOptions opts, IServiceProvider sp, ILoggerFactory loggerFactory, ILogger startupLog, ITimingStore? timings)
{
    // DiskGuard is resolved once at startup: it captures the state-database
    // directory (built from opts) which is itself immutable for the process
    // lifetime. The cloud-init / runcmd / network-profile fields below are
    // resolved live via IOptionsMonitor on every VM launch.
    var diskGuard = MultipassDiskGuardConfig.Build(opts, startupLog);
    var provider = new MultipassSandboxProvider(
        // Resolve through IOptionsMonitor so cloud-init / runcmd edits land
        // on the next VM launch without restart. Sandboxes already running
        // keep the snapshot they were constructed with.
        () =>
        {
            var live = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;
            var multipassSandbox = live.MultipassSandbox ?? new MultipassSandboxConfig();
            return new MultipassSandboxOptions
            {
                ExtraCloudInit = live.MultipassExtraCloudInit,
                ExtraRuncmd = live.MultipassExtraRuncmd,
                NetworkProfiles = live.SandboxNetworkProfiles,
                UseBaselineImages = live.MultipassUseBaselineImages,
                CloudInitReadyRetryAttempts = multipassSandbox.CloudInitReadyRetryAttempts,
                VmStartTimeout = multipassSandbox.VmStartTimeout,
                VmStopTimeout = multipassSandbox.VmStopTimeout,
                MaxConcurrentBoots = multipassSandbox.MaxConcurrentBoots,
                BootLaunchDelay = TimeSpan.FromMilliseconds(multipassSandbox.BootLaunchDelayMs),
                DiskGuard = diskGuard,
            };
        },
        loggerFactory.CreateLogger<MultipassSandboxProvider>(),
        timings);

    // Startup banner: log free disk for each guarded path so the operator
    // can see at a glance whether the host is close to the threshold. Mirrors
    // the existing baseline-image banner pattern. Speaks to the capability
    // interface so this code does not depend on the concrete provider type.
    if (diskGuard is not null)
    {
        LogDiskGuardBanner(provider, startupLog);
    }

    return provider;
}

static void LogDiskGuardBanner(IDiskGuardedSandboxProvider provider, ILogger startupLog)
{
    foreach (var sample in provider.SampleDiskGuardState())
    {
        var freeRendered = sample.FreeBytes is long b ? FormatBytes(b) : "(unknown)";
        startupLog.LogInformation(
            "Disk-guard: {Path} free={FreeBytes} threshold={Threshold}",
            sample.Path, freeRendered, FormatBytes(sample.ThresholdBytes));
    }
}

static string FormatBytes(long bytes)
{
    const double gib = 1024d * 1024 * 1024;
    const double mib = 1024d * 1024;
    if (bytes >= gib) return $"{bytes / gib:F2} GiB";
    if (bytes >= mib) return $"{bytes / mib:F2} MiB";
    return $"{bytes:N0} B";
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

// --- Pre-merge CI gate verifier -----------------------------------------------
// LocalGitPreMergeVerifier materialises the orchestrator's merge result into
// a worktree on the host bare repo and runs the operator-configured
// PreMergeVerifyArgv against it before the forge auto-merge API call. The
// gate stays opt-in: the orchestrator skips the verifier when the project's
// PreMergeVerifyArgv is empty, so projects that have not configured the gate
// see no behaviour change.
builder.Services.AddSingleton<IPreMergeVerifier>(sp => new LocalGitPreMergeVerifier(
    sp.GetRequiredService<IGitHost>(),
    sp.GetRequiredService<ILogger<LocalGitPreMergeVerifier>>()));

// --- Pull request service (in-memory by default) -----------------------------
builder.Services.AddSingleton<IPullRequestService, InMemoryPullRequestService>();

// --- Agents ------------------------------------------------------------------
builder.Services.AddSingleton<IAgentRunner, ClaudeAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, CopilotAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, CodexAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, GeminiAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, CursorAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, OpencodeAgentRunner>();
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
// Chain order: BUILT-IN-FIRST → PLUGINS → BUILT-IN-LAST.
//
// 1. ClaudeOAuthFileCredentialProvider — reads Claude's credentials fresh
//    from a JSON file (default ~/.claude/.credentials.json, the path the
//    local `claude` CLI refreshes in-place) on every pickup, so a host-side
//    token rotation is picked up without an orchestrator restart. The
//    provider ships a sanitised creds JSON bundle (access token and expiry
//    only; no refresh_token) to the sandbox via CODEYBOX_CLAUDE_OAUTH_JSON
//    so ClaudeAgentRunner can materialise it inside the VM without racing the
//    host-side CLI's single-use refresh token.
// 2. Plugin ICredentialProvider implementations — inserted in discovery order
//    (between OAuth-file and env-var). Vault-issued short-lived credentials
//    are preferred over env-var fallbacks. Per-project ordering is expressed
//    via Project.CredentialProviderPriority; see docs/credential-plugins.md.
// 3. CodexOAuthFileCredentialProvider and EnvironmentCredentialProvider —
//    fallback providers. Codex host auth is deliberately after plugins so a
//    project-selected credential plugin can isolate Codex credentials from the
//    operator's ~/.codex/auth.json.
//
// Operators with no credential plugins see zero behaviour change: the chain
// is identical to the pre-plugin OAuth-file → env-var behaviour.
//
// ChainedCredentialProvider is registered under three service types so that:
//   - ICredentialProvider resolves the global chain (smoke gates, startup validation).
//   - IProjectAwareCredentialProvider lets PipelineRunner apply per-project
//     CredentialProviderPriority at agent pickup time.
//   - ChainedCredentialProvider is directly resolvable for callers that need both.
// Resolve credential file paths once so the file-watching CredentialFileSource
// singletons can be created up-front. A single source per file is shared between
// the credential provider (which produces sandbox bundles for child VMs) and
// the quota probe (which probes the upstream usage endpoint on the host). When
// the file changes — operator running the CLI on the host, scripted refresh,
// child-VM writeback — every consumer observes the new token within ~1 s and
// quota probes invalidate their per-token snapshot, so a stale 401 doesn't pin
// for the full cache TTL.
var claudeOAuthFilePath =
    Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_OAUTH_FILE")
    ?? builder.Configuration["CodeyBox:ClaudeOAuthFile"];
var claudeOAuthProviderConfigured = !string.IsNullOrWhiteSpace(claudeOAuthFilePath);
if (string.IsNullOrWhiteSpace(claudeOAuthFilePath))
    claudeOAuthFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");
if (claudeOAuthFilePath!.StartsWith("~/", StringComparison.Ordinal))
    claudeOAuthFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        claudeOAuthFilePath[2..]);

var codexOAuthFilePath =
    Environment.GetEnvironmentVariable("CODEYBOX_CODEX_OAUTH_FILE")
    ?? builder.Configuration["CodeyBox:CodexOAuthFile"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "auth.json");
if (codexOAuthFilePath.StartsWith("~/", StringComparison.Ordinal))
    codexOAuthFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        codexOAuthFilePath[2..]);

var geminiHome = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".gemini");
var geminiOAuthFilePath =
    Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_FILE")
    ?? builder.Configuration["CodeyBox:GeminiOAuthFile"]
    ?? Path.Combine(geminiHome, "oauth_creds.json");
if (geminiOAuthFilePath.StartsWith("~/", StringComparison.Ordinal))
    geminiOAuthFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        geminiOAuthFilePath[2..]);
var geminiSettingsFilePath =
    Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_SETTINGS_FILE")
    ?? builder.Configuration["CodeyBox:GeminiSettingsFile"]
    ?? Path.Combine(geminiHome, "settings.json");
if (geminiSettingsFilePath.StartsWith("~/", StringComparison.Ordinal))
    geminiSettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        geminiSettingsFilePath[2..]);

// Cursor subscription credentials. Path is operator-configurable; default
// matches what `agent login` writes in current Cursor CLI versions
// (~/.config/cursor/auth.json — XDG-style; the legacy
// ~/.cursor/credentials.json path is no longer read by the binary). The
// orchestrator never bind-mounts this path into the sandbox — only the file
// contents are shipped via CODEYBOX_CURSOR_AUTH_JSON and CursorAgentRunner
// re-materialises them inside the VM at the matching XDG path.
var cursorAuthFilePath =
    Environment.GetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_FILE")
    ?? builder.Configuration["CodeyBox:CursorAuthFile"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "cursor",
        "auth.json");
if (cursorAuthFilePath.StartsWith("~/", StringComparison.Ordinal))
    cursorAuthFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        cursorAuthFilePath[2..]);

var opencodeAuthFilePath =
    Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_AUTH_FILE")
    ?? builder.Configuration["CodeyBox:OpencodeAuthFile"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "auth.json");
if (opencodeAuthFilePath.StartsWith("~/", StringComparison.Ordinal))
    opencodeAuthFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        opencodeAuthFilePath[2..]);

// Optional override for where the sandbox-side credential file gets written.
// Operators who confirm a different `opencode auth login` destination set
// CODEYBOX_OPENCODE_AUTH_DEST on the host; the runner uses this value as the
// destination path inside the VM. Defaults to the XDG path opencode appears
// to use at the time of writing; verify with `opencode auth login` output.
var opencodeAuthDestPath =
    Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_AUTH_DEST")
    ?? builder.Configuration["CodeyBox:OpencodeAuthDestPath"];

builder.Services.AddSingleton(sp => new ClaudeCredentialFileSource(
    claudeOAuthFilePath,
    sp.GetService<ILogger<CredentialFileSource>>(),
    watch: CredentialFileWatcherSettings.IsEnabled(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(sp => new CodexCredentialFileSource(
    codexOAuthFilePath,
    sp.GetService<ILogger<CredentialFileSource>>(),
    watch: CredentialFileWatcherSettings.IsEnabled(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(sp => new GeminiOAuthCredentialFileSource(
    geminiOAuthFilePath,
    sp.GetService<ILogger<CredentialFileSource>>(),
    watch: CredentialFileWatcherSettings.IsEnabled(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(sp => new GeminiSettingsCredentialFileSource(
    geminiSettingsFilePath,
    sp.GetService<ILogger<CredentialFileSource>>(),
    watch: CredentialFileWatcherSettings.IsEnabled(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(sp => new CursorCredentialFileSource(
    cursorAuthFilePath,
    sp.GetService<ILogger<CredentialFileSource>>(),
    watch: CredentialFileWatcherSettings.IsEnabled(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(sp => new OpencodeCredentialFileSource(
    opencodeAuthFilePath,
    sp.GetService<ILogger<CredentialFileSource>>(),
    watch: CredentialFileWatcherSettings.IsEnabled(sp.GetRequiredService<IConfiguration>())));

// Bridges the host-side ClaudeCredentialFileSource watcher to in-flight VMs:
// when ~/.claude/.credentials.json rotates while a Claude agent is running in
// a sandbox, the pusher writes the fresh sanitised bundle into the VM's
// ~/.claude/.credentials.json before its next Anthropic call goes 401. The
// runner picks the pusher up via its optional constructor parameter and
// registers each active sandbox for the duration of RunAsync/RunResumedAsync.
builder.Services.AddSingleton<ClaudeTokenRotationPusher>(sp => new ClaudeTokenRotationPusher(
    sp.GetRequiredService<ClaudeCredentialFileSource>(),
    sp.GetService<ILogger<ClaudeTokenRotationPusher>>()));
builder.Services.AddSingleton<IClaudeTokenRotationPusher>(sp =>
    sp.GetRequiredService<ClaudeTokenRotationPusher>());

builder.Services.AddSingleton<ChainedCredentialProvider>(sp =>
{
    var builtInFirst = new List<ICredentialProvider>();
    var namedPlugins = new List<(string Id, ICredentialProvider Provider)>();
    var builtInLast = new List<ICredentialProvider>();

    if (claudeOAuthProviderConfigured)
    {
        builtInFirst.Add(new ClaudeOAuthFileCredentialProvider(
            sp.GetRequiredService<ClaudeCredentialFileSource>(),
            sandboxEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
            sp.GetService<ILogger<ClaudeOAuthFileCredentialProvider>>()));
    }

    // Codex (ChatGPT subscription) auth file. Default ~/.codex/auth.json — the
    // codex CLI hard-reads that path. CodexAgentRunner writes the file into
    // the sandbox before invoking codex. Prefer an explicit CODEX_AUTH_JSON
    // environment secret when the host process is already provisioned that way.
    builtInFirst.Add(new CodexAuthJsonEnvironmentCredentialProvider(
        sp.GetService<ILogger<CodexAuthJsonEnvironmentCredentialProvider>>()));
    builtInFirst.Add(new CodexOAuthFileCredentialProvider(
        sp.GetRequiredService<CodexCredentialFileSource>(),
        sp.GetService<ILogger<CodexOAuthFileCredentialProvider>>()));

    // Gemini (Google AI Studio / Code Assist) OAuth files. The CLI hard-reads
    // ~/.gemini/{oauth_creds,settings}.json — there's no env-var alternative
    // for OAuth-personal — so the orchestrator ships their contents to the
    // sandbox via env vars and GeminiAgentRunner.PrepareSandboxAsync writes
    // them back to ~/.gemini/ inside the VM.
    builtInFirst.Add(new GeminiOAuthFileCredentialProvider(
        sp.GetRequiredService<GeminiOAuthCredentialFileSource>(),
        sp.GetRequiredService<GeminiSettingsCredentialFileSource>(),
        sp.GetService<ILogger<GeminiOAuthFileCredentialProvider>>()));

    // Cursor subscription credentials. Same pattern as Codex: the CLI hard-
    // reads its own credentials file, we ship the contents into the sandbox
    // via CODEYBOX_CURSOR_AUTH_JSON and the runner re-materialises them.
    builtInFirst.Add(new CursorOAuthFileCredentialProvider(
        sp.GetRequiredService<CursorCredentialFileSource>(),
        sp.GetService<ILogger<CursorOAuthFileCredentialProvider>>()));

    // opencode (sst/opencode "Go" subscription) auth file. The opencode CLI
    // hard-reads its credential file in the target user's home; this
    // provider ships the raw bytes to the sandbox via OPENCODE_AUTH_JSON
    // and OpencodeAgentRunner writes them back inside the VM before
    // invoking `opencode run`.
    builtInFirst.Add(new OpencodeOAuthFileCredentialProvider(
        sp.GetRequiredService<OpencodeCredentialFileSource>(),
        destinationPath: opencodeAuthDestPath,
        sp.GetService<ILogger<OpencodeOAuthFileCredentialProvider>>()));

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

    builtInLast.Add(new ClaudeEnvironmentCredentialProvider());
    builtInLast.Add(new EnvironmentCredentialProvider(new[]
    {
        new AgentCredentialMapping(AgentKind.Copilot, "CODEYBOX_COPILOT_TOKEN", "GH_TOKEN"),
        new AgentCredentialMapping(AgentKind.Codex, "CODEYBOX_CODEX_API_KEY", "OPENAI_API_KEY"),
        new AgentCredentialMapping(AgentKind.Gemini, "CODEYBOX_GEMINI_API_KEY", "GEMINI_API_KEY"),
        // Cursor: the CLI uses subscription auth via ~/.cursor/credentials.json
        // (NOT an env-var key). The orchestrator ships the file's contents to
        // the sandbox via CODEYBOX_CURSOR_AUTH_JSON and CursorAgentRunner
        // materialises it inside the VM. This env-var mapping is the fallback
        // when an operator wants to inject the JSON directly via env var
        // without an on-host credential file.
        new AgentCredentialMapping(AgentKind.Cursor, "CODEYBOX_CURSOR_AUTH_JSON", "CODEYBOX_CURSOR_AUTH_JSON"),
        // Note: no OPENCODE_API_KEY mapping. The opencode subscription IS the
        // credential path; auth flows exclusively through the auth.json file
        // materialised by OpencodeOAuthFileCredentialProvider. See the brief
        // for the relevant 'Don't do' rule and docs/agents.md for setup.
    }));
    builtInLast.Add(new EnvironmentCredentialProvider(new[]
    {
        // Also accept the conventional OpenAI SDK variable. This keeps Codex
        // audit runners authenticated in hosts that inject OPENAI_API_KEY
        // directly instead of the CodeyBox-namespaced variant above.
        new AgentCredentialMapping(AgentKind.Codex, "OPENAI_API_KEY", "OPENAI_API_KEY"),
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
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

// Named client for credential smoke probes. Authorization is added per-request
// from the credential bundle; the header is never logged. Timeout is generous
// (15 s) since the probe runs at most once per credential fingerprint per TTL.
builder.Services.AddHttpClient("agent-smoke", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

// Named client for the startup model-list probes used by AgentClassConfigValidator.
// Authorization is added per-request; headers are never logged. Per-call timeout is
// shorter than the validator's overall 10 s deadline so a slow provider can't soak
// the entire budget on a single probe.
builder.Services.AddHttpClient("agent-modellist", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

// --- Quota probes ------------------------------------------------------------
// Registered as IEnumerable<IAgentQuotaProbe>; the router resolves by Kind.
// OAuth files are reread by the provider delegate on each probe pickup because
// local agent CLIs refresh those files in place. Probe results are still cached
// per token by the probe implementations.
builder.Services.AddSingleton<QuotaRouterOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var qr = cbOpts.QuotaRouter;
    return new QuotaRouterOptions
    {
        MinQuotaPct = qr.MinQuotaPct,
        QuotaRecheckInterval = TimeSpan.FromSeconds(qr.QuotaRecheckIntervalSeconds),
        QuotaCacheTtl = TimeSpan.FromSeconds(qr.QuotaCacheTtlSeconds),
        UnknownPolicy = qr.UnknownPolicy,
        ObservedFailureWindow = TimeSpan.FromMinutes(qr.ObservedFailureWindowMinutes),
        ObservedFailureRetention = TimeSpan.FromMinutes(qr.ObservedFailureRetentionMinutes),
        CapRetryInterval = TimeSpan.FromSeconds(qr.CapRetryIntervalSeconds),
    };
});
builder.Services.AddSingleton<IQuotaFailureStore>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteQuotaFailureStore(cbOpts.StateDatabasePath);
});
builder.Services.AddSingleton<IAgentFallbackHistoryStore>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentFallbackHistoryStore(cbOpts.StateDatabasePath);
});
// OAuth-refreshing quota-token sources. These wrap the raw credential file
// sources with provider-specific refresh logic so an expired access_token is
// re-minted via the provider's OAuth refresh endpoint before the probe sends
// it. Without this, an expired token would 401, the snapshot would become
// AvailablePct=-1, and the router's default UnknownPolicy=UseObservedFailures
// would fall open onto an agent that immediately 429s. See
// OauthCredentialFileRefresher.cs for the per-provider refresh contracts.
builder.Services.AddSingleton<IClaudeQuotaTokenSource>(sp => new ClaudeOauthCredentialFileRefresher(
    sp.GetRequiredService<ClaudeCredentialFileSource>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClaudeOauthCredentialFileRefresher>()));
builder.Services.AddSingleton<ICodexQuotaTokenSource>(sp => new CodexOauthCredentialFileRefresher(
    sp.GetRequiredService<CodexCredentialFileSource>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<CodexOauthCredentialFileRefresher>()));
builder.Services.AddSingleton<IGeminiQuotaTokenSource>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new GeminiOauthCredentialFileRefresher(
        sp.GetRequiredService<GeminiOAuthCredentialFileSource>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GeminiOauthCredentialFileRefresher>(),
        geminiOauthClientId: Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_CLIENT_ID")
            ?? config["CodeyBox:GeminiOauthClientId"],
        geminiOauthClientSecret: Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_CLIENT_SECRET")
            ?? config["CodeyBox:GeminiOauthClientSecret"],
        cliTokenRefresher: GeminiOauthCredentialFileRefresher.TryCreateCliRefreshHandler());
});

builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<ClaudeCredentialFileSource>();
    var tokenSource = sp.GetRequiredService<IClaudeQuotaTokenSource>();
    var probe = new ClaudeQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        // Sync-over-async is intentional and safe here: ASP.NET Core has no
        // SynchronizationContext (no deadlock potential), the cache hit-path
        // is fully synchronous, and only a stale-token miss blocks the thread
        // on the OAuth refresh round-trip (bounded by the agent-quota client's
        // 10s timeout).
        () => new AgentQuotaCredentials(
            tokenSource.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult()
                ?? Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY")),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<ClaudeQuotaProbe>());
    source.TokenUpdated += probe.InvalidateCache;
    return probe;
});
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<CodexCredentialFileSource>();
    var tokenSource = sp.GetRequiredService<ICodexQuotaTokenSource>();
    var probe = new CodexQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        () =>
        {
            var codexAuth = tokenSource.GetTokensAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            return new AgentQuotaCredentials(
                codexAuth.AccessToken ?? Environment.GetEnvironmentVariable("CODEYBOX_CODEX_API_KEY"),
                codexAuth.AccountId ?? Environment.GetEnvironmentVariable("CODEYBOX_CODEX_ACCOUNT_ID"));
        },
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<CodexQuotaProbe>());
    source.TokenUpdated += probe.InvalidateCache;
    return probe;
});
// Gemini OAuth-subscription path (Code Assist Individual / AI Pro / AI Ultra).
// API-key (PayPerApi) and Vertex paths have no analogous endpoint and stay
// PayPerApi members in the agent class config — for those the router treats
// a missing probe result as unlimited.
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<GeminiOAuthCredentialFileSource>();
    var tokenSource = sp.GetRequiredService<IGeminiQuotaTokenSource>();
    var probe = new GeminiQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        () => new AgentQuotaCredentials(
            tokenSource.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult()
                ?? Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_TOKEN")),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<GeminiQuotaProbe>());
    source.TokenUpdated += probe.InvalidateCache;
    return probe;
});
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<CursorCredentialFileSource>();
    var probe = new CursorQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        () => new AgentQuotaCredentials(
            CredentialFileTokenExtractor.ExtractCursorAccessToken(source.GetRaw())
                ?? CredentialFileTokenExtractor.ExtractCursorAccessToken(
                    Environment.GetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_JSON"))),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<CursorQuotaProbe>());
    source.TokenUpdated += probe.InvalidateCache;
    return probe;
});

// opencode: no verified usage endpoint at integration time. The probe ships
// as Unknown-only so the router falls onto its QuotaUnknownPolicy
// (UseObservedFailures) for opencode members. Replace with a real
// HTTP-backed probe once an endpoint is confirmed.
builder.Services.AddSingleton<IAgentQuotaProbe>(_ => new OpencodeQuotaProbe());

// --- Agent class router ------------------------------------------------------
builder.Services.AddSingleton<AgentClassRouter>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentClasses");

    // Build and validate the catalog. Shared with AgentConfigHotReload so a
    // reload of CodeyBox:AgentClasses runs the same validation rules.
    var catalog = AgentClassesConfigBuilder.Build(cbOpts.AgentClasses, startupLog);
    var subscriptionMembers = catalog.Sum(c => c.Members.Count(m => m.Billing == AgentBilling.Subscription));
    startupLog.LogInformation("Quota gate enabled for {Count} subscription members", subscriptionMembers);

    // Build and validate time-of-day score modifiers.
    var todModifiers = AgentClassesConfigBuilder.BuildTodModifiers(cbOpts.AgentScoreModifiers, startupLog);

    return new AgentClassRouter(
        catalog,
        sp.GetServices<IAgentQuotaProbe>(),
        sp.GetRequiredService<QuotaRouterOptions>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentClassRouter>(),
        TimeProvider.System,
        todModifiers,
        sp.GetService<IQuotaFailureStore>(),
        sp.GetService<IAgentBurnEstimator>(),
        sp.GetService<IAgentRunningCounters>(),
        sp.GetService<AgentAvailabilityRegistry>(),
        sp.GetService<IAgentBudgetProvider>(),
        sp.GetService<AgentConcurrencySnapshot>());
});

// --- Per-agent concurrency / rate-aware dispatch -----------------------------
builder.Services.AddSingleton<AgentConcurrencyOptions>(sp =>
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AgentConcurrency);
// AgentConcurrencySnapshot is the shared swappable holder: OrchestratorService
// (dispatch gate) and PipelineRunner (pickup-time rebase-resolver cap-aware
// routing) both read through this single instance. The hot-reload coordinator
// updates it via OrchestratorService.ApplyAgentConcurrencyReload, and both
// consumers' next read picks up the new caps — without the shared holder,
// PipelineRunner would keep gating against the pre-reload caps until restart.
builder.Services.AddSingleton<AgentConcurrencySnapshot>(sp =>
    new AgentConcurrencySnapshot(sp.GetRequiredService<AgentConcurrencyOptions>()));

// IncrementalRebaseSnapshot — hot-reloadable feature flag for the
// between-iteration incremental rebase. Same swappable-singleton pattern as
// AgentConcurrencySnapshot: PipelineRunner reads through it, and the
// hot-reload coordinator publishes new values on Replace so an edit to
// CodeyBox:IncrementalRebase takes effect on the next audit iteration
// without a process restart.
builder.Services.AddSingleton<IncrementalRebaseSnapshot>(sp =>
    new IncrementalRebaseSnapshot(
        sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.IncrementalRebase));

// AgentDefaultsSnapshot — per-agent default model ids, swappable by the
// hot-reload coordinator. Every runner reads through this same instance so
// an operator edit to CodeyBox:AgentDefaults takes effect on the next
// dispatched agent run without a process restart.
builder.Services.AddSingleton<CodeyBox.Core.AgentDefaultsSnapshot>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var dict = new Dictionary<string, string?>(opts.AgentDefaults, opts.AgentDefaults.Comparer);
    return new CodeyBox.Core.AgentDefaultsSnapshot(dict);
});

builder.Services.AddSingleton<AgentBurnEstimatorOptions>(sp =>
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AgentBurnEstimator);
builder.Services.AddSingleton<AgentBurnEstimator>(sp => new AgentBurnEstimator(
    // Deferred resolution: the cost store backs onto a SQLite file that may
    // not yet be initialised when the router is constructed (e.g. in unit
    // tests that only build the router DI subgraph). Resolving lazily on the
    // first GetEstimateAsync keeps router construction allocation-only.
    () => sp.GetRequiredService<IWorkItemCostStore>(),
    sp.GetRequiredService<AgentBurnEstimatorOptions>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentBurnEstimator>(),
    TimeProvider.System));
// Concrete and interface share the same singleton — AgentConfigHotReload needs
// the concrete type to call ApplyConfigReload, the router takes the interface.
builder.Services.AddSingleton<IAgentBurnEstimator>(sp =>
    sp.GetRequiredService<AgentBurnEstimator>());

// Per-agent/per-model spend budgets → synthetic quota for the router.
builder.Services.AddSingleton<AgentBudgetCalculator>(sp => new AgentBudgetCalculator(
    // Deferred resolution mirrors the burn estimator: the usage store backs onto
    // a SQLite file that may not be initialised when the router subgraph is built.
    () => sp.GetRequiredService<IAgentUsageStore>(),
    // Bind the initial options from configuration. There is intentionally no
    // AgentBudgetOptions DI singleton: it would be a stale snapshot after a
    // hot-reload (which mutates the calculator's internal copy via
    // ApplyConfigReload), so future injectors must not read limits from DI.
    // RetentionDays is read live via IOptionsMonitor where it is consumed.
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AgentBudgets,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentBudgetCalculator>(),
    TimeProvider.System));
builder.Services.AddSingleton<IAgentBudgetProvider>(sp =>
    sp.GetRequiredService<AgentBudgetCalculator>());
// Separate reload abstraction so AgentConfigHotReload depends on a Core contract
// rather than the concrete calculator implementation type.
builder.Services.AddSingleton<IAgentBudgetConfigReloadable>(sp =>
    sp.GetRequiredService<AgentBudgetCalculator>());
// OrchestratorService implements IAgentRunningCounters. AgentClassRouter also
// depends on it, and OrchestratorService depends on AgentClassRouter — using a
// deferred wrapper breaks that cycle by resolving the singleton lazily on the
// first read, after both have been constructed.
builder.Services.AddSingleton<IAgentRunningCounters>(sp =>
    new DeferredAgentRunningCounters(() => sp.GetRequiredService<OrchestratorService>()));

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
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new CursorSmokeProbe(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CursorSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new OpencodeSmokeProbe(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpencodeSmokeProbe>()));

// --- Model-list probes (used by AgentClassConfigValidator at startup) --------
// Registered as IEnumerable<IAgentModelListProbe>; the validator resolves by Kind.
// Copilot has no probe — its CLI does not accept a --model flag, so AgentClass
// members never carry a Copilot ModelId in the first place.
builder.Services.AddSingleton<IAgentModelListProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<ClaudeCredentialFileSource>();
    return new ClaudeModelListProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        () => (
            CredentialFileTokenExtractor.ExtractClaudeAccessToken(source.GetRaw()),
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY")),
        loggerFactory.CreateLogger<ClaudeModelListProbe>());
});
builder.Services.AddSingleton<IAgentModelListProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<CodexCredentialFileSource>();
    return new CodexModelListProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        () =>
        {
            var codexAuth = CredentialFileTokenExtractor.ExtractCodexAccessTokens(source.GetRaw());
            return (
                codexAuth.AccessToken,
                codexAuth.AccountId ?? Environment.GetEnvironmentVariable("CODEYBOX_CODEX_ACCOUNT_ID"),
                Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? Environment.GetEnvironmentVariable("CODEYBOX_CODEX_API_KEY"));
        },
        loggerFactory.CreateLogger<CodexModelListProbe>());
});
builder.Services.AddSingleton<IAgentModelListProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return new OpencodeModelListProbe(
        new DefaultOpencodeCliRunner(),
        binary: Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_BINARY"),
        loggerFactory.CreateLogger<OpencodeModelListProbe>());
});
builder.Services.AddSingleton<IAgentModelListProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<GeminiOAuthCredentialFileSource>();
    return new GeminiModelListProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        () => (
            CredentialFileTokenExtractor.ExtractGeminiAccessToken(source.GetRaw())
                ?? Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_TOKEN"),
            Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY")),
        loggerFactory.CreateLogger<GeminiModelListProbe>());
});
builder.Services.AddSingleton<IAgentModelListProbe, CursorModelListProbe>();
builder.Services.AddHostedService<AgentClassConfigValidator>();

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
builder.Services.AddSingleton<AvailabilityOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var a = cbOpts.Smoke.Availability;
    return new AvailabilityOptions
    {
        FastFailThresholdSeconds = a.FastFailThresholdSeconds,
        MaxConsecutiveFastFails = a.MaxConsecutiveFastFails,
        PeriodicSweepInterval = TimeSpan.FromSeconds(Math.Max(0, a.PeriodicSweepIntervalSeconds)),
    };
});
builder.Services.AddSingleton<AgentAvailabilityRegistry>(sp => new AgentAvailabilityRegistry(
    sp.GetRequiredService<AvailabilityOptions>(),
    TimeProvider.System,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentAvailabilityRegistry>()));
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
// ProjectRepository observes IOptionsMonitor<ProjectsOptions>, so an
// appsettings.json edit that adds a new project — or changes an existing
// project's audit timeout — takes effect within the framework's debounce
// window (~1 s). The ProjectsOptionsRemovalValidator above rejects edits
// that drop a project with in-flight work items, so reloads that would
// strand running pipelines are surfaced as ERR and the prior project list
// is retained.
builder.Services.AddSingleton<IProjectRepository>(sp => new ProjectRepository(
    sp.GetRequiredService<IOptionsMonitor<ProjectsOptions>>(),
    sp.GetRequiredService<ILogger<ProjectRepository>>(),
    sp.GetService<PresetCatalogOptions>()));
builder.Services.AddSingleton<IUpstreamRemoteFactory, UpstreamRemoteFactory>();
builder.Services.AddSingleton(_ =>
{
    var options = builder.Configuration.GetSection("CodeyBox:Presets").Get<PresetCatalogOptions>()
        ?? new PresetCatalogOptions();
    options.ProjectRoot ??= builder.Environment.ContentRootPath;
    return options;
});
builder.Services.AddSingleton<IPresetCatalog>(sp => new PresetCatalog(sp.GetRequiredService<PresetCatalogOptions>()));
builder.Services.AddSingleton<IAuditor, GraphicalSmokeAuditor>();
builder.Services.AddSingleton<IAuditor, PromptRevisionTrailerAuditor>();
builder.Services.AddSingleton<ProjectAuditorComposer>();

// --- Built-in deep auditors (release in_review phase) ------------------------
// Registered as IDeepAuditor; ReleaseService resolves the subset configured per
// project by name match. LLM auditors require AgentCredentials at runtime.
builder.Services.AddSingleton<IDeepAuditor, OwaspAsvsDeepAuditor>();
builder.Services.AddSingleton<IDeepAuditor, ArchCoherenceDeepAuditor>();
builder.Services.AddSingleton<IDeepAuditor, DepsCveScanDeepAuditor>();

// --- Webhooks ----------------------------------------------------------------
// AllowAutoRedirect=false prevents SSRF via HTTP 3xx redirects to private
// addresses that bypass the blocklist in ValidateWebhookUrl.
builder.Services.AddHttpClient("webhook")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    });
builder.Services.AddSingleton<WebhookEventBroadcaster>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    // WebhookEventBroadcaster's constructor enforces capacity >= 1 and throws
    // ArgumentOutOfRangeException with a clear message naming the bad value.
    return new WebhookEventBroadcaster(opts.WebhookEventBus.RingBufferCapacity);
});
builder.Services.AddSingleton<IWebhookDispatcher>(sp =>
{
    var broadcaster = sp.GetRequiredService<WebhookEventBroadcaster>();
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    if (opts.Webhooks.Count == 0)
        return new BroadcastingWebhookDispatcher(broadcaster, new NullWebhookDispatcher());

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

    var http = new HttpWebhookDispatcher(
        new WebhookDispatcherOptions { Endpoints = endpointConfigs },
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<HttpWebhookDispatcher>>());
    return new BroadcastingWebhookDispatcher(broadcaster, http);
});

// --- Notifications ------------------------------------------------------------
// Human/systems notification pipeline: conditions evaluate system state,
// rules route matching notifications to providers (email, etc.).
// Safe no-op when unconfigured — no startup failure without SMTP config.
builder.Services.AddSingleton<OrchestratorProgressClock>();
builder.Services.AddSingleton<LeakDetectionSink>();

builder.Services.AddSingleton<INotificationProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<EmailNotificationProvider>>();
    var isDev = sp.GetRequiredService<IHostEnvironment>().IsDevelopment();
    Func<EmailProviderOptions> optsAccessor = () =>
        sp.GetRequiredService<IOptionsMonitor<NotificationsOptions>>().CurrentValue.Email;
    var opts = optsAccessor();
    if (opts.Enabled)
        return new EmailNotificationProvider(optsAccessor, logger, isDevelopment: isDev);
    return new NullNotificationProvider("email");
});

// ICondition registrations — one per supported condition.
builder.Services.AddSingleton<ICondition, QueueEmptyCondition>();
builder.Services.AddSingleton<ICondition>(sp => new AllQuotasExhaustedCondition(
    sp.GetRequiredService<IEnumerable<IAgentQuotaProbe>>(),
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.QuotaRouter.MinQuotaPct,
    sp.GetRequiredService<IAgentRegistry>(),
    sp.GetRequiredService<ILogger<AllQuotasExhaustedCondition>>()));
builder.Services.AddSingleton<ICondition, WorkItemPermanentlyFailedCondition>();
builder.Services.AddSingleton<ICondition>(sp => new OrchestratorStallCondition(
    sp.GetRequiredService<OrchestratorProgressClock>(),
    sp.GetRequiredService<IOptionsMonitor<NotificationsOptions>>()));
builder.Services.AddSingleton<ICondition, SandboxLeakReapedCondition>();

// INotificationBuilder registrations — one per condition.
builder.Services.AddSingleton<INotificationBuilder, QueueEmptyNotificationBuilder>();
builder.Services.AddSingleton<INotificationBuilder>(sp => new AllQuotasExhaustedNotificationBuilder(
    sp.GetRequiredService<IEnumerable<IAgentQuotaProbe>>(),
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.QuotaRouter.MinQuotaPct));
builder.Services.AddSingleton<INotificationBuilder, WorkItemPermanentlyFailedNotificationBuilder>();
builder.Services.AddSingleton<INotificationBuilder>(sp => new OrchestratorStallNotificationBuilder(
    sp.GetRequiredService<IOptionsMonitor<NotificationsOptions>>()));
builder.Services.AddSingleton<INotificationBuilder, SandboxLeakReapedNotificationBuilder>();

// Rules engine — BackgroundService that evaluates conditions and dispatches.
builder.Services.AddSingleton<NotificationRulesEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationRulesEngine>());

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
// Release store must be created BEFORE the work-item store so the releases
// table (referenced by work_items.release_id FK index) is present first.
builder.Services.AddSingleton<IReleaseStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteReleaseStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IWorkItemStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteIdempotencyStore(opts.StateDatabasePath);
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
builder.Services.AddSingleton<IAgentUsageStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentUsageStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IAgentStreamSummaryStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentStreamSummaryStore(opts.StateDatabasePath);
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
builder.Services.AddSingleton<DeadWorkerReaper>(sp =>
{
    // Resolve DeadWorkerOptions through the live CodeyBoxOptions monitor on
    // every sweep. Edits to CodeyBox:DeadWorker:MaxRecoveryAttempts (and
    // DeadWorkerThreshold) take effect on the next sweep without restart.
    // CheckInterval is sampled at PeriodicTimer construction so changes
    // require a restart — documented on the field itself.
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    // Run the same Validate() startup check on the resolved value once so
    // misconfigured DeadWorkerOptions surfaces here (matches the previous
    // factory behaviour where DeadWorkerOptions.Validate() ran at resolve time).
    sp.GetRequiredService<DeadWorkerOptions>();
    return new DeadWorkerReaper(
        sp.GetRequiredService<IWorkerRegistry>(),
        sp.GetRequiredService<IWorkItemStore>(),
        sp.GetRequiredService<ITaskQueue>(),
        () => monitor.CurrentValue.DeadWorker,
        sp.GetRequiredService<ILogger<DeadWorkerReaper>>(),
        sp.GetRequiredService<IWebhookDispatcher>());
});

// --- Agent cost extractors + calculator ------------------------------------
builder.Services.AddSingleton<IReadOnlyDictionary<AgentKind, IAgentCostExtractor>>(sp =>
{
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentCosts");
    var registry = sp.GetRequiredService<IAgentRegistry>();
    var extractors = new Dictionary<AgentKind, IAgentCostExtractor>
    {
        [AgentKind.Claude] = new ClaudeCostExtractor(),
        [AgentKind.Codex] = new CodexCostExtractor(),
        [AgentKind.Gemini] = new GeminiCostExtractor(),
        [AgentKind.Cursor] = new CursorCostExtractor(),
        [AgentKind.Opencode] = new OpencodeCostExtractor(),
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
// Bundled per-(agent, model) pricing defaults shipped with CodeyBox so new
// installs get cost reporting without the operator hand-populating every
// entry from provider docs. Operator config under CodeyBox:AgentPricing
// always wins per (agentKind, modelId). See docs/agent-pricing.md.
builder.Services.AddSingleton<AgentPricingDefaultsSnapshot>(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    return AgentPricingDefaults.Load(env.ContentRootPath);
});
builder.Services.AddSingleton<AgentPricingState>(sp =>
{
    var defaults = sp.GetRequiredService<AgentPricingDefaultsSnapshot>();
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var merged = AgentPricingMerge.Merge(defaults.Baseline, opts.AgentPricing);
    return new AgentPricingState(defaults, merged);
});
builder.Services.AddSingleton<AgentCostCalculator>(sp =>
{
    var pricingState = sp.GetRequiredService<AgentPricingState>();
    var merged = pricingState.LastMerge;
    var defaults = pricingState.Defaults;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentPricing");
    startupLog.LogInformation(
        "AgentPricing loaded: bundled={Bundled}, operator-overrides={Operator}, total={Total} (bundled lastUpdated={LastUpdated})",
        merged.BundledRateCount, merged.OperatorRateCount, merged.TotalRateCount,
        string.IsNullOrEmpty(defaults.Meta.LastUpdated) ? "(unknown)" : defaults.Meta.LastUpdated);
    var extractors = sp.GetRequiredService<IReadOnlyDictionary<AgentKind, IAgentCostExtractor>>();
    AgentCostCalculator.ValidateAtStartup(merged.Options,
        sp.GetRequiredService<IAgentRegistry>().Available, extractors, startupLog);
    var calculator = new AgentCostCalculator(new AgentPricingOptions(), extractors);
    pricingState.ApplySuccessfulMerge(merged, calculator);
    return calculator;
});
builder.Services.AddSingleton<AgentStreamsOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AgentStreams;
    AgentStreamsOptions.ValidateAtStartup(
        opts,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentStreams"));
    return opts;
});
builder.Services.AddSingleton<IAgentStreamStore>(sp =>
    new AgentStreamStore(
        sp.GetRequiredService<AgentStreamsOptions>(),
        sp.GetRequiredService<ILogger<AgentStreamStore>>()));
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.AgentStreamAnalysis;
    if (opts.StallThreshold < TimeSpan.Zero)
        throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:StallThreshold must be non-negative");
    if (opts.MaxLineBytes < 1024)
        throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxLineBytes must be >= 1024");
    if (opts.MaxJsonDepth < 1)
        throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxJsonDepth must be >= 1");
    if (opts.MaxEvents < 1)
        throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxEvents must be >= 1");
    if (opts.MaxToolCalls < 0)
        throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxToolCalls must be non-negative");
    if (opts.MaxStalls < 0)
        throw new InvalidOperationException("CodeyBox:AgentStreamAnalysis:MaxStalls must be non-negative");
    return opts;
});
builder.Services.AddSingleton<IAgentStreamParser, ClaudeStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, CodexStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, GeminiStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, UnknownAgentStreamParser>();

// Per-provider buffered-stdout tool-call counters. Used by the orchestrator
// to emit agent.tool_call.<name> telemetry when the agent runs without
// structured-stream capture; the dictionary lookup keeps the orchestrator
// from referencing any provider's stream-json shape directly.
builder.Services.AddSingleton<IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>>(sp =>
    new Dictionary<AgentKind, IAgentToolCallCounter>
    {
        [AgentKind.Claude] = new ClaudeToolCallCounter(),
    });

// Per-provider quota-failure detectors. Each agent library owns its own
// patterns + stream-json shape; the orchestrator dispatches by AgentKind.
builder.Services.AddSingleton<IAgentQuotaFailureDetector, ClaudeQuotaFailureDetector>();
builder.Services.AddSingleton<IAgentQuotaFailureDetector, CodexQuotaFailureDetector>();
builder.Services.AddSingleton<IAgentQuotaFailureDetector, GeminiQuotaFailureDetector>();
builder.Services.AddSingleton<IAgentQuotaFailureDetector>(sp =>
{
    // Cursor detector accepts operator-extensible patterns from
    // CodeyBox:QuotaFailurePatterns:cursor. Defaults already cover the observed
    // "out of usage" exhaustion shape; the config hook is for follow-on shapes
    // operators see in production before a code release can land.
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var extras = cbOpts.QuotaFailurePatterns is null
        ? null
        : cbOpts.QuotaFailurePatterns
            .Where(kvp => string.Equals(kvp.Key, AgentKind.Cursor.Value, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kvp => kvp.Value ?? new List<QuotaFailurePatternOptions>())
            .Where(p => !string.IsNullOrEmpty(p.Pattern))
            .Select(p => new QuotaFailurePattern(p.Pattern, p.Kind))
            .ToArray();
    return new CursorQuotaFailureDetector(extras);
});
builder.Services.AddSingleton<IAgentQuotaFailureDetector, OpencodeQuotaFailureDetector>();
builder.Services.AddSingleton<IQuotaFailureClassifier>(sp =>
    new CompositeQuotaFailureClassifier(sp.GetServices<IAgentQuotaFailureDetector>()));

builder.Services.AddSingleton<PipelineOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.GitIdentity");
    var hostIdentity = HostGitIdentityReader.Read(startupLog);
    return new PipelineOptions
    {
        SandboxImageReference = opts.SandboxImageReference,
        AgentAllowedHosts = opts.AgentAllowedHosts,
        AuditToolAllowedHosts = opts.AuditToolAllowedHosts,
        UpstreamPushMaxAttempts = opts.UpstreamPushMaxAttempts,
        UpstreamPushBackoff = TimeSpan.FromSeconds(opts.UpstreamPushBackoffSeconds),
        ShutdownGrace = TimeSpan.FromSeconds(Math.Max(1, opts.Shutdown.GraceSeconds)),
        PhaseAbsoluteTimeoutMultiplier = opts.PhaseAbsoluteTimeoutMultiplier,
        HostGitIdentity = hostIdentity,
    };
});
builder.Services.AddSingleton<WorkItemRetrier>(sp => new WorkItemRetrier(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ITaskQueue>(),
    sp.GetRequiredService<IGitHost>(),
    sp.GetRequiredService<ILogger<WorkItemRetrier>>(),
    sp.GetRequiredService<IAgentStreamSummaryStore>(),
    sp.GetService<IAuditReportStore>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IReleaseStore>()));

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
    sp.GetRequiredService<IStdoutBroadcaster>(),
    sp.GetService<IAgentStreamStore>(),
    sp.GetService<IQuotaFailureStore>(),
    sp.GetRequiredService<QuotaRetryScheduler>(),
    sp.GetService<AgentClassRouter>(),
    sp.GetService<IAgentFallbackHistoryStore>(),
    sp.GetRequiredService<IQuotaFailureClassifier>(),
    sp.GetRequiredService<IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>>(),
    sp.GetService<ITaskQueue>(),
    sp.GetService<OrchestratorOptions>(),
    sp.GetService<AgentAvailabilityRegistry>(),
    sp.GetService<IAgentRunningCounters>(),
    sp.GetService<AgentConcurrencyOptions>(),
    sp.GetRequiredService<IPreMergeVerifier>(),
    sp.GetRequiredService<AgentConcurrencySnapshot>(),
    sp.GetService<IAgentUsageStore>(),
    sp.GetService<IAgentBudgetProvider>(),
    sp.GetRequiredService<IncrementalRebaseSnapshot>()));
builder.Services.AddSingleton<IPipelineRunner>(sp => sp.GetRequiredService<PipelineRunner>());

builder.Services.AddSingleton<QuotaRetryScheduler>(sp => new QuotaRetryScheduler(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<WorkItemRetrier>(),
    sp.GetRequiredService<OrchestratorOptions>(),
    sp.GetRequiredService<ILogger<QuotaRetryScheduler>>(),
    sp.GetRequiredService<AgentClassRouter>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IQueueController>(),
    sp.GetRequiredService<IWebhookDispatcher>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<QuotaRetryScheduler>());

builder.Services.AddSingleton<OrchestratorOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.Orchestrator");
    return OrchestratorOptionsFactory.Build(
        cbOpts.Concurrency,
        cbOpts.WorkerPool,
        cbOpts.AutoRetryOnQuotaFailure.Enabled,
        cbOpts.AutoRetryOnQuotaFailure.PeriodicCheckInterval,
        cbOpts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin,
        cbOpts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem,
        startupLog);
});
builder.Services.AddSingleton<CancellationRegistry>(sp =>
    new CancellationRegistry(sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
// --- Release management -------------------------------------------------------
builder.Services.AddSingleton<ReleaseService>(sp => new ReleaseService(
    sp.GetRequiredService<IReleaseStore>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<ISandboxProvider>(),
    sp.GetRequiredService<IGitHost>(),
    sp.GetRequiredService<IAgentRegistry>(),
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetRequiredService<IUpstreamRemoteFactory>(),
    sp.GetServices<IDeepAuditor>(),
    sp.GetRequiredService<IChangelogGenerator>(),
    sp.GetRequiredService<PipelineOptions>(),
    sp.GetRequiredService<ITaskQueue>(),
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<ILogger<ReleaseService>>(),
    sp.GetService<IAgentStreamStore>()));

builder.Services.AddHostedService(sp => new ReleaseMainSyncService(
    sp.GetRequiredService<IReleaseStore>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IUpstreamRemoteFactory>(),
    sp.GetRequiredService<ILogger<ReleaseMainSyncService>>()));

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
    sp.GetRequiredService<DeadWorkerReaper>(),
    sp.GetService<ReleaseService>(),
    sp.GetRequiredService<AgentConcurrencyOptions>(),
    sp.GetRequiredService<AgentConcurrencySnapshot>(),
    sp.GetRequiredService<IBaselineImageResolver>(),
    sp.GetRequiredService<OrchestratorProgressClock>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
// R8.1: expose the orchestrator as IShutdownDispatchGate so the
// SandboxSuspendOnShutdownService can pause new dispatch before the per-VM
// teardown begins (incident 2026-05-29 fix).
builder.Services.AddSingleton<IShutdownDispatchGate>(
    sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadWorkerReaper>());
// R8-core: suspend in-flight sandboxes on graceful shutdown so the next process
// can resume them. Both halves of the cycle are IHostedLifecycleService so the
// host awaits StoppingAsync (suspend) and StartingAsync (resume) natively
// rather than blocking a thread-pool callback. The resume runs BEFORE
// OrchestratorService.ExecuteAsync (and before the dead-worker reaper) so
// adopted in-VM agents are observed before the standard recovery sweep fires.
//
// R8.1 (incident 2026-05-29): the suspend handler is wired with the orchestrator
// as an IShutdownDispatchGate so it pauses new dispatch BEFORE snapshotting the
// suspendable set — without that ordering, the dispatch loop keeps creating
// new sandboxes that race the snapshot. Teardown mode is operator-tunable via
// CodeyBox:Shutdown:SandboxTeardownMode (Suspend / Stop / Dispose); default
// Suspend for backward compatibility.
builder.Services.AddHostedService(sp => new SandboxSuspendOnShutdownService(
    sp.GetRequiredService<ISandboxProvider>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ILogger<SandboxSuspendOnShutdownService>>(),
    dispatchGate: sp.GetService<IShutdownDispatchGate>(),
    teardownMode: sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>()
        .CurrentValue.Shutdown.SandboxTeardownMode));
// Startup reconciler runs BEFORE the resume handler (registration order ==
// StartingAsync execution order) so VMs left wedged in Suspending state from
// a prior unclean shutdown are returned to a clean state before resume tries
// to multipass-start them or the leak reaper considers them on its first sweep.
builder.Services.AddHostedService(sp => new StartupSandboxReconciliationService(
    sp.GetService<ISandboxProvider>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ILogger<StartupSandboxReconciliationService>>()));
builder.Services.AddHostedService(sp => new SandboxResumeOnStartupService(
    sp.GetService<ISandboxProvider>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ILogger<SandboxResumeOnStartupService>>()));

// Hot-reload bridge: subscribes to IOptionsMonitor<CodeyBoxOptions> and pushes
// changes to AgentConcurrency / AgentClasses / AgentBurnEstimator into the
// running router, orchestrator, and burn estimator without a restart. The
// orchestrator/router are constructed with the initial options, so the
// coordinator captures that same baseline at StartAsync to avoid emitting a
// spurious "config_reloaded" entry on the first OnChange.
builder.Services.AddSingleton<AgentConfigHotReload>(sp =>
{
    var pricingState = sp.GetRequiredService<AgentPricingState>();
    return new AgentConfigHotReload(
        sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>(),
        sp.GetRequiredService<OrchestratorService>(),
        sp.GetRequiredService<AgentClassRouter>(),
        sp.GetRequiredService<AgentBurnEstimator>(),
        sp.GetRequiredService<ILogger<AgentConfigHotReload>>(),
        defaults: sp.GetRequiredService<CodeyBox.Core.AgentDefaultsSnapshot>(),
        costCalculator: sp.GetRequiredService<AgentCostCalculator>(),
        pricingState: pricingState,
        budgetReloader: sp.GetRequiredService<IAgentBudgetConfigReloadable>(),
        incrementalRebase: sp.GetRequiredService<IncrementalRebaseSnapshot>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentConfigHotReload>());
builder.Services.AddHostedService(sp => new StartupSmokeProbeService(
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetServices<IAgentSmokeProbe>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<SmokeOptions>(),
    sp.GetRequiredService<ILogger<StartupSmokeProbeService>>(),
    sp.GetService<AgentAvailabilityRegistry>()));
builder.Services.AddSingleton<PeriodicSmokeProbeService>(sp => new PeriodicSmokeProbeService(
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetServices<IAgentSmokeProbe>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<SmokeOptions>(),
    sp.GetRequiredService<AvailabilityOptions>(),
    sp.GetRequiredService<AgentAvailabilityRegistry>(),
    sp.GetRequiredService<ILogger<PeriodicSmokeProbeService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PeriodicSmokeProbeService>());
builder.Services.AddHostedService(sp => new AuditAgentStartupValidationService(
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetRequiredService<ILogger<AuditAgentStartupValidationService>>()));
// Live accessor: operator edits to CodeyBox:AuditLog:RetainedDays take effect
// on the next daily retention sweep without restart. The Serilog rolling-file
// writer pinned RetainedDays at startup though, so its sink retention does
// require a restart — documented on AuditLogOptions.RetainedDays.
builder.Services.AddHostedService(sp => new AuditReportRetentionService(
    sp.GetRequiredService<IAuditReportStore>(),
    () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.AuditLog.RetainedDays,
    sp.GetRequiredService<ILogger<AuditReportRetentionService>>()));
builder.Services.AddHostedService(sp => new IdempotencyKeyRetentionService(
    sp.GetRequiredService<IIdempotencyStore>(),
    sp.GetRequiredService<ILogger<IdempotencyKeyRetentionService>>()));
builder.Services.AddHostedService(sp => new AgentUsageRetentionService(
    sp.GetRequiredService<IAgentUsageStore>(),
    () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.AgentBudgets,
    sp.GetRequiredService<ILogger<AgentUsageRetentionService>>()));
builder.Services.AddHostedService(sp => new AgentStreamRetentionService(
    sp.GetRequiredService<IAgentStreamStore>(),
    sp.GetRequiredService<ILogger<AgentStreamRetentionService>>()));
builder.Services.AddSingleton<StreamAnalysisService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamAnalysisService>());
builder.Services.AddHostedService(sp => new BudgetAlertService(
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IWorkItemCostStore>(),
    sp.GetRequiredService<IQueueController>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.BudgetAlerts,
    sp.GetRequiredService<ILogger<BudgetAlertService>>()));

builder.Services.AddSingleton<SandboxLeakReaper>(sp =>
{
    // Live accessor: thresholds and policy fields (LeakAgeThreshold, AutoDispose,
    // MaxConcurrentAutoDispose, PreemptRetention) are re-read on every sweep so
    // operator edits take effect without restart. CheckInterval and Enabled are
    // sampled once at PeriodicTimer construction — limitation documented on the
    // fields themselves.
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    return new SandboxLeakReaper(
        sp.GetRequiredService<ISandboxProvider>(),
        sp.GetRequiredService<IWebhookDispatcher>(),
        () => monitor.CurrentValue.SandboxLeak,
        sp.GetRequiredService<ILogger<SandboxLeakReaper>>(),
        sp.GetRequiredService<IWorkItemStore>(),
        leakSink: sp.GetRequiredService<LeakDetectionSink>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<SandboxLeakReaper>());

// --- B1 baseline-image reaper ------------------------------------------------
// Reference-counted GC for content-hashed Multipass baseline VMs. The reaper
// stays inactive when the registered sandbox provider does not implement
// IBaselineImageResolver — the constructor accepts a null resolver and
// ExecuteAsync short-circuits with an info log. Registered as a singleton so
// the /baselines endpoint can read GetLatestReport() through DI.
builder.Services.AddSingleton<BaselineImageReaper>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    return new BaselineImageReaper(
        sp.GetRequiredService<IBaselineImageResolver>(),
        sp.GetRequiredService<IWorkItemStore>(),
        () => monitor.CurrentValue.BaselineImageReaper,
        sp.GetRequiredService<ILogger<BaselineImageReaper>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<BaselineImageReaper>());

// --- Stale-base PR sweeper ---------------------------------------------------
// Periodically polls open PRs across every github-upstream project, detects
// the ones whose base branch has moved and produced a conflict the auto-merger
// can no longer resolve, and fires the upstream.pr_stale_base webhook event so
// operators see the orphan PR within minutes (5-minute SLA per the bug spec).
// See StalePullRequestSweeper.
builder.Services.AddHostedService(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    return new StalePullRequestSweeper(
        sp.GetRequiredService<IProjectRepository>(),
        sp.GetRequiredService<IUpstreamRemoteFactory>(),
        sp.GetRequiredService<IWebhookDispatcher>(),
        () => monitor.CurrentValue.StalePullRequestSweep,
        sp.GetRequiredService<ILogger<StalePullRequestSweeper>>());
});

// --- Plugin foundation -------------------------------------------------------
// Discovers assemblies from CodeyBox:Plugins, registers plugin types under
// their Core interfaces before the container is frozen, then runs
// IPluginInitializer.InitializeAsync at startup via PluginInitializationService.
// See docs/plugins.md for author guidance, allowlist config, and threat model.
preDiscoveredPlugins = builder.Services.AddCodeyBoxPlugins(builder.Configuration);

var app = builder.Build();

// Force the immutable-options baseline and retaining monitor cache to exist
// before the host starts observing file-change reloads.
_ = app.Services.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;

// Convert WorkItemStoreDiskFullException into a clean 503 instead of letting
// the raw exception escape the HTTP layer. Once SQLite refuses to accept
// writes there is no recovery without operator intervention; returning a
// service-unavailable response is the closest thing to "stop accepting new
// work cleanly" the bug report asked for.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (WorkItemStoreDiskFullException ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CodeyBox.Api.DiskFull");
        logger.LogCritical(ex, "Refusing request {Path}: state store reports disk full ({Operation})",
            ctx.Request.Path, ex.Operation);
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers["Retry-After"] = "300";
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(
            "{\"error\":\"state store full\",\"detail\":\"host disk is exhausted; no further state transitions can be persisted\"}");
    }
});

app.UseApiKeyAuth(anonymousPrefixes: ["/healthz", "/webhooks/"]);

// Idempotency-Key support for mutating endpoints — see IdempotencyMiddleware
// for behaviour. Ordered after auth so unauthenticated requests can't poison
// the cache, and before endpoint mapping so all mutating handlers benefit.
IdempotencyMiddleware.Use(app);

WorkItemEndpoints.Map(app);
WorkItemTimingsEndpoints.Map(app);
WorkItemCostsEndpoints.Map(app);
AgentPricingEndpoints.Map(app);
ProjectBudgetEndpoints.Map(app);
WorkItemDiffEndpoints.Map(app);
SuggestionEndpoints.Map(app);
AuditReportEndpoints.Map(app);
AgentStreamEndpoints.Map(app);
SseEndpoints.Map(app);
ChangelogEndpoints.Map(app);
FleetEndpoints.Map(app);
PluginEndpoints.Map(app);
WorkerRegistryEndpoints.Map(app);
SandboxEndpoints.Map(app);
BaselineEndpoints.Map(app);
QuotaRetryStatusEndpoints.Map(app);
ReleaseEndpoints.Map(app);

app.MapHub<AgentStdoutHub>("/hubs/agent-stdout");

app.MapGet("/quota", async (
    IEnumerable<IAgentQuotaProbe> probes,
    IQuotaFailureStore? failureStore,
    QuotaRouterOptions options,
    IAgentBudgetProvider? budgetProvider,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    IReadOnlyList<QuotaFailureObservation> failures = failureStore is null
        ? Array.Empty<QuotaFailureObservation>()
        : await failureStore.ListRecentAsync(TimeSpan.FromMinutes(60), now, ct);

    var snapshots = new List<object>();
    foreach (var probe in probes.Where(p => p is not PayPerApiQuotaProbe and not NullQuotaProbe))
    {
        var member = new AgentMembership
        {
            Agent = probe.Kind,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };
        var snapshot = await probe.GetAvailabilityAsync(member, ct);
        var recentFailuresForProbe = failures
            .Where(f => f.Agent == probe.Kind && f.ObservedAt >= now - options.ObservedFailureWindow)
            .ToList();
        var recentDefaultFailure = recentFailuresForProbe.Any(f => f.ModelId is null);
        var recentFailure = recentFailuresForProbe.Count > 0;
        var modelKeys = snapshot.PerModel.Keys
            .Concat(recentFailuresForProbe.Where(f => f.ModelId is not null).Select(f => f.ModelId!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        snapshots.Add(new
        {
            agent = probe.Kind.Value,
            latestSnapshot = snapshot,
            observedFailuresLast60m = failures
                .Where(f => f.Agent == probe.Kind)
                .GroupBy(f => new { f.ProjectId, f.ModelId, f.FailureKind })
                .Select(g => new
                {
                    projectId = g.Key.ProjectId?.Value,
                    modelId = g.Key.ModelId,
                    failureKind = g.Key.FailureKind.ToString(),
                    count = g.Count(),
                    latestObservedAt = g.Max(x => x.ObservedAt),
                })
                .ToList(),
            wouldAllow = QuotaRouter.WouldAllow(snapshot.AvailablePct, recentFailure, options),
            defaultModelWouldAllow = QuotaRouter.WouldAllow(snapshot.AvailablePct, recentDefaultFailure, options),
            perModelWouldAllow = modelKeys.ToDictionary(
                modelId => modelId,
                modelId => QuotaRouter.WouldAllow(
                    snapshot.PerModel.TryGetValue(modelId, out var modelQuota) ? modelQuota.AvailablePct : snapshot.AvailablePct,
                    recentFailuresForProbe.Any(f =>
                    f.Agent == probe.Kind &&
                    string.Equals(f.ModelId, modelId, StringComparison.OrdinalIgnoreCase)),
                    options),
                StringComparer.OrdinalIgnoreCase),
        });
    }

    var budgets = new List<object>();
    bool budgetsError = false;
    if (budgetProvider is not null)
    {
        IReadOnlyList<AgentBudgetUsageView> views;
        try
        {
            views = await budgetProvider.SummariseAllAsync(ct);
        }
        catch (Exception ex)
        {
            // Surface the failure to operators polling /quota (e.g. during an
            // incident) rather than masquerading as "no budgets configured".
            loggerFactory.CreateLogger("Quota").LogWarning(
                ex, "Quota: budget summarisation failed; returning budgetsError=true");
            views = Array.Empty<AgentBudgetUsageView>();
            budgetsError = true;
        }
        foreach (var v in views)
        {
            budgets.Add(new
            {
                agent = v.Agent,
                model = v.Model,
                windows = v.Windows.Select(w => new
                {
                    kind = w.Kind,
                    hours = w.Hours,
                    usedCents = w.UsedCents,
                    limitCents = w.LimitCents,
                    percentRemaining = Math.Round(w.PercentRemaining, 2),
                    resetAt = w.ResetAt,
                }).ToList(),
            });
        }
    }

    return Results.Ok(new
    {
        generatedAt = now,
        minQuotaPct = options.MinQuotaPct,
        unknownPolicy = options.UnknownPolicy.ToString(),
        observedFailureWindowMinutes = options.ObservedFailureWindow.TotalMinutes,
        probes = snapshots,
        budgets,
        budgetsError,
        observedFailuresLast60m = failures,
    });
});

app.MapGet("/concurrency", async (
    OrchestratorService orchestrator,
    AgentClassRouter router,
    IAgentBurnEstimator burnEstimator,
    AgentAvailabilityRegistry? availability,
    CancellationToken ct) =>
{
    var state = orchestrator.GetConcurrencyState();

    // Latest avg-burn per agent for every kind appearing in caps or running.
    var allAgents = new HashSet<string>(
        state.PerAgentCaps.Keys
            .Concat(state.CurrentlyRunningPerAgent.Keys),
        StringComparer.OrdinalIgnoreCase);

    var burns = new List<object>(allAgents.Count);
    foreach (var name in allAgents)
    {
        var est = await burnEstimator.GetEstimateAsync(new AgentKind(name), ct);
        burns.Add(new
        {
            agent = name,
            avgBurnPctPerItem = est.AvgBurnPctPerItem,
            sampleCount = est.SampleCount,
        });
    }

    // Per-class rate-aware fit estimates (one entry per subscription member).
    var fits = new List<MemberFitView>();
    foreach (var classId in router.ClassIds)
    {
        var classFits = await router.SummariseFitsAsync(classId, ct);
        fits.AddRange(classFits);
    }

    // Smoke-gate / fast-fail exclusion state, one entry per agent the registry
    // has seen. The /concurrency endpoint is the canonical operator surface for
    // "why isn't this agent picking up work" — exclusion has to live alongside
    // burn / fit so the operator sees the full picture without joining JSON.
    var availabilityView = availability?.Snapshot() ?? Array.Empty<AgentAvailabilitySnapshot>();

    return Results.Ok(new
    {
        globalMaxConcurrent = state.GlobalMaxConcurrent,
        currentlyRunningTotal = state.CurrentlyRunningTotal,
        perAgentCaps = state.PerAgentCaps,
        currentlyRunningPerAgent = state.CurrentlyRunningPerAgent,
        burnEstimates = burns,
        memberFits = fits.Select(f => new
        {
            classId = f.ClassId,
            agent = f.Agent.Value,
            modelId = f.ModelId,
            availablePct = f.AvailablePct,
            avgBurnPctPerItem = f.AvgBurnPctPerItem,
            sampleCount = f.SampleCount,
            fitInWindow = double.IsNaN(f.FitInWindow) ? (double?)null : f.FitInWindow,
            runningOnAgent = f.RunningOnAgent,
        }),
        agentAvailability = availabilityView.Select(s => new
        {
            agent = s.Agent.Value,
            excluded = s.Excluded,
            reason = s.Reason,
            consecutiveFastFails = s.ConsecutiveFastFails,
            lastSmokePassedAt = s.LastSmokePassedAt,
            lastSmokeFailedAt = s.LastSmokeFailedAt,
            lastFastFailAt = s.LastFastFailAt,
        }),
    });
});

// ── Admin: agent availability ─────────────────────────────────────────────
// Operators use these endpoints after correcting a smoke / fast-fail
// exclusion (e.g. installing the missing binary, rotating credentials) to
// either trigger an immediate probe or to clear the fast-fail counter.

app.MapPost("/admin/agent/{name}/smoke", async (
    string name,
    PeriodicSmokeProbeService periodic,
    AgentAvailabilityRegistry registry,
    CancellationToken ct) =>
{
    // Canonical AgentKind values are lowercase ("cursor", "claude", ...) so a
    // capitalised typo (POST /admin/agent/Cursor/smoke) used to return 404
    // even when the underlying probe was registered. Normalise so case
    // never silently shadows the operator's intent.
    var kind = new AgentKind(name.ToLowerInvariant());
    var result = await periodic.ProbeAsync(kind, ct);
    if (result is null)
        return Results.NotFound(new { error = $"no smoke probe registered for agent '{name}'" });
    var availability = registry.GetAvailability(kind);
    return Results.Ok(new
    {
        agent = kind.Value,
        smoke = new
        {
            ok = result.Ok,
            reason = result.FailureReason,
            durationMs = (long)result.Duration.TotalMilliseconds,
        },
        availability = new
        {
            available = availability.Available,
            reason = availability.Reason,
        },
    });
});

app.MapPost("/admin/agent/{name}/reset", (string name, AgentAvailabilityRegistry registry, IAgentRegistry agents) =>
{
    // Mirror /smoke: normalise to lowercase so case-mismatched names match the
    // canonical kinds returned by IAgentRegistry.Available.
    var kind = new AgentKind(name.ToLowerInvariant());
    // Validate the agent is actually registered; without this, a typo
    // (e.g. /admin/agent/curser/reset) silently returns 200 and the operator
    // never realises the call did nothing.
    if (!agents.Available.Contains(kind))
        return Results.NotFound(new { error = $"unknown agent '{name}'" });
    registry.Reset(kind);
    var availability = registry.GetAvailability(kind);
    return Results.Ok(new
    {
        agent = kind.Value,
        availability = new
        {
            available = availability.Available,
            reason = availability.Reason,
        },
    });
});

app.MapGet("/admin/agents/availability", (AgentAvailabilityRegistry registry) =>
{
    return Results.Ok(new
    {
        agents = registry.Snapshot().Select(s => new
        {
            agent = s.Agent.Value,
            excluded = s.Excluded,
            reason = s.Reason,
            consecutiveFastFails = s.ConsecutiveFastFails,
            lastSmokePassedAt = s.LastSmokePassedAt,
            lastSmokeFailedAt = s.LastSmokeFailedAt,
            lastFastFailAt = s.LastFastFailAt,
        }),
    });
});

app.MapGet("/events/schema", () => Results.Ok(EventSchema.GetSchema()));

app.MapGet("/healthz", (ISandboxProvider sandboxes) =>
{
    // Surface free-disk metrics for each path the disk-guard monitors so
    // dashboards can alert before the orchestrator starts deferring or the
    // state store hits SQLITE_FULL. Providers opt in by implementing the
    // IDiskGuardedSandboxProvider capability interface — providers without
    // a disk-guard report an empty list, preserving the previous response
    // shape on a best-effort basis.
    object[] disk = sandboxes is IDiskGuardedSandboxProvider guarded
        ? guarded.SampleDiskGuardState().Select(s => (object)new
        {
            path = s.Path,
            freeBytes = s.FreeBytes,
            thresholdBytes = s.ThresholdBytes,
            belowThreshold = s.FreeBytes is long b && b < s.ThresholdBytes,
        }).ToArray()
        : [];
    return Results.Ok(new { status = "ok", disk });
});

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
    public sealed class MultipassSandboxConfig
    {
        /// <summary>
        /// Number of <c>cloud-init status --wait</c> attempts before falling
        /// back to the Multipass readiness probe for exit 1.
        /// </summary>
        public int CloudInitReadyRetryAttempts { get; set; } =
            MultipassSandboxOptions.DefaultCloudInitReadyRetryAttempts;

        /// <summary>
        /// Deadline for the post-launch poll that waits for the VM to reach
        /// the <c>Running</c> state. Defaults to 3 minutes. Bump on hosts that
        /// observe boot contention under concurrent launches.
        /// </summary>
        public TimeSpan VmStartTimeout { get; set; } =
            MultipassSandboxOptions.DefaultVmStartTimeout;

        /// <summary>
        /// Deadline for the post-stop poll that waits for the VM to reach the
        /// <c>Stopped</c> state. Defaults to 2 minutes.
        /// </summary>
        public TimeSpan VmStopTimeout { get; set; } =
            MultipassSandboxOptions.DefaultVmStopTimeout;

        /// <summary>
        /// Max concurrent multipass launch/start operations.
        /// Independent of worker pool concurrency. Default 2.
        /// </summary>
        public int MaxConcurrentBoots { get; set; } =
            MultipassSandboxOptions.DefaultMaxConcurrentBoots;

        /// <summary>
        /// Optional inter-boot delay in milliseconds. 0 = no delay.
        /// </summary>
        public int BootLaunchDelayMs { get; set; } =
            (int)MultipassSandboxOptions.DefaultBootLaunchDelay.TotalMilliseconds;
    }

    /// <summary>
    /// Top-level options bag bound from the <c>CodeyBox</c> configuration
    /// section. See <c>docs/configuration.md</c> for the full hot-reload
    /// contract per field. Summary of the rule of thumb consumers should
    /// follow when adding new fields:
    /// <list type="bullet">
    /// <item><b>Hot-reloadable</b> fields are read fresh from
    ///   <see cref="IOptionsMonitor{T}"/> on each consumer access (or
    ///   re-applied via the <c>AgentConfigHotReload</c> bridge). Today:
    ///   <c>AgentConcurrency</c>, <c>AgentClasses</c>, <c>AgentScoreModifiers</c>,
    ///   <c>AgentBurnEstimator</c>, <c>AgentPricing</c>, <c>DeadWorker</c>
    ///   (per-sweep), <c>SandboxLeak</c> (thresholds, per-sweep),
    ///   <c>AuditLog.RetainedDays</c> (DB retention, per-sweep), and the
    ///   sandbox launch fields (<c>Multipass*</c>, <c>SandboxNetworkProfiles</c>,
    ///   per-launch).</item>
    /// <item><b>Startup-only and rejected</b> on reload by
    ///   <see cref="ImmutableCodeyBoxOptionsValidator"/>:
    ///   <c>SandboxProvider</c>, <c>StateDatabasePath</c>,
    ///   <c>GitRootDirectory</c>, <c>AgentStreams.Path</c>. The retaining
    ///   options-monitor cache keeps the startup value visible to consumers
    ///   after a rejected reload.</item>
    /// <item><b>Startup-only by capture</b> — bound into a downstream
    ///   singleton (PipelineOptions, OrchestratorOptions, QuotaRouterOptions,
    ///   SmokeOptions, AvailabilityOptions, WebhookEventBroadcaster,
    ///   HttpWebhookDispatcher, ClaudeChangelogGenerator, Serilog sinks,
    ///   etc.) at startup. Edits land in <see cref="IOptionsMonitor{T}.CurrentValue"/>
    ///   but the captured singleton continues to use the prior value until
    ///   restart. Add a hot-reload bridge if/when an operator-facing knob
    ///   should not require restart.</item>
    /// </list>
    /// </summary>
    public sealed class CodeyBoxOptions
    {
        public string GitRootDirectory { get; set; } = "/var/lib/codeybox/repos";
        public string StateDatabasePath { get; set; } = "/var/lib/codeybox/state.db";
        public string SandboxImageReference { get; set; } = "";
        public string[] AgentAllowedHosts { get; set; } = ["api.anthropic.com", "api.openai.com", "api.githubcopilot.com", "generativelanguage.googleapis.com"];
        public string[] AuditToolAllowedHosts { get; set; } =
        [
            "api.nuget.org",
            "www.nuget.org",
            "pypi.org",
            "files.pythonhosted.org",
            "registry.npmjs.org",
            "vuln.go.dev",
            "proxy.golang.org",
            "sum.golang.org",
            "crates.io",
            "index.crates.io",
            "static.crates.io",
            "github.com",
        ];
        /// <summary>
        /// Legacy concurrency knob (deprecated). Used as
        /// <see cref="WorkerPool"/>.<see cref="WorkerPoolOptions.MaxConcurrentWorkers"/>
        /// only when that key is not explicitly set; a deprecation warning is emitted.
        /// When both are set, <see cref="WorkerPool"/>.<see cref="WorkerPoolOptions.MaxConcurrentWorkers"/>
        /// wins. Prefer <c>WorkerPool:MaxConcurrentWorkers</c> for new configuration.
        /// </summary>
        public int? Concurrency { get; set; }

        /// <summary>Worker pool sizing and spawn-pacing configuration.</summary>
        public WorkerPoolOptions WorkerPool { get; set; } = new();

        /// <summary>Per-agent concurrency caps (codex/claude/gemini/...) layered on top of WorkerPool.</summary>
        public AgentConcurrencyOptions AgentConcurrency { get; set; } = new();

        /// <summary>Per-agent burn-rate estimator config (rate-aware dispatch gate).</summary>
        public AgentBurnEstimatorOptions AgentBurnEstimator { get; set; } = new();

        /// <summary>Per-agent/per-model multi-window spend budgets (synthetic quota for the router).</summary>
        public AgentBudgetOptions AgentBudgets { get; set; } = new();

        /// <summary>
        /// Per-agent default model IDs. Keyed by <c>AgentKind.Value</c>
        /// (case-insensitive). The runner uses this value in <c>--model</c>
        /// when no per-item override is provided and the class member does
        /// not carry an explicit <c>ModelId</c>. Edits hot-reload via
        /// <see cref="Core.AgentDefaultsSnapshot"/> and take effect on the
        /// next dispatched agent run.
        /// </summary>
        public Dictionary<string, string?> AgentDefaults { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Graceful shutdown drain and preemption timing.</summary>
        public ShutdownOptions Shutdown { get; set; } = new();

        /// <summary>Heartbeat and dead-worker reaper configuration.</summary>
        public DeadWorkerOptions DeadWorker { get; set; } = new();

        public int UpstreamPushMaxAttempts { get; set; } = 5;
        public int UpstreamPushBackoffSeconds { get; set; } = 15;
        public double PhaseAbsoluteTimeoutMultiplier { get; set; } = 3.0;

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

        /// <summary>Multipass sandbox launch-time readiness tuning.</summary>
        public MultipassSandboxConfig MultipassSandbox { get; set; } = new();

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
        ///   "multi-llm": "cb-multi-llm",
        ///   "graphical": "cb-graphical"
        /// }
        /// </code>
        /// Bridge names are limited to 15 characters by Linux IFNAMSIZ.
        /// Profile names (the keys) have no such limit.
        /// </summary>
        public Dictionary<string, string> SandboxNetworkProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [CodeyBox.Sandbox.SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
        };

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
        /// Disk-guard preflight configuration. Enabled by default
        /// (<see cref="DiskGuardOptions.Enabled"/>=<c>true</c>,
        /// <see cref="DiskGuardOptions.MinFreeBytes"/>=10 GiB); every
        /// <c>MultipassSandboxProvider.CreateAsync</c> call checks free space
        /// on the configured mounts and defers the work item (same machinery
        /// as the budget cap) when any mount is below the threshold. Set
        /// <c>CodeyBox:DiskGuard:Enabled=false</c> to disable.
        /// </summary>
        public DiskGuardOptions DiskGuard { get; set; } = new();

        /// <summary>
        /// Outbound webhook endpoints. Empty list disables webhooks entirely.
        /// Each entry configures one HTTPS target that receives pipeline events.
        /// </summary>
        public List<WebhookEndpointOptions> Webhooks { get; set; } = [];

        /// <summary>
        /// In-process event broadcaster used by the SSE endpoints
        /// (<c>GET /workitems/events</c> and <c>GET /workitems/{id}/events</c>).
        /// Shared with the webhook dispatcher so SSE subscribers and webhook
        /// receivers see the same event surface.
        /// </summary>
        public WebhookEventBusOptions WebhookEventBus { get; set; } = new();

        /// <summary>
        /// Audit log configuration: rolling file paths, retention, and size caps.
        /// </summary>
        public AuditLogOptions AuditLog { get; set; } = new();

        /// <summary>Structured agent stdout stream capture configuration.</summary>
        public AgentStreamsOptions AgentStreams { get; set; } = new();

        /// <summary>Read-only analytics parser configuration for captured agent streams.</summary>
        public AgentStreamParserOptions AgentStreamAnalysis { get; set; } = new();

        /// <summary>
        /// Agent class definitions for quota-aware routing. Each class lists one or
        /// more agent members in preference order. See docs/agent-classes.md.
        /// </summary>
        public List<AgentClassOptions> AgentClasses { get; set; } = [];

        /// <summary>Quota router tuning knobs.</summary>
        public QuotaRouterConfig QuotaRouter { get; set; } = new();

        /// <summary>
        /// Operator-extensible per-agent quota stderr patterns. Keys are agent
        /// kind values (e.g. <c>cursor</c>); each entry adds a substring + kind
        /// to the per-provider detector's built-in defaults. See
        /// docs/quota-gate.md for the schema and supported agent kinds.
        /// </summary>
        public Dictionary<string, List<QuotaFailurePatternOptions>> QuotaFailurePatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>Automatic retry for quota-failed items.</summary>
        public AutoRetryOnQuotaFailureConfig AutoRetryOnQuotaFailure { get; set; } = new();

        /// <summary>OpenTelemetry export configuration. See docs/observability.md.</summary>
        public OtelOptions Otel { get; set; } = new();

        /// <summary>Changelog automation configuration. See docs/changelog-automation.md.</summary>
        public ChangelogOptions Changelog { get; set; } = new();

        /// <summary>
        /// Human/systems notification rules and provider configuration.
        /// Edits hot-reload via IOptionsMonitor — conditions, cooldowns,
        /// and provider routing take effect on the next sweep without restart.
        /// Empty Rules list = notifications disabled.
        /// </summary>
        public NotificationsOptions Notifications { get; set; } = new();

        /// <summary>
        /// Sandbox leak reaper configuration. The reaper periodically scans for
        /// <c>codeybox-*</c> Multipass VMs that outlived their work item and logs
        /// (or optionally auto-disposes) them. See docs/sandbox-leaks.md.
        /// </summary>
        public SandboxLeakOptions SandboxLeak { get; set; } = new();

        /// <summary>
        /// B1 baseline-image reaper configuration. Reference-counted GC for
        /// the content-hashed baseline VMs produced by the Multipass
        /// provider; inactive when the registered sandbox provider does not
        /// implement <see cref="IBaselineImageResolver"/>.
        /// </summary>
        public BaselineImageReaperOptions BaselineImageReaper { get; set; } = new();

        /// <summary>
        /// Stale-base PR sweeper configuration. Detects open CodeyBox-authored
        /// PRs whose base branch has moved and produced a merge conflict the
        /// orchestrator can no longer resolve in-pipeline, and fires the
        /// <c>upstream.pr_stale_base</c> webhook event so operators see the
        /// orphan PR within minutes. See <see cref="StalePullRequestSweeper"/>.
        /// </summary>
        public StalePullRequestSweeperOptions StalePullRequestSweep { get; set; } = new();

        /// <summary>
        /// Startup config-validation knobs. Controls whether AgentClass ModelId
        /// values are cross-checked against the provider's live model list.
        /// </summary>
        public ConfigValidationOptions ConfigValidation { get; set; } = new();

        /// <summary>
        /// Between-iteration incremental rebase toggle. When enabled,
        /// <see cref="CodeyBox.Orchestrator.PipelineRunner"/> runs the
        /// pickup-time rebase flow as best-effort between audit iterations
        /// so the work branch stays close to base BETWEEN reworks (smaller
        /// and rarer merge-time conflicts). Off by default. Hot-reloadable.
        /// </summary>
        public IncrementalRebaseOptions IncrementalRebase { get; set; } = new();
    }

    /// <summary>
    /// Startup config-validation knobs. Bound from <c>CodeyBox:ConfigValidation</c>.
    /// </summary>
    public sealed class ConfigValidationOptions
    {
        /// <summary>
        /// When <c>true</c>, an <see cref="AgentClassOptions"/> member whose
        /// <c>ModelId</c> is not present in the provider's live model list
        /// throws at startup. Default <c>false</c> (warn only) so disconnected
        /// dev/UAT hosts still come up.
        /// </summary>
        public bool FailOnUnknownModel { get; set; } = false;
    }

    public sealed class AutoRetryOnQuotaFailureConfig
    {
        public bool Enabled { get; set; } = false;
        public string PeriodicCheckInterval { get; set; } = "00:05:00";
        public string ClockDriftSafetyMargin { get; set; } = "00:02:00";
        public int MaxAutoRetriesPerWorkItem { get; set; } = 3;
    }

    /// <summary>
    /// Disk-guard preflight configuration. Bound from <c>CodeyBox:DiskGuard</c>.
    /// </summary>
    public sealed class DiskGuardOptions
    {
        /// <summary>
        /// Master switch. Default true so a stock deployment refuses to launch
        /// new sandboxes when the host is out of disk; set false to disable
        /// the preflight entirely (e.g. on a development laptop where the
        /// staging path lives on a small partition).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Minimum free bytes per monitored mount. Below this the provider
        /// throws <c>SandboxDiskDeferredException</c> and the orchestrator
        /// reschedules the pickup. Default 10 GiB.
        /// </summary>
        public long MinFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

        /// <summary>
        /// Path under which Multipass stores VM images. Default matches the
        /// snap install. Override for non-snap installs or custom data
        /// directories.
        /// </summary>
        public string MultipassDataPath { get; set; } = "/var/snap/multipass/common/data";

        /// <summary>
        /// Recheck delay before retrying a deferred work item. Defaults to
        /// 5 minutes. Same form as other TimeSpan options
        /// (<c>hh:mm:ss</c>).
        /// </summary>
        public string RecheckIn { get; set; } = "00:05:00";

        /// <summary>
        /// Extra paths to check in addition to <see cref="MultipassDataPath"/>.
        /// The wiring code automatically adds the state-database directory so
        /// SQLite writes won't be the first thing to ENOSPC on a host whose
        /// /var/lib/codeybox lives on a different volume.
        /// </summary>
        public List<string> AdditionalPaths { get; set; } = [];
    }

    public sealed class ShutdownOptions
    {
        /// <summary>
        /// Maximum time the host waits for in-flight phases to preempt or drain
        /// during SIGTERM/Ctrl-C. Defaults to 60 seconds.
        /// </summary>
        public int GraceSeconds { get; set; } = 60;

        /// <summary>
        /// How to tear down in-flight worker sandboxes during graceful shutdown.
        /// Default <see cref="SandboxTeardownMode.Suspend"/> (original
        /// behaviour: freeze RAM via <c>multipass suspend</c> and resume on
        /// next startup). Operators running stateless workloads that recover
        /// fully from the preempt-checkpoint flow should consider
        /// <see cref="SandboxTeardownMode.Stop"/> or
        /// <see cref="SandboxTeardownMode.Dispose"/> — both avoid the qemu disk-image
        /// write-lock wedge that caused the 2026-05-29 incident, where a
        /// SIGKILL during suspend stranded the orphan qemu processes and
        /// blocked <c>multipass stop</c>/<c>multipass delete --purge</c>.
        /// </summary>
        public SandboxTeardownMode SandboxTeardownMode { get; set; } = SandboxTeardownMode.Suspend;
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

        /// <summary>
        /// Tuning for the availability registry (fast-fail circuit breaker +
        /// periodic smoke probe sweep). Bound from <c>CodeyBox:Smoke:Availability</c>.
        /// </summary>
        public AvailabilityConfig Availability { get; set; } = new();
    }

    /// <summary>Config binding for the availability registry.</summary>
    public sealed class AvailabilityConfig
    {
        /// <summary>Fast-fail threshold in seconds. Default 10.</summary>
        public int FastFailThresholdSeconds { get; set; } = 10;

        /// <summary>Consecutive sub-threshold non-zero exits before excluding. Default 3.</summary>
        public int MaxConsecutiveFastFails { get; set; } = 3;

        /// <summary>Background sweep interval in seconds. Default 300 (5 min); set 0 to disable.</summary>
        public int PeriodicSweepIntervalSeconds { get; set; } = 300;
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
        /// <summary>
        /// Clearance/capability tags this member is trusted to handle, e.g.
        /// <c>["sensitive", "architectural"]</c>. Default empty — members with
        /// no tags can only run work items that require no tags. See
        /// docs/agent-classes.md for the recommended tag vocabulary.
        /// </summary>
        public List<string> Capabilities { get; set; } = [];
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
        /// <summary>How the router treats unknown probe snapshots. Default UseObservedFailures.</summary>
        public QuotaUnknownPolicy UnknownPolicy { get; set; } = QuotaUnknownPolicy.UseObservedFailures;
        /// <summary>Minutes a recent quota-shaped failure blocks the same agent/model. Default 10.</summary>
        public int ObservedFailureWindowMinutes { get; set; } = 10;
        /// <summary>Minutes observed quota failures are retained in state.db. Default 30.</summary>
        public int ObservedFailureRetentionMinutes { get; set; } = 30;
        /// <summary>
        /// Seconds before a cap-spill-deferred work item is reconsidered (every
        /// eligible class member was at its per-agent concurrency cap). Default
        /// 15; the orchestrator's own atomic-reservation defer uses the same
        /// 15s cadence, so leave defaults aligned unless you have a reason.
        /// </summary>
        public int CapRetryIntervalSeconds { get; set; } = 15;
    }

    /// <summary>
    /// One operator-supplied quota-failure pattern entry. Appended to a
    /// detector's built-in defaults; matched against stderr and stdout
    /// substring case-insensitively. Bound from
    /// <c>CodeyBox:QuotaFailurePatterns:&lt;agent-kind&gt;</c>.
    /// </summary>
    public sealed class QuotaFailurePatternOptions
    {
        /// <summary>The substring to search for in stderr/stdout.</summary>
        public string Pattern { get; set; } = string.Empty;
        /// <summary>How to classify the failure when the substring matches.</summary>
        public QuotaFailureKind Kind { get; set; } = QuotaFailureKind.LimitReached;
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
    /// In-process event broadcaster configuration. Drives both the SSE
    /// endpoints' Last-Event-ID replay and the per-work-item ring buffer
    /// size used to feed reconnecting clients.
    /// </summary>
    public sealed class WebhookEventBusOptions
    {
        /// <summary>
        /// Number of recent events retained per work item (and globally)
        /// for SSE Last-Event-ID resume. Older events are evicted FIFO.
        /// </summary>
        public int RingBufferCapacity { get; set; } = 1000;

        /// <summary>
        /// How often the SSE handler emits a ':keepalive' comment when the
        /// stream is otherwise idle. Default 15s — half of AWS ALB's 60s
        /// idle timeout, so proxies don't reap connections that are simply
        /// not transmitting events.
        /// </summary>
        public int HeartbeatSeconds { get; set; } = 15;
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
public partial class Program
{
    /// <summary>
    /// Resolve <c>HostOptions.ShutdownTimeout</c> from operator config and the
    /// resolved provider's suspend capability. Shutdown:GraceSeconds bounds the
    /// normal request-drain / preempt-checkpoint window; a suspend-on-shutdown
    /// provider can legitimately need far longer to let the host finish writing
    /// each VM's RAM snapshot (the RAM-scaled <see cref="SuspendTimeoutPolicy"/>
    /// budget — 30 min for the default 12 GiB VM) and drains in parallel batches,
    /// so a deployment with more in-flight VMs than the batch cap spans
    /// <c>ceil(N/batch)</c> sequential waves. The ceiling must cover the slowest
    /// wave-chain PLUS the post-suspend drain grace (suspend runs in StoppingAsync,
    /// the preempt-checkpoint / listener-drain window runs after), not one VM, or
    /// the host SIGKILLs us mid-snapshot on a later wave or mid-drain.
    /// ShutdownTimeout is a CEILING, not a fixed wait: a shutdown with
    /// nothing to suspend still returns as soon as every hosted service's
    /// StoppingAsync completes, so raising it only affects the suspend case.
    ///
    /// <para>The concurrent-sandbox bound is resolved through
    /// <see cref="OrchestratorOptionsFactory"/> — the same validation/precedence
    /// path the orchestrator pool uses (WorkerPool wins, legacy Concurrency is the
    /// fallback, default 1) — so this ceiling cannot drift below the actual pool
    /// size. All VMs are provisioned at <see cref="SandboxResourceLimits.Default"/>
    /// (no per-VM RAM override is wired through SandboxSpec today), so the default
    /// profile RAM is the largest per-VM suspend budget the host must cover.</para>
    /// </summary>
    internal static TimeSpan ComputeHostShutdownTimeout(
        CodeyBoxOptions cbOpts, bool providerSuspendsOnShutdown, ILogger log)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(1, cbOpts.Shutdown.GraceSeconds));
        var maxConcurrent = OrchestratorOptionsFactory
            .Build(cbOpts.Concurrency, cbOpts.WorkerPool, log)
            .MaxConcurrentWorkers;
        return SuspendTimeoutPolicy.ResolveHostShutdownTimeout(
            providerSuspendsOnShutdown, grace, maxConcurrent);
    }
}
