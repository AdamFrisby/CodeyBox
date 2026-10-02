using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.DiscordPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Outbound rendering tests for the Discord provider: severity, summary and
/// fields render as a native embed with action-row buttons — never a dumped
/// text blob — and every button the provider emits decodes through the
/// shared <c>custom_id</c> contract the inbound endpoint honours.
/// </summary>
public sealed class DiscordMessageBuilderTests
{
    private static DiscordPluginOptions Options() => new()
    {
        Enabled = true,
        DefaultChannelId = "1100000000000000001",
        AgnesBaseUrl = "https://agnes.example.invalid",
    };

    private static Notification QuestionNotification() => new()
    {
        ConditionId = "operator_question",
        Title = "Input needed",
        Summary = "Which approach?",
        Severity = NotificationSeverity.Warning,
        Timestamp = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        Fields = new Dictionary<string, string> { ["Project"] = "demo", ["Work item"] = "wi-1" },
        AnswerUrl = "https://codeybox.example.invalid/questions/1",
        CorrelationToken = "abc123:q-001",
        Actions =
        [
            new NotificationAction { Label = "Use rollbacks", Value = "Use rollbacks", WorkItemId = "abc123", QuestionId = "q-001" },
            new NotificationAction { Label = "Forward only", Value = "Forward only", WorkItemId = "abc123", QuestionId = "q-001" },
        ],
    };

