using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using App = CodeyBox.Admin.Web.Components.App;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

public sealed class AppRenderingTests : BunitContext
{
    [Fact]
    public void App_AnonymousRequest_DoesNotRenderInteractiveServerMarker()
    {
        AddAuthorization().SetNotAuthorized();
        Services.AddSingleton<ICodeyBoxApiClient>(new FakeApiClient([]));
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        });
        JSInterop.Setup<string>("Blazor._internal.PageTitle.getAndRemoveExistingTitle")
            .SetResult(string.Empty);

        var cut = Render<App>();

        Assert.DoesNotContain("Blazor:server", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_framework/blazor.web.js", cut.Markup, StringComparison.Ordinal);
    }
}
