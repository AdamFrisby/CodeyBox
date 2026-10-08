using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HetznerSandboxPlugin;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Shared fake Hetzner Cloud plus SSH/key/DNS seams for the hetzner provider
/// tests. The fake mirrors the recorded Hetzner Cloud v1 request/response
/// shapes (bearer auth, <c>{"error":{"code","message"}}</c> failures,
/// <c>meta.pagination</c> paging, ownership labels) without any network.
/// </summary>
internal sealed class HetznerHarness : IDisposable
{
    public const string OwnerId = "test-host";
    public const string OtherOwnerId = "other-host";
    public const string ApiToken = "test-token";

    public FakeHetznerCloud Cloud { get; }
    public FakeHetznerDns Dns { get; } = new();
    public FakeHetznerTransportFactory TransportFactory { get; } = new();
    public FakeHetznerTransport Transport => TransportFactory.Created.Last();
    public HetznerSandboxOptions Options { get; private set; }
    public HetznerSandboxProvider Provider { get; private set; }
    private readonly HttpClient _http;

    public HetznerHarness(
        Func<HetznerSandboxOptions, HetznerSandboxOptions>? configure = null,
        FakeHetznerCloud? sharedCloud = null)
    {
        Cloud = sharedCloud ?? new();
        _http = new HttpClient(Cloud, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new HetznerSandboxOptions
        {
            Enabled = true,
            ApiBaseUrl = "http://localhost/",
            AllowUnsafeHttp = true,
            OwnerId = OwnerId,
            Image = "ubuntu-24.04",
            ServerType = "cx23",
            Location = "fsn1",
            OrchestratorSshCidrs = ["203.0.113.0/24"],
            DnsServerIps = [],
            NtpServerIps = [],
            PollIntervalMilliseconds = 5,
            MaxPollIntervalMilliseconds = 10,
            ReadyTimeoutSeconds = 30,
            SshReadyTimeoutSeconds = 30,
            SshUser = "tester",
        };
        Options = configure is null ? options : configure(options);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HCLOUD_TOKEN"] = ApiToken,
        };
        Provider = new HetznerSandboxProvider(
            () => Options,
            _http,
            new FakeHetznerKeyGenerator(),
            Dns,
            TransportFactory,
            name => env.TryGetValue(name, out var value) ? value : null,
            TimeProvider.System,
            NullLogger.Instance);
    }

    public string SshTempDir(string serverName)
    {
        var suffix = serverName[HetznerSandboxProviderTests.ServerPrefix.Length..];
        return Path.Combine(Path.GetTempPath(), "codeybox-hetzner-" + suffix);
    }

    public void Dispose()
    {
        Provider.Dispose();
        _http.Dispose();
    }
}

internal sealed class FakeHetznerKeyGenerator : IHetznerKeyGenerator
{
    public static string ClientPublicKey { get; } = "ssh-ed25519 " + new string('A', 64);

    public static string HostPublicKey { get; } = "ssh-ed25519 " + new string('B', 64);

    public Task<HetznerClientKeyMaterial> GenerateClientKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        _ = keygenBinary; _ = comment; _ = ct;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "id-ed25519-test");
        File.WriteAllText(path, "fake-private");
        return Task.FromResult(new HetznerClientKeyMaterial(path, ClientPublicKey));
    }

    public Task<HetznerHostKeyMaterial> GenerateHostKeyAsync(
        string keygenBinary, string directory, string comment, CancellationToken ct)
    {
        _ = keygenBinary; _ = directory; _ = comment; _ = ct;
        return Task.FromResult(new HetznerHostKeyMaterial(
            "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n",
            HostPublicKey));
    }
}

internal sealed class FakeHetznerDns : IHetznerDnsResolver
{
    public Dictionary<string, System.Net.IPAddress[]> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        _ = ct;
        if (Hosts.TryGetValue(host, out var ips))
            return Task.FromResult(ips);
        throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }
}

internal sealed class FakeHetznerTransportFactory : IHetznerTransportFactory
{
    public List<FakeHetznerTransport> Created { get; } = [];
    public string LastTarget { get; private set; } = string.Empty;
    public Action<FakeHetznerTransport>? ConfigureTransport { get; set; }

