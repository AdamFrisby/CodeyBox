using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Crock;
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
using CodeyBox.Sandbox.MultipassRemote;
using CodeyBox.Sandbox.Process;
using CodeyBox.Sandbox.Sprites;
using CodeyBox.HostProcess;
using CodeyBox.Webhooks;
using CodeyBox.Notifications;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
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

    // When OTel export is enabled we forward Serilog events to the MEL provider
    // pipeline (the OpenTelemetry logging provider added in the OTel section).
    // UseSerilog(providers:) bridges every registered ILoggerProvider into this
    // collection and the WriteTo.Providers sink fans events out to them, so the
    // existing ILogger call sites flow to OTel with trace correlation while the
    // console / file sinks stay owned by this single logger. Null (OTel off)
    // keeps the original Serilog-only path with zero added overhead.
    var otelLogForwarding = cbConf.Otel.Enabled ? new LoggerProviderCollection() : null;

    var serilogConfig = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "CodeyBox")
        .Enrich.With<SensitiveDataRedactionEnricher>()
        .WriteTo.Console();

    // Rolling plain-text mirror of the console stream. Replaces the
    // historical `>>` shell-redirect of stdout to codeybox-orchestrator.run.log,
    // which grew without bound (22 M+ lines / multi-GB by 2026-06) and made
    // operator tail/grep return weeks-old lines. Bound by both date and size,
    // capped by RetainedFileCountLimit. Disable via
    // CodeyBox:AuditLog:ConsoleLog:Enabled=false if the operator manages
    // run-log capture out of process.
    if (auditOpts.ConsoleLog.Enabled)
    {
        serilogConfig = serilogConfig.WriteTo.File(
            path: auditOpts.ConsoleLog.Path,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: auditOpts.ConsoleLog.RetainedFileCountLimit,
            fileSizeLimitBytes: auditOpts.ConsoleLog.MaxFileSizeBytes,
            rollOnFileSizeLimit: true,
            shared: false);
    }

    serilogConfig = serilogConfig
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
                shared: false));

    if (otelLogForwarding is not null)
        serilogConfig = serilogConfig.WriteTo.Providers(otelLogForwarding);

    Log.Logger = serilogConfig.CreateLogger();

    builder.Host.UseSerilog(Log.Logger, dispose: false, providers: otelLogForwarding);
}

// ── OpenTelemetry ─────────────────────────────────────────────────────────
// Off by default (OtelOptions.Enabled = false). Operators opt in by setting
// CodeyBox:Otel:Enabled=true and CodeyBox:Otel:OtlpEndpoint. When disabled,
// no OTel types are registered — zero overhead in the default configuration.
//
// The Prometheus scrape exporter (CodeyBox:Otel:Prometheus:Enabled) is a
// peer of the OTLP push exporter: enabling either one (or both) registers
// the metric provider, observable gauges, and CodeyBox meters. The
// tracing / logging providers and the OTLP push pipeline activate only
// when CodeyBox:Otel:Enabled=true; Prometheus alone keeps tracing and logs
// on the existing Serilog-only path.
{
    var cbConf = builder.Configuration.GetSection("CodeyBox").Get<CodeyBoxOptions>()
        ?? new CodeyBoxOptions();
    var otelOpts = cbConf.Otel;
    OtelOptions.Validate(otelOpts);

    var prometheusEnabled = otelOpts.Prometheus.Enabled;
    var metricsEnabled = otelOpts.Enabled || prometheusEnabled;

    if (metricsEnabled)
    {
        // service.version defaults to the API assembly version when the operator
        // hasn't pinned a git SHA / release tag.
        var serviceVersion = otelOpts.ServiceVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString();

        // service.name honours the standard OTEL_SERVICE_NAME env var, falling
        // back to the CodeyBox:Otel appsettings value — env wins so the standard
        // OTel bootstrap can retarget identity without editing appsettings.
        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is { Length: > 0 } envServiceName
            ? envServiceName
            : otelOpts.ServiceName;

        // Resource attributes shared by traces, metrics, and logs so the three
        // signals correlate on identical service identity. service.instance.id
        // and deployment.environment are added automatically; appsettings
        // ResourceAttributes are applied, then any OTEL_RESOURCE_ATTRIBUTES env
        // pairs last so the standard env contract overrides appsettings on key
        // collision.
        var instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var deploymentEnv = builder.Environment.EnvironmentName;
        void ConfigureResource(ResourceBuilder r)
        {
            r.AddService(serviceName, serviceVersion: serviceVersion, serviceInstanceId: instanceId);
            if (!string.IsNullOrWhiteSpace(deploymentEnv))
                r.AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", deploymentEnv) });
            if (otelOpts.ResourceAttributes.Count > 0)
                r.AddAttributes(otelOpts.ResourceAttributes.Select(
                    kv => new KeyValuePair<string, object>(kv.Key, kv.Value)));
            var envAttrs = OtelOptions.ParseResourceAttributesEnv(
                Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"));
            if (envAttrs.Count > 0)
                r.AddAttributes(envAttrs);
        }

        var otelBuilder = builder.Services.AddOpenTelemetry()
            .ConfigureResource(ConfigureResource);

        if (otelOpts.Enabled)
        {
            otelBuilder.WithTracing(t => t
                .AddSource("CodeyBox.Pipeline")
                .AddSource("CodeyBox.Sandbox")
                .AddSource("CodeyBox.Upstream")
                .AddSource("CodeyBox.Audit")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => ConfigureOtlp(o, otelOpts)));
        }

        otelBuilder.WithMetrics(m =>
        {
            m.AddMeter("CodeyBox.Pipeline")
             .AddMeter("CodeyBox.Sandbox")
             .AddMeter("CodeyBox.Audit")
             .AddMeter("CodeyBox.Upstream")
             .AddRuntimeInstrumentation();
            if (otelOpts.Enabled)
                m.AddOtlpExporter(o => ConfigureOtlp(o, otelOpts));
            if (prometheusEnabled)
                m.AddPrometheusExporter();
        });

        if (otelOpts.Enabled)
        {
            // Route the existing ILogger output through the OpenTelemetry logging
            // provider. Serilog forwards events here via the LoggerProviderCollection
            // wired above (writeToProviders); LogRecords are stamped with the active
            // Activity's TraceId/SpanId for log↔trace correlation.
            builder.Logging.AddOpenTelemetry(o =>
            {
                o.IncludeScopes = true;
                o.IncludeFormattedMessage = true;
                o.ParseStateValues = true;
                var rb = ResourceBuilder.CreateDefault();
                ConfigureResource(rb);
                o.SetResourceBuilder(rb);
                o.AddOtlpExporter(e => ConfigureOtlp(e, otelOpts));
            });
        }

        // Observable gauges (work items by state, worker pool occupancy, active
        // sandboxes, quota headroom) are registered only when at least one
        // metric exporter is active to preserve the zero-overhead disabled path.
        builder.Services.AddHostedService<CodeyBoxObservableMetrics>();
    }
}

// AddOptions + PostConfigure instead of plain Configure so the AgentClasses
// override resolver runs after the binder. The default ConfigurationBinder
// merges arrays positionally — for AgentClasses that's a silent footgun
// (a shorter operator override exposes the base array's trailing element).
// The post-configure step REPLACES AgentClasses with the highest-precedence
// provider's view, and re-runs on every IOptionsMonitor reload.
builder.Services.AddOptions<CodeyBoxOptions>()
    .Bind(builder.Configuration.GetSection("CodeyBox"))
    .PostConfigure(opts => AgentClassesOverrideResolver.ApplyTo(opts, builder.Configuration));
builder.Services.AddSingleton(sp => new SqliteDatabaseWriteGateFactory(
    () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.SqliteWriteGate,
    sp.GetRequiredService<ILoggerFactory>(),
    TimeProvider.System));
builder.Services.Configure<BuildScriptAuditorOptions>(builder.Configuration.GetSection("CodeyBox:BuildScriptAudit"));
builder.Services.Configure<NotificationsOptions>(builder.Configuration.GetSection("CodeyBox:Notifications"));
// E2eExecutionOptions binds as a standalone section so the pool / dispatcher can
// take IOptionsMonitor<E2eExecutionOptions> directly without dragging the whole
// CodeyBoxOptions graph. The same section is also a property on CodeyBoxOptions
// (for the unbound-key validator's walk and operator visibility); the standalone
// binding is what gets hot-reloaded into the pool's MaxConcurrent.
builder.Services.AddOptions<E2eExecutionOptions>()
    .Bind(builder.Configuration.GetSection("CodeyBox:E2eExecution"))
    .Validate(static opts => IsValidE2eExecutionOptions(opts), "CodeyBox:E2eExecution is invalid")
    .Validate(opts => IsValidE2eExecutionOptionsForConfig(opts, builder.Configuration), "CodeyBox:E2eExecution remote pool prerequisites are invalid");
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
//
// AgentClassesOverrideResolver runs here AS WELL AS in the IOptions PostConfigure
// pipeline: this snapshot pre-seeds the RetainingOptionsMonitorCache below, and
// stock IOptionsMonitor returns that cached value without running the options
// factory, so without this call IOptionsMonitor<CodeyBoxOptions>.CurrentValue
// would observe the raw positional-merge AgentClasses until the first reload.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var snapshot = config.GetSection("CodeyBox").Get<CodeyBoxOptions>() ?? new CodeyBoxOptions();
    AgentClassesOverrideResolver.ApplyTo(snapshot, config);
    AgentFailureClassifier.SetAdditionalTransientNetworkPatterns(snapshot.TransientNetworkFailurePatterns);
    return new CodeyBoxOptionsStartupSnapshot(snapshot);
});
builder.Services.AddSingleton<IOptionsMonitorCache<CodeyBoxOptions>>(
    sp => new RetainingOptionsMonitorCache<CodeyBoxOptions>(
        sp.GetRequiredService<CodeyBoxOptionsStartupSnapshot>().Value,
        opts => AgentFailureClassifier.SetAdditionalTransientNetworkPatterns(opts.TransientNetworkFailurePatterns)));
builder.Services.TryAddSingleton<IProcessRunner, DefaultProcessRunner>();
builder.Services.TryAddSingleton<IOpenSshConfigResolver, OpenSshConfigResolver>();
builder.Services.TryAddSingleton<E2eRemoteHostValidation>();
builder.Services.TryAddSingleton<E2eRemotePoolConfigValidation>();
builder.Services.AddSingleton<IValidateOptions<CodeyBoxOptions>>(
    sp => new ImmutableCodeyBoxOptionsValidator(
        sp.GetRequiredService<CodeyBoxOptionsStartupSnapshot>().Value));
builder.Services.AddSingleton<IValidateOptions<CodeyBoxOptions>>(
    sp => new CodeyBoxOptionsValidator(sp.GetRequiredService<E2eRemotePoolConfigValidation>()));

// Unbound-key startup check. Walks the CodeyBox:* configuration sub-tree
// and surfaces any key that does not bind to a property on the typed
// options graph (the .NET binder silently drops these, which makes a
// misspelled or renamed key a no-op the operator never notices). Default
// behaviour is fail-fast at host start; switch to warn-only via
// CodeyBox:ConfigValidation:UnboundKeys:Mode="warn".
builder.Services.AddHostedService<UnboundConfigKeyHostedValidator>();

// Rejects ProjectsOptions reloads that remove a project still holding
// non-terminal work items. Adding new projects passes cleanly.
builder.Services.AddSingleton<IValidateOptions<ProjectsOptions>, ProjectsOptionsRemovalValidator>();

// Sized from the resolved sandbox provider's capability, not the provider config
// name. SandboxTeardownMode is intentionally hot-reloadable at shutdown time, but
// HostOptions is captured at startup, so suspend-capable providers keep the
// RAM-snapshot ceiling even when startup config says Stop/Dispose. It is only a
// ceiling: shutdown still returns promptly when no suspend work is running.
// Using the DI-resolved provider keeps the deployment knowledge (name → provider)
// in the composition root and out of the Core policy. See ComputeHostShutdownTimeout.
builder.Services.AddOptions<HostOptions>()
    .Configure<IOptions<CodeyBoxOptions>, ISandboxProvider, ILoggerFactory>(
        (o, cbOptsAccessor, sandboxProvider, loggerFactory) =>
        {
            var providerSupportsSuspend = sandboxProvider is ISuspendingSandboxProvider;
            o.ShutdownTimeout = Program.ComputeHostShutdownTimeout(
                cbOptsAccessor.Value,
                providerSupportsSuspend,
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
//   sprites     — Fly.io hosted Firecracker microVMs via sprites.dev. Requires
//                 SPRITES_TOKEN (or configured token env var).
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
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<ISandboxProvider>() as IBaselineImageProvisioner
        ?? NullBaselineImageProvisioner.Instance);

static void ConfigureOtlp(OtlpExporterOptions o, OtelOptions opts)
{
    // Honour the standard OTel env contract: when OTEL_EXPORTER_OTLP_ENDPOINT /
    // OTEL_EXPORTER_OTLP_HEADERS are set we leave the exporter's SDK defaults in
    // place (the SDK reads those vars itself, including the http path-append
    // semantics), so env overrides appsettings. We only assign from
    // CodeyBox:Otel when the corresponding env var is absent.
    var envEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    if (string.IsNullOrWhiteSpace(envEndpoint) && !string.IsNullOrWhiteSpace(opts.OtlpEndpoint))
        o.Endpoint = new Uri(opts.OtlpEndpoint);

    // OTEL_EXPORTER_OTLP_PROTOCOL is part of the same env contract: when it is
    // set the SDK reads it itself, so forcing the appsettings protocol here would
    // silently override an env-only deployment (e.g. http/protobuf on :4318 while
    // appsettings still defaults to grpc). Only assign from CodeyBox:Otel when the
    // env var is absent.
    var envProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
    if (string.IsNullOrWhiteSpace(envProtocol))
        o.Protocol = opts.ExportProtocol == "httpprotobuf"
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

    var envHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
    if (string.IsNullOrWhiteSpace(envHeaders) && !string.IsNullOrEmpty(opts.OtlpHeaders))
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
                "Choose one of: multipass, multipass-remote, sprites, bubblewrap, process " +
                "(see docs/sandbox-providers.md for trade-offs).");
        }
    }

    var inner = BuildSandboxProviderInner(sp, opts, environment, startupLog, loggerFactory, kind);
    var orchestratorOptions = sp.GetRequiredService<OrchestratorOptions>();
    startupLog.LogInformation(
        "Sandbox admission control: provider={Provider}, MaxConcurrentSandboxes={MaxConcurrentSandboxes}",
        inner.Name,
        orchestratorOptions.MaxConcurrentSandboxes);
    return SandboxAdmissionControlledProvider.Wrap(
        inner,
        orchestratorOptions.MaxConcurrentSandboxes,
        loggerFactory.CreateLogger<SandboxAdmissionControlledProvider>());
}

static ISandboxProvider BuildSandboxProviderInner(
    IServiceProvider sp,
    CodeyBoxOptions opts,
    IHostEnvironment environment,
    ILogger startupLog,
    ILoggerFactory loggerFactory,
    string kind)
{
    return kind switch
    {
        "process" => BuildProcess(opts, environment, startupLog, loggerFactory),
        "bubblewrap" => new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions(),
            loggerFactory.CreateLogger<BubblewrapSandboxProvider>(),
            sp.GetService<ITimingStore>()),
        "multipass" => BuildMultipass(
            opts,
            sp,
            loggerFactory,
            startupLog,
            sp.GetService<ITimingStore>(),
            sp.GetService<ISandboxResourceUsageStore>()),
        "multipass-remote" => BuildMultipassRemote(sp, loggerFactory),
        "sprites" => BuildSprites(sp, loggerFactory, startupLog),
        _ => throw new InvalidOperationException(
            $"Unknown CodeyBox:SandboxProvider '{kind}'. Valid: multipass, multipass-remote, sprites, bubblewrap, process"),
    };
}

static IE2eExecutionPool BuildE2eExecutionPool(IServiceProvider sp)
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var e2eOptions = sp.GetRequiredService<IOptionsMonitor<E2eExecutionOptions>>();
    var poolKind = (e2eOptions.CurrentValue.PoolKind ?? "remote-ssh").Trim().ToLowerInvariant();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var startupOptions = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;
    if (poolKind == "local" && !environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "CodeyBox:E2eExecution:PoolKind=local is development-only. Use remote-ssh for production E2E replay execution.");
    }

    if (poolKind == "remote-ssh" && e2eOptions.CurrentValue.Enabled)
    {
        ValidateEnabledRemoteE2eConfig(
            e2eOptions.CurrentValue,
            startupOptions,
            sp.GetRequiredService<E2eRemotePoolConfigValidation>());
    }

    if (poolKind == "remote-ssh")
    {
        return BuildRemoteE2eExecutionPool(sp, loggerFactory, e2eOptions);
    }

    ISandboxProvider provider = poolKind switch
    {
        "local" => BuildE2eLocalSandboxProvider(sp, loggerFactory),
        _ => throw new InvalidOperationException(
            $"Unknown CodeyBox:E2eExecution:PoolKind '{e2eOptions.CurrentValue.PoolKind}'. Valid: local, remote-ssh"),
    };

    return new LocalE2eExecutionPool(
        provider,
        e2eOptions,
        loggerFactory.CreateLogger<LocalE2eExecutionPool>(),
        fallbackImageReference: () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.SandboxImageReference,
        name: poolKind);
}

static IE2eExecutionPool BuildRemoteE2eExecutionPool(
    IServiceProvider sp,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<E2eExecutionOptions> e2eOptions)
{
    var currentOptions = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;
    var hostConfigs = GetE2eRemoteHostConfigs(currentOptions);
    if (hostConfigs.Count == 0)
    {
        var provider = BuildE2eMultipassRemote(sp, loggerFactory, hostIndex: 0);
        return new LocalE2eExecutionPool(
            provider,
            e2eOptions,
            loggerFactory.CreateLogger<LocalE2eExecutionPool>(),
            fallbackImageReference: () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.SandboxImageReference,
            name: "remote-ssh");
    }

    var hosts = hostConfigs
        .Select((cfg, index) => new E2eExecutionHost(
            string.IsNullOrWhiteSpace(cfg.RemoteSandbox.SshTarget) ? $"remote-ssh:{index}" : cfg.RemoteSandbox.SshTarget!,
            BuildE2eMultipassRemote(sp, loggerFactory, index),
            cfg.MaxConcurrent ?? 1))
        .ToArray();

    return new MultiHostE2eExecutionPool(
        hosts,
        e2eOptions,
        loggerFactory.CreateLogger<MultiHostE2eExecutionPool>(),
        fallbackImageReference: () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.SandboxImageReference);
}

static CompositeManagedSandboxProvider BuildManagedSandboxLifecycleProvider(IServiceProvider sp)
{
    var providers = new List<IManagedSandboxLifecycle> { sp.GetRequiredService<ISandboxProvider>() };
    if (sp.GetRequiredService<IE2eExecutionPool>() is IManagedSandboxProviderSource source)
    {
        providers.AddRange(source.ManagedSandboxProviders);
    }
    return new CompositeManagedSandboxProvider(providers);
}

static bool IsValidE2eExecutionOptions(E2eExecutionOptions opts)
{
    if (opts.MaxConcurrent is < E2eExecutionOptions.MinimumMaxConcurrent or > E2eExecutionOptions.MaximumMaxConcurrent)
        return false;
    if (opts.PollInterval < TimeSpan.Zero || opts.PerRunTimeout <= TimeSpan.Zero)
        return false;
    if (!string.Equals(opts.PoolKind, "local", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(opts.PoolKind, "remote-ssh", StringComparison.OrdinalIgnoreCase))
        return false;
    if (opts.AllowedReadinessOrigins.Count == 0)
        return false;
    foreach (var origin in opts.AllowedReadinessOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
    }

    return true;
}

static bool IsValidE2eExecutionOptionsForConfig(E2eExecutionOptions opts, IConfiguration config)
{
    if (!opts.Enabled || !string.Equals(opts.PoolKind, "remote-ssh", StringComparison.OrdinalIgnoreCase))
        return true;

    var cb = config.GetSection("CodeyBox").Get<CodeyBoxOptions>() ?? new CodeyBoxOptions();
    return TryValidateEnabledRemoteE2eConfig(opts, cb, out _);
}

static void ValidateEnabledRemoteE2eConfig(
    E2eExecutionOptions e2e,
    CodeyBoxOptions options,
    E2eRemotePoolConfigValidation? validator = null)
{
    if (!TryValidateEnabledRemoteE2eConfig(e2e, options, out var message, validator))
        throw new InvalidOperationException(message);
}

static bool TryValidateEnabledRemoteE2eConfig(
    E2eExecutionOptions e2e,
    CodeyBoxOptions options,
    out string message,
    E2eRemotePoolConfigValidation? validator = null)
{
    var failures = (validator ?? E2eRemotePoolConfigValidation.Default).ValidateEnabledRemoteE2eConfig(e2e, options);
    if (failures.Count > 0)
    {
        message = string.Join("; ", failures);
        return false;
    }
    message = string.Empty;
    return true;
}

static IReadOnlyList<E2eMultipassRemoteHostConfig> GetE2eRemoteHostConfigs(CodeyBoxOptions options)
{
    if (options.E2eMultipassRemoteSandboxes is { Count: > 0 } hosts)
        return hosts;
    return options.E2eMultipassRemoteSandbox is null
        ? []
        : [options.E2eMultipassRemoteSandbox];
}

static ISandboxProvider BuildE2eLocalSandboxProvider(IServiceProvider sp, ILoggerFactory loggerFactory)
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var startupLog = loggerFactory.CreateLogger("CodeyBox.E2eSandbox");
    if (!environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "CodeyBox:E2eExecution:PoolKind=local is only available in Development.");
    }

    var kind = (opts.SandboxProvider ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(kind))
    {
        if (environment.IsDevelopment())
        {
            startupLog.LogWarning(
                "CodeyBox:E2eExecution:PoolKind=local with CodeyBox:SandboxProvider unset; defaulting E2E provider to 'process' because environment is Development.");
            kind = "process";
        }
        else
        {
            throw new InvalidOperationException(
                "CodeyBox:SandboxProvider must be set when CodeyBox:E2eExecution:PoolKind=local in non-Development environments.");
        }
    }

    return BuildSandboxProviderInner(sp, opts, environment, startupLog, loggerFactory, kind);
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

static MultipassSandboxProvider BuildMultipass(
    CodeyBoxOptions opts,
    IServiceProvider sp,
    ILoggerFactory loggerFactory,
    ILogger startupLog,
    ITimingStore? timings,
    ISandboxResourceUsageStore? resourceUsageStore)
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
            var projects = sp.GetRequiredService<IOptionsMonitor<ProjectsOptions>>().CurrentValue;
            var multipassSandbox = live.MultipassSandbox ?? new MultipassSandboxConfig();
            // Post-bake binary verification is only meaningful on the baseline-
            // clone path. When baseline images are disabled the per-launch
            // cloud-init flow exists no baseline to verify, so skip the build
            // entirely — both to avoid unnecessary work and to keep ordinary
            // Multipass provisioning insulated from any future bake-only fault
            // in the builder.
            //
            // Smoke options are intentionally NOT passed here: the bake gate
            // verifies durable image integrity and must run regardless of the
            // runtime smoke toggles (CodeyBox:Smoke:Enabled,
            // CodeyBox:Smoke:InVm:Enabled), which only govern dispatch-time
            // routing. The exempt-list flows through InVmSmokeOptions because
            // that is its existing configuration home; the builder reads only
            // ExemptAgentsWithoutProbe from it.
            var baselineVerificationCommands = live.MultipassUseBaselineImages
                ? BaselineVerificationProbeBuilder.Build(
                    live,
                    projects,
                    sp.GetServices<IInVmSmokeProbe>(),
                    sp.GetService<InVmSmokeOptions>())
                : Array.Empty<MultipassBaselineVerificationCommand>();
            return new MultipassSandboxOptions
            {
                ExtraCloudInit = live.MultipassExtraCloudInit,
                ExtraRuncmd = live.MultipassExtraRuncmd,
                BaselineVerificationCommands = baselineVerificationCommands,
                NetworkProfiles = live.SandboxNetworkProfiles,
                UseBaselineImages = live.MultipassUseBaselineImages,
                CloudInitReadyRetryAttempts = multipassSandbox.CloudInitReadyRetryAttempts,
                VmStartTimeout = multipassSandbox.VmStartTimeout,
                VmStopTimeout = multipassSandbox.VmStopTimeout,
                MaxConcurrentBoots = multipassSandbox.MaxConcurrentBoots,
                BootLaunchDelay = TimeSpan.FromMilliseconds(multipassSandbox.BootLaunchDelayMs),
                DisableAgentOutputHttpIngest = multipassSandbox.DisableAgentOutputHttpIngest,
                CaptureResourceMetrics = multipassSandbox.CaptureResourceMetrics,
                ResourceMetricsCaptureTimeout = multipassSandbox.ResourceMetricsCaptureTimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(multipassSandbox.ResourceMetricsCaptureTimeoutSeconds)
                    : MultipassSandboxOptions.DefaultResourceMetricsCaptureTimeout,
                DiskGuard = diskGuard,
                PackageCacheSeeds = live.MultipassPackageCacheSeeds?.Select(s => new PackageCacheSeedOptions
                {
                    HostSourcePath = s.HostSourcePath,
                    VmDestPath = s.VmDestPath,
                    MaxSizeMB = s.MaxSizeMB
                }).ToList() ?? [],
                ExecutableProvisions = live.MultipassExecutableProvisions?.Select(e => new ExecutableProvisionOptions
                {
                    HostSourcePath = e.HostSourcePath,
                    VmDestPath = e.VmDestPath,
                    VmSymlinks = e.VmSymlinks?.ToList() ?? [],
                    Label = e.Label,
                }).ToList() ?? [],
            };
        },
        loggerFactory.CreateLogger<MultipassSandboxProvider>(),
        timings,
        resourceUsageStore);

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

