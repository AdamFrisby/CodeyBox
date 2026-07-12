using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the per-instance credential path for Crock. Regression guard for the
/// quota/execution credential divergence: a Crock member with its own
/// CredentialReference must resolve an EXECUTION credential carrying the
/// member's OWN CrockCode config (its Anthropic key) — not fall back to the
/// global CODEYBOX_CROCK_CONFIG_JSON bundle — so the batch is submitted under
/// the same key the quota probe routed on.
/// </summary>
public sealed class AgentInstanceCredentialResolverCrockTests : IDisposable
{
    private readonly string _tempDir;

    public AgentInstanceCredentialResolverCrockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cb-crock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ResolveCredentialAsync_FilePath_ShipsMemberConfigAsExecutionCredential()
    {
        const string raw =
            """{"anthropic_api_key":"sk-ant-member-a","tunnel_provider":"cloudflared"}""";
        var path = Path.Combine(_tempDir, "crock-config.json");
        await File.WriteAllTextAsync(path, raw);

        var member = NewCrockMember(filePath: path);

        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);

        Assert.NotNull(credential);
        Assert.Equal(AgentKind.Crock, credential!.Agent);
        // The runner materialises CROCK_CONFIG_JSON into ~/.crockcode/config.json;
        // it must be the MEMBER's config verbatim so execution bills key A.
        Assert.True(credential.EnvironmentVariables.TryGetValue("CROCK_CONFIG_JSON", out var shipped));
        Assert.Equal(raw, shipped);
        // The env-only member bundle carries no mounts; the sandbox-global
        // daemon mount is grafted on by PipelineRunner, not here.
        Assert.Empty(credential.Mounts);
    }

    [Fact]
    public async Task ResolveCredentialAsync_ConfigWithoutKey_FallsThroughToGlobalProvider()
    {
        // No anthropic key in the member config → the resolver declines so the
        // global provider chain handles it (returns null here since there is no
        // token env var configured either).
        var path = Path.Combine(_tempDir, "crock-config.json");
        await File.WriteAllTextAsync(path, """{"tunnel_provider":"ngrok"}""");

        var member = NewCrockMember(filePath: path);

        var credential = await AgentInstanceCredentialResolver.ResolveCredentialAsync(member);

        Assert.Null(credential);
    }

    private static AgentMembership NewCrockMember(string filePath) => new()
    {
        Agent = AgentKind.Crock,
        Billing = AgentBilling.PayPerApi,
        QualityScore = 50,
        CredentialReference = new AgentCredentialReference { FilePath = filePath },
    };
}
