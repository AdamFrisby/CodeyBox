using Microsoft.Extensions.Options;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Api;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.CrunVm;
using CodeyBox.Sandbox.GVisor;
using CodeyBox.Sandbox.Kata;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.Process;

var builder = WebApplication.CreateBuilder(args);

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
//   gvisor      — User-space kernel (runsc). Single package + one line in
//                 ~/.config/containers/containers.conf. Real syscall isolation.
//   kata        — Microvm with separate guest kernel. Defaults to QEMU
//                 (no /etc edits). Firecracker mode requires /etc/kata-containers
//                 edits — advanced.
//   crun-vm     — libkrun-backed microvm. Lighter alternative to Kata.
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
                "Choose one of: bubblewrap, multipass, gvisor, kata, crun-vm, process " +
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
                NetworkProfiles = opts.SandboxNetworkProfiles,
            },
            loggerFactory.CreateLogger<MultipassSandboxProvider>()),
        "gvisor" => new GVisorSandboxProvider(
            new GVisorSandboxOptions { NetworkName = opts.SandboxNetworkName },
            loggerFactory.CreateLogger<GVisorSandboxProvider>()),
        "kata" => new KataSandboxProvider(
            new KataSandboxOptions { NetworkName = opts.SandboxNetworkName },
            loggerFactory.CreateLogger<KataSandboxProvider>()),
        "crun-vm" => new CrunVmSandboxProvider(
            new CrunVmSandboxOptions { NetworkName = opts.SandboxNetworkName },
            loggerFactory.CreateLogger<CrunVmSandboxProvider>()),
        _ => throw new InvalidOperationException(
            $"Unknown CodeyBox:SandboxProvider '{kind}'. Valid: bubblewrap, multipass, gvisor, kata, crun-vm, process"),
    };
}

static ISandboxProvider BuildProcess(CodeyBoxOptions opts, IHostEnvironment env, ILogger startupLog, ILoggerFactory loggerFactory)
{
    if (!env.IsDevelopment() && !opts.DangerouslyAllowProcessSandbox)
    {
        throw new InvalidOperationException(
            "CodeyBox:SandboxProvider=process is UNSAFE outside Development. " +
            "Set CodeyBox:DangerouslyAllowProcessSandbox=true to override (NOT recommended), " +
            "or pick bubblewrap | gvisor | kata | crun-vm.");
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
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();

// --- Credentials -------------------------------------------------------------
// Each agent's API key has a per-agent host env var that maps to the
// canonical sandbox env var the agent CLI reads. Operators add new agents
// by appending to this list (or registering a different ICredentialProvider).
builder.Services.AddSingleton<ICredentialProvider>(_ => new EnvironmentCredentialProvider(new[]
{
    new AgentCredentialMapping(AgentKind.Claude, "CODEYBOX_CLAUDE_API_KEY", "ANTHROPIC_API_KEY"),
    new AgentCredentialMapping(AgentKind.Copilot, "CODEYBOX_COPILOT_TOKEN", "GH_TOKEN"),
    new AgentCredentialMapping(AgentKind.Codex, "CODEYBOX_CODEX_API_KEY", "OPENAI_API_KEY"),
}));

// --- Projects + per-project upstream + audit composer ------------------------
builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
builder.Services.AddSingleton<IUpstreamRemoteFactory, UpstreamRemoteFactory>();
builder.Services.AddSingleton<IPresetCatalog, PresetCatalog>();
builder.Services.AddSingleton<ProjectAuditorComposer>();

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
builder.Services.AddSingleton<OrchestratorOptions>(sp =>
    new OrchestratorOptions { Concurrency = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.Concurrency });
builder.Services.AddSingleton<CancellationRegistry>(sp =>
    new CancellationRegistry(sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
builder.Services.AddHostedService<OrchestratorService>();

var app = builder.Build();

app.UseApiKeyAuth(anonymousPrefixes: ["/healthz"]);

WorkItemEndpoints.Map(app);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

namespace CodeyBox.Api
{
    public sealed class CodeyBoxOptions
    {
        public string GitRootDirectory { get; set; } = "/var/lib/codeybox/repos";
        public string StateDatabasePath { get; set; } = "/var/lib/codeybox/state.db";
        public string SandboxImageReference { get; set; } = "codeybox/agent:latest";
        public string[] AgentAllowedHosts { get; set; } = ["api.anthropic.com", "api.openai.com", "api.githubcopilot.com"];
        public int Concurrency { get; set; } = 2;
        public int UpstreamPushMaxAttempts { get; set; } = 5;
        public int UpstreamPushBackoffSeconds { get; set; } = 15;

        /// <summary>
        /// Which sandbox provider to use. One of: <c>bubblewrap</c>,
        /// <c>gvisor</c>, <c>kata</c>, <c>crun-vm</c>, <c>process</c>.
        /// Default is empty — startup defaults to 'process' in Development
        /// and refuses to start in other environments.
        /// </summary>
        public string? SandboxProvider { get; set; }

        /// <summary>
        /// Name of the podman network providers attach to when egress is
        /// requested. Operators configure host firewall on this network.
        /// </summary>
        public string SandboxNetworkName { get; set; } = "codeybox-egress";

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
        /// Empty → no host-enforced profile, sandboxes use only the in-VM
        /// (advisory) firewall. For real enforcement against compromised
        /// agents, populate this and run setup-host-networks.sh.
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
    }
}