static MultipassRemoteSandboxProvider BuildMultipassRemote(IServiceProvider sp, ILoggerFactory loggerFactory)
    => BuildMultipassRemoteFromConfig(
        sp,
        loggerFactory,
        live => live.MultipassRemoteSandbox);

static MultipassRemoteSandboxProvider BuildE2eMultipassRemote(IServiceProvider sp, ILoggerFactory loggerFactory, int hostIndex)
    => BuildMultipassRemoteFromConfig(
        sp,
        loggerFactory,
        live =>
        {
            var hosts = GetE2eRemoteHostConfigs(live);
            return hostIndex >= 0 && hostIndex < hosts.Count ? hosts[hostIndex].RemoteSandbox : null;
        });

static MultipassRemoteSandboxProvider BuildMultipassRemoteFromConfig(
    IServiceProvider sp,
    ILoggerFactory loggerFactory,
    Func<CodeyBoxOptions, MultipassRemoteSandboxConfig?> configSelector)
{
    // All options resolved through IOptionsMonitor so SSH endpoint, key path,
    // staging dir, and timeouts hot-reload on the next CreateAsync without an
    // orchestrator restart. The OpenSSH-CLI transport is constructed once and
    // re-reads options the same way for every call.
    var transportLogger = loggerFactory.CreateLogger<OpenSshCliTransport>();
    var runner = sp.GetService<IProcessRunner>() ?? new DefaultProcessRunner();
    var transport = new OpenSshCliTransport(
        () => ReadRemoteOpts(sp, configSelector),
        runner,
        transportLogger);
    return new MultipassRemoteSandboxProvider(
        () => ReadRemoteOpts(sp, configSelector),
        transport,
        loggerFactory.CreateLogger<MultipassRemoteSandboxProvider>());

    static MultipassRemoteSandboxOptions ReadRemoteOpts(
        IServiceProvider sp,
        Func<CodeyBoxOptions, MultipassRemoteSandboxConfig?> configSelector)
    {
        var live = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;
        var cfg = configSelector(live) ?? new MultipassRemoteSandboxConfig();
        var fromDefaults = new MultipassRemoteSandboxOptions();
        return new MultipassRemoteSandboxOptions
        {
            SshBinary = !string.IsNullOrWhiteSpace(cfg.SshBinary) ? cfg.SshBinary! : fromDefaults.SshBinary,
            SshTarget = cfg.SshTarget ?? "",
            SshPort = cfg.SshPort,
            SshKeyPath = cfg.SshKeyPath,
            ExtraSshOptions = cfg.ExtraSshOptions?.ToArray() ?? [],
            AcceptUnknownHostKeys = cfg.AcceptUnknownHostKeys,
            ServerAliveIntervalSeconds = cfg.ServerAliveIntervalSeconds ?? fromDefaults.ServerAliveIntervalSeconds,
            ServerAliveCountMax = cfg.ServerAliveCountMax ?? fromDefaults.ServerAliveCountMax,
            ConnectTimeoutSeconds = cfg.ConnectTimeoutSeconds ?? fromDefaults.ConnectTimeoutSeconds,
            LocalTarBinary = !string.IsNullOrWhiteSpace(cfg.LocalTarBinary) ? cfg.LocalTarBinary! : fromDefaults.LocalTarBinary,
            RemoteMultipassPath = !string.IsNullOrWhiteSpace(cfg.RemoteMultipassPath) ? cfg.RemoteMultipassPath! : fromDefaults.RemoteMultipassPath,
            RemoteStagingRoot = !string.IsNullOrWhiteSpace(cfg.RemoteStagingRoot) ? cfg.RemoteStagingRoot! : fromDefaults.RemoteStagingRoot,
            DefaultImage = cfg.DefaultImage,
            VmStartTimeout = cfg.VmStartTimeout ?? fromDefaults.VmStartTimeout,
            VmStopTimeout = cfg.VmStopTimeout ?? fromDefaults.VmStopTimeout,
            VmStateCheckInterval = cfg.VmStateCheckInterval ?? fromDefaults.VmStateCheckInterval,
            VmNamePrefix = !string.IsNullOrWhiteSpace(cfg.VmNamePrefix) ? cfg.VmNamePrefix! : fromDefaults.VmNamePrefix,
        };
    }
}

static SpritesSandboxProvider BuildSprites(IServiceProvider sp, ILoggerFactory loggerFactory, ILogger startupLog)
{
    startupLog.LogInformation(
        "Using sprites.dev sandbox provider; host mounts are staged through the Sprites API.");
    return new SpritesSandboxProvider(
        () =>
        {
            var live = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue;
            var cfg = live.Sprites ?? new SpritesSandboxConfig();
            var defaults = new SpritesSandboxOptions();
            return new SpritesSandboxOptions
            {
                ApiBaseUrl = !string.IsNullOrWhiteSpace(cfg.ApiBaseUrl) ? cfg.ApiBaseUrl : defaults.ApiBaseUrl,
                TokenEnvironmentVariable = !string.IsNullOrWhiteSpace(cfg.TokenEnvironmentVariable)
                    ? cfg.TokenEnvironmentVariable
                    : defaults.TokenEnvironmentVariable,
                NamePrefix = !string.IsNullOrWhiteSpace(cfg.NamePrefix) ? cfg.NamePrefix : defaults.NamePrefix,
                WaitForCapacity = cfg.WaitForCapacity,
                UrlAuth = !string.IsNullOrWhiteSpace(cfg.UrlAuth) ? cfg.UrlAuth : defaults.UrlAuth,
                MaxListPages = cfg.MaxListPages > 0 ? cfg.MaxListPages : defaults.MaxListPages,
                AllowUnsafeHttp = cfg.AllowUnsafeHttp,
                AllowPersistentTmpfsDowngrade = cfg.AllowPersistentTmpfsDowngrade,
                SetupCommands = cfg.SetupCommands ?? defaults.SetupCommands,
                NetworkProfiles = CopySpritesNetworkProfiles(cfg.NetworkProfiles),
                MaxSyncArchiveBase64Bytes = cfg.MaxSyncArchiveBase64Bytes > 0
                    ? cfg.MaxSyncArchiveBase64Bytes
                    : defaults.MaxSyncArchiveBase64Bytes,
                MaxSyncArchiveBytes = cfg.MaxSyncArchiveBytes > 0
                    ? cfg.MaxSyncArchiveBytes
                    : defaults.MaxSyncArchiveBytes,
                MaxSyncArchiveExpandedBytes = cfg.MaxSyncArchiveExpandedBytes > 0
                    ? cfg.MaxSyncArchiveExpandedBytes
                    : defaults.MaxSyncArchiveExpandedBytes,
                MaxSyncArchiveEntries = cfg.MaxSyncArchiveEntries > 0
                    ? cfg.MaxSyncArchiveEntries
                    : defaults.MaxSyncArchiveEntries,
                MaxFileSyncBase64Bytes = cfg.MaxFileSyncBase64Bytes > 0
                    ? cfg.MaxFileSyncBase64Bytes
                    : defaults.MaxFileSyncBase64Bytes,
                MaxFileSyncBytes = cfg.MaxFileSyncBytes > 0
                    ? cfg.MaxFileSyncBytes
                    : defaults.MaxFileSyncBytes,
                DefaultCpuCount = cfg.DefaultCpuCount,
                DefaultMemoryBytes = cfg.DefaultMemoryBytes,
                Region = cfg.Region,
            };
        },
        loggerFactory.CreateLogger<SpritesSandboxProvider>());

    static Dictionary<string, List<string>> CopySpritesNetworkProfiles(Dictionary<string, List<string>>? source)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
            return result;

        foreach (var (name, hosts) in source)
            result[name] = hosts?.ToList() ?? [];
        return result;
    }
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
        new LocalGitHostOptions
        {
            RootDirectory = opts.GitRootDirectory,
            EnableSharedUpstreamMirror = opts.EnableSharedUpstreamMirror,
            SharedUpstreamMirrorDirectory = opts.SharedUpstreamMirrorDirectory
        },
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
builder.Services.AddSingleton<IRequiredBuildVerifier>(sp => new SandboxRequiredBuildVerifier(
    sp.GetRequiredService<ISandboxProvider>(),
    sp.GetRequiredService<IGitHost>(),
    sp.GetRequiredService<PipelineOptions>()));

// --- Pull request service (in-memory by default) -----------------------------
builder.Services.AddSingleton<IPullRequestService, InMemoryPullRequestService>();

// --- Agents ------------------------------------------------------------------
builder.Services.AddSingleton<IAgentRunner>(sp => new ClaudeAgentRunner(
    sp.GetRequiredService<AgentDefaultsSnapshot>(),
    sp.GetRequiredService<IClaudeTokenRotationPusher>(),
    sp.GetRequiredService<ClaudeThinkingBlockSanitizerConfig>(),
    sp.GetRequiredService<CodeyBox.Core.AgentNetworkToleranceSnapshot>(),
    sp.GetRequiredService<IQuotaFailureClassifier>()));
builder.Services.AddSingleton<IAgentRunner, CopilotAgentRunner>();
builder.Services.AddSingleton<IAgentRunner>(sp => new CodexAgentRunner(
    sp.GetRequiredService<AgentDefaultsSnapshot>(),
    sp.GetRequiredService<CodeyBox.Core.AgentNetworkToleranceSnapshot>(),
    sp.GetRequiredService<IQuotaFailureClassifier>()));
builder.Services.AddSingleton<IAgentRunner, GeminiAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, CursorAgentRunner>();
builder.Services.AddSingleton<IAgentRunner, OpencodeAgentRunner>();
builder.Services.AddSingleton<IAgentRunner>(_ => new AntigravityAgentRunner
{
    // agy's built-in --print-timeout default (5m) aborts a one-shot session with
    // "timed out waiting for response" and zero changes the first time a single
    // gemini turn on a large work item exceeds it. Override with a generous,
    // operator-tunable budget. CodeyBox:Antigravity:PrintTimeoutMinutes (default 20).
    PrintTimeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<int?>("CodeyBox:Antigravity:PrintTimeoutMinutes") ?? 20),
});
// Crock: scaffolded and registered, but DISABLED in shipped agent-class config.
// Operators opt in by adding `crock` to an AgentClass member list once the
// dependent follow-up (cost/usage accounting, watchdog accommodation,
// credential/tunnel provisioning) lands. See docs/agents.md (TBD section).
builder.Services.AddSingleton<IAgentRunner, CrockAgentRunner>();
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
builder.Services.AddOptions<AgentPromptPreprocessingOptions>()
    .Bind(builder.Configuration.GetSection("CodeyBox:PromptPreprocessing"));
builder.Services.AddSingleton<IAgentPromptPreprocessor, ProjectRulesPromptPreprocessor>();
// Attachment support is API/storage-only in this foundation task. The
// reserved preprocessor remains no-op until a future in-VM delivery task
// defines a safe, non-prompt-injection delivery contract.
builder.Services.AddSingleton<IAgentPromptPreprocessor, AttachmentManifestPromptPreprocessor>();
// Cross-agent handoff brief injection: fires only when the involvement store
// shows a prior phase ran under a different AgentKind. Gated end-to-end by
// CodeyBox:PipelineTuning:EnableHandoffSeeding (default off) — the builder
// returns null when the flag is unset so the preprocessor stays a no-op.
builder.Services.AddSingleton<ICrossAgentHandoffBriefBuilder, AgentStreamBriefBuilder>();
builder.Services.AddSingleton<IAgentPromptPreprocessor, CrossAgentHandoffPromptPreprocessor>();

// --- Knob framework ----------------------------------------------------------
// Add a new tuning knob by registering its IKnob implementation here — the
// registry exposes it to the API for set/validate and the work-prompt
// preprocessor picks up its fragment without further edits.
builder.Services.AddSingleton<IKnob, CodeyBox.Orchestrator.Knobs.ChangeScopeKnob>();
builder.Services.AddSingleton<IKnob, CodeyBox.Orchestrator.Knobs.PlanKnob>();
builder.Services.AddSingleton<IKnobRegistry, KnobRegistry>();
builder.Services.AddSingleton<IMergeScopeResolver, CodeyBox.Orchestrator.Knobs.ChangeScopeMergeScopeResolver>();
builder.Services.AddSingleton<IAgentPromptPreprocessor, CodeyBox.Orchestrator.Knobs.KnobWorkPromptPreprocessor>();

builder.Services.AddSingleton<AgentPromptPreprocessorChain>();

// ClaudeSessionWorker — resumable Claude runner. Registered alongside (NOT
// instead of) the one-shot ClaudeAgentRunner so the default dispatch path is
// unchanged; config-gated opt-in callers resolve the concrete type to drive
// multi-turn sessions across a stop/resume-able VM. The options snapshot is a
// singleton so a hot reload can flip Enabled mid-process without restart.
// Mutable singleton so the IOptionsMonitor.OnChange handler below can flip
// Transport / Enabled / EmitTurnMetrics in place; the worker reads the live
// value on every turn so the flip applies to the NEXT dispatch.
builder.Services.AddSingleton<CodeyBox.Agents.Claude.ClaudeSessionWorkerOptions>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    var live = new CodeyBox.Agents.Claude.ClaudeSessionWorkerOptions();
    ClaudeSessionOptionsBinder.Apply(live, monitor.CurrentValue.ClaudeSession);
    monitor.OnChange(opts => ClaudeSessionOptionsBinder.Apply(live, opts.ClaudeSession));
    return live;
});
// Orchestration-side dispatch gate. PipelineRunner takes a provider-agnostic
// AgentSessionDispatchOptions so the orchestration boundary doesn't depend on
// any per-provider options shape; the composition root maps the per-provider
// Enabled flag into the orchestrator-owned options and forwards hot-reload
// changes through the same OnChange handler that drives the worker options.
builder.Services.AddSingleton<CodeyBox.Orchestrator.AgentSessionDispatchOptions>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    var dispatch = new CodeyBox.Orchestrator.AgentSessionDispatchOptions();
    AgentSessionDispatchOptionsBinder.Apply(dispatch, monitor.CurrentValue.ClaudeSession);
    monitor.OnChange(opts => AgentSessionDispatchOptionsBinder.Apply(dispatch, opts.ClaudeSession));
    return dispatch;
});
// Default metrics sink is the no-op; operators wire a logging/metrics-backed
// sink by registering their own IClaudeSessionMetricsSink before this line.
builder.Services.TryAddSingleton<CodeyBox.Agents.Claude.IClaudeSessionMetricsSink>(
    CodeyBox.Agents.Claude.NullClaudeSessionMetricsSink.Instance);
// ACP transport. Always registered so the operator can flip
// CodeyBox:ClaudeSession:Transport=acp at runtime; the worker only opens an
// ACP transport when the resolved config asks for it.
builder.Services.AddSingleton<CodeyBox.Agents.Claude.AcpClaudeTransport>(sp =>
    new CodeyBox.Agents.Claude.AcpClaudeTransport(
        sp.GetRequiredService<CodeyBox.Core.AgentNetworkToleranceSnapshot>()));
builder.Services.AddSingleton<CodeyBox.Agents.Claude.ClaudeSessionWorker>(sp =>
{
    var runner = sp.GetServices<IAgentRunner>()
        .OfType<ClaudeAgentRunner>()
        .First();

    // Resume hook: when the registered provider exposes the suspend/resume
    // contract (multipass; not process / bubblewrap), bring the VM back up by
    // delegating to its ResumeSandboxAsync. The AgentSessionSandboxRef.Id IS
    // the multipass VM name (default sandboxRefFactory derives it from
    // ISandbox.Id, which MultipassSandbox sets to the VM name). Non-suspending
    // providers leave the hook unwired so a stop/resume cycle isn't attempted
    // against them — ResumeSessionAsync then short-circuits the resume step.
    var provider = sp.GetService<ISandboxProvider>();
    Func<AgentSessionSandboxRef, CancellationToken, Task>? resumeHook = null;
    if (provider is ISuspendingSandboxProvider suspending)
        resumeHook = (sandboxRef, ct) => suspending.ResumeSandboxAsync(sandboxRef.Id, ct);

    return new CodeyBox.Agents.Claude.ClaudeSessionWorker(
        runner,
        sandboxReattacher: null,
        sandboxResumeHook: resumeHook,
        credentialProvider: sp.GetService<ICredentialProvider>(),
        sandboxRefFactory: null,
        metricsSink: sp.GetRequiredService<CodeyBox.Agents.Claude.IClaudeSessionMetricsSink>(),
        options: sp.GetRequiredService<CodeyBox.Agents.Claude.ClaudeSessionWorkerOptions>(),
        acpTransport: sp.GetRequiredService<CodeyBox.Agents.Claude.AcpClaudeTransport>(),
        printTransport: null,
        onTransportDegraded: (sessionId, reason) =>
            AuditLog.ClaudeAcpTransportDegraded(sessionId, reason));
});

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
        // Note: Antigravity is NOT in this verbatim mapping. The agy CLI's
        // OAuth token bundle is shipped to the sandbox by the dedicated
        // AntigravityEnvironmentCredentialProvider registered separately below.
    }));
    // Antigravity uses Sign-in-with-Google OAuth. The dedicated provider ships
    // the agy token bundle verbatim (refresh_token RETAINED) into the sandbox,
    // where the runner writes it to ~/.gemini/antigravity-cli/antigravity-oauth-token
    // (agy's fileTokenStorage path). agy must self-refresh the short-lived
    // access_token in-VM; the host authenticates from the keyring, a separate
    // store, so this does not race the host CLI. (Unlike Claude/Gemini, which
    // strip the refresh_token — agy has no other in-VM refresh path.)
    builtInLast.Add(new AntigravityEnvironmentCredentialProvider(
        sp.GetService<ILogger<AntigravityEnvironmentCredentialProvider>>()));
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

builder.Services.AddHttpClient("check-completion", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
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
    return QuotaRouterConfigMapper.ToOptions(cbOpts.QuotaRouter);
});
builder.Services.AddSingleton<QuotaGatePolicy>(sp =>
    new QuotaGatePolicy(sp.GetRequiredService<QuotaRouterOptions>()));
builder.Services.AddSingleton<IAgentQuotaGate>(sp => new QuotaGateAvailability(
    sp.GetRequiredService<QuotaGatePolicy>(),
    sp.GetService<IQuotaFailureStore>(),
    sp.GetRequiredService<QuotaRouterOptions>().ObservedFailureWindow));
builder.Services.AddSingleton<IQuotaFailureStore>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteQuotaFailureStore(
        cbOpts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<AgentQuotaAvailabilityBroadcaster>();
builder.Services.AddSingleton<IAgentQuotaAvailabilityPublisher>(sp =>
    sp.GetRequiredService<AgentQuotaAvailabilityBroadcaster>());
builder.Services.AddSingleton<IAgentQuotaAvailabilityObservationSource>(sp =>
    sp.GetRequiredService<AgentQuotaAvailabilityBroadcaster>());
builder.Services.AddSingleton<IAgentFallbackHistoryStore>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentFallbackHistoryStore(
        cbOpts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IAgentInvolvementStore>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentInvolvementStore(
        cbOpts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
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

// Every quota probe is wrapped so a transient blip serves the most recent real
// reading (bounded by ProbeMaxStalenessSeconds + the reading's own reset) rather
// than collapsing to unknown and letting the router fall open. Discard-on-
// Permanent/NoCredential is driven by the probe's QuotaUnknownReason.
static IAgentQuotaProbe WrapLastKnownGood(IAgentQuotaProbe inner, IServiceProvider sp)
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    var lf = sp.GetRequiredService<ILoggerFactory>();
    return new LastKnownGoodQuotaProbe(
        inner,
        () => new LastKnownGoodQuotaOptions
        {
            MaxStaleness = TimeSpan.FromSeconds(monitor.CurrentValue.QuotaRouter.ProbeMaxStalenessSeconds),
        },
        lf.CreateLogger<LastKnownGoodQuotaProbe>(),
        sp.GetService<TimeProvider>());
}

builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<ClaudeCredentialFileSource>();
    var tokenSource = sp.GetRequiredService<IClaudeQuotaTokenSource>();
    var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    var probe = new ClaudeQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        // Sync-over-async is intentional and safe here: ASP.NET Core has no
        // SynchronizationContext (no deadlock potential), the cache hit-path
        // is fully synchronous, and only a stale-token miss blocks the thread
        // on the OAuth refresh round-trip (bounded by the agent-quota client's
        // 10s timeout).
        member => AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            member,
            () => new AgentQuotaCredentials(
                tokenSource.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult()
                    ?? Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY")))
            ?? new AgentQuotaCredentials(null),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<ClaudeQuotaProbe>(),
        // Resilience knobs are read on every probe call so values bound from
        // CodeyBox:QuotaRouter hot-reload through IOptionsMonitor without
        // restarting the process.
        resilienceProvider: () =>
        {
            var qr = optionsMonitor.CurrentValue.QuotaRouter;
            return new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = qr.ProbeMaxRetries,
                RetryInitialDelay = TimeSpan.FromMilliseconds(qr.ProbeRetryInitialDelayMs),
                MaxConsecutiveFailures = qr.ProbeMaxConsecutiveFailures,
                MaxStaleness = TimeSpan.FromSeconds(qr.ProbeMaxStalenessSeconds),
            };
        },
        timeProvider: null);
    var wrapped = WrapLastKnownGood(probe, sp);
    source.TokenUpdated += ((IAgentQuotaCacheInvalidator)wrapped).InvalidateCredentialState;
    return wrapped;
});
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<CodexCredentialFileSource>();
    var tokenSource = sp.GetRequiredService<ICodexQuotaTokenSource>();
    var probe = new CodexQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        member => AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            member,
            () =>
        {
            var codexAuth = tokenSource.GetTokensAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            return new AgentQuotaCredentials(
                codexAuth.AccessToken ?? Environment.GetEnvironmentVariable("CODEYBOX_CODEX_API_KEY"),
                codexAuth.AccountId ?? Environment.GetEnvironmentVariable("CODEYBOX_CODEX_ACCOUNT_ID"));
        }) ?? new AgentQuotaCredentials(null),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<CodexQuotaProbe>());
    var wrapped = WrapLastKnownGood(probe, sp);
    source.TokenUpdated += ((IAgentQuotaCacheInvalidator)wrapped).InvalidateCredentialState;
    return wrapped;
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
        member => AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            member,
            () => new AgentQuotaCredentials(
                tokenSource.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult()
                    ?? Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_TOKEN")))
            ?? new AgentQuotaCredentials(null),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<GeminiQuotaProbe>());
    var wrapped = WrapLastKnownGood(probe, sp);
    source.TokenUpdated += ((IAgentQuotaCacheInvalidator)wrapped).InvalidateCredentialState;
    return wrapped;
});
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<CursorCredentialFileSource>();
    var probe = new CursorQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        member => AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            member,
            () => new AgentQuotaCredentials(
                CredentialFileTokenExtractor.ExtractCursorAccessToken(source.GetRaw())
                    ?? CredentialFileTokenExtractor.ExtractCursorAccessToken(
                        Environment.GetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_JSON"))))
            ?? new AgentQuotaCredentials(null),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<CursorQuotaProbe>());
    var wrapped = WrapLastKnownGood(probe, sp);
    source.TokenUpdated += ((IAgentQuotaCacheInvalidator)wrapped).InvalidateCredentialState;
    return wrapped;
});

