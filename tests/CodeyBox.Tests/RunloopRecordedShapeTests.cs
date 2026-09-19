using System.Text.Json;
using CodeyBox.RunloopPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape fixtures for the Runloop API contract. A live integration
/// test cannot run in CI (it needs a funded <c>RUNLOOP_API_KEY</c>, creates
/// billable cloud VMs, and requires egress to api.runloop.ai), so these
/// fixtures — transcribed from the published OpenAPI field names — pin the
/// shapes the client parses. Any single-field production mutation in the
/// deserializers flips these red. See <see cref="RunloopIntegrationTests"/>
/// for the live counterpart and its documented gate.
/// </summary>
public sealed class RunloopRecordedShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "runloop", name);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "runloop", name);
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void DevboxView_ParsesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<DevboxView>(Fixture("devbox-view.json"), JsonOptions);

        Assert.NotNull(view);
        Assert.Equal("dbx_2XyZ9AbCDeF", view.Id);
        Assert.Equal("running", view.Status);
        Assert.Equal("codeybox-a1b2c3d4-54321", view.Name);
        Assert.Equal("true", view.Metadata?["codeybox-managed"]);
        Assert.Equal("runloop", view.Metadata?["codeybox-provider"]);
        Assert.True(view.CreateTimeMs > 0);
    }

    [Fact]
    public void ExecutionView_ParsesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<AsyncExecutionView>(Fixture("execution-view.json"), JsonOptions);

        Assert.NotNull(view);
        Assert.Equal("completed", view.Status);
        Assert.Equal(0, view.ExitStatus);
        Assert.Equal("hello from the devbox\n", view.Stdout);
        Assert.Equal(string.Empty, view.Stderr);
        Assert.False(view.StdoutTruncated);
        Assert.False(view.StderrTruncated);
    }

    [Fact]
    public void SnapshotView_ParsesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<DiskSnapshotView>(Fixture("snapshot-view.json"), JsonOptions);

        Assert.NotNull(view);
        Assert.Equal("snap_3AbCDeFgHiJk", view.Id);
        Assert.Equal("dbx_2XyZ9AbCDeF", view.SourceDevboxId);
    }

    [Fact]
    public void DevboxList_ParsesRecordedShape()
    {
        var page = JsonSerializer.Deserialize<DevboxListPage>(Fixture("devbox-list.json"), JsonOptions);

        Assert.NotNull(page);
        Assert.False(page.HasMore);
        Assert.Equal(2, page.Devboxes.Count);
        Assert.Contains(page.Devboxes, d => d.Id == "dbx_2XyZ9AbCDeF");
    }
}
