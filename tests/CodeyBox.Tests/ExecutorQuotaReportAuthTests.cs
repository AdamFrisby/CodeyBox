using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Caller binding for executor quota-report ingress: the path host must
/// match the authenticated caller's host-bound token. A shared bearer (the
/// operator key or any token without an <c>ExecutorHostId</c> binding)
/// proves nothing about which host is calling, so it cannot report readings
/// — otherwise any bearer holder could forge another pool's meter by
/// asserting a victim host id. The pool's <c>HolderHostIds</c> remain the
/// second check inside the store.
/// </summary>
public sealed class ExecutorQuotaReportCallerTests
{
    private static WorkInitiator Initiator(string subject) => new()
    {
        Issuer = "test",
        Subject = subject,
        DisplayName = subject,
    };

    private static DefaultHttpContext ContextWith(ApiClientPrincipal principal)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiKeyAuth.PrincipalItemKey] = principal;
        return ctx;
    }

    private static int GetStatusCode(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        return status.StatusCode ?? StatusCodes.Status200OK;
    }

    [Fact]
    public void MissingPrincipal_IsUnauthorized()
    {
        var result = ExecutorEndpoints.CheckQuotaReportCaller(new DefaultHttpContext(), "exec-1");

        Assert.NotNull(result);
    }

    [Fact]
    public void MissingPrincipal_Returns401()
    {
        var result = ExecutorEndpoints.CheckQuotaReportCaller(new DefaultHttpContext(), "exec-1");

        Assert.Equal(StatusCodes.Status401Unauthorized, GetStatusCode(result!));
    }

    [Fact]
    public void AuthenticationDisabled_IsAllowed()
    {
        var ctx = ContextWith(new ApiClientPrincipal(
            ApiKeyAuth.AuthenticationDisabledClientName,
            Initiator("operator"),
            CanDelegateInitiator: false));

        Assert.Null(ExecutorEndpoints.CheckQuotaReportCaller(ctx, "exec-1"));
    }

    [Fact]
    public void UnboundToken_IsForbidden()
    {
        // The shared operator key and any named token without an
        // ExecutorHostId binding carry no host identity: rejecting them here
        // is what stops one bearer holder forging another host's meter.
        var ctx = ContextWith(new ApiClientPrincipal(
            "legacy-operator",
            Initiator("operator"),
            CanDelegateInitiator: false));

        var result = ExecutorEndpoints.CheckQuotaReportCaller(ctx, "exec-1");

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status403Forbidden, GetStatusCode(result));
    }

    [Fact]
    public void BoundToken_MatchingHost_IsAllowed()
    {
        var ctx = ContextWith(new ApiClientPrincipal(
            "exec-1-client",
            Initiator("exec-1"),
            CanDelegateInitiator: false,
            ExecutorHostId: "exec-1"));

        Assert.Null(ExecutorEndpoints.CheckQuotaReportCaller(ctx, "exec-1"));
    }

    [Fact]
    public void BoundToken_OtherHost_IsForbidden()
    {
        var ctx = ContextWith(new ApiClientPrincipal(
            "exec-2-client",
            Initiator("exec-2"),
            CanDelegateInitiator: false,
            ExecutorHostId: "exec-2"));

        var result = ExecutorEndpoints.CheckQuotaReportCaller(ctx, "exec-1");

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status403Forbidden, GetStatusCode(result));
    }

    [Fact]
    public void BoundToken_HostIdComparison_IsExactOrdinal()
    {
        var ctx = ContextWith(new ApiClientPrincipal(
            "exec-1-client",
            Initiator("exec-1"),
            CanDelegateInitiator: false,
            ExecutorHostId: "Exec-1"));

        var result = ExecutorEndpoints.CheckQuotaReportCaller(ctx, "exec-1");

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status403Forbidden, GetStatusCode(result));
    }
}