// opencode: no verified usage endpoint at integration time. The probe ships
// as Unknown-only so the router falls onto its QuotaUnknownPolicy
// (UseObservedFailures) for opencode members. Replace with a real
// HTTP-backed probe once an endpoint is confirmed.
builder.Services.AddSingleton<IAgentQuotaProbe>(sp => WrapLastKnownGood(new OpencodeQuotaProbe(), sp));
// Crock: ships as Unknown-only. Cost/usage accounting against Anthropic's
// Message Batches API is part of the dependent follow-up; until then the
// router falls onto its QuotaUnknownPolicy (UseObservedFailures) for any
// crock member operators opt in.
builder.Services.AddSingleton<IAgentQuotaProbe>(sp => WrapLastKnownGood(new CrockQuotaProbe(), sp));
// Antigravity: the agy gateway exposes NO readable per-model quota meter
// (daily-cloudcode-pa :retrieveUserQuota* return 403), so the probe uses
// :loadCodeAssist as a free authorization/tier liveness read (200 ⇒ available)
// and learns per-model exhaustion reactively from runtime 429s. It does NOT
// burn a live :generateContent ping for routine probing. Token sources fall
// back to the Gemini OAuth file since both CLIs use Sign-in-with-Google;
// operators with distinct credentials can override via
// CODEYBOX_ANTIGRAVITY_OAUTH_TOKEN or per-instance CredentialReference.
builder.Services.AddSingleton<IAgentQuotaProbe>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var source = sp.GetRequiredService<GeminiOAuthCredentialFileSource>();
    var probe = new AntigravityQuotaProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        member => AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            member,
            () => new AgentQuotaCredentials(
                CredentialFileTokenExtractor.ExtractGeminiAccessToken(source.GetRaw())
                    ?? CredentialFileTokenExtractor.ExtractGeminiAccessToken(
                        Environment.GetEnvironmentVariable(AntigravityConstants.OAuthCredsEnvVar))
                    ?? Environment.GetEnvironmentVariable("CODEYBOX_ANTIGRAVITY_OAUTH_TOKEN")
                    ?? Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_OAUTH_TOKEN")))
            ?? new AgentQuotaCredentials(null),
        sp.GetRequiredService<QuotaRouterOptions>().QuotaCacheTtl,
        loggerFactory.CreateLogger<AntigravityQuotaProbe>());
    var wrapped = WrapLastKnownGood(probe, sp);
    source.TokenUpdated += ((IAgentQuotaCacheInvalidator)wrapped).InvalidateCredentialState;
    return wrapped;
});

// --- Agent class router ------------------------------------------------------
builder.Services.AddSingleton<AgentClassRouter>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var startupLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CodeyBox.AgentClasses");

    // Build and validate the catalog. Shared with AgentConfigHotReload so a
    // reload of CodeyBox:AgentClasses runs the same validation rules.
    var catalog = AgentClassesConfigBuilder.Build(cbOpts.AgentClasses, cbOpts.AgentInstances, startupLog);
    var subscriptionMembers = catalog.Sum(c => c.Members.Count(m => m.Billing == AgentBilling.Subscription));
    startupLog.LogInformation("Quota gate enabled for {Count} subscription members", subscriptionMembers);

    // Build and validate time-of-day score modifiers.
    var todModifiers = AgentClassesConfigBuilder.BuildTodModifiers(cbOpts.AgentScoreModifiers, startupLog);
    var inVmSmokeOptions = sp.GetService<InVmSmokeOptions>();
    InVmSmokeSandboxTarget? configuredSmokeTarget =
        string.IsNullOrWhiteSpace(inVmSmokeOptions?.NetworkProfile)
            ? null
            : new InVmSmokeSandboxTarget(inVmSmokeOptions.NetworkProfile, SandboxProfileFlavor.Headless);

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
        sp.GetService<IAgentBudgetProvider>(),
        sp.GetService<AgentConcurrencySnapshot>(),
        configuredSmokeTarget,
        sp.GetService<IAgentDispatchAvailability>(),
        sp.GetRequiredService<IAgentQuotaAvailabilityPublisher>());
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

// PipelineTuningSnapshot — hot-reloadable quota-fallback and merge-staging
// retry tuning knobs consumed by PipelineRunner. Same swappable-singleton
// pattern as AgentConcurrencySnapshot.
builder.Services.AddSingleton<PipelineTuningSnapshot>(sp =>
    new PipelineTuningSnapshot(
        sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.PipelineTuning));

// BudgetDeferralRecheckSnapshot — hot-reloadable budget-cap deferral recheck
// intervals consumed by OrchestratorService. Edits to
// CodeyBox:BudgetDeferralRecheck take effect on the next pickup attempt
// without a process restart.
builder.Services.AddSingleton<BudgetDeferralRecheckSnapshot>(sp =>
    new BudgetDeferralRecheckSnapshot(
        sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.BudgetDeferralRecheck));

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

// AgentNetworkToleranceSnapshot — per-agent network tolerance options,
// swappable by the hot-reload coordinator. Every runner reads through this
// same instance so an operator edit to CodeyBox:AgentNetworkTolerance takes
// effect on the next dispatched agent run without a process restart.
builder.Services.AddSingleton<CodeyBox.Core.AgentNetworkToleranceSnapshot>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new CodeyBox.Core.AgentNetworkToleranceSnapshot(opts.AgentNetworkTolerance);
});

// ClaudeThinkingBlockSanitizerConfig — hot-reloadable toggle gating the
// thinking-block transcript sanitiser + reactive retry path.
builder.Services.AddSingleton<CodeyBox.Core.ClaudeThinkingBlockSanitizerConfig>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new CodeyBox.Core.ClaudeThinkingBlockSanitizerConfig
    {
        Enabled = opts.ClaudeThinkingBlockSanitizer.Enabled,
    };
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
// Worker-pool occupancy for the codeybox.workers.in_use gauge. OrchestratorService
// owns the semaphore-backed pool; resolve it lazily (same cycle-break rationale as
// IAgentRunningCounters) so the observable-metrics hosted service can read the live
// pool total without coupling to the concrete service.
builder.Services.AddSingleton<IWorkerPoolOccupancy>(sp =>
    new DeferredWorkerPoolOccupancy(() => sp.GetRequiredService<OrchestratorService>()));
// Quota-availability snapshot for the codeybox.agent.quota.available_pct gauge.
// Surfaced as a focused contract implemented by AgentClassRouter so telemetry
// does not depend on the concrete router type.
builder.Services.AddSingleton<IAgentQuotaAvailabilitySnapshot>(sp =>
    sp.GetRequiredService<AgentClassRouter>());
builder.Services.AddSingleton<IAgentQuotaAvailabilitySignal>(sp =>
    sp.GetRequiredService<AgentQuotaAvailabilityBroadcaster>());
builder.Services.AddSingleton<IQuotaRetryRouter>(sp =>
    sp.GetRequiredService<AgentClassRouter>());
builder.Services.AddSingleton<IQuotaRetryAdmissionRouter>(sp =>
    sp.GetRequiredService<AgentClassRouter>());
builder.Services.AddSingleton<IAgentRoutingReadiness>(sp =>
    sp.GetRequiredService<AgentClassRouter>());

// --- Credential smoke probes -------------------------------------------------
// Registered as IEnumerable<IAgentSmokeProbe>; the gate resolves by Kind.
// Copilot has no host-side credential smoke probe (its auth surface is not
// directly probeable). The in-VM probe covers binary-presence verification,
// see CopilotInVmSmokeProbe registered below.
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new ClaudeSmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClaudeSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new CodexSmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CodexSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    // Pass the IGeminiQuotaTokenSource (which also implements
    // IGeminiOAuthTokenSource — same refresher instance) so the smoke probe
    // hits Google with a freshly-refreshed access_token instead of the stale
    // on-disk one. Without this hookup the probe parses ~/.gemini/oauth_creds.json's
    // last-written access_token, which the gemini CLI rotates ~hourly, and the
    // probe eventually 401s — benching a fully-usable agent because the smoke
    // path bypassed the refresh logic the quota path already uses.
    new GeminiSmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GeminiSmokeProbe>(),
        sp.GetService<IGeminiQuotaTokenSource>() as IGeminiOAuthTokenSource));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new CursorSmokeProbe(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CursorSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new OpencodeSmokeProbe(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpencodeSmokeProbe>()));
builder.Services.AddSingleton<IAgentSmokeProbe>(sp =>
    new AntigravitySmokeProbe(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AntigravitySmokeProbe>()));

// --- In-VM smoke probes ------------------------------------------------------
// Registered as IEnumerable<IInVmSmokeProbe>; InVmSmokeProber resolves by Kind.
// These exec the agent CLI inside a sandbox cloned from the active baseline,
// catching exit-127 / auth-path failures the host-only probes above cannot see.
builder.Services.AddSingleton<IInVmSmokeProbe, ClaudeInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, CopilotInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, CodexInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, GeminiInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, CursorInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, OpencodeInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, AntigravityInVmSmokeProbe>();
builder.Services.AddSingleton<IInVmSmokeProbe, CrockInVmSmokeProbe>();
// Startup guard (AC#1): bench any configured AgentClass member with no in-VM
// probe (so a CLI-backed agent that would fail at first dispatch is routed past
// at smoke time, not first dispatch). Agents on
// InVmSmokeOptions.ExemptAgentsWithoutProbe (the default still names copilot
// to preserve back-compat for operators who haven't installed the copilot CLI
// yet) are warned but not benched; a registered IInVmSmokeProbe — including
// CopilotInVmSmokeProbe above — supersedes that exemption and is what actually
// gets executed at probe time.
builder.Services.AddHostedService<InVmSmokeProbeCoverageValidator>();

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
// Antigravity model-list probe: no reachable endpoint enumerates the agy
// gateway models for our credential (the cloudcode-pa Code Assist surface
// returns the wrong gemini-2.5 catalog; the daily-cloudcode-pa gateway 403s on
// :retrieveUserQuota* and :fetchAvailableModels). The curated
// AntigravityKnownModels list is authoritative, so the probe just returns it.
builder.Services.AddSingleton<IAgentModelListProbe, AntigravityModelListProbe>();
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
builder.Services.AddSingleton<SmokeOptionsSnapshot>(sp =>
    new SmokeOptionsSnapshot(sp.GetRequiredService<SmokeOptions>()));

builder.Services.AddSingleton<TransitionHealthOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var t = cbOpts.TransitionHealth;
    return TransitionHealthConfigMapper.ToOptions(t.Enabled, t.WindowHours, t.MaxTransitions);
});
builder.Services.AddSingleton<TransitionHealthOptionsSnapshot>(sp =>
    new TransitionHealthOptionsSnapshot(sp.GetRequiredService<TransitionHealthOptions>()));
builder.Services.AddSingleton<ITransitionHealthDataSource>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteTransitionHealthDataSource(cbOpts.StateDatabasePath);
});
builder.Services.AddSingleton<TransitionHealthService>(sp =>
    new TransitionHealthService(
        sp.GetRequiredService<ITransitionHealthDataSource>(),
        sp.GetRequiredService<TransitionHealthOptionsSnapshot>()));
builder.Services.AddSingleton<AvailabilityOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var a = cbOpts.Smoke.Availability;
    return new AvailabilityOptions
    {
        FastFailThresholdSeconds = a.FastFailThresholdSeconds,
        MaxConsecutiveFastFails = a.MaxConsecutiveFastFails,
        MaxConsecutiveNoChanges = a.MaxConsecutiveNoChanges,
        PeriodicSweepInterval = TimeSpan.FromSeconds(Math.Max(0, a.PeriodicSweepIntervalSeconds)),
    };
});
builder.Services.AddSingleton<AgentAvailabilityRegistry>(sp => new AgentAvailabilityRegistry(
    sp.GetRequiredService<AvailabilityOptions>(),
    TimeProvider.System,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentAvailabilityRegistry>()));
// The narrow availability port routing/dispatch/admin consumers bind to —
// same singleton, exposed as the read/run-outcome/snapshot/reset surface.
builder.Services.AddSingleton<IAgentAvailabilityRegistry>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<IAgentEffectiveAvailabilityReader>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<IAgentAvailabilityRecoverySignal>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
// The smoke-mutator port the in-VM prober, coverage policy, and host smoke
// services bind to — same singleton, exposed as the exclusion-taxonomy
// surface (MarkSmokeResult / ExcludeForMissingProbe) those owners need.
builder.Services.AddSingleton<ISmokeAvailabilityRegistry>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<IAgentAuthAvailabilityRegistry>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<IAgentAuthRequiredAvailabilityReader>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<IAgentRestorePublisher>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<IAgentDispatchAvailability>(sp => new AgentDispatchAvailability(
    sp.GetService<IAgentEffectiveAvailabilityReader>(),
    sp.GetService<IInVmSmokeGate>(),
    sp.GetRequiredService<SmokeOptionsSnapshot>(),
    sp.GetRequiredService<IAgentPauseController>()));
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
        sp.GetRequiredService<SmokeOptionsSnapshot>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CredentialSmokeGate>()));

// --- In-VM smoke prober ------------------------------------------------------
builder.Services.AddSingleton<InVmSmokeOptions>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    var v = cbOpts.Smoke.InVm;
    return new InVmSmokeOptions
    {
        Enabled = v.Enabled,
        ImageReference = cbOpts.SandboxImageReference,
        AllowedHosts = cbOpts.AgentAllowedHosts,
        // Dispatch calls pass the resolved project sandbox target directly to
        // IInVmSmokeGate. This option is only an explicit operator override for
        // project-less paths such as manual/admin probes and legacy sweeps.
        NetworkProfile = v.NetworkProfile,
        StepTimeoutSeconds = v.StepTimeoutSeconds,
        ProvisionTimeoutSeconds = v.ProvisionTimeoutSeconds,
        GateDeadlineSeconds = v.GateDeadlineSeconds,
        CacheTtlMinutes = v.CacheTtlMinutes,
        SweepIntervalSeconds = v.SweepIntervalSeconds,
        FailClosedOnProbeFault = v.FailClosedOnProbeFault,
        // Null (unset) keeps the InVmSmokeOptions default (copilot); an explicit
        // list — including empty — overrides it so operators can opt every agent
        // into the in-VM coverage requirement.
        ExemptAgentsWithoutProbe = v.ExemptAgentsWithoutProbe is { } ex ? ex : new InVmSmokeOptions().ExemptAgentsWithoutProbe,
    };
});
builder.Services.AddSingleton<IInVmSmokeCache>(sp =>
    new InVmSmokeCache(TimeSpan.FromMinutes(sp.GetRequiredService<InVmSmokeOptions>().CacheTtlMinutes)));
// Single operator-reset port: clears the availability registry AND invalidates
// the in-VM smoke cache atomically, so a reset can never leave a stale cached
// pass to reconcile back onto the registry before the operator's fix is
// re-verified. The admin endpoint depends on this one contract.
builder.Services.AddSingleton<IAgentAvailabilityReset>(sp => new AgentAvailabilityReset(
    sp.GetRequiredService<ISmokeAvailabilityRegistry>(),
    sp.GetRequiredService<IInVmSmokeCache>(),
    sp.GetRequiredService<IAgentRestorePublisher>()));
builder.Services.AddSingleton<InVmSmokeProber>(sp => new InVmSmokeProber(
    sp.GetRequiredService<ISandboxProvider>(),
    sp.GetRequiredService<IBaselineImageResolver>(),
    sp.GetRequiredService<IBaselineImageProvisioner>(),
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetServices<IInVmSmokeProbe>(),
    sp.GetRequiredService<ISmokeAvailabilityRegistry>(),
    sp.GetRequiredService<IInVmSmokeCache>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<InVmSmokeOptions>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<InVmSmokeProber>(),
    sp.GetRequiredService<SmokeOptionsSnapshot>(),
    sp.GetRequiredService<IAgentAuthFailureClassifier>(),
    sp.GetRequiredService<IAgentAuthAvailabilityRegistry>()));
// The router consults the prober as a dispatch gate (IInVmSmokeGate) so the
// first work item per baseline is verified in-VM before routing; share the
// single InVmSmokeProber instance so the gate, the background sweep service,
// and the cache all observe the same state.
builder.Services.AddSingleton<IInVmSmokeGate>(sp => sp.GetRequiredService<InVmSmokeProber>());
// Coverage enforcement (startup validator + hot-reload) is a pure config policy
// with no VM provisioning, kept in a separate type from the runtime dispatch
// gate so those consumers depend only on the narrow IInVmSmokeCoveragePolicy
// port rather than the full gate contract (interface segregation).
builder.Services.AddSingleton<IInVmSmokeCoveragePolicy>(sp => new InVmSmokeCoveragePolicy(
    sp.GetServices<IInVmSmokeProbe>(),
    sp.GetRequiredService<ISmokeAvailabilityRegistry>(),
    sp.GetRequiredService<InVmSmokeOptions>(),
    sp.GetRequiredService<SmokeOptionsSnapshot>()));
builder.Services.AddHostedService(sp => new InVmSmokeProbeService(
    sp.GetRequiredService<IInVmSmokeGate>(),
    sp.GetRequiredService<InVmSmokeOptions>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<InVmSmokeProbeService>(),
    sp.GetRequiredService<IProjectRepository>()));

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
    sp.GetService<PresetCatalogOptions>(),
    sp.GetRequiredService<IKnobRegistry>()));
builder.Services.AddSingleton<IUpstreamRemoteFactory, UpstreamRemoteFactory>();
builder.Services.AddSingleton(_ =>
{
    var options = builder.Configuration.GetSection("CodeyBox:Presets").Get<PresetCatalogOptions>()
        ?? new PresetCatalogOptions();
    options.ProjectRoot ??= builder.Environment.ContentRootPath;
    return options;
});
// Bridges the hot-reloadable pipeline-tuning knobs
// (CodeyBox:PipelineTuning:CSharpTestPass*) into the TestRunOptions that
// DotnetTestAuditor reads at run time. Reading through PipelineTuningSnapshot on
// each call keeps blame-hang / test-idle-timeout edits hot-reloadable without a
// restart. Defaults (unset knobs) yield TestRunOptions.Default → byte-identical
// legacy dotnet-test command.
static Func<TestRunOptions> DotnetTestRunOptionsAccessor(IServiceProvider sp)
{
    var tuning = sp.GetRequiredService<PipelineTuningSnapshot>();
    return () =>
    {
        var current = tuning.Current;
        return new TestRunOptions
        {
            BlameHangTimeout = current.CSharpTestPassBlameHangTimeout,
            IdleTimeout = current.CSharpTestPassAuditorIdleTimeout,
        };
    };
}

// Bind the run-options accessor ONCE and share the single closure across the
// preset catalog, the DI-registered test runner, and every per-project catalog
// the ProjectAuditorComposer builds. One closure — all consumers observe the
// same hot-reloadable PipelineTuningSnapshot.
builder.Services.AddSingleton<Func<TestRunOptions>>(DotnetTestRunOptionsAccessor);

builder.Services.AddSingleton<IPresetCatalog>(sp => new PresetCatalog(
    sp.GetRequiredService<PresetCatalogOptions>(),
    sp.GetRequiredService<Func<TestRunOptions>>()));

// The canonical dotnet-test runner, registered so the ITestSelector seam
// (a separate work item) can resolve ITestRunnerAuditor from DI and enumerate
// its TestSuiteDescriptor. The preset catalog builds its own instance for the
// audit run from the csharp language YAML; this registration mirrors that
// command with the same hot-reloadable run options.
builder.Services.AddSingleton<ITestRunnerAuditor>(sp => new DotnetTestAuditor(new DotnetTestAuditorOptions
{
    Name = "csharp:test-pass",
    BaseArgv = ["dotnet", "test", "--no-build"],
    CanShortCircuitOnBlockingFinding = true,
    Role = AuditorRole.BuildTestGate,
    BuildTestGateEvidence = BuildTestGateEvidence.Test,
    RunOptionsAccessor = sp.GetRequiredService<Func<TestRunOptions>>(),
}));
builder.Services.AddSingleton<IAuditor, GraphicalSmokeAuditor>();
builder.Services.AddSingleton<IAuditor>(sp => new BuildScriptAuditor(
    () => sp.GetRequiredService<IOptionsMonitor<BuildScriptAuditorOptions>>().CurrentValue));
builder.Services.AddSingleton<IAuditor, PromptRevisionTrailerAuditor>();
builder.Services.AddSingleton<IMechanicalFixer, DotnetFormatMechanicalFixer>();
builder.Services.AddSingleton<IMechanicalFixerRegistry, MechanicalFixerRegistry>();
builder.Services.AddSingleton<IMechanicalFixerInputProvider, DotnetFormatMechanicalFixerInputProvider>();

// Mutation-testing rigor gate (disabled by default; per-project threshold).
// The auditor short-circuits to pass when Enabled=false, so registering the
// defaults is safe; operators replace IMutationRunner / IMutationRatchetStore
// with their own implementations to turn the gate on.
//
// IOptionsMonitor (not IOptions snapshot) so hot-reloads of CodeyBox:Mutation
// (threshold, budget, etc.) take effect without a process restart, consistent
// with the rest of the host's options wiring.
builder.Services.Configure<MutationTestingAuditorOptions>(
    builder.Configuration.GetSection("CodeyBox:Mutation"));
builder.Services.TryAddSingleton<IMutationRunner, NullMutationRunner>();
builder.Services.TryAddSingleton<IMutationRatchetStore, InMemoryMutationRatchetStore>();
builder.Services.AddSingleton<IAuditor>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<MutationTestingAuditorOptions>>();
    var ratchet = sp.GetRequiredService<IMutationRatchetStore>();
    // Loud startup warning when the gate is enabled but the in-memory ratchet
    // store is the registered implementation: it is process-local and every
    // restart wipes the baseline, so the "no-regression" invariant only holds
    // within a single uptime window. Operators flipping Enabled=true on a
    // long-lived host should swap in a file- or SQLite-backed store.
    if (monitor.CurrentValue.Enabled && ratchet is InMemoryMutationRatchetStore)
    {
        sp.GetRequiredService<ILogger<MutationTestingAuditor>>().LogWarning(
            "mutation-rigor gate is enabled but the registered IMutationRatchetStore is " +
            "InMemoryMutationRatchetStore — the no-regression baseline will be reset on every " +
            "process restart. Register a persistent IMutationRatchetStore (file/SQLite) before " +
            "relying on the ratchet across restarts.");
    }
    return new MutationTestingAuditor(
        () => monitor.CurrentValue,
        sp.GetRequiredService<IMutationRunner>(),
        ratchet);
});

// Plan-adherence reviewer (closes the planning loop on the implementation side).
// Hot-reloadable via CodeyBox:PlanAdherence and enabled by default; the auditor
// self-limits to PLANNED items at run time (no plan artifact -> no-op), so
// unplanned items are unaffected. The accessor mirrors the Func<TestRunOptions>
// pattern so the ProjectAuditorComposer observes the same live IOptionsMonitor
// snapshot and composes the reviewer with the resolving project's agent.
builder.Services.Configure<PlanAdherenceAuditorOptions>(
    builder.Configuration.GetSection("CodeyBox:PlanAdherence"));
builder.Services.AddSingleton<Func<PlanAdherenceAuditorOptions>>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<PlanAdherenceAuditorOptions>>();
    return () => monitor.CurrentValue;
});

builder.Services.AddSingleton<ProjectAuditorComposer>();
builder.Services.AddSingleton<ProjectMechanicalFixerComposer>();

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

// Chat provider (Slack / Discord incoming webhooks). Safe no-op when disabled
// or when no webhooks are configured; URLs are read from env vars at send time.
builder.Services.AddHttpClient("notifications-chat");
builder.Services.AddSingleton<INotificationProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ChatNotificationProvider>>();
    Func<ChatProviderOptions> optsAccessor = () =>
        sp.GetRequiredService<IOptionsMonitor<NotificationsOptions>>().CurrentValue.Chat;
    var opts = optsAccessor();
    if (opts.Enabled)
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("notifications-chat");
        return new ChatNotificationProvider(optsAccessor, httpClient, logger);
    }
    return new NullNotificationProvider("chat");
});

