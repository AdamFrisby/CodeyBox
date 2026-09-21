using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinCliCredentialsFileCredentialProvider"/>: the
/// host credentials file is re-read per pickup and shipped verbatim under
/// <c>CODEYBOX_DEVIN_AUTH_TOML</c> (the runner materialises it in-guest);
/// other agent kinds fall through.
/// </summary>
public sealed class DevinCliCredentialsFileCredentialProviderTests : IDisposable
{
    private readonly string _workspace;

    public DevinCliCredentialsFileCredentialProviderTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-devin-creds-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private string WriteCredFile(string content)
    {
        var path = Path.Combine(_workspace, "credentials.toml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task GetAsync_Devin_ShipsTomlVerbatim()
    {
        const string toml = "api_key = \"sk-x\"\napi_server_url = \"https://u.example\"\n";
        var path = WriteCredFile(toml);
        using var provider = new DevinCliCredentialsFileCredentialProvider(path, watch: false);

        var cred = await provider.GetAsync(AgentKind.Devin);

        Assert.NotNull(cred);
        Assert.Equal(AgentKind.Devin, cred!.Agent);
        Assert.Equal(toml, cred.EnvironmentVariables["CODEYBOX_DEVIN_AUTH_TOML"]);
    }

    [Fact]
    public async Task GetAsync_OtherKind_ReturnsNull()
    {
        var path = WriteCredFile("api_key = \"sk-x\"");
        using var provider = new DevinCliCredentialsFileCredentialProvider(path, watch: false);

        Assert.Null(await provider.GetAsync(AgentKind.Cursor));
        Assert.Null(await provider.GetAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task GetAsync_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(_workspace, "absent", "credentials.toml");
        using var provider = new DevinCliCredentialsFileCredentialProvider(path, watch: false);

        Assert.Null(await provider.GetAsync(AgentKind.Devin));
    }

    [Fact]
    public async Task GetAsync_RereadsFileOnEachCall()
    {
        var path = WriteCredFile("api_key = \"sk-first\"");
        using var provider = new DevinCliCredentialsFileCredentialProvider(path, watch: false);

        var first = await provider.GetAsync(AgentKind.Devin);
        Assert.Contains("sk-first", first!.EnvironmentVariables["CODEYBOX_DEVIN_AUTH_TOML"]);

        // Rotated credential on the same path — no orchestrator restart.
        File.WriteAllText(path, "api_key = \"sk-second\"");
        var second = await provider.GetAsync(AgentKind.Devin);
        Assert.Contains("sk-second", second!.EnvironmentVariables["CODEYBOX_DEVIN_AUTH_TOML"]);
    }
}