/// <summary>
/// HTTP-level wiring for quota-report ingress: the endpoint's own layer
/// (live registry registration) composed with the real
/// <see cref="ExecutorQuotaReportStore"/> (holder allowlist, storage).
/// Runs with authentication disabled so the caller-binding layer above
/// passes through as the local operator.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ExecutorQuotaReportIngressTests : IDisposable
{
    private readonly QuotaReportIngressFactory _factory = new();
    private readonly HttpClient _client;

    public ExecutorQuotaReportIngressTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task RegisterAsync(string hostId)
    {
        var resp = await _client.PostAsJsonAsync(
            "/executors/register", new { hostId });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static object ReportBody(string pool, double availablePct) => new
    {
        pool,
        availablePct,
        observedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task UnregisteredHost_ReturnsNotFound()
    {
        var resp = await _client.PostAsJsonAsync(
            "/executors/ghost/quota-reports", ReportBody("exec-pool", 42.5));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task NonHolderHost_ReturnsBadRequest()
    {
        await RegisterAsync("exec-1");
        await RegisterAsync("exec-2");

        var resp = await _client.PostAsJsonAsync(
            "/executors/exec-2/quota-reports", ReportBody("exec-pool", 42.5));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DeclaredHolder_ReturnsOkAndStoresReading()
    {
        await RegisterAsync("exec-1");

        var resp = await _client.PostAsJsonAsync(
            "/executors/exec-1/quota-reports", ReportBody("exec-pool", 42.5));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal("exec-pool", body.GetProperty("pool").GetString());

        var store = _factory.Services.GetRequiredService<ExecutorQuotaReportStore>();
        var snapshot = store.GetSnapshot("exec-pool");
        Assert.Equal(42.5, snapshot.AvailablePct);
    }
}

internal sealed class QuotaReportIngressFactory : WebApplicationFactory<Program>
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-quota-ingress-");
    private string _dbPath => _scratch.DbPath("quota-ingress.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                ["CodeyBox:QuotaRouter:Pools:exec-pool:Kind"] = "ResettingWindow",
                ["CodeyBox:QuotaRouter:Pools:exec-pool:ProbeSource"] = "ExecutorReported",
                ["CodeyBox:QuotaRouter:Pools:exec-pool:ReportedReadingMaxAgeSeconds"] = "300",
                ["CodeyBox:QuotaRouter:Pools:exec-pool:HolderHostIds:0"] = "exec-1",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { }
            TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
            _scratch.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// End-to-end caller binding through the real bearer middleware: requests
/// carrying a host-bound executor token, an unbound token, or no token at
/// all are admitted or rejected by the token-to-host binding before the
/// registry is even consulted. The <see cref="ApiKeyState"/> singleton is
/// replaced in DI, so no process environment variable is touched.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ExecutorQuotaReportMiddlewareTests : IDisposable
{
    private const string ExecutorToken = "test-bearer-bound-to-exec-1";
    private const string UnboundToken = "test-bearer-with-no-host-binding";

    private readonly QuotaReportAuthFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientWith(string? bearer)
    {
        var client = _factory.CreateClient();
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    [Fact]
    public async Task BoundToken_MatchingHost_ReportsSuccessfully()
    {
        using var client = ClientWith(ExecutorToken);

        var registered = await client.PostAsJsonAsync("/executors/register", new { hostId = "exec-1" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var reported = await client.PostAsJsonAsync(
            "/executors/exec-1/quota-reports",
            new { pool = "exec-pool", availablePct = 55.0, observedAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
    }

    [Fact]
    public async Task BoundToken_OtherHost_IsRejectedBeforeRegistryLookup()
    {
        using var client = ClientWith(ExecutorToken);

        // exec-2 is never registered: a 403 (not 404) proves the caller
        // binding runs before the registry existence check, so a rejected
        // caller cannot probe which host ids exist.
        var reported = await client.PostAsJsonAsync(
            "/executors/exec-2/quota-reports",
            new { pool = "exec-pool", availablePct = 55.0, observedAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.Forbidden, reported.StatusCode);
    }

    [Fact]
    public async Task UnboundToken_IsRejected()
    {
        using var client = ClientWith(UnboundToken);

        var registered = await client.PostAsJsonAsync("/executors/register", new { hostId = "exec-1" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var reported = await client.PostAsJsonAsync(
            "/executors/exec-1/quota-reports",
            new { pool = "exec-pool", availablePct = 55.0, observedAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.Forbidden, reported.StatusCode);
    }

    [Fact]
    public async Task MissingBearer_IsUnauthorized()
    {
        using var client = ClientWith(null);

        var reported = await client.PostAsJsonAsync(
            "/executors/exec-1/quota-reports",
            new { pool = "exec-pool", availablePct = 55.0, observedAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.Unauthorized, reported.StatusCode);
    }
}

internal sealed class QuotaReportAuthFactory : WebApplicationFactory<Program>
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-quota-auth-");
    private string _dbPath => _scratch.DbPath("quota-auth.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The startup required-configuration gate validates the
                // settings the real ApiKeyState factory would consume. This
                // factory replaces ApiKeyState wholesale with a hand-built
                // enabled state (so no process environment variable is
                // touched), which the gate cannot see — opting out here keeps
                // the gate satisfied without changing the middleware under
                // test, which resolves the replaced ApiKeyState from DI.
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                ["CodeyBox:QuotaRouter:Pools:exec-pool:Kind"] = "ResettingWindow",
                ["CodeyBox:QuotaRouter:Pools:exec-pool:ProbeSource"] = "ExecutorReported",
                ["CodeyBox:QuotaRouter:Pools:exec-pool:ReportedReadingMaxAgeSeconds"] = "300",
                ["CodeyBox:QuotaRouter:Pools:exec-pool:HolderHostIds:0"] = "exec-1",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<ApiKeyState>();
            services.AddSingleton(new ApiKeyState(Token: null, Disabled: false, Clients:
            [
                new ResolvedApiClient(
                    "exec-1-client",
                    "test-bearer-bound-to-exec-1",
                    new WorkInitiator { Issuer = "test", Subject = "exec-1", DisplayName = "exec-1" },
                    CanDelegateInitiator: false,
                    ExecutorHostId: "exec-1"),
                new ResolvedApiClient(
                    "unbound-client",
                    "test-bearer-with-no-host-binding",
                    new WorkInitiator { Issuer = "test", Subject = "service", DisplayName = "service" },
                    CanDelegateInitiator: false,
                    ExecutorHostId: null),
            ]));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { }
            TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
            _scratch.Dispose();
        }
        base.Dispose(disposing);
    }
}
