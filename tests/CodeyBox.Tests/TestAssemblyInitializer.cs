using System.Runtime.CompilerServices;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Runs once when the test assembly loads, before any test executes.
/// Pre-sets <c>ASPNETCORE_URLS</c> to <c>http://127.0.0.1:0</c> so the
/// production default in <c>src/CodeyBox.Api/Program.cs</c> — which pins
/// <c>http://127.0.0.1:5000</c> when no URL config is present — is skipped
/// under <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
/// WebApplicationFactory swaps the IServer for an in-memory TestServer, so
/// the URL is normally inert; port 0 (auto-assign) guarantees safety even
/// if a code path ever lets Kestrel bind, since parallel xunit tests would
/// otherwise race on the fixed 5000.
/// </summary>
internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        // Fail fast in CI on schema drift: every WebhookEvent that goes
        // through the broadcaster is validated for the three required
        // envelope fields. Production code path keeps this off.
        WebhookEventBroadcaster.StrictSchemaValidationForTests = true;
    }
}
