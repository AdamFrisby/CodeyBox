using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using App = CodeyBox.Admin.Web.Components.App;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The interactive circuit is gated on authentication only when
/// authentication is required. With RequireAuth off there is no login and no
/// principal, so an anonymous request must still get the circuit and the
/// page scripts — otherwise every page renders as static HTML.
/// </summary>
public sealed class AppRenderingTests : BunitContext
{
    private void SetupAnonymous(bool requireAuth)
    {
        AddAuthorization().SetNotAuthorized();
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([]));
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        });
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBoxAdmin:RequireAuth"] = requireAuth ? "true" : "false",
            })
            .Build());
        JSInterop.Setup<string>("Blazor._internal.PageTitle.getAndRemoveExistingTitle")
            .SetResult(string.Empty);
        // The root route is the map now, and the map reads its browser prefs at init.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void App_AnonymousRequest_WithAuthRequired_DoesNotRenderInteractiveServerMarker()
    {
        SetupAnonymous(requireAuth: true);

        var cut = Render<App>();

        Assert.DoesNotContain("Blazor:server", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_framework/blazor.web.js", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void App_AnonymousRequest_WithoutAuthRequired_RendersTheCircuitAndPageScripts()
    {
        SetupAnonymous(requireAuth: false);

        var cut = Render<App>();

        Assert.Contains("_framework/blazor.web.js", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("js/fleet-map.js", cut.Markup, StringComparison.Ordinal);
    }
}
