using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the per-instance file-path credential path for Antigravity. This is the
/// fix for the env-var freeze: <see cref="AntigravityEnvironmentCredentialProvider"/>
/// reads <c>CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON</c> once at process launch, so
/// a keyring rotation requires a re-dump AND an orchestrator restart. Wiring
/// the antigravity member's <c>CredentialFilePath</c> at
/// <c>~/.codeybox/antigravity-token.json</c> moves the credential read into
/// <see cref="AgentInstanceCredentialResolver"/>, which re-reads the file on
/// every dispatch — a re-dump of the file (e.g. by a periodic keyring-to-file
/// refresher) is picked up on the next antigravity dispatch with no restart.
/// </summary>
public sealed class AgentInstanceCredentialResolverAntigravityTests : IDisposable
{
    private readonly string _tempDir;

    public AgentInstanceCredentialResolverAntigravityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cb-aigr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_tempDir);
    }

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_ShipsAgyBundleVerbatimWithRefreshToken()
    {
        const string raw =
            """{"auth_method":"consumer","token":{"access_token":"ya29.live","token_type":"Bearer","refresh_token":"rt-in-vm","expiry":"2026-06-10T19:57:49+10:00"}}""";
        var path = Path.Combine(_tempDir, "antigravity-token.json");
        await File.WriteAllTextAsync(path, raw);

        var member = NewAntigravityMember(filePath: path);

        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Antigravity, credential!.Agent);
        Assert.True(credential.EnvironmentVariables.TryGetValue(
            AntigravityConstants.OAuthCredsEnvVar, out var bundle));
        // Verbatim — agy's fileTokenStorage reads this exact envelope and needs
        // the refresh_token to refresh the access_token in-VM.
        Assert.Equal(raw, bundle);
        Assert.Contains("rt-in-vm", bundle);
    }

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_RereadsFileOnEachDispatchWithoutRestart()
    {
        const string firstDump =
            """{"auth_method":"consumer","token":{"access_token":"ya29.first","token_type":"Bearer","refresh_token":"rt-1","expiry":"2026-06-10T19:57:49+10:00"}}""";
        const string secondDump =
            """{"auth_method":"consumer","token":{"access_token":"ya29.second","token_type":"Bearer","refresh_token":"rt-2","expiry":"2026-06-11T19:57:49+10:00"}}""";
        var path = Path.Combine(_tempDir, "antigravity-token.json");
        await File.WriteAllTextAsync(path, firstDump);

        var member = NewAntigravityMember(filePath: path);

        var first = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);
        Assert.Equal(firstDump, first!.EnvironmentVariables[AntigravityConstants.OAuthCredsEnvVar]);

        // Operator re-dumps the keyring token to the same file (e.g. via
        // codey-dump-antigravity-token.sh). No process restart, no credential
        // reference change — just the on-disk bytes.
        await File.WriteAllTextAsync(path, secondDump);

        var second = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);

        Assert.Equal(secondDump, second!.EnvironmentVariables[AntigravityConstants.OAuthCredsEnvVar]);
        Assert.Contains("ya29.second", second.EnvironmentVariables[AntigravityConstants.OAuthCredsEnvVar]);
        Assert.Contains("rt-2", second.EnvironmentVariables[AntigravityConstants.OAuthCredsEnvVar]);
    }

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_MissingFile_ReturnsNull()
    {
        var member = NewAntigravityMember(
            filePath: Path.Combine(_tempDir, "does-not-exist.json"));

        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);

        Assert.Null(credential);
    }

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_MalformedJson_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "antigravity-token.json");
        await File.WriteAllTextAsync(path, "not-json");

        var member = NewAntigravityMember(filePath: path);

        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);

        Assert.Null(credential);
    }

    private static AgentMembership NewAntigravityMember(string filePath) => new()
    {
        Agent = AgentKind.Antigravity,
        Billing = AgentBilling.Subscription,
        QualityScore = 70,
        CredentialReference = new AgentCredentialReference { FilePath = filePath },
    };
}
