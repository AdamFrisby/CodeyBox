using System.Text.Json;
using CodeyBox.BlaxelPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the Blaxel API contract the provider is coded against. Each test
/// asserts values the production deserializers produced from a recorded
/// response shape, so a drift between the provider and the real service fails
/// here — not deep inside a pipeline phase. Shapes: control-plane sandbox
/// view (metadata/spec/state/status), both list envelopes (cursor-paginated
/// wrapper and legacy bare array), and data-plane process view + logs.
/// There is deliberately no live counterpart in CI (billable cloud VMs); the
/// gated <c>BlaxelIntegrationTests</c> covers the real service on demand.
/// </summary>
public sealed class BlaxelRecordedShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "blaxel", name);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "blaxel", name);
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void SandboxView_DeserializesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<BlaxelSandboxView>(ReadFixture("sandbox-view.json"), JsonOptions);
        Assert.NotNull(view);

        Assert.Equal("codeybox-a1b2c3d4e5f60708-12345", view.Name);
        Assert.Equal(
            "https://sbx-codeybox-a1b2c3d4e5f60708-12345-testws.us-pdx-1.bl.run",
            view.Url);
        Assert.Equal("us-pdx-1", view.Region);
        Assert.Equal("RUNNING", view.State);
        Assert.Equal("DEPLOYED", view.Status);
        Assert.Equal("true", view.Metadata!.Labels!["codeybox-managed"]);
        Assert.Equal("blaxel", view.Metadata.Labels["codeybox-provider"]);

        // The provider addresses the data plane with the reported endpoint.
        Assert.Equal(view.Url, BlaxelSandboxProvider.ResolveSandboxUrl(view, "testws"));
    }

    [Fact]
    public void SandboxList_WrappedEnvelope_ParsesDataAndCursor()
    {
        var page = BlaxelSandboxListPage.Parse(ReadFixture("sandbox-list-wrapped.json"));

        var sandbox = Assert.Single(page.Sandboxes);
        Assert.Equal("codeybox-a1b2c3d4e5f60708-12345", sandbox.Name);
        Assert.Equal("STANDBY", sandbox.State);
        Assert.True(page.HasMore);
        Assert.Equal("cursor-next-page", page.Cursor);
    }

    [Fact]
    public void SandboxList_BareArray_ParsesWithoutCursor()
    {
        var page = BlaxelSandboxListPage.Parse(ReadFixture("sandbox-list-bare.json"));

        var sandbox = Assert.Single(page.Sandboxes);
        Assert.Equal("codeybox-legacy-1", sandbox.Name);
        Assert.Equal("eu-lon-1", sandbox.Region);
        Assert.Equal("RUNNING", sandbox.State);
        Assert.False(page.HasMore);
        Assert.Null(page.Cursor);
    }

    [Fact]
    public void ProcessView_DeserializesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<BlaxelProcessView>(ReadFixture("process-view.json"), JsonOptions);
        Assert.NotNull(view);

        Assert.Equal("1234", view.Pid);
        Assert.Equal("completed", view.Status);
        Assert.Equal(0, view.ExitCode);
        Assert.Equal("hi\n", view.Stdout);
        Assert.Equal(string.Empty, view.Stderr);
        Assert.True(view.IsTerminal);
    }

    [Fact]
    public void ProcessLogs_DeserializesRecordedShape()
    {
        var logs = JsonSerializer.Deserialize<BlaxelProcessLogs>(ReadFixture("process-logs.json"), JsonOptions);
        Assert.NotNull(logs);

        Assert.Equal("hi\n", logs.Stdout);
        Assert.Equal("boom\n", logs.Stderr);
        Assert.Equal("hi\nboom\n", logs.Logs);
    }
}
