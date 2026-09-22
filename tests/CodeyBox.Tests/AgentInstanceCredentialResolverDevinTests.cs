using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the per-instance file-path credential path for Devin: a member whose
/// <c>CredentialReference.FilePath</c> points at a credentials.toml gets the
/// raw TOML shipped verbatim under <c>CODEYBOX_DEVIN_AUTH_TOML</c>, and its
/// quota credentials resolve to (api_key, api_server_url) extracted from that
/// file — the login-assigned endpoint the quota RPC lives on.
/// </summary>
public sealed class AgentInstanceCredentialResolverDevinTests : IDisposable
{
    private readonly string _tempDir;

    public AgentInstanceCredentialResolverDevinTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cb-devin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static AgentMembership NewDevinMember(string filePath) => new()
    {
        Agent = AgentKind.Devin,
        Billing = AgentBilling.Subscription,
        QualityScore = 90,
        CredentialReference = new AgentCredentialReference { FilePath = filePath },
    };

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_ShipsTomlVerbatim()
    {
        const string toml = "api_key = \"sk-x\"\napi_server_url = \"https://u.example\"\n";
        var path = Path.Combine(_tempDir, "credentials.toml");
        await File.WriteAllTextAsync(path, toml);

        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(
            NewDevinMember(path));

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Devin, credential!.Agent);
        Assert.Equal(toml, credential.EnvironmentVariables["CODEYBOX_DEVIN_AUTH_TOML"]);
    }

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_MissingFile_ReturnsNull()
    {
        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(
            NewDevinMember(Path.Combine(_tempDir, "absent.toml")));

        Assert.Null(credential);
    }

    [Fact]
    public void ResolveQuotaCredentials_FilePath_ExtractsKeyAndEndpoint()
    {
        const string toml = "api_key = \"sk-quota\"\napi_server_url = \"https://quota.example\"\n";
        var path = Path.Combine(_tempDir, "credentials.toml");
        File.WriteAllText(path, toml);

        var creds = AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            NewDevinMember(path), () => null);

        Assert.NotNull(creds);
        Assert.Equal("sk-quota", creds!.AccessToken);
        Assert.Equal("https://quota.example", creds.EndpointBaseUrl);
    }

    [Fact]
    public void ResolveQuotaCredentials_WindsurfKeyOnly_ExtractsToken()
    {
        // Live-verified: a real credentials.toml from `devin auth login` has
        // windsurf_api_key, not api_key.
        const string toml = "windsurf_api_key = \"devin-session-token$abc\"\n" +
            "api_server_url = \"https://server.codeium.com\"\n";
        var path = Path.Combine(_tempDir, "credentials.toml");
        File.WriteAllText(path, toml);

        var creds = AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            NewDevinMember(path), () => null);

        Assert.NotNull(creds);
        Assert.Equal("devin-session-token$abc", creds!.AccessToken);
        Assert.Equal("https://server.codeium.com", creds.EndpointBaseUrl);
    }

    [Fact]
    public void ResolveQuotaCredentials_NoApiKey_FallsThrough()
    {
        // A file without api_key yields no usable credential — the member's
        // file must not mask a healthy fallback credential.
        var path = Path.Combine(_tempDir, "credentials.toml");
        File.WriteAllText(path, "api_server_url = \"https://u.example\"\n");

        var fallback = new AgentQuotaCredentials("fallback-token");
        var creds = AgentInstanceCredentialResolver.ResolveQuotaCredentials(
            NewDevinMember(path), () => fallback);

        Assert.Same(fallback, creds);
    }
}