    public IRemoteHostTransport Create(HetznerSshTransportSpec spec)
    {
        LastTarget = spec.SshTarget;
        var transport = new FakeHetznerTransport();
        ConfigureTransport?.Invoke(transport);
        Created.Add(transport);
        return transport;
    }
}

internal sealed class FakeHetznerTransport : IRemoteHostTransport
{
    public string DiagnosticId => "fake";
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<(string HostPath, string RemotePath)> StageInCalls { get; } = [];
    public List<(string RemotePath, string HostPath)> StageOutCalls { get; } = [];
    public int? LastMaxStdoutBytes { get; private set; }
    public int? LastMaxStderrBytes { get; private set; }
    public Action<string, string>? OnStageIn { get; set; }
    public Action<string, string>? OnStageOut { get; set; }
    public bool ThrowTransportLossOnRun { get; set; }
    public Func<IReadOnlyList<string>, string?, ProcessRunResult> OnRun { get; set; } =
        (_, _) => new ProcessRunResult(0, "ok", string.Empty);

    public string LastArgv() => string.Join(" ", Calls.Last());

    public Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true)
    {
        ct.ThrowIfCancellationRequested();
        _ = stdoutChunkCallback; _ = stderrChunkCallback;
        _ = killOnOutputLimit;
        Calls.Add(argv.ToArray());
        LastMaxStdoutBytes = maxStdoutBytes;
        LastMaxStderrBytes = maxStderrBytes;
        if (ThrowTransportLossOnRun)
            throw new RemoteSshTransportException("simulated transport loss");
        return Task.FromResult(OnRun(argv, stdin));
    }

    public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
    {
        _ = ct;
        OnStageIn?.Invoke(hostPath, remotePath);
        StageInCalls.Add((hostPath, remotePath));
        return Task.CompletedTask;
    }

    public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
    {
        _ = ct;
        OnStageOut?.Invoke(remotePath, hostPath);
        StageOutCalls.Add((remotePath, hostPath));
        return Task.CompletedTask;
    }
}

internal sealed class FakeHetznerCloud : HttpMessageHandler
{
    public ConcurrentDictionary<long, FakeServer> Servers { get; } = new();
    public ConcurrentDictionary<long, FakeNamedResource> SshKeys { get; } = new();
    public ConcurrentDictionary<long, FakeNamedResource> Firewalls { get; } = new();
    public ConcurrentDictionary<long, FakeFloatingIp> FloatingIps { get; } = new();
    public List<string> ServerCreateBodies { get; } = [];
    public List<string> FirewallCreateBodies { get; } = [];
    public List<string> Events { get; } = [];
    public string InitialServerStatus = "initializing";
    public string? ForceServerStatus;
    public int RunningAfterPolls = 1;
    public int BlindListCalls;
    public bool FailServerDelete;
    public (HttpStatusCode Status, string Code, string Message, bool StoreServer)? FailNextServerCreate;
    public (HttpStatusCode Status, string Code, string Message)? FailNextSshKeyCreate;
    public string? NextCreateRetryAfterSeconds;
    /// <summary>
    /// Overrides the <c>ip</c> value returned for the next floating-IP
    /// create, so tests can feed the provider a hostile API response
    /// (e.g. newline-bearing) through the real HTTP parsing path.
    /// </summary>
    public string? FloatingIpOverride;
    private long _seq;
    private readonly object _lock = new();

    public long NextId()
    {
        lock (_lock)
            return ++_seq + 1000;
    }

    public void Log(string entry)
    {
        lock (_lock)
            Events.Add(entry);
    }