// ICondition registrations — one per supported condition.
builder.Services.AddSingleton<ICondition, QueueEmptyCondition>();
builder.Services.AddSingleton<ICondition>(sp => new AllQuotasExhaustedCondition(
    sp.GetRequiredService<IEnumerable<IAgentQuotaProbe>>(),
    sp.GetRequiredService<IAgentQuotaGate>(),
    sp.GetRequiredService<IAgentRegistry>(),
    sp.GetRequiredService<ILogger<AllQuotasExhaustedCondition>>()));
builder.Services.AddSingleton<ICondition>(sp => new AgentAuthRequiredCondition(
    sp.GetRequiredService<IAgentAuthRequiredAvailabilityReader>(),
    sp.GetRequiredService<IAgentRegistry>()));
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
builder.Services.AddSingleton<INotificationBuilder>(sp => new AgentAuthRequiredNotificationBuilder(
    sp.GetRequiredService<IAgentAuthRequiredAvailabilityReader>(),
    sp.GetRequiredService<IAgentRegistry>()));
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
builder.Services.AddSingleton<IAgentSupervisionNotifier>(sp =>
    sp.GetRequiredService<AgentStdoutBroadcastService>());
builder.Services.AddSingleton<IAgentSupervisionService>(sp => new AgentSupervisionService(
    () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.AgentSupervision,
    sp.GetRequiredService<IAgentSupervisionNotifier>(),
    sp.GetRequiredService<ILogger<AgentSupervisionService>>()));

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
    return new SqliteReleaseStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<Func<IReleaseStore?>>(sp => () => sp.GetService<IReleaseStore>());
builder.Services.AddSingleton<SqliteWorkItemStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemStore(
        opts.StateDatabasePath,
        writeGateFactory: sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IWorkItemStore>(sp => sp.GetRequiredService<SqliteWorkItemStore>());
builder.Services.AddSingleton<IAuditProgressStore>(sp => sp.GetRequiredService<SqliteWorkItemStore>());
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteIdempotencyStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<ISuggestionStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteSuggestionStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
// --- Work-item attachments ---------------------------------------------------
// Metadata index lives next to the work-item rows in state.db; blobs live on
// disk under a content-addressed root (default ~/.codeybox/attachments).
builder.Services.AddSingleton<SqliteWorkItemAttachmentStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemAttachmentStore(opts.StateDatabasePath);
});
builder.Services.AddSingleton<IWorkItemAttachmentStore>(sp =>
    sp.GetRequiredService<SqliteWorkItemAttachmentStore>());
builder.Services.AddSingleton<HostWorkItemAttachmentBlobStore>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    return new HostWorkItemAttachmentBlobStore(
        () => AttachmentsOptions.ResolveRoot(monitor.CurrentValue.Attachments.RootDirectory),
        sp.GetService<ILogger<HostWorkItemAttachmentBlobStore>>());
});
builder.Services.AddSingleton<IWorkItemAttachmentBlobStore>(sp =>
    sp.GetRequiredService<HostWorkItemAttachmentBlobStore>());
builder.Services.AddSingleton<IWorkItemAttachmentBlobStoreAdmin>(sp =>
    sp.GetRequiredService<HostWorkItemAttachmentBlobStore>());
builder.Services.AddHostedService(sp => new AttachmentCleanupService(
    sp.GetRequiredService<IWorkItemAttachmentStore>(),
    sp.GetRequiredService<IWorkItemAttachmentBlobStoreAdmin>(),
    () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.Attachments,
    sp.GetRequiredService<ILogger<AttachmentCleanupService>>()));
builder.Services.AddSingleton<IWorkItemQuestionStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemQuestionStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<ITestCaseStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteTestCaseStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IE2eRunStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteE2eRunStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IE2eReplayRuntime, E2eReplayRuntime>();
builder.Services.AddSingleton<E2eReplayArtifactAdmissionValidator>();
builder.Services.AddSingleton<E2eRunCancellationRegistry>();
// E2E pool selection is deliberately independent of the coding pipeline's
// admitted ISandboxProvider. PoolKind=remote-ssh builds the existing
// multipass-over-SSH provider; PoolKind=local builds an unwrapped provider
// for development only.
builder.Services.AddSingleton<IE2eExecutionPool>(BuildE2eExecutionPool);
builder.Services.AddHostedService<E2eRunDispatcher>();
builder.Services.AddSingleton<IAuditReportStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAuditReportStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<ITimingStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteTimingStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<ISandboxResourceUsageStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteSandboxResourceUsageStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IWorkItemCostStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkItemCostStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<AgentCostCalculator>(),
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IAgentUsageStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentUsageStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IAgentStreamSummaryStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentStreamSummaryStore(
        opts.StateDatabasePath,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IQueueController>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteQueueController(
        opts.StateDatabasePath,
        sp.GetRequiredService<ILogger<SqliteQueueController>>(),
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<SqliteAgentPauseController>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteAgentPauseController(
        opts.StateDatabasePath,
        sp.GetRequiredService<ILogger<SqliteAgentPauseController>>(),
        TimeProvider.System,
        sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
});
builder.Services.AddSingleton<IAgentPauseController>(sp =>
    sp.GetRequiredService<SqliteAgentPauseController>());
builder.Services.AddSingleton<IAgentPauseSignal>(sp =>
    sp.GetRequiredService<SqliteAgentPauseController>());
builder.Services.AddSingleton<InMemoryTaskQueue>();
builder.Services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<InMemoryTaskQueue>());
builder.Services.AddSingleton<WorkItemCreationService>();
builder.Services.AddSingleton<ITaskTemplateRegistry, FileTaskTemplateRegistry>();

// --- Dead-worker registry + reaper -------------------------------------------
builder.Services.AddSingleton<IWorkerRegistry>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return new SqliteWorkerRegistry(
        opts.StateDatabasePath,
        sp.GetRequiredService<ILogger<SqliteWorkerRegistry>>(),
        writeGateFactory: sp.GetRequiredService<SqliteDatabaseWriteGateFactory>());
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
        sp.GetRequiredService<IWebhookDispatcher>(),
        startupRecoveryBarrier: sp.GetRequiredService<IStartupInitialRecoveryBarrier>());
});

// --- Worker progress watchdog -----------------------------------------------
// Lifecycle-wide progress enforcer that complements the dead-worker reaper
// (heartbeat-stale path) and WorkTimeout (agent subprocess only). Trips when
// a bound worker is heartbeating but its item shows no progress: item.updatedAt,
// agent-stream mtime, process CPU, and sandbox activity signals are all stale.
builder.Services.AddSingleton<WorkerProgressWatchdogOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.WorkerProgressWatchdog;
    opts.Validate();
    return opts;
});
builder.Services.AddSingleton<IActiveSandboxProgressProvider>(sp =>
    sp.GetRequiredService<ISandboxProvider>() as IActiveSandboxProgressProvider
    ?? NullActiveSandboxProgressProvider.Instance);
builder.Services.AddSingleton<IWorkerProgressActivitySource>(sp =>
    new DefaultWorkerProgressActivitySource(sp.GetRequiredService<IActiveSandboxProgressProvider>()));
builder.Services.AddSingleton<WorkerProgressWatchdog>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    // Validate the startup-resolved value here too so a misconfigured options
    // block surfaces at DI resolve time, matching the reaper's pattern.
    sp.GetRequiredService<WorkerProgressWatchdogOptions>();
    return new WorkerProgressWatchdog(
        sp.GetRequiredService<IWorkerRegistry>(),
        sp.GetRequiredService<IWorkItemStore>(),
        sp.GetRequiredService<ITaskQueue>(),
        () => monitor.CurrentValue.WorkerProgressWatchdog,
        sp.GetRequiredService<ILogger<WorkerProgressWatchdog>>(),
        sp.GetService<IAgentStreamStore>(),
        sp.GetService<IWebhookDispatcher>(),
        startupRecoveryBarrier: sp.GetRequiredService<IStartupInitialRecoveryBarrier>(),
        activitySource: sp.GetRequiredService<IWorkerProgressActivitySource>());
});

// --- Per-item stale-updatedAt watchdog --------------------------------------
// Item-centric counterpart to WorkerProgressWatchdog: walks items by state
// (not by the worker registry) and uses only item.UpdatedAt as the progress
// signal, ignoring worker heartbeat / CPU / sandbox activity. Catches the
// reconnect-loop wedge (worker still alive, CPU active, item frozen) and the
// orphan-after-restart wedge (item Working but no live worker) that the
// per-worker watchdog cannot see. Also powers POST /workitems/{id}/recover.
builder.Services.AddSingleton<ItemStaleProgressWatchdog>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    sp.GetRequiredService<WorkerProgressWatchdogOptions>();
    return new ItemStaleProgressWatchdog(
        sp.GetRequiredService<IWorkItemStore>(),
        sp.GetRequiredService<ITaskQueue>(),
        sp.GetRequiredService<IWorkerRegistry>(),
        () => monitor.CurrentValue.WorkerProgressWatchdog,
        sp.GetRequiredService<ILogger<ItemStaleProgressWatchdog>>(),
        sp.GetService<IWebhookDispatcher>(),
        startupRecoveryBarrier: sp.GetRequiredService<IStartupInitialRecoveryBarrier>(),
        // Recovery cancels the running pipeline's CT so its finally blocks
        // tear down the active sandbox / VM. Without this the wedged worker
        // can keep running on a row that has been requeued to a fresh slot.
        cancellations: sp.GetRequiredService<CancellationRegistry>());
});

// --- Worker pool health watchdog --------------------------------------------
// Dispatcher-level watchdog for a pool that is under-filled while runnable work
// and an available agent exist. Complements WorkerProgressWatchdog, which owns
// per-worker lifecycle stalls after a worker has already been spawned.
builder.Services.AddSingleton<WorkerPoolHealthWatchdogOptions>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.WorkerPoolHealthWatchdog;
    opts.Validate();
    return opts;
});
builder.Services.AddSingleton<WorkerPoolHealthWatchdog>(sp =>
{
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    sp.GetRequiredService<WorkerPoolHealthWatchdogOptions>();
    return new WorkerPoolHealthWatchdog(
        sp.GetRequiredService<IWorkerPoolHealthSource>(),
        () => monitor.CurrentValue.WorkerPoolHealthWatchdog,
        sp.GetRequiredService<ILogger<WorkerPoolHealthWatchdog>>(),
        quotaRecovery: sp.GetRequiredService<IWorkerPoolQuotaRecovery>(),
        webhooks: sp.GetService<IWebhookDispatcher>(),
        startupRecoveryBarrier: sp.GetRequiredService<IStartupInitialRecoveryBarrier>());
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
        [AgentKind.Copilot] = new CopilotCostExtractor(),
        [AgentKind.Antigravity] = new AntigravityCostExtractor(),
    };
    // Warn once at startup for registered agents with no extractor.
    foreach (var kind in registry.Available)
    {
        if (!extractors.ContainsKey(kind))
            startupLog.LogWarning(
                "No cost extractor registered for agent '{Agent}'; token usage will not be extracted, but completed invocations still record elapsed fallback cost rows", kind.Value);
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
    var calculator = new AgentCostCalculator(
        new AgentPricingOptions(),
        extractors,
        sp.GetRequiredService<CodeyBox.Core.AgentDefaultsSnapshot>());
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
builder.Services.AddSingleton<IAgentStreamParser, AntigravityStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, ClaudeStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, CodexStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, CopilotStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, CursorStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, GeminiStreamParser>();
builder.Services.AddSingleton<IAgentStreamParser, OpencodeStreamParser>();
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
builder.Services.AddSingleton<IAgentQuotaFailureDetector, AntigravityQuotaFailureDetector>();
builder.Services.AddSingleton<IQuotaFailureClassifier>(sp =>
    new CompositeQuotaFailureClassifier(sp.GetServices<IAgentQuotaFailureDetector>()));
builder.Services.AddSingleton<IAgentAuthFailureClassifier>(sp =>
{
    var cbOpts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return AuthFailurePatternBinder.Build(cbOpts);
});
// Single composition-root point for the auth-required side-effect handler.
// PipelineRunner and ReleaseService consume the abstraction directly so the
// registry / webhook / logger plumbing is not duplicated across both classes.
builder.Services.AddSingleton<IAgentAuthRequiredHandler>(sp =>
    new AgentAuthRequiredHandler(
        sp.GetRequiredService<IAgentAuthAvailabilityRegistry>(),
        sp.GetRequiredService<IWebhookDispatcher>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentAuthRequiredHandler>()));

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
        RequiredBuildVerificationTimeout = TimeSpan.FromSeconds(Math.Max(60, opts.RequiredBuildVerificationTimeoutSeconds)),
        EmitPlanTestCases = opts.EmitPlanTestCases,
        HostGitIdentity = hostIdentity,
    };
});
builder.Services.AddSingleton<WorkItemRetrier>(sp => new WorkItemRetrier(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ITaskQueue>(),
    sp.GetRequiredService<IGitHost>(),
    sp.GetRequiredService<ILogger<WorkItemRetrier>>(),
    sp.GetRequiredService<IAgentStreamSummaryStore>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IReleaseStore>(),
    sp.GetService<IWorkItemQuestionStore>(),
    sp.GetRequiredService<IAuditProgressStore>()));

builder.Services.AddSingleton(sp =>
{
    var options = new CheckAndActCompletionOptions();
    sp.GetRequiredService<IConfiguration>()
        .GetSection("CodeyBox:CheckAndActCompletion")
        .Bind(options);
    return options;
});
builder.Services.AddSingleton<ICheckAndActCompletionRunner>(sp =>
    new DefaultCheckAndActCompletionRunner(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<CheckAndActCompletionOptions>(),
        sp.GetRequiredService<ILogger<DefaultCheckAndActCompletionRunner>>()));

builder.Services.AddSingleton<WorkItemTerminalTransition>(sp => new WorkItemTerminalTransition(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<ILogger<WorkItemTerminalTransition>>()));
builder.Services.AddSingleton<IWorkItemTerminalTransition>(sp =>
    sp.GetRequiredService<WorkItemTerminalTransition>());
builder.Services.AddSingleton<IWorkItemTerminalRevisionBuilder>(sp =>
    sp.GetRequiredService<WorkItemTerminalTransition>());
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
    sp.GetRequiredService<IWorkItemAutoRetryScheduler>(),
    sp.GetService<AgentClassRouter>(),
    sp.GetService<IAgentFallbackHistoryStore>(),
    sp.GetRequiredService<IQuotaFailureClassifier>(),
    sp.GetRequiredService<IReadOnlyDictionary<AgentKind, IAgentToolCallCounter>>(),
    sp.GetService<ITaskQueue>(),
    sp.GetService<OrchestratorOptions>(),
    sp.GetService<IAgentAvailabilityRegistry>(),
    sp.GetService<IAgentRunningCounters>(),
    sp.GetService<AgentConcurrencyOptions>(),
    sp.GetRequiredService<IPreMergeVerifier>(),
    sp.GetRequiredService<AgentConcurrencySnapshot>(),
    usageStore: sp.GetService<IAgentUsageStore>(),
    budgetProvider: sp.GetService<IAgentBudgetProvider>(),
    incrementalRebase: sp.GetRequiredService<IncrementalRebaseSnapshot>(),
    pipelineTuning: sp.GetRequiredService<PipelineTuningSnapshot>(),
    involvement: sp.GetService<IAgentInvolvementStore>(),
    // Resolve through the live IOptionsMonitor so PostAgentTransitionTimeout
    // edits applied via config hot-reload take effect on the next bounded
    // transition without restart, mirroring the watchdog's own sweep accessor.
    watchdogOptionsAccessor: () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.WorkerProgressWatchdog,
    requiredBuildVerifier: sp.GetRequiredService<IRequiredBuildVerifier>(),
    dispatchAvailability: sp.GetService<IAgentDispatchAvailability>(),
    auditProgress: sp.GetRequiredService<IAuditProgressStore>(),
    agentPauseController: sp.GetRequiredService<IAgentPauseController>(),
    promptPreprocessors: sp.GetRequiredService<AgentPromptPreprocessorChain>(),
    knobRegistry: sp.GetRequiredService<IKnobRegistry>(),
    checkCompletionRunner: sp.GetService<ICheckAndActCompletionRunner>(),
    agentSupervision: sp.GetService<IAgentSupervisionService>(),
    // Resumable session worker (item 3 of the rollout). PipelineRunner
    // sees only the ISessionAgentRunner abstraction and the orchestrator-
    // owned dispatch options; the concrete Claude worker is wired into
    // the abstraction here at the composition root. Composed with
    // Project.ClaudeSession.Enabled and the global Enabled flag — the
    // pipeline keeps the legacy independent-phase path for any item that
    // doesn't opt in to all three.
    sessionAgentRunner: sp.GetService<CodeyBox.Agents.Claude.ClaudeSessionWorker>(),
    sessionDispatchOptions: sp.GetService<CodeyBox.Orchestrator.AgentSessionDispatchOptions>(),
    sessionHandleSnapshot: sp.GetService<CodeyBox.Agents.Claude.ClaudeSessionWorker>() is { } worker
        ? worker.SnapshotPersistedHandle
        : null,
    cancellationRegistry: sp.GetRequiredService<CancellationRegistry>(),
    terminalTransitions: sp.GetRequiredService<IWorkItemTerminalTransition>(),
    terminalRevisionBuilder: sp.GetRequiredService<IWorkItemTerminalRevisionBuilder>(),
    mechanicalFixerComposer: sp.GetRequiredService<ProjectMechanicalFixerComposer>(),
    mechanicalFixerInputProviders: sp.GetServices<IMechanicalFixerInputProvider>(),
    authFailureClassifier: sp.GetRequiredService<IAgentAuthFailureClassifier>(),
    authAvailability: sp.GetRequiredService<IAgentAuthAvailabilityRegistry>(),
    inVmSmokeGate: sp.GetService<IInVmSmokeGate>(),
    authRequiredHandler: sp.GetRequiredService<IAgentAuthRequiredHandler>(),
    authRequiredReader: sp.GetRequiredService<IAgentAuthRequiredAvailabilityReader>(),
    testCaseStore: sp.GetService<ITestCaseStore>(),
    mergeScopeResolver: sp.GetRequiredService<IMergeScopeResolver>(),
    quotaAvailabilityPublisher: sp.GetRequiredService<IAgentQuotaAvailabilityPublisher>()));
builder.Services.AddSingleton<IPipelineRunner>(sp => sp.GetRequiredService<PipelineRunner>());

builder.Services.AddSingleton<QuotaRetryScheduler>(sp => new QuotaRetryScheduler(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<WorkItemRetrier>(),
    sp.GetRequiredService<OrchestratorOptions>(),
    sp.GetRequiredService<ILogger<QuotaRetryScheduler>>(),
    sp.GetRequiredService<IQuotaRetryRouter>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IQueueController>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetService<TimeProvider>(),
    sp.GetRequiredService<IBaselineImageResolver>(),
    autoRetryOptionsAccessor: () =>
    {
        var current = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.AutoRetryOnQuotaFailure;
        return OrchestratorOptionsFactory.BuildAutoRetryOptions(
            current.Enabled,
            current.PeriodicCheckInterval,
            current.ClockDriftSafetyMargin,
            current.MaxAutoRetriesPerWorkItem,
            current.MaxWaitingForQuotaResetSweepBatchSize);
    },
    quotaAvailabilitySignal: sp.GetRequiredService<IAgentQuotaAvailabilitySignal>(),
    agentAvailabilityRecoverySignal: sp.GetRequiredService<IAgentAvailabilityRecoverySignal>(),
    pauseSignal: sp.GetRequiredService<IAgentPauseSignal>()));
builder.Services.AddSingleton<TransientRetryScheduler>(sp => new TransientRetryScheduler(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<WorkItemRetrier>(),
    sp.GetRequiredService<OrchestratorOptions>(),
    sp.GetRequiredService<ILogger<TransientRetryScheduler>>(),
    sp.GetRequiredService<IWorkItemTerminalTransition>(),
    projects: sp.GetRequiredService<IProjectRepository>(),
    queueController: sp.GetRequiredService<IQueueController>(),
    webhooks: sp.GetRequiredService<IWebhookDispatcher>(),
    timeProvider: sp.GetService<TimeProvider>(),
    transientRetryOptionsAccessor: () =>
    {
        var current = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.AutoRetryOnTransientFailure;
        return OrchestratorOptionsFactory.BuildTransientRetryOptions(
            current.Enabled,
            current.PeriodicCheckInterval,
            current.BaseDelay,
            current.Multiplier,
            current.MaxDelay,
            current.MaxAutoRetriesPerWorkItem,
            current.MaxElapsedTime,
            current.JitterMode);
    }));
builder.Services.AddSingleton<IWorkerPoolQuotaRecovery>(sp =>
    sp.GetRequiredService<QuotaRetryScheduler>());
builder.Services.AddSingleton<IQuotaFailureAutoRetryScheduler>(sp =>
    sp.GetRequiredService<QuotaRetryScheduler>());
builder.Services.AddSingleton<IQuotaRetryDispatchPromoter>(sp =>
    sp.GetRequiredService<QuotaRetryScheduler>());
builder.Services.AddSingleton<ITransientFailureAutoRetryScheduler>(sp =>
    sp.GetRequiredService<TransientRetryScheduler>());
builder.Services.AddSingleton<IWorkItemAutoRetryScheduler>(sp =>
    new WorkItemAutoRetryScheduler(
        sp.GetRequiredService<IQuotaFailureAutoRetryScheduler>(),
        sp.GetRequiredService<ITransientFailureAutoRetryScheduler>()));
builder.Services.AddSingleton<AgentQuotaRecoveryProbeMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<QuotaRetryScheduler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentQuotaRecoveryProbeMonitor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransientRetryScheduler>());
builder.Services.AddSingleton<AgentPauseRetryScheduler>(sp => new AgentPauseRetryScheduler(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<WorkItemRetrier>(),
    sp.GetRequiredService<IAgentPauseController>(),
    sp.GetRequiredService<ILogger<AgentPauseRetryScheduler>>(),
    sp.GetRequiredService<IAgentPauseSignal>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentPauseRetryScheduler>());

// IAgentRestoreSignal is implemented by the same AgentAvailabilityRegistry
// singleton — exposed here as the narrow port so the restore-retry scheduler
// can subscribe without depending on the concrete registry type.
builder.Services.AddSingleton<IAgentRestoreSignal>(sp =>
    sp.GetRequiredService<AgentAvailabilityRegistry>());
builder.Services.AddSingleton<AgentRestoreRetryScheduler>(sp => new AgentRestoreRetryScheduler(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<WorkItemRetrier>(),
    () =>
    {
        var live = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.AutoRequeueOnAgentRestore;
        return OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
            live.Enabled,
            live.LookbackGrace,
            live.PostRestoreMargin,
            live.InvolvementTerminalLookback,
            live.InvolvementTerminalClockSkew,
            live.MaxCandidatesPerSweep,
            live.EventQueueCapacity);
    },
    sp.GetRequiredService<ILogger<AgentRestoreRetryScheduler>>(),
    sp.GetRequiredService<IAgentRestoreSignal>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetService<IAgentInvolvementStore>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentRestoreRetryScheduler>());

// --- Failure-class recovery -------------------------------------------------
// Pure deterministic classifier in front of the hosted recovery service.
// Operators wire alternate classifiers (e.g. LLM-precision layer) by replacing
// the singleton registration; the service treats the interface as authoritative.
builder.Services.AddSingleton<ITerminalFailureClassifier, DefaultTerminalFailureClassifier>();
builder.Services.AddSingleton<TerminalFailureRecoveryService>(sp => new TerminalFailureRecoveryService(
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<WorkItemRetrier>(),
    sp.GetRequiredService<ITerminalFailureClassifier>(),
    () =>
    {
        var live = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.TerminalFailureRecovery;
        return OrchestratorOptionsFactory.BuildTerminalFailureRecoveryOptions(
            live.Enabled,
            live.PeriodicCheckInterval,
            live.BaseBackoff,
            live.MaxBackoff,
            live.JitterFraction,
            live.MaxAutoRetriesPerWorkItem);
    },
    sp.GetRequiredService<ILogger<TerminalFailureRecoveryService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<TerminalFailureRecoveryService>());

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
        startupLog,
        cbOpts.AutoRetryOnQuotaFailure.MaxWaitingForQuotaResetSweepBatchSize) with
    {
        AutoRetryOnTransientFailure = OrchestratorOptionsFactory.BuildTransientRetryOptions(
            cbOpts.AutoRetryOnTransientFailure.Enabled,
            cbOpts.AutoRetryOnTransientFailure.PeriodicCheckInterval,
            cbOpts.AutoRetryOnTransientFailure.BaseDelay,
            cbOpts.AutoRetryOnTransientFailure.Multiplier,
            cbOpts.AutoRetryOnTransientFailure.MaxDelay,
            cbOpts.AutoRetryOnTransientFailure.MaxAutoRetriesPerWorkItem,
            cbOpts.AutoRetryOnTransientFailure.MaxElapsedTime,
            cbOpts.AutoRetryOnTransientFailure.JitterMode),
        ShutdownDrainTimeout = Program.ComputeOrchestratorShutdownDrainTimeout(cbOpts.Shutdown.GraceSeconds),
        TerminalFailureRecovery = OrchestratorOptionsFactory.BuildTerminalFailureRecoveryOptions(
            cbOpts.TerminalFailureRecovery.Enabled,
            cbOpts.TerminalFailureRecovery.PeriodicCheckInterval,
            cbOpts.TerminalFailureRecovery.BaseBackoff,
            cbOpts.TerminalFailureRecovery.MaxBackoff,
            cbOpts.TerminalFailureRecovery.JitterFraction,
            cbOpts.TerminalFailureRecovery.MaxAutoRetriesPerWorkItem),
    };
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
    () => sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.DeepAuditMaxConcurrency,
    () => TimeSpan.FromSeconds(sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.DeepAuditRemediationItemTimeoutSeconds),
    agentStreams: sp.GetService<IAgentStreamStore>(),
    promptPreprocessors: sp.GetRequiredService<AgentPromptPreprocessorChain>(),
    authFailureClassifier: sp.GetRequiredService<IAgentAuthFailureClassifier>(),
    authAvailability: sp.GetRequiredService<IAgentAuthAvailabilityRegistry>(),
    authRequiredHandler: sp.GetRequiredService<IAgentAuthRequiredHandler>()));