    private static JsonElement PayloadJson(Dictionary<string, object?> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void BuildMessage_RendersSeverityAndFields()
    {
        var (payload, workItemId) = DiscordMessageBuilder.BuildMessage(QuestionNotification(), Options());

        Assert.Equal("abc123", workItemId);
        var root = PayloadJson(payload);
        Assert.Equal("⚠️ [Warning] Input needed", root.GetProperty("content").GetString()!.Split('\n')[0]);
        var embed = root.GetProperty("embeds")[0];
        Assert.Equal(0xECB22E, embed.GetProperty("color").GetInt32());
        Assert.Contains("Which approach?", embed.GetProperty("description").GetString(), StringComparison.Ordinal);
        var fields = embed.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(2, fields.Count);
        Assert.Equal("Project", fields[0].GetProperty("name").GetString());
        Assert.Equal("demo", fields[0].GetProperty("value").GetString());
        // Mentions are never parsed: untrusted text must not ping anyone.
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public void BuildMessage_RendersAnswerButtonsWithDecodableBindings()
    {
        var (payload, _) = DiscordMessageBuilder.BuildMessage(QuestionNotification(), Options());

        var root = PayloadJson(payload);
        var rows = root.GetProperty("components").EnumerateArray().ToList();
        var answerRow = rows[0].GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(2, answerRow.Count);
        Assert.Equal(2, answerRow[0].GetProperty("type").GetInt32());
        Assert.Equal(1, answerRow[0].GetProperty("style").GetInt32());
        Assert.Equal("Use rollbacks", answerRow[0].GetProperty("label").GetString());

        foreach (var button in answerRow)
        {
            var customId = button.GetProperty("custom_id").GetString()!;
            Assert.True(customId.Length <= 100, $"custom_id exceeds Discord budget: {customId}");
            Assert.True(CodeyBox.Core.DiscordButtonCodec.TryDecode(customId, out var w, out var q, out _));
            Assert.Equal("abc123", w);
            Assert.Equal("q-001", q);
        }

        var linkRow = rows[^1].GetProperty("components").EnumerateArray().ToList();
        Assert.All(linkRow, b => Assert.Equal(5, b.GetProperty("style").GetInt32()));
        Assert.Contains(linkRow, b => b.GetProperty("url").GetString() == "https://codeybox.example.invalid/questions/1");
        Assert.Contains(linkRow, b => b.GetProperty("url").GetString() == "https://agnes.example.invalid/workitems/abc123");
    }

    [Fact]
    public void BuildMessage_CriticalSeverity_IsRed()
    {
        var notification = QuestionNotification() with { Severity = NotificationSeverity.Critical };

        var (payload, _) = DiscordMessageBuilder.BuildMessage(notification, Options());

        var embed = PayloadJson(payload).GetProperty("embeds")[0];
        Assert.Equal(0xE01E5A, embed.GetProperty("color").GetInt32());
        Assert.StartsWith("🚨", embed.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_OversizedAnswer_DegradesToLinks()
    {
        var notification = QuestionNotification() with
        {
            Actions =
            [
                new NotificationAction { Label = "Huge", Value = new string('a', 200), WorkItemId = "abc123", QuestionId = "q-001" },
            ],
        };

        var (payload, workItemId) = DiscordMessageBuilder.BuildMessage(notification, Options());

        Assert.Equal("abc123", workItemId);
        var root = PayloadJson(payload);
        var buttons = root.GetProperty("components").EnumerateArray()
            .SelectMany(r => r.GetProperty("components").EnumerateArray())
            .ToList();
        // The unresolvable answer button is gone; the question stays
        // answerable through the link buttons.
        Assert.DoesNotContain(buttons, b =>
            b.TryGetProperty("custom_id", out _));
        Assert.Contains(buttons, b =>
            b.GetProperty("style").GetInt32() == 5
            && b.GetProperty("url").GetString() == "https://codeybox.example.invalid/questions/1");
    }

    [Fact]
    public void BuildMessage_LinksMode_RendersNoAnswerButtons()
    {
        var options = Options();
        options.ActionsMode = DiscordActionsMode.Links;

        var (payload, _) = DiscordMessageBuilder.BuildMessage(QuestionNotification(), options);

        var root = PayloadJson(payload);
        var buttons = root.GetProperty("components").EnumerateArray()
            .SelectMany(r => r.GetProperty("components").EnumerateArray())
            .ToList();
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Equal(5, b.GetProperty("style").GetInt32()));
    }

    [Fact]
    public void BuildMessage_FleetNotification_HasNoButtons()
    {
        var notification = new Notification
        {
            ConditionId = "queue_empty",
            Title = "Queue.device is empty",
            Summary = "Nothing to do.",
            Severity = NotificationSeverity.Information,
        };

        var (payload, workItemId) = DiscordMessageBuilder.BuildMessage(notification, Options());

        Assert.Null(workItemId);
        var root = PayloadJson(payload);
        Assert.False(root.TryGetProperty("components", out _));
        Assert.Equal(0x2EB67D, root.GetProperty("embeds")[0].GetProperty("color").GetInt32());
    }

    [Fact]
    public void BuildMessage_CapsActionRowsAtDiscordLimit()
    {
        var options = Options();
        options.MaxActions = 25;
        var actions = Enumerable.Range(0, 25)
            .Select(i => new NotificationAction { Label = $"Option {i}", Value = $"v{i}", WorkItemId = "abc123", QuestionId = "q-001" })
            .ToList();
        var notification = new Notification
        {
            ConditionId = "operator_question",
            Title = "Pick one",
            AnswerUrl = "https://codeybox.example.invalid/questions/1",
            CorrelationToken = "abc123:q-001",
            Actions = actions,
        };

        var (payload, _) = DiscordMessageBuilder.BuildMessage(notification, options);

        var root = PayloadJson(payload);
        var rows = root.GetProperty("components").EnumerateArray().ToList();
        Assert.True(rows.Count <= 5, $"Discord rejects more than 5 action rows (got {rows.Count})");
        Assert.Equal(25, rows.SelectMany(r => r.GetProperty("components").EnumerateArray()).Count());
    }

    [Fact]
    public void BuildDecidedPayload_ShowsDecisionAndRemovesButtons()
    {
        var payload = DiscordMessageBuilder.BuildDecidedPayload(
            "Input needed", "Decided: Use rollbacks — by discord:U1 (alice)", "operator_question");

        var root = PayloadJson(payload);
        Assert.Contains("Use rollbacks", root.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Use rollbacks", root.GetProperty("embeds")[0].GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("components").EnumerateArray());
    }
}
