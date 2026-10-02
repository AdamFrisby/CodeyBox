using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Api;
using CodeyBox.Api.Majordomo;
using CodeyBox.Majordomo;
using CodeyBox.Tests.Uat.ProjectsAndConfiguration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance coverage for the majordomo panel's server surface: the
/// autonomy switch (read, flip, clear, reject garbage), the operator
/// conversation (post, list, validate, operator-only gate), and the
/// executor's conversation trail (every tool call lands a tool-call and
/// tool-result row the panel replays inline).
/// </summary>
public sealed class MajordomoPanelApiTests
{
    private const string McpPath = "/mcp/majordomo";

    private static async Task<McpClient> ConnectAsync(HttpClient http)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, McpPath),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            loggerFactory: null,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    private static string OutcomeOf(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
            return structured.GetProperty("outcome").GetString()!;
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        Assert.NotNull(text);
        return JsonSerializer.Deserialize<JsonElement>(text!).GetProperty("outcome").GetString()!;
    }

    private static Dictionary<string, object?> CreateArgs(string title) => new()
    {
        ["item"] = new Dictionary<string, object?>
        {
            ["project_id"] = "test-project",
            ["title"] = title,
            ["prompt"] = "do " + title,
        },
    };

    private sealed record ModeBody(string Mode, string Source);
    private sealed record ConversationPage(string? Summary, List<ConversationRow> Entries);
    private sealed record ConversationRow(long Sequence, string Role, string Text, string? ToolName, DateTimeOffset RecordedAt);

    [Fact]
    public async Task Mode_Get_ReturnsConfiguredDefault()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        var mode = await http.GetFromJsonAsync<ModeBody>("/majordomo/mode");
        Assert.NotNull(mode);
        Assert.Equal("proposed", mode!.Mode);
        Assert.Equal("config", mode.Source);
    }

    [Fact]
    public async Task Mode_Set_Flips_And_Clear_Restores()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        var flipped = await (await http.PostAsJsonAsync("/majordomo/mode", new { mode = "autonomous" }))
            .Content.ReadFromJsonAsync<ModeBody>();
        Assert.Equal("autonomous", flipped!.Mode);
        Assert.Equal("override", flipped.Source);

        var reread = await http.GetFromJsonAsync<ModeBody>("/majordomo/mode");
        Assert.Equal("autonomous", reread!.Mode);
        Assert.Equal("override", reread.Source);

        var cleared = await (await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/majordomo/mode")))
            .Content.ReadFromJsonAsync<ModeBody>();
        Assert.Equal("proposed", cleared!.Mode);
        Assert.Equal("config", cleared.Source);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("7")]
    [InlineData("")]
    public async Task Mode_Set_RejectsUnknownValues(string mode)
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync("/majordomo/mode", new { mode });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var reread = await http.GetFromJsonAsync<ModeBody>("/majordomo/mode");
        Assert.Equal("proposed", reread!.Mode);
    }

    [Fact]
    public void ServerOptions_RejectsOutOfRangeStreamPoll()
    {
        Assert.Contains("ConversationStreamPollSeconds",
            MajordomoServerOptions.Validate(new MajordomoServerOptions { ConversationStreamPollSeconds = 0 }));
        Assert.Contains("ConversationStreamPollSeconds",
            MajordomoServerOptions.Validate(new MajordomoServerOptions { ConversationStreamPollSeconds = 61 }));
        Assert.Null(MajordomoServerOptions.Validate(new MajordomoServerOptions()));
    }

    [Fact]
    public async Task Conversation_Post_And_List_RoundTrip()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        var posted = await http.PostAsJsonAsync("/majordomo/conversation", new { text = "What is stuck?" });
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var entry = await posted.Content.ReadFromJsonAsync<ConversationRow>();
        Assert.Equal("operator", entry!.Role);
        Assert.Equal("What is stuck?", entry.Text);

        var page = await http.GetFromJsonAsync<ConversationPage>("/majordomo/conversation");
        Assert.Contains(page!.Entries, e => e.Text == "What is stuck?" && e.Role == "operator");

        var filtered = await http.GetFromJsonAsync<ConversationPage>(
            $"/majordomo/conversation?afterSequence={entry.Sequence}");
        Assert.DoesNotContain(filtered!.Entries, e => e.Sequence <= entry.Sequence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Conversation_Post_RejectsBlankText(string text)
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync("/majordomo/conversation", new { text });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Conversation_Post_RejectsOverlongText()
    {
        using var inner = new WorkItemApiFactory();
        using var host = inner.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeyBox:Majordomo:Conversation:MaxEntryChars"] = "1024",
                })));
        using var http = host.CreateClient();

        var response = await http.PostAsJsonAsync(
            "/majordomo/conversation", new { text = new string('x', 2000) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    public async Task Conversation_List_RejectsOutOfRangeLimit(string limit)
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        var response = await http.GetAsync($"/majordomo/conversation?limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Conversation_Stream_EmitsNewRowsAsServerSentEvents()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/majordomo/conversation", new { text = "stream me" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = await http.GetStreamAsync(
            "/majordomo/conversation/stream?afterSequence=0", cts.Token);
        using var reader = new StreamReader(stream);
        string? frame = null;
        while (!cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
                break;
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                frame = line;
                break;
            }
        }
        Assert.NotNull(frame);
        Assert.Contains("stream me", frame);
    }

    [Fact]
    public async Task Executor_RecordsToolCalls_InConversation()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        await using (var mcp = await ConnectAsync(http))
        {
            var call = await mcp.CallToolAsync("get_queue_status", new Dictionary<string, object?>());
            Assert.Equal("executed", OutcomeOf(call));
        }

        var page = await http.GetFromJsonAsync<ConversationPage>("/majordomo/conversation");
        var rows = page!.Entries.Where(e => e.ToolName == "get_queue_status").ToList();
        Assert.Contains(rows, e => e.Role == "toolcall");
        Assert.Contains(rows, e => e.Role == "toolresult");
    }

    [Fact]
    public async Task ModeOverride_FlipsExecutor_BetweenProposedAndAutonomous()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();

        await using var mcp = await ConnectAsync(http);
        var proposed = await mcp.CallToolAsync("create_work_item", CreateArgs("override flip a"));
        Assert.Equal("proposed", OutcomeOf(proposed));

        var flip = await http.PostAsJsonAsync("/majordomo/mode", new { mode = "Autonomous" });
        Assert.Equal(HttpStatusCode.OK, flip.StatusCode);

        var executed = await mcp.CallToolAsync("create_work_item", CreateArgs("override flip b"));
        Assert.Equal("executed", OutcomeOf(executed));

        var clear = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/majordomo/mode"));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        var proposedAgain = await mcp.CallToolAsync("create_work_item", CreateArgs("override flip c"));
        Assert.Equal("proposed", OutcomeOf(proposedAgain));
    }

    [Fact]
    public async Task PanelSurface_RefusesMajordomoCredential()
    {
        const string operatorKey = "test-token-operator-panel-surface-00";
        const string majordomoKey = "test-token-majordomo-panel-surface0";
        using var envOperator = new EnvironmentVariableScope("CODEYBOX_API_KEY", operatorKey);
        using var envMajordomo = new EnvironmentVariableScope("CODEYBOX_TEST_MAJORDOMO_TOKEN", majordomoKey);

        using var inner = new WorkItemApiFactory();
        using var host = inner.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "false",
                    ["CodeyBox:ApiClients:0:Name"] = "majordomo",
                    ["CodeyBox:ApiClients:0:TokenEnvVar"] = "CODEYBOX_TEST_MAJORDOMO_TOKEN",
                    ["CodeyBox:ApiClients:0:Principal:Issuer"] = "test",
                    ["CodeyBox:ApiClients:0:Principal:Subject"] = "majordomo",
                    ["CodeyBox:ApiClients:0:Principal:DisplayName"] = "Test majordomo",
                })));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", majordomoKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/majordomo/mode")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/majordomo/conversation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.PostAsJsonAsync("/majordomo/conversation", new { text = "let me in" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.PostAsJsonAsync("/majordomo/mode", new { mode = "autonomous" })).StatusCode);
    }
}