    /// <summary>
    /// Seeds a server as if left behind by a crashed run: owned labels plus
    /// optionally a full dangling set (SSH key, firewall, floating IP) under
    /// the same request label. Returns the server name.
    /// </summary>
    public string SeedOrphan(string owner, HetznerSandboxOptions options, bool withResources = true)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var name = options.ServerNamePrefix + suffix;
        var request = Guid.NewGuid().ToString("N");
        var serverId = NextId();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codeybox-owned"] = "true",
            ["codeybox-owner"] = owner,
            ["codeybox-work-item"] = "none",
            ["codeybox-request"] = request,
            ["codeybox-created"] = "1700000000",
        };
        Servers[serverId] = new FakeServer
        {
            Id = serverId, Name = name, Labels = labels, Ipv4 = "192.0.2.9",
        };
        if (withResources)
        {
            var keyId = NextId();
            SshKeys[keyId] = new FakeNamedResource
            {
                Id = keyId, Name = options.SshKeyNamePrefix + suffix,
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            };
            var fwId = NextId();
            Firewalls[fwId] = new FakeNamedResource
            {
                Id = fwId, Name = options.FirewallNamePrefix + suffix,
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            };
            var fipId = NextId();
            FloatingIps[fipId] = new FakeFloatingIp
            {
                Id = fipId, Name = options.FloatingIpNamePrefix + suffix,
                Ip = "203.0.113.77", Server = null,
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            };
        }
        return name;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: "test-token" })
            return Error(HttpStatusCode.Unauthorized, "unauthorized", "missing or invalid token");

        var path = (request.RequestUri!.AbsolutePath.Trim('/') is var p && p.StartsWith("v1/"))
            ? p["v1/".Length..]
            : p;
        var query = ParseQuery(request.RequestUri.Query);
        var method = request.Method.Method;

        if (path.StartsWith("server_types", StringComparison.Ordinal))
            return ServerTypes(query);
        if (path.StartsWith("images", StringComparison.Ordinal))
            return Images(path, query);
        if (path.StartsWith("locations", StringComparison.Ordinal))
            return Locations(query);
        if (path.StartsWith("servers", StringComparison.Ordinal))
            return await ServersRouteAsync(path, query, method, body, ct);
        if (path.StartsWith("ssh_keys", StringComparison.Ordinal))
            return SshKeysRoute(path, query, method, body);
        if (path.StartsWith("firewalls", StringComparison.Ordinal))
            return FirewallsRoute(path, query, method, body);
        if (path.StartsWith("floating_ips", StringComparison.Ordinal))
            return await FloatingIpsRouteAsync(path, query, method, body);
        return Error(HttpStatusCode.NotFound, "not_found", "no such endpoint: " + path);
    }

    private HttpResponseMessage ServerTypes(Dictionary<string, string> query)
    {
        _ = query;
        return Json(new
        {
            server_types = new object[]
            {
                new { id = 22L, name = "cx23", cores = 2, memory = 4.0, disk = 40, deprecated = false },
                new { id = 23L, name = "deprecated-type", cores = 1, memory = 2.0, disk = 20, deprecated = true },
            },
            meta = PageMeta(1, null),
        });
    }

    private HttpResponseMessage Images(string path, Dictionary<string, string> query)
    {
        _ = query;
        var rest = path["images".Length..].Trim('/');
        if (rest.Length > 0 && long.TryParse(rest, out var id))
        {
            var match = AllImages().FirstOrDefault(i => i.Id == id);
            return match is null
                ? Error(HttpStatusCode.NotFound, "not_found", "image not found")
                : Json(new { image = match });
        }
        return Json(new { images = AllImages(), meta = PageMeta(1, null) });
    }

    private static List<FakeImage> AllImages() =>
    [
        new FakeImage(1, "ubuntu-24.04", "system", "available", "x86", Deprecated: false),
        new FakeImage(2, "deprecated-img", "system", "available", "x86", Deprecated: true),
        new FakeImage(3, "ambiguous-img", "system", "available", "arm", Deprecated: false),
        new FakeImage(4, "ambiguous-img", "system", "available", "x86", Deprecated: false),
    ];

    private sealed record FakeImage(
        long Id, string Name, string Type, string Status, string Architecture, bool Deprecated);

    private static HttpResponseMessage Locations(Dictionary<string, string> query)
    {
        _ = query;
        return Json(new
        {
            locations = new object[] { new { id = 1L, name = "fsn1" } },
            meta = PageMeta(1, null),
        });
    }

    private async Task<HttpResponseMessage> ServersRouteAsync(
        string path, Dictionary<string, string> query, string method, string body, CancellationToken ct)
    {
        _ = ct;
        var rest = path["servers".Length..].Trim('/');
        if (rest.Length == 0)
        {
            if (method == HttpMethod.Post.Method)
                return CreateServer(body);
            var selector = ParseSelector(query);
            List<object> items;
            lock (_lock)
            {
                if (BlindListCalls > 0)
                {
                    BlindListCalls--;
                    items = [];
                }
                else
                {
                    items = Servers.Values
                        .Where(s => MatchesLabels(s.Labels, selector))
                        .OrderBy(s => s.Id)
                        .Select(ServerDto)
                        .ToList();
                }
            }
            return Paged("servers", items, query);
        }
        var slash = rest.IndexOf('/');
        if (slash >= 0)
        {
            var idText = rest[..slash];
            var action = rest[(slash + 1)..];
            if (!long.TryParse(idText, out var actionId))
                return Error(HttpStatusCode.NotFound, "not_found", "bad server id");
            if (!Servers.ContainsKey(actionId))
                return Error(HttpStatusCode.NotFound, "not_found", "server not found");
            if (action is "actions/shutdown" or "actions/poweroff")
            {
                Log($"{(action.EndsWith("shutdown") ? "shutdown" : "poweroff")}-server:{actionId}");
                return Json(new { action = new { id = NextId(), status = "running", command = action.Replace("actions/", "") + "_server" } },
                    HttpStatusCode.Created);
            }
            return Error(HttpStatusCode.NotFound, "not_found", "no such action");
        }
        if (!long.TryParse(rest, out var id))
            return Error(HttpStatusCode.NotFound, "not_found", "bad server id");
        if (method == HttpMethod.Get.Method)
        {
            if (!Servers.TryGetValue(id, out var server))
                return Error(HttpStatusCode.NotFound, "not_found", "server not found");
            lock (_lock)
                server.Polls++;
            return Json(new { server = ServerDto(server) });
        }
        if (method == HttpMethod.Delete.Method)
        {
            if (FailServerDelete)
                return Error(HttpStatusCode.InternalServerError, "server_error", "delete failed");
            Log($"delete-server:{id}");
            return Servers.TryRemove(id, out _)
                ? Json(new { action = new { id = NextId(), status = "success", command = "delete_server" } })
                : Error(HttpStatusCode.NotFound, "not_found", "server not found");
        }
        return Error(HttpStatusCode.NotFound, "not_found", "no such endpoint");
    }

    private HttpResponseMessage CreateServer(string body)
    {
        ServerCreateBodies.Add(body);
        var failure = FailNextServerCreate;
        FailNextServerCreate = null;
        var retryAfter = NextCreateRetryAfterSeconds;
        NextCreateRetryAfterSeconds = null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var name = root.GetProperty("name").GetString()!;
        var labels = root.GetProperty("labels").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var publicNet = root.GetProperty("public_net");
        var enableIpv4 = publicNet.GetProperty("enable_ipv4").GetBoolean();
        var enableIpv6 = publicNet.GetProperty("enable_ipv6").GetBoolean();
        var id = NextId();
        var server = new FakeServer
        {
            Id = id,
            Name = name,
            Labels = labels,
            Ipv4 = enableIpv4 ? "192.0.2." + (id % 250 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Ipv6Net = enableIpv6 ? "2001:db8::/64" : null,
        };
        if (failure is { StoreServer: true })
            Servers[id] = server;
        if (failure is { } fail)
        {
            var error = Error(fail.Status, fail.Code, fail.Message);
            if (retryAfter is not null)
                error.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return error;
        }
        Servers[id] = server;
        Log($"create-server:{id}");
        return Json(new { server = ServerDto(server) }, HttpStatusCode.Created);
    }

    private object ServerDto(FakeServer server)
    {
        var status = ForceServerStatus
            ?? (server.Polls > RunningAfterPolls ? "running" : InitialServerStatus);
        return new
        {
            id = server.Id,
            name = server.Name,
            status,
            public_net = new
            {
                ipv4 = server.Ipv4 is null ? null : new { ip = server.Ipv4 },
                ipv6 = server.Ipv6Net is null ? null : new { ip = server.Ipv6Net },
            },
            labels = server.Labels,
        };
    }

    private HttpResponseMessage SshKeysRoute(
        string path, Dictionary<string, string> query, string method, string body)
    {
        var rest = path["ssh_keys".Length..].Trim('/');
        if (rest.Length == 0)
        {
            if (method == HttpMethod.Post.Method)
            {
                var failure = FailNextSshKeyCreate;
                FailNextSshKeyCreate = null;
                if (failure is { } fail)
                    return Error(fail.Status, fail.Code, fail.Message);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var id = NextId();
                var key = new FakeNamedResource
                {
                    Id = id,
                    Name = root.GetProperty("name").GetString()!,
                    Labels = root.GetProperty("labels").EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
                };
                SshKeys[id] = key;
                Log($"create-ssh-key:{id}");
                return Json(new { ssh_key = new { id = key.Id, name = key.Name, labels = key.Labels } },
                    HttpStatusCode.Created);
            }
            return Paged("ssh_keys",
                SshKeys.Values.Where(k => MatchesLabels(k.Labels, ParseSelector(query)))
                    .OrderBy(k => k.Id)
                    .Select(k => (object)new { id = k.Id, name = k.Name, labels = k.Labels })
                    .ToList(),
                query);
        }
        if (method == HttpMethod.Delete.Method && long.TryParse(rest, out var deleteId))
        {
            Log($"delete-ssh-key:{deleteId}");
            return SshKeys.TryRemove(deleteId, out _)
                ? Json(new { action = new { id = NextId(), status = "success", command = "delete_ssh_key" } })
                : Error(HttpStatusCode.NotFound, "not_found", "ssh key not found");
        }
        return Error(HttpStatusCode.NotFound, "not_found", "no such endpoint");
    }

    private HttpResponseMessage FirewallsRoute(
        string path, Dictionary<string, string> query, string method, string body)
    {
        var rest = path["firewalls".Length..].Trim('/');
        if (rest.Length == 0)
        {
            if (method == HttpMethod.Post.Method)
            {
                FirewallCreateBodies.Add(body);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var id = NextId();
                var firewall = new FakeNamedResource
                {
                    Id = id,
                    Name = root.GetProperty("name").GetString()!,
                    Labels = root.GetProperty("labels").EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
                };
                Firewalls[id] = firewall;
                Log($"create-firewall:{id}");
                return Json(new { firewall = new { id = firewall.Id, name = firewall.Name, labels = firewall.Labels } },
                    HttpStatusCode.Created);
            }
            return Paged("firewalls",
                Firewalls.Values.Where(f => MatchesLabels(f.Labels, ParseSelector(query)))
                    .OrderBy(f => f.Id)
                    .Select(f => (object)new { id = f.Id, name = f.Name, labels = f.Labels })
                    .ToList(),
                query);
        }
        if (method == HttpMethod.Delete.Method && long.TryParse(rest, out var deleteId))
        {
            Log($"delete-firewall:{deleteId}");
            return Firewalls.TryRemove(deleteId, out _)
                ? Json(new { action = new { id = NextId(), status = "success", command = "delete_firewall" } })
                : Error(HttpStatusCode.NotFound, "not_found", "firewall not found");
        }
        return Error(HttpStatusCode.NotFound, "not_found", "no such endpoint");
    }

    private Task<HttpResponseMessage> FloatingIpsRouteAsync(
        string path, Dictionary<string, string> query, string method, string body)
    {
        var rest = path["floating_ips".Length..].Trim('/');
        if (rest.Length == 0)
        {
            if (method == HttpMethod.Post.Method)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var id = NextId();
                var floating = new FakeFloatingIp
                {
                    Id = id,
                    Name = root.GetProperty("name").GetString()!,
                    Ip = FloatingIpOverride
                        ?? "203.0.113." + (id % 200 + 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Server = root.TryGetProperty("server", out var serverEl)
                        && serverEl.ValueKind == JsonValueKind.Number ? serverEl.GetInt64() : null,
                    Labels = root.GetProperty("labels").EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
                };
                FloatingIps[id] = floating;
                Log($"create-floating-ip:{id}");
                return Task.FromResult(Json(new
                {
                    floating_ip = new
                    {
                        id = floating.Id, name = floating.Name, ip = floating.Ip,
                        server = floating.Server, labels = floating.Labels,
                    },
                    action = new { id = NextId(), status = "success", command = "assign_floating_ip" },
                }, HttpStatusCode.Created));
            }
            return Task.FromResult(Paged("floating_ips",
                FloatingIps.Values.Where(f => MatchesLabels(f.Labels, ParseSelector(query)))
                    .OrderBy(f => f.Id)
                    .Select(f => (object)new
                    {
                        id = f.Id, name = f.Name, ip = f.Ip, server = f.Server, labels = f.Labels,
                    })
                    .ToList(),
                query));
        }
        var slash = rest.IndexOf('/');
        if (slash >= 0 && rest[(slash + 1)..] == "actions/unassign" && long.TryParse(rest[..slash], out var unassignId))
        {
            if (!FloatingIps.TryGetValue(unassignId, out var floating))
                return Task.FromResult(Error(HttpStatusCode.NotFound, "not_found", "floating IP not found"));
            floating.Server = null;
            Log($"unassign-floating-ip:{unassignId}");
            return Task.FromResult(Json(new { action = new { id = NextId(), status = "success", command = "unassign_floating_ip" } },
                HttpStatusCode.Created));
        }
        if (method == HttpMethod.Delete.Method && long.TryParse(rest, out var deleteId))
        {
            Log($"delete-floating-ip:{deleteId}");
            return Task.FromResult(FloatingIps.TryRemove(deleteId, out _)
                ? Json(new { action = new { id = NextId(), status = "success", command = "delete_floating_ip" } })
                : Error(HttpStatusCode.NotFound, "not_found", "floating IP not found"));
        }
        return Task.FromResult(Error(HttpStatusCode.NotFound, "not_found", "no such endpoint"));
    }

    private HttpResponseMessage Paged(string property, List<object> items, Dictionary<string, string> query)
    {
        var page = query.TryGetValue("page", out var pageRaw) && int.TryParse(pageRaw, out var p) ? Math.Max(1, p) : 1;
        var perPage = query.TryGetValue("per_page", out var perRaw) && int.TryParse(perRaw, out var pp)
            ? Math.Clamp(pp, 1, 100) : 25;
        var skip = (page - 1) * perPage;
        var slice = items.Skip(skip).Take(perPage).ToList();
        int? next = skip + perPage < items.Count ? page + 1 : null;
        return Json(new Dictionary<string, object>
        {
            [property] = slice,
            ["meta"] = PageMeta(page, next),
        });
    }

    private static object PageMeta(int page, int? next) =>
        new { pagination = new { page, per_page = 25, previous_page = page > 1 ? page - 1 : (int?)null, next_page = next, last_page = page, total_entries = 0 } };

    private static Dictionary<string, string> ParseSelector(Dictionary<string, string> query)
    {
        var selector = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!query.TryGetValue("label_selector", out var raw) || string.IsNullOrEmpty(raw))
            return selector;
        foreach (var part in raw.Split(','))
        {
            var eq = part.IndexOf("==", StringComparison.Ordinal);
            if (eq > 0)
                selector[part[..eq]] = part[(eq + 2)..];
        }
        return selector;
    }

    private static bool MatchesLabels(Dictionary<string, string> labels, Dictionary<string, string> selector) =>
        selector.All(kvp => labels.TryGetValue(kvp.Key, out var value)
            && string.Equals(value, kvp.Value, StringComparison.Ordinal));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                continue;
            result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return result;
    }

    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        // The vendor API returns camelCase/lowercase member names; the fake
        // mirrors that so the client's exact-name parsing is genuinely tested.
        var json = JsonSerializer.Serialize(payload, CamelCaseJson);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    internal static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(new { error = new { code, message } }, status);

    internal sealed class FakeServer
    {
        public long Id;
        public string Name = string.Empty;
        public Dictionary<string, string> Labels = new(StringComparer.Ordinal);
        public string? Ipv4;
        public string? Ipv6Net;
        public int Polls;
    }

    internal sealed class FakeNamedResource
    {
        public long Id;
        public string Name = string.Empty;
        public Dictionary<string, string> Labels = new(StringComparer.Ordinal);
    }

    internal sealed class FakeFloatingIp
    {
        public long Id;
        public string Name = string.Empty;
        public string Ip = string.Empty;
        public long? Server;
        public Dictionary<string, string> Labels = new(StringComparer.Ordinal);
    }
}
