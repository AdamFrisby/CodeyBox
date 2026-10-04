using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class AuditRunCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };

    private const string RunBody = """
{"id":"abc123","project":"proj-a","state":"Queued","aggregate":"Unknown","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":null,"auditors":["tool:lint"],"profile":null,"configDigest":"deadbeef","createdAt":"2026-10-04T00:00:00Z","updatedAt":"2026-10-04T00:00:00Z","completedAt":null,"replayed":false}
""";

    [Fact]
    public async Task Run_PostsSelectionAndRendersSummary()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(RunBody) };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["audit-run", "run", "--project", "proj-a", "--ref", new string('a', 40),
                    "--auditor", "tool:lint", "--idempotency-key", "k1"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.Contains("/audit-runs", captured.RequestUri!.ToString());
            Assert.Equal("k1", captured.Headers.GetValues("Idempotency-Key").Single());
            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal("proj-a", doc.RootElement.GetProperty("project").GetString());
            Assert.Equal("tool:lint", doc.RootElement.GetProperty("auditors")[0].GetString());
            Assert.Contains("abc123", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Run_ProfileSelection_PostsProfile()
    {
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(RunBody) };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["audit-run", "run", "--project", "proj-a", "--ref", new string('b', 40),
                    "--profile", "quick", "--base-ref", new string('c', 40)],
                factory);

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal("quick", doc.RootElement.GetProperty("profile").GetString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task List_RendersRunTable()
    {
        var factory = MakeFactory(_ => JsonResponse("[" + RunBody + "]"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["audit-run", "list", "--project", "proj-a", "--limit", "10"], factory);

            Assert.Equal(0, code);
            Assert.Contains("abc123", output.Out.ToString());
            Assert.Contains("proj-a", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Show_Cancel_Report_Logs_Artifacts_HitScopedRoutes()
    {
        var paths = new List<string>();
        var factory = MakeFactory(req =>
        {
            paths.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            return req.Method == HttpMethod.Post && req.RequestUri!.PathAndQuery.EndsWith("/cancel")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunBody) }
                : JsonResponse(RunBody);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            Assert.Equal(0, await CliApp.InvokeAsync(["audit-run", "show", "abc123", "--project", "proj-a"], factory));
            Assert.Equal(0, await CliApp.InvokeAsync(["audit-run", "cancel", "abc123"], factory));
            Assert.Equal(0, await CliApp.InvokeAsync(
                ["audit-run", "report", "abc123", "--project", "proj-a", "--json"], factory));
            Assert.Equal(0, await CliApp.InvokeAsync(
                ["audit-run", "logs", "abc123", "--project", "proj-a", "--json"], factory));
            Assert.Equal(0, await CliApp.InvokeAsync(
                ["audit-run", "artifacts", "abc123", "--project", "proj-a", "--json"], factory));
            Assert.Equal(0, await CliApp.InvokeAsync(
                ["audit-run", "artifact", "abc123", "auditor-tool.log", "--project", "proj-a", "--json"], factory));
            Assert.Contains(paths, p => p.Contains("/audit-runs/abc123?project=proj-a"));
            Assert.Contains(paths, p => p.Contains("/audit-runs/abc123/cancel"));
            Assert.Contains(paths, p => p.Contains("/audit-runs/abc123/reports"));
            Assert.Contains(paths, p => p.Contains("/audit-runs/abc123/logs"));
            Assert.Contains(paths, p => p.Contains("/audit-runs/abc123/artifacts?project=proj-a"));
            Assert.Contains(paths, p => p.Contains("/audit-runs/abc123/artifacts/auditor-tool.log"));
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Run_ServerRejection_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"Unknown auditor\"}"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["audit-run", "run", "--project", "proj-a", "--ref", new string('a', 40),
                    "--auditor", "nope"],
                factory);

            Assert.NotEqual(0, code);
            Assert.NotEmpty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
