using System.Text;
using System.Text.Json;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Calls Anthropic's messages API with the computer-use tool to plan the next
/// exploration actions for cheap-model CUA authoring.
/// </summary>
public sealed class AnthropicComputerUseModelClient : IComputerUseModelClient
{
    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string ComputerUseBeta = "computer-use-2025-01-24";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;

    public AnthropicComputerUseModelClient(HttpClient httpClient, Func<string?> apiKeyProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    }

    public async Task<IReadOnlyList<ComputerUseRequest>> PlanNextActionsAsync(
        ComputerUseModelTurnContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheapModelAllowlist.EnsureCheap(context.ModelId);

        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("ANTHROPIC_API_KEY is required for cheap-model CUA authoring.");

        var contentBlocks = new List<object>
        {
            new
            {
                type = "text",
                text = BuildPrompt(context),
            },
        };

        if (context.ScreenshotPng is { Length: > 0 })
        {
            contentBlocks.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/png",
                    data = Convert.ToBase64String(context.ScreenshotPng),
                },
            });
        }

        var payload = new
        {
            model = context.ModelId,
            max_tokens = 1024,
            tools = new object[]
            {
                new
                {
                    type = ComputerUseBeta,
                    name = "computer",
                    display_width_px = 1280,
                    display_height_px = 800,
                },
            },
            messages = new[]
            {
                new { role = "user", content = contentBlocks },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Headers.Add("anthropic-beta", ComputerUseBeta);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var tail = body.Length <= 240 ? body : body[^240..];
            throw new InvalidOperationException(
                $"Anthropic computer-use call failed ({(int)response.StatusCode}): {tail}");
        }

        return ParseToolUses(body);
    }

    internal static IReadOnlyList<ComputerUseRequest> ParseToolUses(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var actions = new List<ComputerUseRequest>();
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return actions;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var type) || type.GetString() != "tool_use")
                continue;
            if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                continue;

            var action = input.TryGetProperty("action", out var actionElement)
                ? actionElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(action))
                continue;

            switch (action.Trim().ToLowerInvariant())
            {
                case "screenshot":
                    actions.Add(new ComputerUseRequest { Action = "screenshot" });
                    break;
                case "left_click":
                case "click":
                    actions.Add(new ComputerUseRequest
                    {
                        Action = "click",
                        X = ReadCoordinate(input, 0),
                        Y = ReadCoordinate(input, 1),
                    });
                    break;
                case "type":
                case "key":
                    actions.Add(new ComputerUseRequest
                    {
                        Action = action.Equals("type", StringComparison.OrdinalIgnoreCase) ? "type" : "key",
                        Text = ReadString(input, "text"),
                        Key = ReadString(input, "key") ?? ReadString(input, "text"),
                    });
                    break;
            }
        }

        return actions;
    }

    private static string BuildPrompt(ComputerUseModelTurnContext context)
    {
        var builder = new StringBuilder();
        builder.Append("Explore the target capability and drive the real UI. Target: ");
        builder.Append(context.Plan.TargetName);
        if (!string.IsNullOrWhiteSpace(context.Plan.EntryUrl))
        {
            builder.Append(" Entry URL: ");
            builder.Append(context.Plan.EntryUrl);
        }

        builder.Append(" Turn ");
        builder.Append(context.TurnIndex + 1);
        builder.Append('.');
        return builder.ToString();
    }

    private static int ReadCoordinate(JsonElement input, int index)
    {
        if (!input.TryGetProperty("coordinate", out var coordinate) || coordinate.ValueKind != JsonValueKind.Array)
            return 0;

        var i = 0;
        foreach (var value in coordinate.EnumerateArray())
        {
            if (i == index && value.TryGetInt32(out var parsed))
                return parsed;
            i++;
        }

        return 0;
    }

    private static string? ReadString(JsonElement input, string name)
        => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
