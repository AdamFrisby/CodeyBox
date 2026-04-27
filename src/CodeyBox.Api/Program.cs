using Microsoft.Extensions.Options;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Api;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Upstream.GitHub;

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

ApiKeyAuth.Configure(builder);

// --- Sandbox provider --------------------------------------------------------
// Default to the dev-only Process provider. Production deployments should
// register Sandbox.Kata or Sandbox.CrunVm here instead.
builder.Services.AddSingleton<ISandboxProvider, ProcessSandboxProvider>();

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
builder.Services.AddSingleton<ICredentialProvider>(_ => new EnvironmentCredentialProvider(new[]
{
    new AgentCredentialMapping(AgentKind.Claude, "CODEYBOX_CLAUDE_API_KEY", "ANTHROPIC_API_KEY"),
    new AgentCredentialMapping(AgentKind.Copilot, "CODEYBOX_COPILOT_TOKEN", "GH_TOKEN"),
    new AgentCredentialMapping(AgentKind.Codex, "CODEYBOX_CODEX_API_KEY", "OPENAI_API_KEY"),
}));

// --- Upstream remote ---------------------------------------------------------
builder.Services.AddSingleton<IUpstreamRemote>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    return opts.Upstream.Kind switch
    {
        "github" => new GitHubUpstreamRemote(
            sp.GetRequiredService<IGitHost>(),
            new GitHubUpstreamOptions
            {
                Owner = opts.Upstream.GitHubOwner ?? throw new InvalidOperationException("Upstream:GitHubOwner required"),
                Repository = opts.Upstream.GitHubRepository ?? throw new InvalidOperationException("Upstream:GitHubRepository required"),
                Token = Environment.GetEnvironmentVariable("CODEYBOX_GITHUB_TOKEN")
                    ?? throw new InvalidOperationException("CODEYBOX_GITHUB_TOKEN required when using github upstream"),
            }),
        "git-generic" => new GitGenericUpstreamRemote(
            sp.GetRequiredService<IGitHost>(),
            new GitGenericUpstreamOptions
            {
                UpstreamUrl = opts.Upstream.GenericUrl ?? throw new InvalidOperationException("Upstream:GenericUrl required"),
            }),
        _ => new NoopUpstreamRemote(),
    };
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
// Auditors: register IAuditor instances here (or in a separate composition
// module). The default deployment ships with no auditors — the audit phase
// is a no-op until at least one is registered.
builder.Services.AddSingleton<IAuditorRegistry, AuditorRegistry>();
builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("CodeyBox:Audit");
    return new AuditOptions
    {
        MaxIterations = section.GetValue("MaxIterations", 3),
        FailingSeverity = Enum.TryParse<AuditSeverity>(section["FailingSeverity"], out var s) ? s : AuditSeverity.Error,
        StopOnFirstFailure = section.GetValue("StopOnFirstFailure", false),
        PerIterationTimeout = TimeSpan.FromMinutes(section.GetValue("PerIterationTimeoutMinutes", 10)),
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
        public UpstreamConfig Upstream { get; set; } = new();

        public sealed class UpstreamConfig
        {
            public string Kind { get; set; } = "noop";
            public string? GitHubOwner { get; set; }
            public string? GitHubRepository { get; set; }
            public string? GenericUrl { get; set; }
        }
    }
}