builder.Services.AddHostedService(sp => new ReleaseMainSyncService(
    sp.GetRequiredService<IReleaseStore>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<IUpstreamRemoteFactory>(),
    sp.GetRequiredService<ILogger<ReleaseMainSyncService>>()));

builder.Services.AddSingleton<StartupRecoveryBarrier>();
builder.Services.AddSingleton<IStartupRecoveryInputBarrier>(
    sp => sp.GetRequiredService<StartupRecoveryBarrier>());
builder.Services.AddSingleton<IStartupRecoveryInputSink>(
    sp => sp.GetRequiredService<StartupRecoveryBarrier>());
builder.Services.AddSingleton<IStartupInitialRecoveryBarrier>(
    sp => sp.GetRequiredService<StartupRecoveryBarrier>());
builder.Services.AddSingleton<IStartupInitialRecoverySink>(
    sp => sp.GetRequiredService<StartupRecoveryBarrier>());
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
    sp.GetRequiredService<ReleaseService>(),
    sp.GetRequiredService<AgentConcurrencyOptions>(),
    sp.GetRequiredService<AgentConcurrencySnapshot>(),
    sp.GetRequiredService<IBaselineImageResolver>(),
    sp.GetRequiredService<OrchestratorProgressClock>(),
    sp.GetRequiredService<QuotaRouterOptions>(),
    sp.GetRequiredService<BudgetDeferralRecheckSnapshot>(),
    sp.GetRequiredService<IStartupRecoveryInputBarrier>(),
    sp.GetRequiredService<IStartupInitialRecoverySink>(),
    dispatchAvailability: sp.GetRequiredService<IAgentDispatchAvailability>(),
    knobRegistry: sp.GetRequiredService<IKnobRegistry>(),
    quotaRetryDispatchPromoter: sp.GetRequiredService<IQuotaRetryDispatchPromoter>(),
    quotaRetryAdmissionRouter: sp.GetRequiredService<IQuotaRetryAdmissionRouter>()));
