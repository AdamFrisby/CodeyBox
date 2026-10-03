using System.Text.Json;
using CodeyBox.ModalPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape fixtures for the Modal control-plane contract. A live
/// integration test cannot run in CI (it needs funded <c>MODAL_TOKEN_ID</c> /
/// <c>MODAL_TOKEN_SECRET</c>, creates billable cloud sandboxes, and requires
/// egress to api.modal.com), and Modal offers no stable public REST sandbox
/// contract (the control plane is SDK-driven), so these fixtures — transcribed
/// from SDK/CLI traffic shapes at the time of writing — pin the shapes the
/// client parses. Any single-field production mutation in the deserializers
/// flips these red. See <see cref="ModalIntegrationTests"/> for the live
/// counterpart and its documented gate.
/// </summary>
public sealed class ModalRecordedShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "modal", name);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "modal", name);
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void SandboxView_ParsesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<ModalSandboxView>(Fixture("sandbox-view.json"), JsonOptions);

        Assert.NotNull(view);
        Assert.Equal("sb_000042", view.Id);
        Assert.Equal("running", view.Status);
        Assert.Equal("codeybox-a1b2c3d4-54321", view.Name);
        Assert.Equal("true", view.Metadata?["codeybox-managed"]);
        Assert.Equal("modal", view.Metadata?["codeybox-provider"]);
        Assert.True(view.CreatedAtUnix > 0);
    }

    [Fact]
    public void ExecView_ParsesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<ModalExecView>(Fixture("exec-view.json"), JsonOptions);

        Assert.NotNull(view);
        Assert.Equal("completed", view.Status);
        Assert.Equal(0, view.ExitCode);
        Assert.Equal("hello from the sandbox\n", view.Stdout);
        Assert.Equal(string.Empty, view.Stderr);
        Assert.False(view.StdoutTruncated);
        Assert.False(view.StderrTruncated);
    }

    [Fact]
    public void SnapshotView_ParsesRecordedShape()
    {
        var view = JsonSerializer.Deserialize<ModalSnapshotView>(Fixture("snapshot-view.json"), JsonOptions);

        Assert.NotNull(view);
        Assert.Equal("snap_000003", view.Id);
    }

    [Fact]
    public void SandboxList_ParsesRecordedShape()
    {
        var page = JsonSerializer.Deserialize<ModalSandboxListPage>(Fixture("sandbox-list.json"), JsonOptions);

        Assert.NotNull(page);
        Assert.False(page.HasMore);
        var sandbox = Assert.Single(page.Sandboxes);
        Assert.Equal("sb_000042", sandbox.Id);
        Assert.Equal("running", sandbox.Status);
    }
}
