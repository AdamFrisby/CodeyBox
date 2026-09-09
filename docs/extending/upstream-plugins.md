# Upstream Remote Plugins

CodeyBox ships three built-in upstream remotes (`noop`, `github`, `git-generic`).
The **upstream-remote plugin SDK** lets third parties register additional forges —
Gitea, Forgejo, SourceHut, corporate-internal git, and so on — without touching
the orchestrator core.

## Quick start

1. Create a .NET class library targeting `net10.0`.
2. Add a `PackageReference` to `CodeyBox.PluginSdk` (which pulls `CodeyBox.Core` transitively).
3. Implement `IUpstreamRemote`.
4. Decorate the class with `[CodeyBoxPlugin(id, displayName)]`.
5. Build the library and point the orchestrator at its DLL.

See `samples/CodeyBox.SampleGiteaUpstreamPlugin/` for the canonical reference
implementation.

## Implementing IUpstreamRemote

```csharp
using CodeyBox.Core;
using CodeyBox.PluginSdk;

[CodeyBoxPlugin(
    id: "myorg.gitea-upstream",
    displayName: "My Gitea Upstream",
    minHostApiVersion: "1.0")]
public sealed class MyGiteaUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    // IUpstreamRemote.Name must equal the Upstream.Kind value operators put in
    // their project config. Keep it lowercase and stable — it appears in project
    // config files and the error message for unknown kinds.
    public string Name => "gitea";

    private IPluginHost _host = null!;
    private IUpstreamPluginHost _upstreamHost = null!;

    // Inject IGitHost to push branches, and IHttpClientFactory for REST calls.
    public MyGiteaUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory) { ... }

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        _upstreamHost = (IUpstreamPluginHost)_host;   // always succeeds against the orchestrator host
        return Task.CompletedTask;
    }

    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        // Read per-project config — see PluginConfig convention below.
        var cfg = _upstreamHost.GetProjectUpstreamConfig(request.ProjectId);
        var baseUrl = cfg["BaseUrl"];

        // Read the auth token from the env var named by the operator.
        // The orchestrator copies Upstream.TokenEnvVar into request.TokenEnvVar.
        var token = string.IsNullOrWhiteSpace(request.TokenEnvVar)
            ? null
            : Environment.GetEnvironmentVariable(request.TokenEnvVar);

        // Optional auto-merge: the orchestrator copies Upstream.AutoMerge into
        // request.AutoMerge, and the merge strategy into request.MergeMethod.
        ...
        return new UpstreamCompletionOutcome { BranchPushed = true, PullRequestUrl = "..." };
    }

    public Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(false, "push-only not supported; use CompleteAsync"));
}
```

## PluginConfig convention

Plugins cannot predict their own credentials or server addresses at compile time.
Operators supply these via `Upstream.PluginConfig` in the project config:

```json
"Upstream": {
  "Kind": "gitea",
  "TokenEnvVar": "MY_GITEA_TOKEN",
  "PluginConfig": {
    "BaseUrl": "https://git.mycompany.example/api/v1",
    "Owner": "myteam",
    "Repository": "myproject"
  }
}
```

Inside `CompleteAsync`, retrieve the config with:

```csharp
var cfg = _upstreamHost.GetProjectUpstreamConfig(request.ProjectId);
```

`IUpstreamPluginHost.GetProjectUpstreamConfig` is the **only** place the plugin
host exposes per-project state to a plugin. It returns an empty dictionary when
the project has no `PluginConfig` entries or is unknown; never throws.

Document which keys your plugin reads in your plugin's README.

### Token security

Tokens must **never** appear in `PluginConfig`. Always read tokens from environment
variables:

1. The operator sets `Upstream.TokenEnvVar` to the name of the env var holding the token.
2. The orchestrator copies this name into `UpstreamCompletionRequest.TokenEnvVar`.
3. Inside `CompleteAsync`, read the token with:

```csharp
var token = string.IsNullOrWhiteSpace(request.TokenEnvVar)
    ? null
    : Environment.GetEnvironmentVariable(request.TokenEnvVar);
```

This keeps credentials out of config files and plugin binaries entirely.

## Built-in precedence

Built-in remotes (`noop`, `github`, `git-generic`) always win over plugins with
the same `Name`. A plugin registering `Name = "github"` is unreachable —
the orchestrator logs a warning and ignores it. Operators cannot shadow built-ins
via plugins.

## Webhook events

Plugins can emit the same `work_item.pull_request_opened` event as the built-in
GitHub remote. If your plugin has `IWebhookDispatcher` injected (available from
DI), call:

```csharp
await _webhooks.PublishAsync(new WebhookEvent
{
    Type = "work_item.pull_request_opened",
    Payload = new { workItemId = request.WorkItemId, pullRequestUrl = prUrl },
}, ct);
```

This lets operators subscribe to PR-opened events regardless of which forge is
in use.

## Sample Gitea plugin

`samples/CodeyBox.SampleGiteaUpstreamPlugin/` provides a complete working example:

- Reads `BaseUrl`, `Owner`, and `Repository` from `PluginConfig`.
- Pushes the work branch via the host git module (`IGitHost.PushToUpstreamAsync`).
- Opens a PR via Gitea's `/api/v1/repos/{owner}/{repo}/pulls` API.
- Optionally auto-merges via `POST /pulls/{index}/merge` when `Upstream.AutoMerge = true`.
- Authenticates using the token from the env var named in `Upstream.TokenEnvVar`.
- Uses `IPluginHost.GetProjectUpstreamConfig` as documented above.

Build it standalone:

```bash
dotnet build samples/CodeyBox.SampleGiteaUpstreamPlugin/
```

Then configure the orchestrator:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["sample.gitea-upstream"],
      "AssemblyPaths": ["/path/to/CodeyBox.SampleGiteaUpstreamPlugin.dll"]
    }
  }
}
```

## Registering your plugin

1. Add the plugin assembly path to `CodeyBox:Plugins:AssemblyPaths`.
2. Add the plugin ID to `CodeyBox:Plugins:Allowlist` (or use `"*"` for development).
3. Set `Upstream.Kind` in your project config to match `IUpstreamRemote.Name`.
4. Add `Upstream.PluginConfig` entries for any plugin-specific settings.

See `docs/extending/plugins.md` for full plugin registration and allowlisting guidance.