builder.Services.AddSingleton<IInfrastructureDeferralScheduler>(
    sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddSingleton<IRefactorProjectGateStatusProvider>(
    sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddSingleton<IRefactorProjectDispatchGate>(
    sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddSingleton<WorkerPoolHealthCoordinator>(sp => new WorkerPoolHealthCoordinator(
    sp.GetRequiredService<OrchestratorService>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ITaskQueue>(),
    sp.GetRequiredService<ILogger<WorkerPoolHealthCoordinator>>(),
    sp.GetRequiredService<IProjectRepository>(),
    sp.GetRequiredService<IQueueController>(),
    sp.GetRequiredService<IAgentRegistry>(),
    sp.GetRequiredService<IAgentRoutingReadiness>(),
    sp.GetRequiredService<IAgentDispatchAvailability>(),
    sp.GetRequiredService<IRefactorProjectDispatchGate>()));
builder.Services.AddSingleton<IWorkerPoolHealthSource>(sp =>
    sp.GetRequiredService<WorkerPoolHealthCoordinator>());
builder.Services.AddSingleton<IAgentCapacitySnapshot>(sp =>
    sp.GetRequiredService<WorkerPoolHealthCoordinator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
// R8.1: expose the orchestrator as IShutdownDispatchGate so the
// SandboxShutdownTeardownService can pause new dispatch before the per-VM
// teardown begins (incident 2026-05-29 fix).
builder.Services.AddSingleton<IShutdownDispatchGate>(
    sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadWorkerReaper>());
// Run the watchdog as a hosted service. AttachWorkerPoolSlotReleaser is called
// after OrchestratorService is fully constructed so the watchdog can release
// a wedged worker's pool slot synchronously.
builder.Services.AddHostedService(sp =>
{
    var watchdog = sp.GetRequiredService<WorkerProgressWatchdog>();
    watchdog.AttachWorkerPoolSlotReleaser(sp.GetRequiredService<OrchestratorService>());
    return watchdog;
});
builder.Services.AddHostedService(sp =>
{
    var watchdog = sp.GetRequiredService<ItemStaleProgressWatchdog>();
    watchdog.AttachWorkerPoolSlotReleaser(sp.GetRequiredService<OrchestratorService>());
    return watchdog;
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerPoolHealthWatchdog>());
// R8-core/R8.1: tear down in-flight sandboxes on graceful shutdown using the
// operator-selected SandboxTeardownMode. Suspend is opt-in and writes resume
// bookkeeping so the next process can reattach; Stop is the default and avoids
// the unreliable RAM-snapshot path while preserving PipelineRunner's checkpoint
// recovery path for active work.
// The shutdown half is lifecycle-bound (StoppingAsync). Startup resume defaults
// to background mode and starts after ApplicationStarted so a wedged multipassd
// cannot keep Kestrel offline; OrchestratorService waits for startup recovery
// input before its dead-worker startup recovery sweep. Blocking resume mode
// still runs through IHostedLifecycleService.StartingAsync, so the host awaits
// it natively.
//
// R8.1 (incident 2026-05-29): the shutdown teardown service is wired with the
// orchestrator as an IShutdownDispatchGate so it pauses new dispatch BEFORE
// snapshotting the active sandbox set — without that ordering, the dispatch
// loop keeps creating new sandboxes that race the snapshot. Teardown mode is
// operator-tunable via CodeyBox:Shutdown:SandboxTeardownMode (Stop / Suspend /
// Dispose); default Stop to avoid multipass suspend/qemu-lock wedges unless an
// operator opts in.
// Resolve teardown mode through IOptionsMonitor at shutdown time so a hot config
// edit affects the next graceful shutdown.
builder.Services.AddHostedService(sp =>
{
    var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    var shutdown = optionsMonitor.CurrentValue.Shutdown;
    return new SandboxShutdownTeardownService(
        sp.GetRequiredService<ISandboxProvider>(),
        sp.GetRequiredService<IWorkItemStore>(),
        sp.GetRequiredService<ILogger<SandboxShutdownTeardownService>>(),
        nonSuspendTeardownTimeout: TimeSpan.FromSeconds(Math.Max(1, shutdown.GraceSeconds)),
        dispatchGate: sp.GetService<IShutdownDispatchGate>(),
        teardownModeAccessor: () => optionsMonitor.CurrentValue.Shutdown.SandboxTeardownMode);
});
// Startup reconciler is registered before the resume handler and runs as a
// background sweep so Multipass recovery cannot keep Kestrel offline. It skips
// VMs with live SuspendedVmName mappings; those are owned by the resume handler
// below, while orphaned Suspending VMs from a prior unclean shutdown get an
// early cleanup attempt before regular leak handling has to deal with them.
builder.Services.AddHostedService(sp => new StartupSandboxReconciliationService(
    sp.GetService<ISandboxProvider>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ILogger<StartupSandboxReconciliationService>>()));
builder.Services.AddHostedService(sp => new SandboxResumeOnStartupService(
    sp.GetService<ISandboxProvider>(),
    sp.GetRequiredService<IWorkItemStore>(),
    sp.GetRequiredService<ILogger<SandboxResumeOnStartupService>>(),
    () =>
    {
        var shutdown = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>().CurrentValue.Shutdown;
        return Program.BuildSandboxStartupResumeOptions(shutdown);
    },
    sp.GetRequiredService<IStartupRecoveryInputSink>(),
    sp.GetRequiredService<IInfrastructureDeferralScheduler>(),
    sp.GetRequiredService<IHostApplicationLifetime>()));

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
        sanitizerConfig: sp.GetRequiredService<CodeyBox.Core.ClaudeThinkingBlockSanitizerConfig>(),
        networkTolerance: sp.GetRequiredService<CodeyBox.Core.AgentNetworkToleranceSnapshot>(),
        costCalculator: sp.GetRequiredService<AgentCostCalculator>(),
        pricingState: pricingState,
        budgetReloader: sp.GetRequiredService<IAgentBudgetConfigReloadable>(),
        incrementalRebase: sp.GetRequiredService<IncrementalRebaseSnapshot>(),
        pipelineTuning: sp.GetRequiredService<PipelineTuningSnapshot>(),
        budgetDeferralRecheck: sp.GetRequiredService<BudgetDeferralRecheckSnapshot>(),
        quotaRouterOptions: sp.GetRequiredService<QuotaRouterOptions>(),
        coverage: sp.GetService<IInVmSmokeCoveragePolicy>(),
        smokeOptions: sp.GetRequiredService<SmokeOptionsSnapshot>(),
        pauses: sp.GetRequiredService<IAgentPauseController>(),
        agents: sp.GetRequiredService<IAgentRegistry>(),
        transitionHealth: sp.GetRequiredService<TransitionHealthOptionsSnapshot>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentConfigHotReload>());
builder.Services.AddHostedService(sp => new StartupSmokeProbeService(
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetServices<IAgentSmokeProbe>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<SmokeOptionsSnapshot>(),
    sp.GetRequiredService<ILogger<StartupSmokeProbeService>>(),
    sp.GetService<ISmokeAvailabilityRegistry>(),
    sp.GetRequiredService<InVmSmokeOptions>()));
builder.Services.AddSingleton<PeriodicSmokeProbeService>(sp => new PeriodicSmokeProbeService(
    sp.GetRequiredService<ICredentialProvider>(),
    sp.GetServices<IAgentSmokeProbe>(),
    sp.GetRequiredService<IWebhookDispatcher>(),
    sp.GetRequiredService<SmokeOptionsSnapshot>(),
    sp.GetRequiredService<AvailabilityOptions>(),
    sp.GetRequiredService<ISmokeAvailabilityRegistry>(),
    sp.GetRequiredService<ILogger<PeriodicSmokeProbeService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PeriodicSmokeProbeService>());
// Expose the host-side on-demand probe through the core port so the admin
// /smoke endpoint depends on the abstraction, not the background-service type.
builder.Services.AddSingleton<IHostSmokeProbeRunner>(sp => sp.GetRequiredService<PeriodicSmokeProbeService>());

// Periodic metric samplers. The host runs each IMetricSampler on its own loop,
// re-reading the sampler's Enabled / Interval each cycle so plugin-side
// hot-reload takes effect without a host restart. The first sampler shipping
// against this extension point is the statistics plugin's quota sampler — but
// the host is sampler-agnostic; further plugins (throughput, audit pass rate,
// cost-over-time) can register additional IMetricSampler implementations.
// See docs/plugins.md (IMetricSampler) and docs/statistics-plugin.md.
builder.Services.AddHostedService<MetricSamplerHost>();
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

builder.Services.AddSingleton<CompositeManagedSandboxProvider>(BuildManagedSandboxLifecycleProvider);
builder.Services.AddSingleton<IManagedSandboxLifecycle>(sp => sp.GetRequiredService<CompositeManagedSandboxProvider>());
builder.Services.AddSingleton<SandboxLeakReaper>(sp =>
{
    // Live accessor: thresholds and policy fields (LeakAgeThreshold, AutoDispose,
    // MaxConcurrentAutoDispose, PreemptRetention) are re-read on every sweep so
    // operator edits take effect without restart. CheckInterval and Enabled are
    // sampled once at PeriodicTimer construction — limitation documented on the
    // fields themselves.
    var monitor = sp.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
    return new SandboxLeakReaper(
        sp.GetRequiredService<IManagedSandboxLifecycle>(),
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

// Resolve the Prometheus exporter options once so middleware and endpoint
// mapping see a consistent view. The options were validated at host build
// time; this just reads the snapshot. Toggling Enabled at runtime is NOT
// supported (the exporter is wired into the metric provider builder).
var prometheusOpts = builder.Configuration
    .GetSection("CodeyBox:Otel:Prometheus")
    .Get<PrometheusExporterOptions>() ?? new PrometheusExporterOptions();

// When the Prometheus scrape endpoint is enabled AND RequireApiKey is off,
// exempt EXACTLY that path from API-key auth. Scrapers (Prometheus, conky,
// curl-from-cron) typically cannot send the Bearer token. The exemption is
// exact-path only so a sibling route or descendant can't piggy-back on it.
var prometheusAnonymousPaths = (prometheusOpts.Enabled && !prometheusOpts.RequireApiKey)
    ? new[] { prometheusOpts.Path }
    : Array.Empty<string>();

app.UseApiKeyAuth(
    anonymousPrefixes: ["/healthz", "/webhooks/"],
    anonymousExactPaths: prometheusAnonymousPaths);

// Idempotency-Key support for mutating endpoints — see IdempotencyMiddleware
// for behaviour. Ordered after auth so unauthenticated requests can't poison
// the cache, and before endpoint mapping so all mutating handlers benefit.
IdempotencyMiddleware.Use(app);

WorkItemEndpoints.Map(app);
TestCaseEndpoints.Map(app);
E2eRunEndpoints.Map(app);
WorkItemAttachmentEndpoints.Map(app);
TaskTemplateEndpoints.Map(app);
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
AgentSupervisionEndpoints.Map(app);
SandboxEndpoints.Map(app);
SandboxResourceUsageEndpoints.Map(app);
BaselineEndpoints.Map(app);
QuotaRetryStatusEndpoints.Map(app);
QuotaHistoryEndpoints.Map(app);
CapacityEndpoints.Map(app);
ResetCreditEndpoints.Map(app);
ResetAdviceEndpoints.Map(app);
ReleaseEndpoints.Map(app);
AgentPauseEndpoints.Map(app);

// Prometheus scrape endpoint — registered only when the exporter is enabled
// so the surface is invisible (route not on the table) by default. Mapped
// after the API-key middleware so the exemption above takes effect when
// RequireApiKey=false; when RequireApiKey=true, the middleware enforces the
// Bearer token on this path like any other endpoint.
if (prometheusOpts.Enabled)
{
    app.MapPrometheusScrapingEndpoint(prometheusOpts.Path);
}

app.MapHub<AgentStdoutHub>("/hubs/agent-stdout");

app.MapGet("/quota", async (
    IEnumerable<IAgentQuotaProbe> probes,
    AgentClassRouter? router,
    IQuotaFailureStore? failureStore,
    QuotaRouterOptions options,
    IAgentQuotaGate quotaGate,
    IAgentBudgetProvider? budgetProvider,
    IAgentPauseController? agentPauses,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    IReadOnlyList<QuotaFailureObservation> failures = failureStore is null
        ? Array.Empty<QuotaFailureObservation>()
        : await failureStore.ListRecentAsync(TimeSpan.FromMinutes(60), now, ct);
    var pausedStates = agentPauses is null
        ? Array.Empty<AgentPauseState>()
        : await agentPauses.ListPausedAsync(ct);
    var pausedByKey = pausedStates.ToDictionary(
        s => s.AgentInstanceId ?? s.Agent.Value,
        s => s,
        StringComparer.OrdinalIgnoreCase);
    var pausedByAgent = pausedStates
        .Where(s => s.AgentInstanceId is null)
        .ToDictionary(s => s.Agent, s => s);
    var probeByKind = probes
        .Where(p => p is not PayPerApiQuotaProbe and not NullQuotaProbe)
        .ToDictionary(p => p.Kind);
    var representedProbeKeys = new HashSet<(AgentKind Agent, string? ModelId)>();

    var snapshots = new List<object>();
    var kindAggregateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    async Task AddSnapshotAsync(AgentMembership member, string? classId, string? classDisplayName)
    {
        if (!probeByKind.TryGetValue(member.Agent, out var probe))
            return;

        representedProbeKeys.Add((member.Agent, member.ModelId));
        var snapshot = await probe.GetAvailabilityAsync(member, ct);
        var recentFailuresForProbe = failures
            .Where(f => f.Agent == member.Agent && f.ObservedAt >= now - options.ObservedFailureWindow)
            .ToList();
        var recentDefaultFailure = recentFailuresForProbe.Any(f => f.ModelId is null);
        var recentFailure = recentFailuresForProbe.Count > 0;
        var paused = pausedByKey.TryGetValue(member.RouteKey, out var pause)
            || pausedByAgent.TryGetValue(member.Agent, out pause);
        var modelKeys = snapshot.PerModel.Keys
            .Concat(recentFailuresForProbe.Where(f => f.ModelId is not null).Select(f => f.ModelId!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool WouldAllow(AgentMembership gateMember, bool hasRecentFailure) =>
            quotaGate.Allows(
                gateMember,
                snapshot,
                now,
                hasRecentFailure,
                "recent observed quota failure");
        snapshots.Add(new
        {
            agent = member.Agent.Value,
            agentInstanceId = member.RouteKey,
            instanceId = member.InstanceId,
            classId,
            classDisplayName,
            billing = member.Billing.ToString(),
            modelId = member.ModelId,
            latestSnapshot = snapshot,
            observedFailuresLast60m = failures
                .Where(f => f.Agent == member.Agent)
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
            paused,
            pausedReason = pause?.PausedReason,
            pausedAt = pause?.PausedAt,
            pausedBy = pause?.PausedBy,
            pauseExpiresAt = pause?.ExpiresAt,
            dispatchStatus = paused ? "paused" : "quota",
            dispatchReason = paused ? $"paused by operator: {pause?.PausedReason}" : null,
            wouldAllow = !paused && WouldAllow(member with { ModelId = null }, recentFailure),
            defaultModelWouldAllow = !paused && WouldAllow(member with { ModelId = null }, recentDefaultFailure),
            perModelWouldAllow = modelKeys.ToDictionary(
                modelId => modelId,
                modelId =>
                {
                    if (paused) return false;
                    var modelMember = member with { ModelId = modelId };
                    var modelHasRecentFailure = recentFailuresForProbe.Any(f =>
                        f.Agent == probe.Kind &&
                        string.Equals(f.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
                    return WouldAllow(modelMember, modelHasRecentFailure);
                },
                StringComparer.OrdinalIgnoreCase),
        });
        kindAggregateCounts[member.Agent.Value] =
            kindAggregateCounts.TryGetValue(member.Agent.Value, out var count) ? count + 1 : 1;
    }

    if (router is not null)
    {
        var seenMemberRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in router.SnapshotConfiguredMembers())
        {
            if (entry.Member.Billing != AgentBilling.Subscription)
                continue;
            if (!seenMemberRows.Add($"{entry.Member.RouteKey}\0{entry.Member.ModelId ?? string.Empty}"))
                continue;
            await AddSnapshotAsync(entry.Member, entry.ClassId, entry.DisplayName);
        }
    }

    foreach (var probe in probeByKind.Values)
    {
        if (representedProbeKeys.Any(k => k.Agent == probe.Kind && k.ModelId is null))
            continue;

        await AddSnapshotAsync(new AgentMembership
        {
            Agent = probe.Kind,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        }, classId: null, classDisplayName: null);
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
        intraKindRoutingPolicy = options.IntraKindRoutingPolicy.ToString(),
        probes = snapshots,
        kindAggregates = kindAggregateCounts
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new
            {
                agent = kv.Key,
                instances = kv.Value,
            })
            .ToList(),
        pausedAgents = pausedStates.Select(s => new
        {
            agent = s.Agent.Value,
            agentInstanceId = s.AgentInstanceId,
            paused = s.Paused,
            pausedAt = s.PausedAt,
            pausedReason = s.PausedReason,
            pausedBy = s.PausedBy,
            expiresAt = s.ExpiresAt,
            updatedAt = s.UpdatedAt,
        }).ToList(),
        budgets,
        budgetsError,
        observedFailuresLast60m = failures,
    });
});

app.MapGet("/concurrency", async (
    OrchestratorService orchestrator,
    AgentClassRouter router,
    IAgentBurnEstimator burnEstimator,
    IAgentAvailabilityRegistry? availability,
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
            status = est.Status.ToString(),
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
            status = f.BurnEstimateStatus.ToString(),
            fitInWindow = double.IsFinite(f.FitInWindow) ? f.FitInWindow : (double?)null,
            runningOnAgent = f.RunningOnAgent,
        }),
        agentAvailability = availabilityView.Select(s => new
        {
            agent = s.Agent.Value,
            excluded = s.Excluded,
            reason = s.Reason,
            consecutiveFastFails = s.ConsecutiveFastFails,
            consecutiveNoChanges = s.ConsecutiveNoChanges,
            lastSmokePassedAt = s.LastSmokePassedAt,
            lastSmokeFailedAt = s.LastSmokeFailedAt,
            lastFastFailAt = s.LastFastFailAt,
            lastNoChangesAt = s.LastNoChangesAt,
        }),
    });
});

// ── Admin: agent availability ─────────────────────────────────────────────
// Operators use these endpoints after correcting a smoke / fast-fail
// exclusion (e.g. installing the missing binary, rotating credentials) to
// either trigger an immediate probe or to clear the fast-fail counter.

app.MapPost("/admin/agent/{name}/smoke", async (
    string name,
    IHostSmokeProbeRunner hostProbe,
    IInVmSmokeGate inVmGate,
    IAgentAvailabilityRegistry registry,
    CancellationToken ct) =>
{
    // Canonical AgentKind values are lowercase ("cursor", "claude", ...) so a
    // capitalised typo (POST /admin/agent/Cursor/smoke) used to return 404
    // even when the underlying probe was registered. Normalise so case
    // never silently shadows the operator's intent.
    var kind = new AgentKind(name.ToLowerInvariant());

    // Host-side credential probe (env-var presence on the orchestrator host).
    var hostResult = await hostProbe.ProbeAsync(kind, ct);

    // In-VM gate: the real in-sandbox CLI verification the host probe could not
    // be (exit 127 / auth-path drift / workspace-trust). Force a re-probe so an
    // operator who just corrected such an issue clears the in-VM bench here
    // rather than waiting for the next background sweep — otherwise "smoke ok"
    // from the host probe could mask a standing in-VM exclusion. Routes through
    // the IInVmSmokeGate port, not the concrete prober. Null when in-VM smoke is
    // disabled or no in-VM probe is registered for this agent.
    var inVmAvailability = await inVmGate.ForceProbeAsync(kind, ct);

    // 404 only when neither layer knows this agent — a typo, not a healthy agent
    // that simply has one probe layer.
    if (hostResult is null && inVmAvailability is null)
        return Results.NotFound(new { error = $"no smoke probe registered for agent '{name}'" });

    var availability = registry.GetAvailability(kind);
    object? hostSmoke = hostResult is null ? null : new
    {
        ok = hostResult.Ok,
        reason = hostResult.FailureReason,
        durationMs = (long)hostResult.Duration.TotalMilliseconds,
    };
    object? inVmSmoke = inVmAvailability is null ? null : new
    {
        available = inVmAvailability.Available,
        reason = inVmAvailability.Reason,
    };
    return Results.Ok(new
    {
        agent = kind.Value,
        smoke = hostSmoke,
        inVmSmoke,
        availability = new
        {
            available = availability.Available,
            reason = availability.Reason,
        },
    });
});

app.MapPost("/admin/agent/{name}/reset", (string name, IAgentAvailabilityRegistry registry, IAgentRegistry agents, IAgentAvailabilityReset reset) =>
{
    // Mirror /smoke: normalise to lowercase so case-mismatched names match the
    // canonical kinds returned by IAgentRegistry.Available.
    var kind = new AgentKind(name.ToLowerInvariant());
    // Validate the agent is actually registered; without this, a typo
    // (e.g. /admin/agent/curser/reset) silently returns 200 and the operator
    // never realises the call did nothing.
    if (!agents.Available.Contains(kind))
        return Results.NotFound(new { error = $"unknown agent '{name}'" });
    // Single reset port: clears the registry AND invalidates the in-VM smoke
    // cache together, so a stale cached pass can't reconcile straight back onto
    // the registry before the operator's fix is re-verified.
    reset.Reset(kind);
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

app.MapGet("/admin/agents/availability", (IAgentAvailabilityRegistry registry) =>
{
    return Results.Ok(new
    {
        agents = registry.Snapshot().Select(s => new
        {
            agent = s.Agent.Value,
            excluded = s.Excluded,
            reason = s.Reason,
            consecutiveFastFails = s.ConsecutiveFastFails,
            consecutiveNoChanges = s.ConsecutiveNoChanges,
            lastSmokePassedAt = s.LastSmokePassedAt,
            lastSmokeFailedAt = s.LastSmokeFailedAt,
            lastFastFailAt = s.LastFastFailAt,
            lastNoChangesAt = s.LastNoChangesAt,
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
        /// the <c>Running</c> state, and for each blocking cloud-init readiness
        /// probe. Defaults to 3 minutes. Bump on hosts that observe boot
        /// contention under concurrent launches.
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

        /// <summary>
        /// When true, disables the detached agent-output HTTP-ingest transport
        /// (#290) so all agent execs use the attached <c>multipass exec</c> pipe.
        /// Repair switch for the merge-phase regression where the detached
        /// transport drops a freshly-created sandbox's agent stdout + exit code.
        /// </summary>
        public bool DisableAgentOutputHttpIngest { get; set; }

        /// <summary>
        /// When true, multipass sandbox disposal makes one short best-effort
        /// in-VM exec before delete/preserve teardown to capture resource
        /// metrics for capacity planning. Default false.
        /// </summary>
        public bool CaptureResourceMetrics { get; set; }

        /// <summary>
        /// Timeout for the best-effort resource metrics capture exec. Values
        /// less than or equal to zero use the provider default (5 seconds).
        /// </summary>
        public int ResourceMetricsCaptureTimeoutSeconds { get; set; } =
            (int)MultipassSandboxOptions.DefaultResourceMetricsCaptureTimeout.TotalSeconds;
    }

    /// <summary>
    /// Configuration for <c>CodeyBox:SandboxProvider=multipass-remote</c>.
    /// Drives the SSH-backed multipass provider that runs each work-item VM
    /// on a remote host while the orchestrator brain stays local.
    /// </summary>
    public sealed class MultipassRemoteSandboxConfig
    {
        /// <summary>SSH destination passed verbatim to <c>ssh &lt;target&gt;</c>. Required.</summary>
        public string? SshTarget { get; set; }

        /// <summary>OpenSSH binary. Default <c>ssh</c> (resolved via $PATH).</summary>
        public string? SshBinary { get; set; }

        /// <summary>Optional SSH port override.</summary>
        public int? SshPort { get; set; }

        /// <summary>Identity file path. Null = use whatever <c>~/.ssh/config</c> / agent resolves.</summary>
        public string? SshKeyPath { get; set; }

        /// <summary>Extra <c>-o Key=Value</c> options appended verbatim. Validated to look like <c>Key=Value</c> with no whitespace.</summary>
        public IList<string>? ExtraSshOptions { get; set; }

        /// <summary>
        /// When true, host key trust-on-first-use (<c>StrictHostKeyChecking=accept-new</c>).
        /// Leave false in production — let an unknown host key fail loudly.
        /// </summary>
        public bool AcceptUnknownHostKeys { get; set; }

        /// <summary>OpenSSH <c>ServerAliveInterval</c> seconds. Null = provider default (30).</summary>
        public int? ServerAliveIntervalSeconds { get; set; }

        /// <summary>OpenSSH <c>ServerAliveCountMax</c>. Null = provider default (6).</summary>
        public int? ServerAliveCountMax { get; set; }

        /// <summary>OpenSSH <c>ConnectTimeout</c> seconds. Null = provider default (20).</summary>
        public int? ConnectTimeoutSeconds { get; set; }

        /// <summary>Local tar binary used by the staging pipeline. Default <c>tar</c>.</summary>
        public string? LocalTarBinary { get; set; }

        /// <summary>Absolute path to <c>multipass</c> on the remote host.</summary>
        public string? RemoteMultipassPath { get; set; }

        /// <summary>Absolute path on the remote host where per-sandbox staging dirs live.</summary>
        public string? RemoteStagingRoot { get; set; }

        /// <summary>Default multipass image alias when SandboxSpec.ImageReference is empty.</summary>
        public string? DefaultImage { get; set; }

        /// <summary>Deadline for waiting on the VM to reach <c>Running</c>. Null = provider default.</summary>
        public TimeSpan? VmStartTimeout { get; set; }

        /// <summary>Deadline for waiting on the VM to reach <c>Stopped</c>. Null = provider default.</summary>
        public TimeSpan? VmStopTimeout { get; set; }

        /// <summary>VM-state polling interval used during waits. Null = provider default.</summary>
        public TimeSpan? VmStateCheckInterval { get; set; }

        /// <summary>
        /// Naming prefix the provider applies to every VM it creates on the
        /// remote host. Used by <c>multipass list</c> filtering and by the
        /// leak-dispose safety check that refuses to delete arbitrary VMs.
        /// </summary>
        public string? VmNamePrefix { get; set; }
    }

    /// <summary>
    /// Per-host configuration for the E2E replay pool. Capacity belongs here,
    /// not on <see cref="MultipassRemoteSandboxConfig"/>, so the normal coding
    /// fleet's remote provider config remains focused on SSH/provider settings.
    /// </summary>
    public sealed class E2eMultipassRemoteHostConfig
    {
        /// <summary>Remote Multipass provider settings for this E2E host.</summary>
        public MultipassRemoteSandboxConfig RemoteSandbox { get; set; } = new();

        /// <summary>
        /// Per-host lease cap. Null defaults to one replay per host; the global
        /// <c>E2eExecution:MaxConcurrent</c> still caps aggregate pool pressure.
        /// </summary>
        public int? MaxConcurrent { get; set; }

        public string? SshTarget { get => RemoteSandbox.SshTarget; set => RemoteSandbox.SshTarget = value; }
        public string? SshBinary { get => RemoteSandbox.SshBinary; set => RemoteSandbox.SshBinary = value; }
        public int? SshPort { get => RemoteSandbox.SshPort; set => RemoteSandbox.SshPort = value; }
        public string? SshKeyPath { get => RemoteSandbox.SshKeyPath; set => RemoteSandbox.SshKeyPath = value; }
        public IList<string>? ExtraSshOptions { get => RemoteSandbox.ExtraSshOptions; set => RemoteSandbox.ExtraSshOptions = value; }
        public bool AcceptUnknownHostKeys { get => RemoteSandbox.AcceptUnknownHostKeys; set => RemoteSandbox.AcceptUnknownHostKeys = value; }
        public int? ServerAliveIntervalSeconds { get => RemoteSandbox.ServerAliveIntervalSeconds; set => RemoteSandbox.ServerAliveIntervalSeconds = value; }
        public int? ServerAliveCountMax { get => RemoteSandbox.ServerAliveCountMax; set => RemoteSandbox.ServerAliveCountMax = value; }
        public int? ConnectTimeoutSeconds { get => RemoteSandbox.ConnectTimeoutSeconds; set => RemoteSandbox.ConnectTimeoutSeconds = value; }
        public string? LocalTarBinary { get => RemoteSandbox.LocalTarBinary; set => RemoteSandbox.LocalTarBinary = value; }
        public string? RemoteMultipassPath { get => RemoteSandbox.RemoteMultipassPath; set => RemoteSandbox.RemoteMultipassPath = value; }
        public string? RemoteStagingRoot { get => RemoteSandbox.RemoteStagingRoot; set => RemoteSandbox.RemoteStagingRoot = value; }
        public string? DefaultImage { get => RemoteSandbox.DefaultImage; set => RemoteSandbox.DefaultImage = value; }
        public TimeSpan? VmStartTimeout { get => RemoteSandbox.VmStartTimeout; set => RemoteSandbox.VmStartTimeout = value; }
        public TimeSpan? VmStopTimeout { get => RemoteSandbox.VmStopTimeout; set => RemoteSandbox.VmStopTimeout = value; }
        public TimeSpan? VmStateCheckInterval { get => RemoteSandbox.VmStateCheckInterval; set => RemoteSandbox.VmStateCheckInterval = value; }
        public string? VmNamePrefix { get => RemoteSandbox.VmNamePrefix; set => RemoteSandbox.VmNamePrefix = value; }

        public static implicit operator E2eMultipassRemoteHostConfig(MultipassRemoteSandboxConfig config)
            => new() { RemoteSandbox = config };
    }

    /// <summary>
    /// Configuration for <c>CodeyBox:SandboxProvider=sprites</c>. The Sprites
    /// rc30 create API accepts only name, capacity wait, and URL auth settings;
    /// CPU/RAM/region values are retained as explicit no-op operator hints.
    /// </summary>
    public sealed class SpritesSandboxConfig
    {
        /// <summary>Sprites REST/WebSocket API base URL. Default <c>https://api.sprites.dev</c>.</summary>
        public string ApiBaseUrl { get; set; } = "https://api.sprites.dev";

        /// <summary>Environment variable read for the bearer token. Default <c>SPRITES_TOKEN</c>.</summary>
        public string TokenEnvironmentVariable { get; set; } = "SPRITES_TOKEN";

        /// <summary>Managed sprite name prefix. Must remain <c>codeybox-</c>-compatible for leak reaping.</summary>
        public string NamePrefix { get; set; } = SpritesSandboxProvider.DefaultNamePrefix;

        /// <summary>Whether create should wait for capacity before returning.</summary>
        public bool WaitForCapacity { get; set; }

        /// <summary>
        /// URL auth setting sent on create. Sprites supports <c>sprite</c> (default,
        /// bearer-token-authenticated per-sprite URL) and <c>public</c>. Setting
        /// <c>public</c> drops access control on the sprite's per-sprite HTTP endpoint
        /// while a work item runs inside it (source tree, agent process, staged mounts),
        /// leaving it reachable without the bearer token — a foot-gun that silently
        /// removes network-level access control on a live work sandbox. Prefer
        /// <c>sprite</c> unless you have an explicit reason and a separate boundary.
        /// </summary>
        public string UrlAuth { get; set; } = "sprite";

        /// <summary>Safety ceiling for paged list calls during leak reaping.</summary>
        public int MaxListPages { get; set; } = 100;

        /// <summary>
        /// Test-only escape hatch for local mock servers. Production Sprites
        /// traffic carries bearer tokens and exec environment variables, so
        /// the provider rejects <c>http://</c> unless this is explicitly true.
        /// </summary>
        public bool AllowUnsafeHttp { get; set; }

        /// <summary>
        /// Explicit downgrade for non-secret tmpfs mounts. Sprites has no
        /// tmpfs API, so the default is to reject <c>SandboxMount.Tmpfs</c>.
        /// When true, non-credential tmpfs paths are created as ordinary
        /// sprite directories and remain subject to sprite persistence until
        /// teardown succeeds.
        /// </summary>
        public bool AllowPersistentTmpfsDowngrade { get; set; }

        /// <summary>
        /// Shell commands run inside each fresh sprite to provision it (rc30
        /// create has no image/baseline field). They execute BEFORE the work
        /// item's egress allow-list is applied and BEFORE mounts are staged,
        /// so they run against OPEN egress until the default-deny policy is
        /// posted after provisioning completes and before the agent runs. This
        /// is deliberate: these operator-trusted commands need to reach package
        /// registries (npm/apt/curl), and no agent code or credential material
        /// is present yet. Use to install agent CLIs and project toolchains.
        /// Note: with open egress, a curl-piped installer can reach arbitrary
        /// hosts; only list commands you trust.
        /// </summary>
        public List<string> SetupCommands { get; set; } = [];

        /// <summary>Sprites egress allow-list domains keyed by CodeyBox network profile name.</summary>
        public Dictionary<string, List<string>> NetworkProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Maximum base64 stdout bytes accepted for directory sync-back.</summary>
        public int MaxSyncArchiveBase64Bytes { get; set; } = 128 * 1024 * 1024;

        /// <summary>Maximum compressed gzip/tar bytes accepted for directory sync-back.</summary>
        public int MaxSyncArchiveBytes { get; set; } = 96 * 1024 * 1024;

        /// <summary>Maximum summed regular-file bytes accepted for directory sync-back.</summary>
        public long MaxSyncArchiveExpandedBytes { get; set; } = 512L * 1024 * 1024;

        /// <summary>Maximum tar entries accepted for directory sync-back.</summary>
        public int MaxSyncArchiveEntries { get; set; } = 200_000;

        /// <summary>Maximum base64 stdout bytes accepted for single-file sync-back.</summary>
        public int MaxFileSyncBase64Bytes { get; set; } = 64 * 1024 * 1024;

        /// <summary>Maximum decoded bytes accepted for single-file sync-back.</summary>
        public long MaxFileSyncBytes { get; set; } = 48L * 1024 * 1024;

        /// <summary>Operator hint only; Sprites rc30 has no create-time CPU field.</summary>
        public int? DefaultCpuCount { get; set; }

        /// <summary>Operator hint only; Sprites rc30 has no create-time RAM field.</summary>
        public long? DefaultMemoryBytes { get; set; }

        /// <summary>Operator hint only; Sprites rc30 has no create-time region field.</summary>
        public string? Region { get; set; }
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
    ///   <c>TemplateDirectory</c>, <c>MaxTemplateChecks</c>, <c>AgentConcurrency</c>, <c>AgentClasses</c>, <c>AgentScoreModifiers</c>,
    ///   <c>AgentBurnEstimator</c>, <c>AgentPauses</c>, <c>AgentPricing</c>, <c>SqliteWriteGate</c>,
    ///   <c>Smoke.Enabled</c>, <c>DeadWorker</c>
    ///   (per-sweep), <c>Shutdown.SandboxResumeMode</c>,
    ///   <c>Shutdown.SandboxResumeTimeout</c>,
    ///   <c>Shutdown.SandboxAdoptionDeadlineSeconds</c>, <c>SandboxLeak</c>
    ///   (thresholds, per-sweep),
    ///   <c>AuditLog.RetainedDays</c> (DB retention, per-sweep), and the
    ///   sandbox launch fields (<c>Multipass*</c>, <c>SandboxNetworkProfiles</c>,
    ///   per-launch), and <c>Shutdown.SandboxTeardownMode</c> (next graceful
    ///   shutdown).</item>
    /// <item><b>Startup-only and rejected</b> on reload by
    ///   <see cref="ImmutableCodeyBoxOptionsValidator"/>:
    ///   <c>SandboxProvider</c>, <c>StateDatabasePath</c>,
    ///   <c>GitRootDirectory</c>, <c>AgentStreams.Path</c>,
    ///   <c>WorkerPool.MaxConcurrentSandboxes</c>,
    ///   <c>EnableSharedUpstreamMirror</c>, and
    ///   <c>SharedUpstreamMirrorDirectory</c>. The retaining
    ///   options-monitor cache keeps the startup value visible to consumers
    ///   after a rejected reload.</item>
    /// <item><b>Startup-only by capture</b> — bound into a downstream
    ///   singleton (PipelineOptions, OrchestratorOptions, QuotaRouterOptions,
    ///   Smoke cache TTL, AvailabilityOptions, WebhookEventBroadcaster,
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
        public bool EnableSharedUpstreamMirror { get; set; } = false;
        public string SharedUpstreamMirrorDirectory { get; set; } = "_upstream-mirror";
        public string StateDatabasePath { get; set; } = "/var/lib/codeybox/state.db";
        public SqliteWriteGateOptions SqliteWriteGate { get; set; } = new();
        public string TemplateDirectory { get; set; } = "templates";
        public const int DefaultMaxTemplateChecks = 256;
        public const int MaximumMaxTemplateChecks = 1000;
        public int MaxTemplateChecks { get; set; } = DefaultMaxTemplateChecks;
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
            "release-assets.githubusercontent.com",
            "semgrep.dev",
            "registry.semgrep.dev",
            "api.semgrep.dev",
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

        /// <summary>
        /// Per-agent network tolerance settings. Keyed by <c>AgentKind.Value</c>
        /// (case-insensitive). The runner uses these values to override the CLI
        /// network tolerance settings (e.g. retries, timeouts). Edits hot-reload via
        /// <see cref="Core.AgentNetworkToleranceSnapshot"/> and take effect on the
        /// next dispatched agent run. Defaults: Codex request retries = 8,
        /// Codex stream retries = 15, Codex stream idle timeout unset, Claude
        /// API timeout unset. Timeout values are capped at the API's maximum
        /// work-attempt window (480 minutes).
        /// </summary>
        public Dictionary<string, AgentNetworkToleranceOptions?> AgentNetworkTolerance { get; set; } =
            AgentNetworkToleranceOptions.DefaultByAgent();

        /// <summary>
        /// Operator-configured per-agent pauses. Keyed by agent kind value.
        /// Applied at startup and hot-reloaded by <see cref="AgentConfigHotReload"/>.
        /// Runtime API/work-item pauses remain persisted in SQLite separately;
        /// removing an entry here resumes only pauses owned by config.
        /// </summary>
        public Dictionary<string, AgentPauseConfig> AgentPauses { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Graceful shutdown drain and preemption timing.</summary>
        public ShutdownOptions Shutdown { get; set; } = new();

        /// <summary>
        /// Deprecated compatibility section for older configs that set
        /// <c>CodeyBox:PlanReview:UseAuditors</c>. Auditor-backed plan review
        /// is always enabled by the pipeline; this option is accepted only so
        /// strict unbound-key validation does not reject existing deployments.
        /// </summary>
        public PlanReviewOptions PlanReview { get; set; } = new();

        /// <summary>Heartbeat and dead-worker reaper configuration.</summary>
        public DeadWorkerOptions DeadWorker { get; set; } = new();

        /// <summary>
        /// Lifecycle-wide worker progress watchdog: catches the
        /// "heartbeating-but-no-progress" wedge (pre-agent setup hang OR
        /// post-agent commit/transition hang) that neither the dead-worker
        /// reaper nor <c>WorkTimeout</c> covers. Hot-reloadable: edits to
        /// <c>ProgressTimeout</c>, <c>AutoRecover</c>,
        /// <c>ProcessCpuProgressSignalEnabled</c>,
        /// <c>ActiveSandboxProgressSignalEnabled</c>, and
        /// <c>PostAgentTransitionTimeout</c> take effect on the next sweep
        /// without restart. <c>CheckInterval</c> is sampled at
        /// PeriodicTimer construction and requires a restart to change.
        /// </summary>
        public WorkerProgressWatchdogOptions WorkerProgressWatchdog { get; set; } = new();

        /// <summary>
        /// Dispatcher-level watchdog for under-filled pools with runnable work.
        /// Hot-reloadable: timeout and recovery settings are read on each sweep.
        /// </summary>
        public WorkerPoolHealthWatchdogOptions WorkerPoolHealthWatchdog { get; set; } = new();

        public const int DefaultMaxBulkItems = 1000;
        public const int MaximumMaxBulkItems = 10_000;
        public int MaxBulkItems { get; set; } = DefaultMaxBulkItems;
        public int UpstreamPushMaxAttempts { get; set; } = 5;
        public int UpstreamPushBackoffSeconds { get; set; } = 15;
        public double PhaseAbsoluteTimeoutMultiplier { get; set; } = 3.0;

        /// <summary>
        /// Hard ceiling (seconds) on a single required-build verification
        /// across every phase / resume path. Defaults to 15 minutes; raise
        /// for very large .NET solutions or lower to fail faster during
        /// infrastructure degradation. Floor 60 s. Captured once at startup
        /// into <see cref="PipelineOptions.RequiredBuildVerificationTimeout"/>;
        /// edits require restart.
        /// </summary>
        public int RequiredBuildVerificationTimeoutSeconds { get; set; } = 900;

        /// <summary>
        /// When true (default), approving a plan (the <c>plan</c> knob on) emits
        /// and reconciles a test case per declared plan scenario, linked to the
        /// work item. Unplanned items never reach this path and are unaffected.
        /// Set false to keep the planning phase without materialising test cases.
        /// Captured once at startup into
        /// <see cref="PipelineOptions.EmitPlanTestCases"/>; edits require restart.
        /// </summary>
        public bool EmitPlanTestCases { get; set; } = true;

        /// <summary>
        /// Maximum concurrent release deep-audit phases across all releases.
        /// Bounds LLM/sandbox resource usage. Default 4.
        /// Hot-reloadable: read on each deep-audit start attempt.
        /// </summary>
        public int DeepAuditMaxConcurrency { get; set; } = 4;

        /// <summary>
        /// Maximum seconds to wait for a single remediation work item to reach a
        /// terminal state before failing the deep audit. Default 1800 (30 min).
        /// Hot-reloadable: read on each remediation dispatch.
        /// </summary>
        public int DeepAuditRemediationItemTimeoutSeconds { get; set; } = 1800;

        /// <summary>Pipeline-runner quota-fallback and retry tuning. Hot-reloadable.</summary>
        public PipelineTuningOptions PipelineTuning { get; set; } = new();

        /// <summary>Per-project budget-cap deferral recheck intervals. Hot-reloadable.</summary>
        public BudgetDeferralRecheckOptions BudgetDeferralRecheck { get; set; } = new();

        /// <summary>
        /// Which sandbox provider to use. One of: <c>multipass</c>,
        /// <c>multipass-remote</c>, <c>sprites</c>, <c>bubblewrap</c>,
        /// <c>process</c>.
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
        /// when SandboxProvider=multipass. Use only for top-level cloud-init
        /// directives CodeyBox does not generate (e.g. <c>packages:</c> or
        /// <c>apt:</c>). Use <see cref="MultipassExtraRuncmd"/> for install
        /// commands; duplicate generated blocks such as <c>runcmd:</c> and
        /// <c>write_files:</c> are rejected by the Multipass provider.
        /// </summary>
        public string? MultipassExtraCloudInit { get; set; }

        /// <summary>Multipass sandbox launch-time readiness tuning.</summary>
        public MultipassSandboxConfig MultipassSandbox { get; set; } = new();

        /// <summary>
        /// Configuration for <c>SandboxProvider=multipass-remote</c>. Optional;
        /// only consumed when that provider is selected.
        /// </summary>
        public MultipassRemoteSandboxConfig? MultipassRemoteSandbox { get; set; }

        /// <summary>
        /// Configuration for <c>SandboxProvider=sprites</c>. Optional; provider
        /// defaults to the public sprites.dev API and <c>SPRITES_TOKEN</c>.
        /// </summary>
        public SpritesSandboxConfig? Sprites { get; set; }

        /// <summary>
        /// Dedicated remote Multipass configuration for the E2E replay pool.
        /// This intentionally does not fall back to
        /// <see cref="MultipassRemoteSandbox"/> when E2E execution is enabled:
        /// replay load must target a separately sized cheap CPU pool rather
        /// than the coding-agent remote fleet.
        /// </summary>
        public E2eMultipassRemoteHostConfig? E2eMultipassRemoteSandbox { get; set; }

        /// <summary>
        /// Multi-host E2E replay fleet. When populated, this list supersedes
        /// <see cref="E2eMultipassRemoteSandbox"/> and the E2E pool distributes
        /// clone-per-test leases across the configured cheap CPU hosts.
        /// </summary>
        public List<E2eMultipassRemoteHostConfig> E2eMultipassRemoteSandboxes { get; set; } = [];

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
        /// Configurable list of package cache seeds to copy from the host to the baseline VM at bake time.
        /// </summary>
        public List<PackageCacheSeedConfig> MultipassPackageCacheSeeds { get; set; } = [];

        /// <summary>
        /// Executable binaries to ship into the baseline VM at bake time. Each
        /// entry copies one host file to an absolute VM path with mode 0755 and
        /// optional symlinks (e.g. into <c>/usr/local/bin</c>). Use when the
        /// upstream installer is non-durable (a <c>curl … | bash</c> URL has
        /// drifted or now serves HTML) and the operator already has a vetted
        /// copy of the binary staged on the host.
        /// </summary>
        public List<ExecutableProvisionConfig> MultipassExecutableProvisions { get; set; } = [];

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

        /// <summary>Config-gated live human supervision and injection channel.</summary>
        public AgentSupervisionOptions AgentSupervision { get; set; } = new();

        /// <summary>Read-only analytics parser configuration for captured agent streams.</summary>
        public AgentStreamParserOptions AgentStreamAnalysis { get; set; } = new();

        /// <summary>
        /// Agent class definitions for quota-aware routing. Each class lists one or
        /// more agent members in preference order. See docs/agent-classes.md.
        /// </summary>
        public List<AgentClassOptions> AgentClasses { get; set; } = [];

        /// <summary>
        /// Optional reusable agent instances. AgentClass members can reference
        /// these by InstanceId, allowing multiple subscriptions for the same
        /// agent kind to be pooled independently.
        /// </summary>
        public List<AgentInstanceOptions> AgentInstances { get; set; } = [];

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
        /// Operator-extensible per-agent auth/login-prompt output patterns.
        /// Keys are agent kind values (e.g. <c>antigravity</c>); each entry adds
        /// a case-insensitive stderr/stdout substring to the built-in login-prompt
        /// detector. Keep stdout patterns narrowly tied to CLI login transcripts
        /// because stdout can contain model-produced task text.
        /// </summary>
        public Dictionary<string, List<AuthFailurePatternOptions>> AuthFailurePatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Time-of-day score modifiers. Applied as small effective-score adjustments
        /// to act as tiebreakers between near-equivalent models during peak cost windows.
        /// See docs/configuration.md for the schedule schema.
        /// </summary>
        public AgentScoreModifiersOptions AgentScoreModifiers { get; set; } = new();

        /// <summary>Credential smoke test tuning knobs.</summary>
        public SmokeConfig Smoke { get; set; } = new();

        /// <summary>
        /// Pipeline transition-health metric tuning. Controls the
        /// <c>/fleet/transition-health</c> endpoint's rolling window and
        /// optional "last N transitions" cap. Hot-reloadable.
        /// </summary>
        public TransitionHealthConfig TransitionHealth { get; set; } = new();

        /// <summary>Agent token pricing for cost estimation. See docs/cost-reporting.md.</summary>
        public AgentPricingOptions AgentPricing { get; set; } = new();

        /// <summary>Monthly cost-budget alert sweep configuration. See docs/budget-alerts.md.</summary>
        public BudgetAlertOptions BudgetAlerts { get; set; } = new();

        /// <summary>Automatic retry for quota-failed items.</summary>
        public AutoRetryOnQuotaFailureConfig AutoRetryOnQuotaFailure { get; set; } = new();

        /// <summary>Automatic retry for transient transport/network failed items.</summary>
        public AutoRetryOnTransientFailureConfig AutoRetryOnTransientFailure { get; set; } = new();

        /// <summary>
        /// Operator-extensible transient transport/network stderr/stdout
        /// patterns appended to <see cref="AgentFailureClassifier"/>'s built-in
        /// conservative defaults.
        /// </summary>
        public List<string> TransientNetworkFailurePatterns { get; set; } = [];

        /// <summary>
        /// Failure-class recovery policy. Classifies every terminal failure
        /// (Failed, AuditFailed, MergeConflictResolutionFailed) and routes by
        /// class — see <see cref="TerminalFailureRecoveryConfig"/>. Replaces the
        /// external operator chaperone's blunt requeue reflex.
        /// </summary>
        public TerminalFailureRecoveryConfig TerminalFailureRecovery { get; set; } = new();

        /// <summary>
        /// Auto-requeue policy for infra-failed work items on agent recovery —
        /// see <see cref="AutoRequeueOnAgentRestoreConfig"/>. Enabled by
        /// default so restored agents automatically drain outage-window
        /// infra-failed victims through normal routing.
        /// </summary>
        public AutoRequeueOnAgentRestoreConfig AutoRequeueOnAgentRestore { get; set; } = new();

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

        /// <summary>
        /// Claude thinking-block transcript sanitizer configuration.
        /// Bound from <c>CodeyBox:ClaudeThinkingBlockSanitizer</c>.
        /// Gate behind this flag (default <c>true</c>) while the upstream
        /// thinking-block immutability bug is open; disable once Anthropic
        /// ships a fix.
        /// </summary>
        public ClaudeThinkingBlockSanitizerOptions ClaudeThinkingBlockSanitizer { get; set; } = new();

        /// <summary>
        /// Claude resumable-session worker configuration. Bound from
        /// <c>CodeyBox:ClaudeSession</c>. The session worker is OFF by default;
        /// every Claude dispatch keeps using the existing one-shot
        /// <c>ClaudeAgentRunner</c> until an operator opts in here. See
        /// <see cref="CodeyBox.Agents.Claude.ClaudeSessionWorkerOptions"/> for
        /// behaviour.
        /// </summary>
        public ClaudeSessionOptions ClaudeSession { get; set; } = new();

        /// <summary>
        /// End-to-end replay execution pool configuration. Bound from
        /// <c>CodeyBox:E2eExecution</c>. Sizes the cheap CPU-only VM pool that
        /// runs committed e2e-replay artifacts; intentionally separate from the
        /// orchestrator's coding-worker fleet so E2E load never competes for
        /// agent-dispatch slots. Disabled by default — operators opt in per
        /// deployment.
        /// </summary>
        public E2eExecutionOptions E2eExecution { get; set; } = new();

        /// <summary>
        /// Work-item attachments storage configuration. Hot-reloadable: the
        /// root directory, limits, and TTL are read on every upload, sweep,
        /// and orphan scan.
        /// </summary>
        public AttachmentsOptions Attachments { get; set; } = new();
    }

    /// <summary>
    /// Deprecated compatibility plan-review options. The review loop is always
    /// auditor-backed; <see cref="UseAuditors"/> is ignored.
    /// </summary>
    public sealed class PlanReviewOptions
    {
        public bool UseAuditors { get; set; } = true;
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

        /// <summary>
        /// Unbound CodeyBox configuration key detection. Catches typos /
        /// stale renames under <c>CodeyBox:*</c> that the .NET binder would
        /// otherwise drop silently.
        /// </summary>
        public UnboundKeyValidationOptions UnboundKeys { get; set; } = new();
    }

    /// <summary>
    /// Tuning for the startup unbound-key inspector. Bound from
    /// <c>CodeyBox:ConfigValidation:UnboundKeys</c>.
    /// </summary>
    public sealed class UnboundKeyValidationOptions
    {
        /// <summary>
        /// Master switch. Default <c>true</c> — every startup walks the
        /// operator-provided <c>CodeyBox:*</c> tree against the typed options
        /// graph.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// <c>"strict"</c> (default) throws at startup; <c>"warn"</c>
        /// downgrades to a single warning log. Any other value is treated as
        /// <c>"strict"</c>.
        /// </summary>
        public string Mode { get; set; } = "strict";

        /// <summary>
        /// Operator-supplied full configuration paths under <c>CodeyBox:*</c>
        /// whose subtrees are skipped entirely (exact, case-insensitive
        /// match). Use for extension namespaces bound outside
        /// <c>CodeyBoxOptions</c> / <c>ProjectsOptions</c>. The built-in
        /// defaults already cover the framework-internal separately-bound
        /// sections (BuildScriptAudit, Mutation, Plugins, …) by walking them
        /// with their typed root POCO so typos inside still surface.
        /// </summary>
        public List<string> AdditionalExemptPaths { get; set; } = new();
    }

    /// <summary>
    /// Per-agent operator pause config. Bound from
    /// <c>CodeyBox:AgentPauses:{agent}</c>.
    /// </summary>
    public sealed class AgentPauseConfig
    {
        /// <summary>
        /// Whether this config entry should pause the agent. Default true so
        /// a present entry is a pause declaration.
        /// </summary>
        public bool Paused { get; set; } = true;

        /// <summary>Operator-visible pause reason.</summary>
        public string? Reason { get; set; }

        /// <summary>Optional absolute auto-resume time.</summary>
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>
        /// Optional relative auto-resume duration in seconds, measured from the
        /// reload that applies a changed config value.
        /// </summary>
        public int? DurationSeconds { get; set; }
    }

    /// <summary>
    /// Claude thinking-block transcript sanitizer configuration.
    /// Bound from <c>CodeyBox:ClaudeThinkingBlockSanitizer</c>.
    /// </summary>
    public sealed class ClaudeThinkingBlockSanitizerOptions
    {
        /// <summary>
        /// Master switch. Default <c>true</c> while the upstream
        /// thinking-block immutability bug is open. Disable once
        /// Anthropic ships a fix.
        /// </summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Configuration for a package cache seed to be copied into the baseline VM.
    /// </summary>
    public sealed class PackageCacheSeedConfig
    {
        public string HostSourcePath { get; set; } = string.Empty;
        public string VmDestPath { get; set; } = string.Empty;
        public double? MaxSizeMB { get; set; }
    }

    /// <summary>
    /// Config-bound shape of <see cref="ExecutableProvisionOptions"/>.
    /// </summary>
    public sealed class ExecutableProvisionConfig
    {
        public string HostSourcePath { get; set; } = string.Empty;
        public string VmDestPath { get; set; } = string.Empty;
        public List<string> VmSymlinks { get; set; } = [];
        public string? Label { get; set; }
    }

    /// <summary>
    /// Claude resumable-session worker configuration. Bound from
    /// <c>CodeyBox:ClaudeSession</c>. See
    /// <see cref="CodeyBox.Agents.Claude.ClaudeSessionWorker"/>.
    /// </summary>
    public sealed class ClaudeSessionOptions
    {
        /// <summary>
        /// Master switch. Default <c>false</c> — Claude dispatches keep using
        /// the legacy one-shot runner unless an operator opts in here.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// When true (default), each <c>SendTurnAsync</c> emits a
        /// <see cref="CodeyBox.Agents.Claude.ClaudeSessionTurnMetrics"/>
        /// snapshot to the registered metrics sink so the cache_read share is
        /// observable. Disable for A/B comparisons against the one-shot path.
        /// </summary>
        public bool EmitTurnMetrics { get; set; } = true;

        /// <summary>
        /// Command-delivery + billing channel for new Claude session turns.
        /// Accepts <c>"print"</c> (default — today's <c>claude --print --resume</c>
        /// path) or <c>"acp"</c> (Agent Client Protocol, drives
        /// <c>claude --ide</c> off the metered <c>-p</c> pool). Case-insensitive,
        /// hot-reloadable: subsequent turns observe the new value on the next
        /// dispatch. Invalid values fall back to <c>"print"</c>.
        /// </summary>
        public string Transport { get; set; } = "print";

        /// <summary>
        /// Optional per-agent-class-member transport overrides. Map key is
        /// the agent-class-member name (case-insensitive); value is
        /// <c>"print"</c> or <c>"acp"</c>. Edit via
        /// <c>CodeyBox:ClaudeSession:TransportOverridesByAgentClassMember:&lt;member&gt;</c>.
        /// </summary>
        public Dictionary<string, string> TransportOverridesByAgentClassMember { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional per-project transport overrides. Map key is the project id
        /// (case-insensitive); value is <c>"print"</c> or <c>"acp"</c>.
        /// </summary>
        public Dictionary<string, string> TransportOverridesByProject { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Session-path enhancement: inject a single pre-emptive self-review
        /// turn after the initial work turn (before the formal audit) using
        /// the composed checklist from the project's auditors. Default off.
        /// </summary>
        public PreemptiveSelfReviewConfig PreemptiveSelfReview { get; set; } = new();
    }

    /// <summary>
    /// Sub-config block for <see cref="ClaudeSessionOptions.PreemptiveSelfReview"/>.
    /// </summary>
    public sealed class PreemptiveSelfReviewConfig
    {
        /// <summary>
        /// Default <c>false</c>. When <c>true</c>, session-mode work items run
        /// one extra warm-session turn after the initial work turn carrying
        /// the auditor-derived self-review guidance. The formal audit remains
        /// independent and owns the pass/fail decision.
        /// </summary>
        public bool Enabled { get; set; }
    }

    public sealed class AutoRetryOnQuotaFailureConfig
    {
        public bool Enabled { get; set; } = false;
        public string PeriodicCheckInterval { get; set; } = "00:05:00";
        public string ClockDriftSafetyMargin { get; set; } = "00:02:00";
        public int MaxAutoRetriesPerWorkItem { get; set; } = 3;
        public int MaxWaitingForQuotaResetSweepBatchSize { get; set; } =
            AutoRetryOnQuotaFailureOptions.DefaultWaitingForQuotaResetSweepBatchSize;
    }

    public sealed class AutoRetryOnTransientFailureConfig
    {
        public bool Enabled { get; set; } = true;
        public string PeriodicCheckInterval { get; set; } = "00:01:00";
        public string BaseDelay { get; set; } = "00:00:30";
        public double Multiplier { get; set; } = 2.0;
        public string MaxDelay { get; set; } = "00:15:00";
        public int MaxAutoRetriesPerWorkItem { get; set; } = 5;
        public string MaxElapsedTime { get; set; } = "01:00:00";
        public string JitterMode { get; set; } = "Full";
    }

    /// <summary>
    /// Bound from <c>CodeyBox:TerminalFailureRecovery</c>. Hot-reloadable.
    /// All TimeSpan-shaped values use the standard <c>HH:MM:SS</c> format.
    /// See <see cref="CodeyBox.Orchestrator.TerminalFailureRecoveryOptions"/>
    /// for runtime semantics.
    /// </summary>
    public sealed class TerminalFailureRecoveryConfig
    {
        public bool Enabled { get; set; } = false;
        public string PeriodicCheckInterval { get; set; } = "00:05:00";
        public string BaseBackoff { get; set; } = "00:01:00";
        public string MaxBackoff { get; set; } = "00:30:00";
        public double JitterFraction { get; set; } = 0.2;
        public int MaxAutoRetriesPerWorkItem { get; set; } = 3;
    }

    /// <summary>
    /// Bound from <c>CodeyBox:AutoRequeueOnAgentRestore</c>. Hot-reloadable.
    /// See <see cref="CodeyBox.Orchestrator.AgentRestoreRetryScheduler"/> for
    /// runtime semantics.
    /// </summary>
    public sealed class AutoRequeueOnAgentRestoreConfig
    {
        /// <summary>
        /// Master switch for restore-driven infra-failure sweeps. Default
        /// <c>true</c>; set false only when an operator wants to perform these
        /// recovery sweeps manually.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Non-negative <see cref="TimeSpan"/> string (for example
        /// <c>"00:30:00"</c>) subtracted from the restore event's outage start
        /// when selecting failed candidates.
        /// </summary>
        public string LookbackGrace { get; set; } = "00:30:00";

        /// <summary>
        /// Non-negative <see cref="TimeSpan"/> string (for example
        /// <c>"00:05:00"</c>) added after the restore timestamp to absorb
        /// ordering races between terminal writes and the restore signal.
        /// </summary>
        public string PostRestoreMargin { get; set; } = "00:05:00";

        /// <summary>
        /// Non-negative <see cref="TimeSpan"/> string (for example
        /// <c>"00:15:00"</c>) used to match a failed agent-involvement row to a
        /// nearby terminal work-item write.
        /// </summary>
        public string InvolvementTerminalLookback { get; set; } = "00:15:00";

        /// <summary>
        /// Non-negative <see cref="TimeSpan"/> string (for example
        /// <c>"00:01:00"</c>) allowing failed involvement rows to land slightly
        /// after the terminal work-item update they explain.
        /// </summary>
        public string InvolvementTerminalClockSkew { get; set; } = "00:01:00";

        /// <summary>
        /// Positive cap applied inside the work-item store before buffering
        /// restore-sweep candidates. Default 500.
        /// </summary>
        public int MaxCandidatesPerSweep { get; set; } = AgentRestoreRetryOptions.DefaultMaxCandidatesPerSweep;

        /// <summary>
        /// Positive bounded-channel capacity for pending restore notifications.
        /// Default 128.
        /// </summary>
        public int EventQueueCapacity { get; set; } = AgentRestoreRetryOptions.DefaultEventQueueCapacity;

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
        /// Whether startup sandbox resume runs in the background after host
        /// startup begins, or blocks startup while still applying
        /// <see cref="SandboxResumeTimeout"/> per VM. Default Background keeps
        /// the HTTP control plane available even when a prior VM is wedged.
        /// Hot-reloadable for the next resume sweep.
        /// Bound from <c>CodeyBox:Shutdown:SandboxResumeMode</c>.
        /// </summary>
        public SandboxResumeMode SandboxResumeMode { get; set; } = SandboxResumeMode.Background;

        /// <summary>
        /// Caller-side cap for each persisted VM's startup resume call. This is
        /// distinct from the provider's own VM start/readiness timeout: it also
        /// protects the orchestrator from providers or daemons that do not
        /// observe cancellation. Hot-reloadable for pending resume attempts.
        /// Bound from <c>CodeyBox:Shutdown:SandboxResumeTimeout</c>.
        /// </summary>
        public TimeSpan SandboxResumeTimeout { get; set; } = SuspendTimeoutPolicy.DefaultFloor;

        /// <summary>
        /// Upper bound on how long the startup resume handler waits for an
        /// adopted in-VM agent process to finish post-resume. Long enough that
        /// a real LLM call can finish, short enough that a wedged agent does
        /// not delay the startup resume sweep indefinitely. Defaults to
        /// <see cref="SandboxStartupResumePolicy.DefaultAdoptionDeadline"/>.
        /// Hot-reloadable for pending adoption attempts.
        /// Bound from <c>CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds</c>.
        /// </summary>
        public int SandboxAdoptionDeadlineSeconds { get; set; } =
            (int)SandboxStartupResumePolicy.DefaultAdoptionDeadline.TotalSeconds;

        /// <summary>
        /// How to tear down in-flight worker sandboxes during graceful shutdown.
        /// Hot-reloadable: read by the shutdown handler when graceful shutdown
        /// begins, so an operator can switch modes without restarting first.
        /// Default <see cref="SandboxTeardownMode.Stop"/>: avoid RAM snapshots
        /// and cleanly stop/preserve the VM on graceful shutdown. Operators who
        /// explicitly want RAM-state preservation can opt in to
        /// <see cref="SandboxTeardownMode.Suspend"/>; it freezes RAM via
        /// <c>multipass suspend</c> and resumes on next startup, but can hit the
        /// qemu disk-image write-lock wedge that caused the 2026-05-29 incident.
        /// <see cref="SandboxTeardownMode.Dispose"/> remains available for full
        /// delete-and-purge teardown.
        /// </summary>
        public SandboxTeardownMode SandboxTeardownMode { get; set; } = SandboxTeardownMode.Stop;
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
        /// In-process Prometheus scrape exporter. Off by default; opt in by
        /// setting <c>CodeyBox:Otel:Prometheus:Enabled=true</c>. Runs alongside
        /// the OTLP push exporter (when <see cref="Enabled"/> is also true), or
        /// stands alone as the only metric exporter when OTLP is disabled.
        /// </summary>
        public PrometheusExporterOptions Prometheus { get; set; } = new();

        /// <summary>
        /// Validates the options, throwing <see cref="InvalidOperationException"/> when
        /// <see cref="Enabled"/> is true and the configuration is incomplete or invalid.
        /// Also validates <see cref="Prometheus"/> when the scrape exporter is enabled.
        /// Safe to call when both are disabled — no-ops immediately.
        /// </summary>
        public static void Validate(OtelOptions opts)
        {
            PrometheusExporterOptions.Validate(opts.Prometheus);

            if (!opts.Enabled) return;

            // The endpoint may come from appsettings OR the standard
            // OTEL_EXPORTER_OTLP_ENDPOINT env var, so telemetry can be enabled
            // from the conventional env-only bootstrap without duplicating it
            // under CodeyBox:Otel. Only one of the two is required.
            var envEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            var hasEnvEndpoint = !string.IsNullOrWhiteSpace(envEndpoint);

            if (string.IsNullOrWhiteSpace(opts.OtlpEndpoint) && !hasEnvEndpoint)
                throw new InvalidOperationException(
                    "CodeyBox:Otel:OtlpEndpoint or the OTEL_EXPORTER_OTLP_ENDPOINT environment " +
                    "variable must be set when CodeyBox:Otel:Enabled=true.");

            // Validate the appsettings endpoint when supplied; an env-only
            // endpoint is validated by the OTel SDK at export time.
            if (!string.IsNullOrWhiteSpace(opts.OtlpEndpoint)
                && (!Uri.TryCreate(opts.OtlpEndpoint, UriKind.Absolute, out var endpointUri)
                    || endpointUri.Scheme is not "http" and not "https"))
                throw new InvalidOperationException(
                    $"CodeyBox:Otel:OtlpEndpoint '{opts.OtlpEndpoint}' is not a valid http/https URL.");

            // Skip appsettings ExportProtocol validation when OTEL_EXPORTER_OTLP_PROTOCOL
            // is set: ConfigureOtlp defers to the env var at export time (the SDK reads
            // it directly), so a stale/invalid appsettings value is harmless and must
            // not block startup of an env-only bootstrap.
            var envProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
            if (string.IsNullOrWhiteSpace(envProtocol)
                && opts.ExportProtocol is not "grpc" and not "httpprotobuf")
                throw new InvalidOperationException(
                    $"CodeyBox:Otel:ExportProtocol '{opts.ExportProtocol}' is not valid. " +
                    "Expected 'grpc' or 'httpprotobuf'.");
        }

        /// <summary>
        /// Parses an <c>OTEL_RESOURCE_ATTRIBUTES</c>-style value
        /// (<c>key1=val1,key2=val2</c>) into resource attribute pairs. Returns an
        /// empty list for null/blank input. Malformed entries (no <c>=</c>, blank
        /// key) are skipped.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, object>> ParseResourceAttributesEnv(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            var result = new List<KeyValuePair<string, object>>();
            foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0) continue;
                var key = pair[..idx].Trim();
                var value = pair[(idx + 1)..].Trim();
                if (key.Length == 0) continue;
                result.Add(new KeyValuePair<string, object>(key, value));
            }
            return result;
        }
    }

    /// <summary>
    /// In-process Prometheus scrape exporter configuration. Bound from
    /// <c>CodeyBox:Otel:Prometheus</c>. Off by default. Exposes the SAME
    /// metric instruments the OTLP push pipeline carries, rendered in
    /// Prometheus exposition format (dots → underscores, tags → labels), so a
    /// Prometheus scraper / curl / kioskish dashboard widget can read fleet
    /// state directly without an OTLP collector in the path.
    /// </summary>
    /// <remarks>
    /// The exporter is wired into the OpenTelemetry metrics pipeline at host
    /// build time, so toggling <see cref="Enabled"/> requires a restart;
    /// hot-reloading the option does nothing on its own. <see cref="Path"/>
    /// and <see cref="RequireApiKey"/> are also captured at startup.
    /// </remarks>
    public sealed class PrometheusExporterOptions
    {
        /// <summary>
        /// Master switch. Default <c>false</c>. When false, the Prometheus
        /// exporter is not registered with the meter provider and the scrape
        /// endpoint is not mapped, so the surface is invisible (no 401, no
        /// 404 — the route simply does not exist on the routing table).
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Scrape endpoint path. Default <c>/metrics</c>. Must begin with
        /// <c>/</c> and contain at least one non-slash character.
        /// </summary>
        public string Path { get; set; } = "/metrics";

        /// <summary>
        /// When <c>true</c>, the scrape endpoint requires the same
        /// <c>Authorization: Bearer &lt;key&gt;</c> header as every other API
        /// endpoint. When <c>false</c> (default), the scrape path is exempted
        /// from API-key auth at request time. The exemption is scoped to the
        /// EXACT configured <see cref="Path"/> — no descendants, no sibling
        /// routes, no other verbs picking up the bypass.
        /// </summary>
        /// <remarks>
        /// The default <c>false</c> assumes the deployment binds the API to
        /// localhost (or a private network) for the operator's own scrape
        /// stack. The exposed series carry fleet/quota gauges and runtime /
        /// HTTP instrumentation — non-sensitive operational data. If the API
        /// is reachable from a public network, set <c>RequireApiKey=true</c>.
        /// </remarks>
        public bool RequireApiKey { get; set; } = false;

        /// <summary>
        /// Validates the options, throwing <see cref="InvalidOperationException"/>
        /// when <see cref="Enabled"/> is true and the configuration is invalid.
        /// No-ops when disabled.
        /// </summary>
        public static void Validate(PrometheusExporterOptions opts)
        {
            if (!opts.Enabled) return;

            // Reject blank or malformed paths early. Without this, the
            // Prometheus middleware would happily map "/" or "" and either
            // shadow the root or fail at runtime with an opaque routing error.
            if (string.IsNullOrWhiteSpace(opts.Path)
                || opts.Path[0] != '/'
                || opts.Path.TrimStart('/').Length == 0)
                throw new InvalidOperationException(
                    $"CodeyBox:Otel:Prometheus:Path '{opts.Path}' is not valid. " +
                    "Expected a path beginning with '/' (e.g. '/metrics').");
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

        /// <summary>
        /// Tuning for the in-VM smoke prober (binary-presence + auth checks run
        /// inside a sandbox cloned from the active baseline). Bound from
        /// <c>CodeyBox:Smoke:InVm</c>.
        /// </summary>
        public InVmSmokeConfig InVm { get; set; } = new();
    }

    /// <summary>
    /// Config binding for the transition-health metric. Bound from
    /// <c>CodeyBox:TransitionHealth</c>. All fields are hot-reloadable; see
    /// <see cref="TransitionHealthOptionsSnapshot"/>.
    /// </summary>
    public sealed class TransitionHealthConfig
    {
        /// <summary>Enable the <c>/fleet/transition-health</c> endpoint and computation. Default true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Rolling window in hours. Default 24. Floor 5 / 60 (5 minutes),
        /// ceiling 30 * 24 (30 days). Values outside the range are clamped at
        /// binding time.
        /// </summary>
        public double WindowHours { get; set; } = 24.0;

        /// <summary>
        /// Optional "last N transitions" cap. Null = use the wall-clock window
        /// only. When set, the score is computed over the most recent
        /// <c>MaxTransitions</c> transitions regardless of how long ago they
        /// happened. Floor 50, ceiling 100_000.
        /// </summary>
        public int? MaxTransitions { get; set; }
    }

    /// <summary>Config binding for the in-VM smoke prober.</summary>
    public sealed class InVmSmokeConfig
    {
        /// <summary>Enable or disable the in-VM smoke prober. Default true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Per-step exec timeout inside the sandbox, in seconds. Default 30.</summary>
        public int StepTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Hard wall-clock timeout on VM provisioning (the
        /// <see cref="ISandboxProvider.CreateAsync"/> call), in seconds. Default 120.
        /// The per-step exec timeout cannot bound this because no sandbox exists
        /// to exec into; a wedged baseline clone would otherwise hang the
        /// dispatch gate forever. Non-positive disables it (tests / synthetic
        /// clocks). See <see cref="InVmSmokeOptions.ProvisionTimeoutSeconds"/>.
        /// </summary>
        public int ProvisionTimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Top-level deadline on the dispatch-gate call (defect-in-depth net for
        /// any inner step the per-operation timeouts don't cover), in seconds.
        /// Default 180. Non-positive disables it. See
        /// <see cref="InVmSmokeOptions.GateDeadlineSeconds"/>.
        /// </summary>
        public int GateDeadlineSeconds { get; set; } = 180;

        /// <summary>Result cache TTL per baseline ref, in minutes. Default 60.</summary>
        public int CacheTtlMinutes { get; set; } = 60;

        /// <summary>Background sweep interval in seconds. Default 300 (5 min); set 0 to disable.</summary>
        public int SweepIntervalSeconds { get; set; } = 300;

        /// <summary>
        /// Fail-closed dispatch-gate policy. When true (the default), an in-VM
        /// probe that cannot reach a verdict (provisioning/exec/timeout/credential
        /// fault) temporarily benches the agent so the router never dispatches to
        /// an unverified CLI; the bench self-heals on the next successful probe.
        /// Set false only on infra so flaky that benching disrupts more than the
        /// exit-127 / auth cascade it guards against. See
        /// <see cref="InVmSmokeOptions.FailClosedOnProbeFault"/>.
        /// </summary>
        public bool FailClosedOnProbeFault { get; set; } = true;

        /// <summary>
        /// Explicit host network profile for project-less probe paths. Dispatch
        /// gates pass the resolved project sandbox target directly, so this is
        /// not used as a fallback for work items.
        /// </summary>
        public string? NetworkProfile { get; set; }

        /// <summary>
        /// Agents allowed to route without a registered in-VM smoke probe.
        /// Uncovered agents are otherwise benched at startup (AC#1). Defaults to
        /// <c>copilot</c> when unset, preserving back-compat for operators who
        /// have not yet installed the Copilot CLI in their baseline image —
        /// when CopilotInVmSmokeProbe is registered the probe runs and the
        /// exemption is unused. Set explicitly to override.
        /// </summary>
        public List<string>? ExemptAgentsWithoutProbe { get; set; }
    }

    /// <summary>Config binding for the availability registry.</summary>
    public sealed class AvailabilityConfig
    {
        /// <summary>Fast-fail threshold in seconds. Default 10.</summary>
        public int FastFailThresholdSeconds { get; set; } = 10;

        /// <summary>Consecutive sub-threshold non-zero exits before excluding. Default 3.</summary>
        public int MaxConsecutiveFastFails { get; set; } = 3;

        /// <summary>
        /// Consecutive DISTINCT work items that the agent leaves empty-diff
        /// (clean exit, no commit) before the no-changes circuit breaker
        /// excludes the agent. Default 3. Set 0 (or negative) to disable.
        /// </summary>
        public int MaxConsecutiveNoChanges { get; set; } = 3;

        /// <summary>Background sweep interval in seconds. Default 300 (5 min); set 0 to disable.</summary>
        public int PeriodicSweepIntervalSeconds { get; set; } = 300;
    }

    /// <summary>Config binding for a single agent class (see CodeyBox:AgentClasses).</summary>
    public sealed class AgentClassOptions
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>
        /// Optional class-level opt-in for the Claude resumable-session worker.
        /// Per-member settings override this value.
        /// </summary>
        public AgentClassClaudeSessionOptions? ClaudeSession { get; set; }
        public List<AgentMembershipOptions> Members { get; set; } = [];
    }

    public sealed class AgentClassClaudeSessionOptions
    {
        public bool? Enabled { get; set; }
    }

    /// <summary>Config binding for a reusable routable agent instance.</summary>
    public sealed class AgentInstanceOptions
    {
        /// <summary>
        /// Stable instance id. Values without a slash are rendered as
        /// <c>{Agent}/{Id}</c>; values that already contain a slash are used as
        /// full route keys.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Underlying agent kind, e.g. "claude", "codex".</summary>
        public string Agent { get; set; } = string.Empty;

        /// <summary>Host OAuth/auth JSON file for this instance.</summary>
        public string? CredentialFilePath { get; set; }

        /// <summary>Host env var containing a raw token/API key for this instance.</summary>
        public string? TokenEnvironmentVariable { get; set; }

        /// <summary>Host env var containing CLI auth JSON for this instance.</summary>
        public string? AuthJsonEnvironmentVariable { get; set; }

        /// <summary>Optional companion settings file, used by Gemini OAuth.</summary>
        public string? SettingsFilePath { get; set; }

        /// <summary>Optional sandbox destination path for file-materializing runners.</summary>
        public string? DestinationPath { get; set; }

        /// <summary>Optional override for the sandbox env var used for token injection.</summary>
        public string? SandboxEnvironmentVariable { get; set; }
    }

    /// <summary>Config binding for one member of an agent class.</summary>
    public sealed class AgentMembershipOptions
    {
        /// <summary>Agent kind value, e.g. "claude", "codex".</summary>
        public string Agent { get; set; } = string.Empty;
        /// <summary>Optional instance id or route key. Null means the default per-kind instance.</summary>
        public string? InstanceId { get; set; }
        /// <summary>"Subscription" or "PayPerApi".</summary>
        public string Billing { get; set; } = "Subscription";
        /// <summary>Optional model override, e.g. "claude-opus-4-7".</summary>
        public string? ModelId { get; set; }
        /// <summary>Inline host OAuth/auth JSON file for this member instance.</summary>
        public string? CredentialFilePath { get; set; }
        /// <summary>Inline host env var containing a raw token/API key for this member instance.</summary>
        public string? TokenEnvironmentVariable { get; set; }
        /// <summary>Inline host env var containing CLI auth JSON for this member instance.</summary>
        public string? AuthJsonEnvironmentVariable { get; set; }
        /// <summary>Inline companion settings file, used by Gemini OAuth.</summary>
        public string? SettingsFilePath { get; set; }
        /// <summary>Inline sandbox destination path for file-materializing runners.</summary>
        public string? DestinationPath { get; set; }
        /// <summary>Inline override for the sandbox env var used for token injection.</summary>
        public string? SandboxEnvironmentVariable { get; set; }
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
        /// <summary>
        /// Optional member-level override for the Claude resumable-session
        /// worker. Null inherits the containing class setting.
        /// </summary>
        public AgentClassClaudeSessionOptions? ClaudeSession { get; set; }
    }

    /// <summary>Quota router tuning. Bound from CodeyBox:QuotaRouter.</summary>
    public sealed class QuotaRouterConfig
    {
        /// <summary>
        /// Fallback floor used when the ramp can't be computed (probe didn't
        /// surface a ResetAt, or no ramp window is configured for the agent).
        /// Default 10. See <see cref="StartFloorPct"/> /
        /// <see cref="EndFloorPct"/> / <see cref="RampWindowSeconds"/> for the
        /// ramped floor that supersedes this when the window IS known.
        /// </summary>
        public double MinQuotaPct { get; set; } = 10.0;
        /// <summary>
        /// Per-window absolute floors, keyed by provider window name
        /// (e.g. <c>five_hour</c>, <c>seven_day</c>). Dispatch requires every
        /// window's available percentage to be at or above its window's
        /// floor — block if any window is below its own floor — using the
        /// per-window readings the probe surfaces in
        /// <c>AgentQuotaSnapshot.PerModel[].Windows</c>. Unlisted windows
        /// fall back to <see cref="MinQuotaPct"/>. The 5 h window's absolute
        /// budget is far smaller than the 7 d, so 10 % of 5 h is thin headroom
        /// for the in-flight + cache-staleness overshoot during a burst (up to
        /// MaxConcurrent concurrent long opus runs already dispatched + new
        /// dispatches inside <c>QuotaCacheTtlSeconds</c>); a higher 5 h floor
        /// absorbs that overshoot. Default <c>{five_hour: 25}</c> for
        /// MaxConcurrent=4; tune up for higher fleet concurrency. Hot-reloadable.
        /// </summary>
        public Dictionary<string, double> MinQuotaPctByWindow { get; set; }
            = new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
            };
        /// <summary>
        /// Early-window quota floor — the effective minimum just after the
        /// quota window resets. Reserves headroom for the operator's
        /// interactive session and monitoring on a freshly-reset week.
        /// Default 25.
        /// </summary>
        public double StartFloorPct { get; set; } = 25.0;
        /// <summary>
        /// Late-window quota floor — the effective minimum as the quota
        /// window approaches reset. Drains otherwise-stranded quota right
        /// before the use-it-or-lose-it reset. Default 3.
        /// </summary>
        public double EndFloorPct { get; set; } = 3.0;
        /// <summary>
        /// Length of the binding quota window the ramp is computed against,
        /// in seconds. Default 604800 (7 days, claude/codex weekly cap).
        /// </summary>
        public int RampWindowSeconds { get; set; } = 7 * 24 * 60 * 60;
        /// <summary>
        /// Optional per-agent ramp window length, keyed by agent kind value.
        /// Lets the operator pin a 24h window for one agent and a 7-day
        /// window for another without touching code. Entries with
        /// non-positive seconds are ignored.
        /// </summary>
        public Dictionary<string, int> RampWindowByAgentSeconds { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Optional per-agent floor overrides keyed by agent kind value
        /// (e.g. <c>codex</c>, <c>claude</c>). Omitted agents and omitted
        /// fields inherit the global ramp/fallback settings above. Set
        /// low values such as StartFloorPct=1, EndFloorPct=0, MinQuotaPct=1
        /// to burn a work-only agent close to empty while keeping oversight
        /// agents on the global reserve. Hot-reloadable.
        /// </summary>
        public Dictionary<string, QuotaRouterFloorConfig> FloorByAgent { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Seconds to wait before re-probing when all subscription members are exhausted. Default 300 (5 min).</summary>
        public int QuotaRecheckIntervalSeconds { get; set; } = 300;
        /// <summary>
        /// Seconds between event-driven recovery probes for members already
        /// observed as quota-unusable. Default 5.
        /// </summary>
        public int QuotaRecoveryProbeIntervalSeconds { get; set; } =
            QuotaRouterDefaults.DefaultQuotaRecoveryProbeIntervalSeconds;
        /// <summary>
        /// Maximum parked quota rows inspected by each event-driven recovery
        /// eligibility pass. Default 128.
        /// </summary>
        public int MaxQuotaRecoveryProbeEligibilityScan { get; set; } =
            QuotaRouterDefaults.DefaultQuotaRecoveryProbeEligibilityScanLimit;
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
        /// <summary>
        /// Default "how many concurrent burns fit in the remaining quota window"
        /// used when the estimator has no historical samples yet. Keeps the
        /// dispatch queue from stalling on cold start. Default 2.0.
        /// </summary>
        public double ColdStartFitInWindow { get; set; } = 2.0;
        /// <summary>
        /// Multiplier for DeadlineAwareDrain. Default 1.0 follows the even pace;
        /// values above 1.0 bias dispatch ahead of pace so free/manual refills are
        /// less likely to strand quota. Hot-reloadable.
        /// </summary>
        public double DrainAggressiveness { get; set; } = 1.0;
        /// <summary>
        /// Operator-declared expected free/manual reset points, keyed by agent kind
        /// value. Each entry may provide explicit <c>Timestamps</c> and/or a
        /// recurring <c>CadenceSeconds</c> with <c>CadenceAnchor</c>. Hot-reloadable.
        /// </summary>
        public Dictionary<string, QuotaRouterExpectedResetConfig> ExpectedResets { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// How pooled instances of the same agent kind are ordered. Default
        /// MostQuotaFirst maximizes runway; RoundRobin spreads wear; Sticky
        /// keeps later phases on the selected instance when available;
        /// DeadlineAwareDrain drains use-it-or-lose-it subscription quota toward
        /// its floor by the nearest known or expected reset. Hot-reloadable.
        /// </summary>
        public IntraKindRoutingPolicy IntraKindRoutingPolicy { get; set; } =
            IntraKindRoutingPolicy.MostQuotaFirst;
        /// <summary>
        /// Additional retries on a transient probe failure (network error / timeout / 5xx)
        /// before recording the failure. Total attempts = 1 + this value. Default 2.
        /// Hot-reloadable.
        /// </summary>
        public int ProbeMaxRetries { get; set; } = 2;
        /// <summary>
        /// Base retry backoff in milliseconds; doubles each attempt. Default 250 ms.
        /// Hot-reloadable.
        /// </summary>
        public int ProbeRetryInitialDelayMs { get; set; } = 250;
        /// <summary>
        /// Consecutive probe failures tolerated before the probe stops returning
        /// the retained last-known-good snapshot. Default 3. Hot-reloadable.
        /// </summary>
        public int ProbeMaxConsecutiveFailures { get; set; } = 3;
        /// <summary>
        /// Maximum age in seconds of a retained last-known-good snapshot before
        /// it is dropped in favour of <c>AvailablePct=-1</c>. Default 300 (5 min).
        /// Hot-reloadable.
        /// </summary>
        public int ProbeMaxStalenessSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Per-agent quota floor override. Null fields inherit the corresponding
    /// global <see cref="QuotaRouterConfig"/> setting.
    /// </summary>
    public sealed class QuotaRouterFloorConfig
    {
        /// <summary>Fallback floor used when the ramp cannot be computed.</summary>
        public double? MinQuotaPct { get; set; }

        /// <summary>Early-window ramp floor for this agent.</summary>
        public double? StartFloorPct { get; set; }

        /// <summary>Late-window ramp floor for this agent.</summary>
        public double? EndFloorPct { get; set; }

        /// <summary>Optional ramp-window length in seconds for this agent.</summary>
        public int? RampWindowSeconds { get; set; }
    }

    /// <summary>
    /// Declared quota refills that the provider probe cannot see until they fire.
    /// Used only by DeadlineAwareDrain.
    /// </summary>
    public sealed class QuotaRouterExpectedResetConfig
    {
        /// <summary>Explicit reset timestamps. Past values are ignored by the router.</summary>
        public List<DateTimeOffset> Timestamps { get; set; } = [];

        /// <summary>Recurring reset period in seconds. Requires <see cref="CadenceAnchor"/>.</summary>
        public int? CadenceSeconds { get; set; }

        /// <summary>Anchor timestamp for the recurring cadence.</summary>
        public DateTimeOffset? CadenceAnchor { get; set; }
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
    /// One operator-supplied auth/login-prompt pattern entry. Appended to the
    /// built-in defaults and matched case-insensitively against the configured
    /// stream. Bound from <c>CodeyBox:AuthFailurePatterns:&lt;agent-kind&gt;</c>.
    /// Defaults to stderr-only because stdout can contain model-controlled task
    /// text; stdout patterns should be narrowly formed CLI login transcripts.
    /// </summary>
    public sealed class AuthFailurePatternOptions
    {
        /// <summary>The substring to search for.</summary>
        public string Pattern { get; set; } = string.Empty;
        /// <summary>
        /// Stream to search. Defaults to stderr. Use Stdout or StderrAndStdout
        /// only for narrow CLI-auth transcript signatures.
        /// </summary>
        public AuthFailurePatternStream Stream { get; set; } = AuthFailurePatternStream.Stderr;
    }

    /// <summary>
    /// Pure conversion from operator-supplied
    /// <see cref="CodeyBoxOptions.AuthFailurePatterns"/> to a wired
    /// <see cref="IAgentAuthFailureClassifier"/>. Extracted from the DI factory
    /// so the binding shape (config section name, per-agent dictionary,
    /// pattern filtering, conversion to <see cref="AuthFailurePattern"/>) is
    /// reachable from unit tests without booting the full host — a bug in
    /// any of those steps would otherwise silently disable the operator's
    /// extensibility hook for new CLI login prompts.
    /// </summary>
    public static class AuthFailurePatternBinder
    {
        public static IAgentAuthFailureClassifier Build(CodeyBoxOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var extras = options.AuthFailurePatterns is null
                ? new Dictionary<string, IReadOnlyList<AuthFailurePattern>>(StringComparer.OrdinalIgnoreCase)
                : options.AuthFailurePatterns
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => (IReadOnlyList<AuthFailurePattern>)(kvp.Value ?? new List<AuthFailurePatternOptions>())
                            .Where(p => !string.IsNullOrWhiteSpace(p.Pattern))
                            .Select(p => new AuthFailurePattern(p.Pattern, p.Stream))
                            .ToArray(),
                        StringComparer.OrdinalIgnoreCase);
            return new AgentAuthFailureClassifier(extras);
        }
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

        /// <summary>
        /// Rolling file sink for the orchestrator's plain-text console stream
        /// (the same lines Serilog writes to stdout). Enabled by default so the
        /// process bounds its own run-log retention instead of relying on an
        /// external shell-redirect (which historically grew without bound).
        /// </summary>
        public ConsoleLogOptions ConsoleLog { get; set; } = new();
    }

    /// <summary>
    /// Configuration for the rolling console / run-log sink. The default
    /// settings keep ~14 rolled files of up to 100 MiB each (≈ 1.4 GiB peak
    /// disk) and roll on both calendar day and size; disable by setting
    /// <c>Enabled=false</c> if the operator manages run-log capture out of
    /// process.
    /// </summary>
    public sealed class ConsoleLogOptions
    {
        /// <summary>
        /// Toggle for the rolling run-log file sink. Stdout/console output is
        /// unaffected — turning this off only stops writing the duplicate
        /// rolling file.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Path template for the rolling run log. Serilog inserts the date
        /// before the trailing dot (e.g. <c>codeybox-console-20260618.log</c>).
        /// Relative paths resolve from the process working directory.
        /// </summary>
        public string Path { get; set; } = "logs/codeybox-console-.log";

        /// <summary>
        /// Total number of rolled files to retain across all dates / size
        /// segments. Counted-by-file (not by day) so the cap holds even when
        /// size rolling produces several segments in a single day. Must be
        /// >= 1. Default: 14.
        /// </summary>
        public int RetainedFileCountLimit { get; set; } = 14;

        /// <summary>
        /// Per-file size cap before rolling to a new segment. Combined with
        /// the day boundary, this is what actually keeps individual files
        /// readable with tail / less. Must be >= 1 MiB. Default: 100 MiB.
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;
    }
}

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program
{
    /// <summary>
    /// Resolve <c>HostOptions.ShutdownTimeout</c> from operator config, the
    /// resolved provider's suspend capability, and the hot-reloadable teardown
    /// mode.
    /// Shutdown:GraceSeconds bounds the normal request-drain / preempt-checkpoint
    /// window. Suspend-capable providers keep a conservative ceiling because
    /// <c>Shutdown.SandboxTeardownMode</c> is read through
    /// <c>IOptionsMonitor</c> when shutdown begins, while
    /// <c>HostOptions.ShutdownTimeout</c> is captured once at startup. An
    /// operator can therefore hot-reload Stop/Dispose to Suspend immediately
    /// before stopping the process, and the host must still have enough time to
    /// finish writing each VM's RAM snapshot (the RAM-scaled
    /// <see cref="SuspendTimeoutPolicy"/> budget — 30 min for the default 12 GiB
    /// VM) and drains in parallel batches, so a deployment with more in-flight
    /// VMs than the batch cap spans <c>ceil(N/batch)</c> sequential waves. The
    /// ceiling must cover the slowest wave-chain PLUS the post-suspend drain
    /// grace (suspend runs in StoppingAsync, the preempt-checkpoint /
    /// listener-drain window runs after), not one VM, or the host SIGKILLs us
    /// mid-snapshot on a later wave or mid-drain.
    /// ShutdownTimeout is a CEILING, not a fixed wait: a shutdown with
    /// nothing to suspend still returns as soon as every hosted service's
    /// StoppingAsync completes, so raising it only affects the suspend case.
    ///
    /// <para>The concurrent-sandbox bound is resolved through
    /// <see cref="OrchestratorOptionsFactory"/> — the same validation/defaulting
    /// path the admission-control decorator uses for
    /// <c>WorkerPool:MaxConcurrentSandboxes</c> — so the shutdown reserve matches
    /// the actual live-VM ceiling. All VMs are provisioned at
    /// <see cref="SandboxResourceLimits.Default"/> (no per-VM RAM override is
    /// wired through SandboxSpec today), so the default profile RAM is the
    /// largest per-VM suspend budget the host must cover.</para>
    /// </summary>
    internal static TimeSpan ComputeHostShutdownTimeout(
        CodeyBoxOptions cbOpts, bool providerSupportsSuspend, ILogger log)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(1, cbOpts.Shutdown.GraceSeconds));
        var maxConcurrent = OrchestratorOptionsFactory
            .Build(cbOpts.Concurrency, cbOpts.WorkerPool, log)
            .MaxConcurrentSandboxes;
        return SuspendTimeoutPolicy.ResolveHostShutdownTimeout(
            providerSupportsSuspend, grace, maxConcurrent);
    }

    internal static TimeSpan ComputeOrchestratorShutdownDrainTimeout(int graceSeconds)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(1, graceSeconds));
        var reserve = grace >= TimeSpan.FromSeconds(10)
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromMilliseconds(Math.Max(100, grace.TotalMilliseconds * 0.2));
        var drain = grace - reserve;
        return drain > TimeSpan.Zero ? drain : TimeSpan.FromMilliseconds(100);
    }

    internal static SandboxStartupResumeOptions BuildSandboxStartupResumeOptions(
        ShutdownOptions shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);

        return new SandboxStartupResumeOptions
        {
            Mode = shutdown.SandboxResumeMode == SandboxResumeMode.Blocking
                ? SandboxStartupResumeMode.Blocking
                : SandboxStartupResumeMode.Background,
            ResumeTimeout = shutdown.SandboxResumeTimeout,
            AdoptionDeadline = TimeSpan.FromSeconds(shutdown.SandboxAdoptionDeadlineSeconds),
        };
    }
}
